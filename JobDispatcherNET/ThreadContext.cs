namespace JobDispatcherNET;

/// <summary>
/// Manages thread-local storage for the job dispatcher.
/// All fields use ThreadLocal — safe on dedicated worker threads.
/// Tracks all created TimerQueues for cleanup of non-worker thread timers.
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

    /// <summary>
    /// Gets the timer queue for the current thread
    /// </summary>
    public static TimerQueue Timer => _timer.Value!;

    /// <summary>
    /// Queue of dispatchers waiting to be flushed on this thread (O(1) enqueue/dequeue)
    /// </summary>
    public static Queue<AsyncExecutable> ExecuterQueue => _executerQueue.Value!;

    /// <summary>
    /// Gets or sets the current executer occupying this thread
    /// </summary>
    public static AsyncExecutable? CurrentExecuter
    {
        get => _currentExecuter.Value;
        set => _currentExecuter.Value = value;
    }

    /// <summary>
    /// Gets or sets the current tick count (set by JobDispatcher worker loop)
    /// </summary>
    public static long TickCount
    {
        get => _tickCount.Value;
        set => _tickCount.Value = value;
    }
}

/// <summary>
/// Tracks all TimerQueue instances via WeakReference.
/// Allows disposing leaked timers from non-worker threads.
/// </summary>
public static class TimerRegistry
{
    private static readonly List<WeakReference<TimerQueue>> _timers = [];
    private static readonly object _lock = new();

    internal static void Track(TimerQueue tq)
    {
        lock (_lock)
        {
            _timers.Add(new WeakReference<TimerQueue>(tq));
        }
    }

    /// <summary>
    /// Disposes all tracked TimerQueues that are still alive.
    /// Call this during application shutdown to clean up non-worker thread timers.
    /// </summary>
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
        }
    }
}
