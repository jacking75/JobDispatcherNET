using System.Threading.Channels;

namespace JobDispatcherNET;

/// <summary>
/// 자기만의 작업 큐를 가지는 actor 의 베이스.
/// 같은 인스턴스의 작업은 lock 없이 자동 직렬화된다.
///
/// v2 변경점:
///   - <see cref="JobOptions"/> 로 큐 크기/drop 정책을 옵션화 (OOM 방어).
///   - <see cref="DoAsync{TState}"/> 오버로드로 closure 할당 제거 가능.
///   - <see cref="RemainingTaskCount"/> 노출로 큐 깊이 모니터링 가능.
///   - 글로벌 셧다운 게이트 <see cref="AcceptingWork"/> 로 종료 시 새 입력 차단.
/// </summary>
public abstract class AsyncExecutable : IAsyncDisposable
{
    /// <summary>
    /// actor 큐에서 발생한 미처리 예외 콜백. 설정하지 않으면 <see cref="JobLog"/> 로 출력.
    /// </summary>
    public static Action<Exception>? OnError { get; set; }

    /// <summary>
    /// 글로벌 입력 차단 플래그. 셧다운 시 false 로 두면 모든 인스턴스의 DoAsync 가 즉시 거부된다.
    /// 거부된 작업은 <see cref="JobMetrics.IncrementDropped"/> 으로 카운트.
    /// </summary>
    public static bool AcceptingWork { get; set; } = true;

    private readonly Channel<JobEntry> _jobQueue;
    private readonly JobOptions _options;
    private int _remainingTaskCount;
    private volatile TaskCompletionSource? _drainTcs;

    /// <summary>큐 깊이(인플라이트 + 대기). 모니터링/메트릭 용도.</summary>
    public int RemainingTaskCount => Volatile.Read(ref _remainingTaskCount);

    protected AsyncExecutable() : this(JobOptions.Default) { }

    protected AsyncExecutable(JobOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;

        if (options.MaxQueueSize is int max && max > 0)
        {
            // Wait 모드 + TryWrite 즉시 결과 — 가득 차면 즉시 false 반환.
            // Drop* 모드는 _remainingTaskCount 와 채널 길이 sync 가 깨지므로 사용하지 않는다.
            _jobQueue = Channel.CreateBounded<JobEntry>(new BoundedChannelOptions(max)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });
        }
        else
        {
            _jobQueue = Channel.CreateUnbounded<JobEntry>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
        }
    }

    /// <summary>
    /// 람다(Action) 작업 등록. closure 가 캡처를 가지면 매 호출 알로케이션 발생.
    /// hot path 에서는 <see cref="DoAsync{TState}"/> 사용을 고려.
    /// </summary>
    public bool DoAsync(Action action)
    {
        if (!AcceptingWork)
        {
            JobMetrics.IncrementDropped();
            return false;
        }
        var job = Job.Rent(action);
        return DoTask(job);
    }

    /// <summary>
    /// state 를 명시적으로 전달하여 closure 알로케이션을 피하는 오버로드.
    /// action 은 static 람다(<c>static (s) =&gt; ...</c>) 권장.
    /// </summary>
    public bool DoAsync<TState>(Action<TState> action, TState state)
    {
        if (!AcceptingWork)
        {
            JobMetrics.IncrementDropped();
            return false;
        }
        var job = Job<TState>.Rent(action, state);
        return DoTask(job);
    }

    /// <summary>
    /// 지연 실행. v2 부터는 timer fire 가 ThreadPool 이 아닌 워커 스레드에서 일어남.
    /// </summary>
    public void DoAsyncAfter(TimeSpan delay, Action action)
    {
        if (!AcceptingWork) { JobMetrics.IncrementDropped(); return; }
        var job = Job.Rent(action);
        ThreadContext.Timer.ScheduleTask(this, delay, job);
    }

    /// <summary>state 전달 버전 지연 실행.</summary>
    public void DoAsyncAfter<TState>(TimeSpan delay, Action<TState> action, TState state)
    {
        if (!AcceptingWork) { JobMetrics.IncrementDropped(); return; }
        var job = Job<TState>.Rent(action, state);
        ThreadContext.Timer.ScheduleTask(this, delay, job);
    }

    /// <summary>true 반환 = 정상 등록, false = 큐 만원/채널 닫힘으로 거부.</summary>
    internal bool DoTask(JobEntry task)
    {
        if (Interlocked.Increment(ref _remainingTaskCount) > 1)
        {
            if (!_jobQueue.Writer.TryWrite(task))
            {
                Interlocked.Decrement(ref _remainingTaskCount);
                ReportDropped(task);
                return false;
            }
        }
        else
        {
            if (!_jobQueue.Writer.TryWrite(task))
            {
                Interlocked.Decrement(ref _remainingTaskCount);
                ReportDropped(task);
                return false;
            }

            var currentExecuter = ThreadContext.CurrentExecuter;
            if (currentExecuter is not null)
            {
                ThreadContext.ExecuterQueue.Enqueue(this);
            }
            else
            {
                try
                {
                    ThreadContext.CurrentExecuter = this;

                    Flush();

                    while (ThreadContext.ExecuterQueue.TryDequeue(out var dispatcher))
                    {
                        dispatcher.Flush();
                    }
                }
                finally
                {
                    ThreadContext.CurrentExecuter = null;
                }
            }
        }

        return true;
    }

    private void ReportDropped(JobEntry task)
    {
        JobMetrics.IncrementDropped();
        try
        {
            _options.OnDropped?.Invoke(this, task);
        }
        catch (Exception ex)
        {
            JobLog.Error("OnDropped callback threw", ex);
        }
    }

    /// <summary>
    /// 워커 측 SpinWait 의 한도. 한도 초과 시 <see cref="Thread.Yield"/>.
    /// SpinWait 자체가 ~10회 후 Yield 로 전환되므로 이 값은 추가 안전망.
    /// </summary>
    public static int MaxFlushSpinIterations { get; set; } = 1000;

    internal void Flush()
    {
        var spinner = new SpinWait();
        int iterations = 0;
        while (true)
        {
            if (_jobQueue.Reader.TryRead(out var job))
            {
                spinner.Reset();
                iterations = 0;

                try
                {
                    job.Execute();
                    JobMetrics.IncrementExecuted();
                }
                catch (Exception ex)
                {
                    JobMetrics.IncrementFailed();
                    if (OnError is { } handler)
                    {
                        try { handler(ex); }
                        catch (Exception inner) { JobLog.Error("OnError handler threw", inner); }
                    }
                    else
                    {
                        JobLog.Error("Unhandled job error", ex);
                    }
                }

                if (Interlocked.Decrement(ref _remainingTaskCount) == 0)
                {
                    _drainTcs?.TrySetResult();
                    break;
                }
            }
            else
            {
                if (++iterations >= MaxFlushSpinIterations)
                {
                    Thread.Yield();
                    iterations = 0;
                    spinner.Reset();
                }
                else
                {
                    spinner.SpinOnce();
                }
            }
        }
    }

    /// <summary>
    /// 큐 drain + 채널 close. signal-based(no polling).
    /// </summary>
    public virtual async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _remainingTaskCount) > 0)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _drainTcs = tcs;

            if (Volatile.Read(ref _remainingTaskCount) > 0)
                await tcs.Task;
        }

        _jobQueue.Writer.Complete();
        GC.SuppressFinalize(this);
    }
}
