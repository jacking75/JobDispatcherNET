namespace JobDispatcherNET;

/// <summary>Policy applied when an actor queue is full.</summary>
public enum DropPolicy
{
    /// <summary>Reject the job and invoke <see cref="JobOptions.OnDropped"/> so the caller sees back-pressure.</summary>
    Reject,

    /// <summary>Reject the job without invoking <see cref="JobOptions.OnDropped"/>.</summary>
    Silent,
}

/// <summary>Why a job was refused.</summary>
public enum DropReason
{
    /// <summary>The actor queue was at <see cref="JobOptions.MaxQueueSize"/>.</summary>
    QueueFull,

    /// <summary>The owning <see cref="JobSystem"/> is no longer accepting work.</summary>
    ShuttingDown,

    /// <summary>The actor has been disposed.</summary>
    Disposed,

    /// <summary>The actor tripped <see cref="JobOptions.MaxConsecutiveFailures"/> and is faulted.</summary>
    Faulted,
}

/// <summary>Where an actor's jobs run when the first job arrives on an idle actor.</summary>
public enum ExecutionMode
{
    /// <summary>
    /// The producer that finds the actor idle flushes it inline, on its own thread.
    /// Lowest latency, and the right choice for actor-to-actor calls inside a worker.
    /// The caveat: a non-worker producer (socket IO thread, ThreadPool continuation,
    /// an ASP.NET request thread) then runs actor code on that thread.
    /// </summary>
    LeaderFlush,

    /// <summary>
    /// A producer on a non-worker thread only hands the actor to the job system's ready queue
    /// and returns; a worker thread flushes it. Producers on worker threads still flush inline.
    /// Use this for actors reached directly from network / ThreadPool threads.
    /// </summary>
    Scheduled,
}

/// <summary>How an actor behaves while one of its jobs is awaiting.</summary>
public enum AsyncReentrancy
{
    /// <summary>
    /// Other queued jobs run while an async job awaits. Highest throughput; the actor's
    /// invariants must tolerate a job observing state changed by jobs that ran during the await.
    /// </summary>
    Interleaved,

    /// <summary>
    /// The actor processes nothing else until the async job completes. Simplest to reason about,
    /// but one slow await stalls the whole actor.
    /// </summary>
    Exclusive,
}

/// <summary>
/// Per-actor options. In a long-running server an unbounded queue is an OOM vector,
/// so setting <see cref="MaxQueueSize"/> is strongly recommended.
/// </summary>
public sealed record JobOptions
{
    /// <summary>Defaults: unbounded queue, inline leader flush, interleaved async.</summary>
    public static readonly JobOptions Default = new();

    /// <summary>Optional name used in logs, metric tags and <c>ToString()</c>.</summary>
    public string? Name { get; init; }

    /// <summary>
    /// Maximum number of jobs (queued + in flight). <c>null</c> means unbounded.
    ///
    /// The continuation of an <see cref="AsyncReentrancy.Interleaved"/> <c>await</c> is exempt: it
    /// belongs to a job the bound already admitted, and refusing it would strand the awaiting task
    /// forever. <see cref="AsyncExecutable.RemainingTaskCount"/> can therefore sit above this value
    /// by the number of async jobs currently awaiting.
    /// </summary>
    public int? MaxQueueSize { get; init; }

    /// <summary>What to do when the queue is full. Ignored when <see cref="MaxQueueSize"/> is <c>null</c>.</summary>
    public DropPolicy DropPolicy { get; init; } = DropPolicy.Reject;

    /// <summary>
    /// Invoked when a job is refused, with the reason. Only called for <see cref="DropPolicy.Reject"/>.
    /// The rejected job itself is not handed out — it is recycled by the library.
    /// </summary>
    public Action<AsyncExecutable, DropReason>? OnDropped { get; init; }

    /// <summary>Which thread runs this actor's jobs. See <see cref="ExecutionMode"/>.</summary>
    public ExecutionMode Mode { get; init; } = ExecutionMode.LeaderFlush;

    /// <summary>
    /// Fairness cap: after this many jobs the flushing thread yields the actor back to the
    /// system ready queue instead of draining to empty. Prevents one hot actor from
    /// monopolising a worker. Requires a running dispatcher; ignored when there are no workers.
    /// Default <see cref="int.MaxValue"/> (drain to empty, the historical behaviour).
    /// </summary>
    public int MaxJobsPerFlush { get; init; } = int.MaxValue;

    /// <summary>
    /// After this many consecutive job failures the actor moves to a faulted state and refuses
    /// further work until <see cref="AsyncExecutable.ClearFault"/> is called. 0 disables the check.
    /// </summary>
    public int MaxConsecutiveFailures { get; init; }

    /// <summary>Behaviour while an async job awaits. See <see cref="AsyncReentrancy"/>.</summary>
    public AsyncReentrancy AsyncReentrancy { get; init; } = AsyncReentrancy.Interleaved;

    /// <summary>
    /// The job system this actor belongs to. <c>null</c> uses <see cref="JobSystem.Default"/>.
    /// </summary>
    public JobSystem? System { get; init; }
}
