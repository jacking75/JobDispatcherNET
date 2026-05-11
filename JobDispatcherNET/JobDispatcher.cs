namespace JobDispatcherNET;

/// <summary>
/// JobDispatcher 동작 옵션.
/// </summary>
public sealed record JobDispatcherOptions
{
    public static readonly JobDispatcherOptions Default = new();

    /// <summary>워커가 unhandled 예외로 죽으면 자동 재기동. 기본 true.</summary>
    public bool RestartFailedWorkers { get; init; } = true;

    /// <summary>한 워커당 누적 재기동 한도. 기본 5. 초과 시 그 슬롯은 영구 정지.</summary>
    public int MaxRestartsPerWorker { get; init; } = 5;

    /// <summary>재기동 간 최소 간격 (지수 백오프 시작값). 기본 1초.</summary>
    public TimeSpan RestartBackoff { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>한 tick 에 워커가 처리할 timer dispatch 수의 상한. 기본 256.</summary>
    public int MaxTimerDrainPerTick { get; init; } = TimerDispatchQueue.MaxDrainPerTick;
}

/// <summary>
/// 전용 OS 스레드 N 개에서 IRunnable 을 실행하는 워커 풀.
///
/// v2 변경점:
///   - <see cref="JobDispatcherOptions"/> 로 supervisor / 백오프 / timer drain 제어.
///   - 워커 사망 시 자동 재기동 (한도 초과 시 영구 정지).
///   - 워커 Run() 직전에 <see cref="TimerDispatchQueue"/> 를 드레인 →
///     timer fire 가 ThreadPool 이 아닌 워커 스레드에서 실행됨.
/// </summary>
public sealed class JobDispatcher<T> : IDisposable, IAsyncDisposable where T : IRunnable, new()
{
    private readonly int _workerCount;
    private readonly Thread[] _threads;
    private readonly int[] _restartCounts;
    private readonly JobDispatcherOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private TaskCompletionSource? _allWorkersDone;
    private int _completedWorkers;
    private int _disposed;

    public JobDispatcher(int workerCount) : this(workerCount, JobDispatcherOptions.Default) { }

    public JobDispatcher(int workerCount, JobDispatcherOptions options)
    {
        if (workerCount < 1) throw new ArgumentOutOfRangeException(nameof(workerCount), "must be >= 1");
        ArgumentNullException.ThrowIfNull(options);
        _workerCount = workerCount;
        _options = options;
        _threads = new Thread[workerCount];
        _restartCounts = new int[workerCount];
    }

    /// <summary>
    /// 워커 스레드 N 개 시작. 모든 워커가 종료되면 완료되는 Task 반환.
    /// </summary>
    public Task RunWorkerThreadsAsync()
    {
        _allWorkersDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        for (int i = 0; i < _workerCount; i++)
        {
            int slot = i;
            StartWorkerOnSlot(slot, isRestart: false);
        }

        return _allWorkersDone.Task;
    }

    private void StartWorkerOnSlot(int slot, bool isRestart)
    {
        var thread = new Thread(() => RunWorker(slot))
        {
            IsBackground = true,
            Name = isRestart ? $"JobWorker-{slot}-r{_restartCounts[slot]}" : $"JobWorker-{slot}",
        };
        _threads[slot] = thread;
        thread.Start();
    }

    private void RunWorker(int slot)
    {
        IRunnable? runner = null;
        bool exitedNormally = false;

        try
        {
            runner = new T();

            while (!_cts.Token.IsCancellationRequested)
            {
                ThreadContext.TickCount = ThreadContext.Timer.GetCurrentTick();

                // ★ Timer dispatch 펌프: 비-워커 스레드(ThreadPool)에서 fire 된 timer 를
                // 워커 스레드로 끌어와 owner.DoTask 를 자기 스레드에서 실행한다.
                int drained = 0;
                int maxDrain = _options.MaxTimerDrainPerTick;
                while (drained < maxDrain && TimerDispatchQueue.TryDequeue(out var item))
                {
                    try
                    {
                        item.Owner.DoTask(item.Job);
                    }
                    catch (Exception ex)
                    {
                        JobLog.Error("Timer-dispatched job failed", ex);
                        AsyncExecutable.OnError?.Invoke(ex);
                    }
                    drained++;
                }

                if (!runner.Run(_cts.Token))
                    break;
            }

            exitedNormally = true;
        }
        catch (OperationCanceledException)
        {
            exitedNormally = true;
        }
        catch (Exception ex)
        {
            JobLog.Error($"Worker slot #{slot} crashed", ex);
            AsyncExecutable.OnError?.Invoke(ex);
        }

        // 정리 (finally 가 아닌 일반 흐름 — 아래에서 return 으로 빠져나가야 하므로)
        try { runner?.Dispose(); } catch { }
        try { ThreadContext.Timer.Dispose(); } catch { }

        // supervisor 처리: 비정상 종료 + 재시작 정책 활성 + dispatcher 살아있음 → 재기동
        if (!exitedNormally
            && _options.RestartFailedWorkers
            && Volatile.Read(ref _disposed) == 0
            && !_cts.IsCancellationRequested)
        {
            int attempts = Interlocked.Increment(ref _restartCounts[slot]);
            if (attempts <= _options.MaxRestartsPerWorker)
            {
                JobMetrics.IncrementWorkerRestarts();
                var backoff = TimeSpan.FromMilliseconds(
                    _options.RestartBackoff.TotalMilliseconds * Math.Pow(2, attempts - 1));
                JobLog.Warn($"Restarting worker slot #{slot} (attempt {attempts}/{_options.MaxRestartsPerWorker}) after {backoff.TotalMilliseconds:F0}ms");

                Thread.Sleep(backoff);

                if (Volatile.Read(ref _disposed) == 0 && !_cts.IsCancellationRequested)
                {
                    StartWorkerOnSlot(slot, isRestart: true);
                    return; // 재기동했으니 본 스레드는 종료, 완료 카운트는 증가시키지 않음
                }
            }
            else
            {
                JobLog.Error($"Worker slot #{slot} exceeded max restarts ({_options.MaxRestartsPerWorker}) — permanently down");
            }
        }

        // 정상/한도초과/dispatcher 종료 — 워커 슬롯 1개 완료로 카운트
        if (Interlocked.Increment(ref _completedWorkers) == _workerCount)
            _allWorkersDone?.TrySetResult();
    }

    /// <summary>현재 살아있는 워커 스레드 수 (메트릭).</summary>
    public int LiveWorkerCount
    {
        get
        {
            int alive = 0;
            foreach (var t in _threads)
                if (t is { IsAlive: true }) alive++;
            return alive;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // 신규 입력 차단은 사용자 책임 (process-wide static 이므로 자동 토글하지 않음).
        // 셧다운 시퀀스: AsyncExecutable.AcceptingWork = false → 잔여 작업 drain → dispatcher.Dispose.

        _cts.Cancel();
        foreach (var thread in _threads)
        {
            if (thread is { IsAlive: true })
                thread.Join(TimeSpan.FromSeconds(5));
        }
        _cts.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
