namespace JobDispatcherNET;

/// <summary>
/// Per-thread state used by the dispatcher.
/// Backed by <c>[ThreadStatic]</c> fields (cheaper than <see cref="ThreadLocal{T}"/> on the hot path).
/// </summary>
public static class ThreadContext
{
    [ThreadStatic] private static AsyncExecutable? _currentExecuter;
    [ThreadStatic] private static Queue<AsyncExecutable>? _executerQueue;
    [ThreadStatic] private static long _tickCount;
    [ThreadStatic] private static bool _isWorkerThread;
    [ThreadStatic] private static JobSystem? _currentSystem;

    /// <summary>
    /// The actor currently being flushed on this thread, or <c>null</c>.
    /// Non-null means "this thread is inside an actor job" — used for the re-entrancy
    /// hand-off in <see cref="AsyncExecutable.DoTask"/> and by the blocking-wait guard.
    /// </summary>
    public static AsyncExecutable? CurrentExecuter
    {
        get => _currentExecuter;
        internal set => _currentExecuter = value;
    }

    /// <summary>
    /// Actors that became ready while this thread was already flushing another actor.
    /// Drained by the outermost flush loop so nested dispatch never recurses.
    /// </summary>
    public static Queue<AsyncExecutable> ExecuterQueue => _executerQueue ??= new Queue<AsyncExecutable>();

    /// <summary>
    /// Monotonic milliseconds owned by the job system, refreshed once per worker iteration while
    /// <see cref="JobSystemOptions.EnableDetailedMetrics"/> is on and left alone otherwise.
    /// Diagnostic only — nothing in the library reads it, and refreshing it costs a timestamp read.
    /// </summary>
    public static long TickCount
    {
        get => _tickCount;
        set => _tickCount = value;
    }

    /// <summary>
    /// True on threads created by <em>any</em> <see cref="JobDispatcher"/> /
    /// <see cref="JobDispatcher{T}"/> in the process, whichever system it serves. Use
    /// <see cref="CurrentSystem"/> when the question is "one of <em>my</em> workers?" — which is what
    /// <see cref="ExecutionMode.Scheduled"/> asks before handing an actor to its own pool.
    /// </summary>
    public static bool IsWorkerThread
    {
        get => _isWorkerThread;
        internal set => _isWorkerThread = value;
    }

    /// <summary>
    /// The job system that owns this thread — a worker or the timer thread — or <c>null</c>
    /// elsewhere. This, not <see cref="IsWorkerThread"/>, is what decides whether an
    /// <see cref="ExecutionMode.Scheduled"/> actor may be flushed inline.
    /// </summary>
    public static JobSystem? CurrentSystem
    {
        get => _currentSystem;
        internal set => _currentSystem = value;
    }
}
