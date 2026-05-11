namespace JobDispatcherNET;

/// <summary>
/// 워커 스레드 전용 ThreadLocal 저장소.
/// 일반 스레드에서 접근 시 lazy 생성되며, 비-워커 스레드에서 만들어진 TimerQueue 는
/// <see cref="TimerRegistry.DisposeAll"/> 로 명시적 정리 필요.
/// </summary>
public static class ThreadContext
{
    private static readonly ThreadLocal<TimerQueue> _timer = new(() =>
    {
        var tq = new TimerQueue();
        TimerRegistry.Track(tq);
        return tq;
    });
    private static readonly ThreadLocal<Queue<AsyncExecutable>> _executerQueue = new(() => new Queue<AsyncExecutable>());
    private static readonly ThreadLocal<AsyncExecutable?> _currentExecuter = new(() => null);
    private static readonly ThreadLocal<long> _tickCount = new();

    public static TimerQueue Timer => _timer.Value!;
    public static Queue<AsyncExecutable> ExecuterQueue => _executerQueue.Value!;

    public static AsyncExecutable? CurrentExecuter
    {
        get => _currentExecuter.Value;
        set => _currentExecuter.Value = value;
    }

    public static long TickCount
    {
        get => _tickCount.Value;
        set => _tickCount.Value = value;
    }
}

/// <summary>
/// 모든 TimerQueue 인스턴스를 WeakReference 로 추적.
/// dead reference 가 무한히 누적되지 않도록 Track 시 주기적으로 청소한다.
/// </summary>
public static class TimerRegistry
{
    private static readonly List<WeakReference<TimerQueue>> _timers = [];
    private static readonly object _lock = new();
    private static int _trackCount;

    /// <summary>몇 번 Track 마다 dead-ref 청소를 수행할지 (기본 64).</summary>
    public static int CleanupInterval { get; set; } = 64;

    internal static void Track(TimerQueue tq)
    {
        lock (_lock)
        {
            _timers.Add(new WeakReference<TimerQueue>(tq));

            if (++_trackCount >= CleanupInterval)
            {
                _trackCount = 0;
                CleanupDeadReferencesLocked();
            }
        }
    }

    private static void CleanupDeadReferencesLocked()
    {
        // _lock 보유 상태에서 호출.
        int writeIdx = 0;
        for (int readIdx = 0; readIdx < _timers.Count; readIdx++)
        {
            if (_timers[readIdx].TryGetTarget(out _))
            {
                if (writeIdx != readIdx)
                    _timers[writeIdx] = _timers[readIdx];
                writeIdx++;
            }
        }
        if (writeIdx < _timers.Count)
            _timers.RemoveRange(writeIdx, _timers.Count - writeIdx);
    }

    /// <summary>현재 추적 중인 살아있는 TimerQueue 수 (메트릭).</summary>
    public static int LiveCount
    {
        get
        {
            lock (_lock)
            {
                int alive = 0;
                foreach (var wr in _timers)
                    if (wr.TryGetTarget(out _)) alive++;
                return alive;
            }
        }
    }

    /// <summary>모든 살아있는 TimerQueue 를 dispose. 셧다운 시 호출.</summary>
    public static void DisposeAll()
    {
        lock (_lock)
        {
            foreach (var wr in _timers)
            {
                if (wr.TryGetTarget(out var tq))
                    tq.Dispose();
            }
            _timers.Clear();
            _trackCount = 0;
        }
    }
}
