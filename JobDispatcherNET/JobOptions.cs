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

    /// <summary>
    /// The actor already has <see cref="JobOptions.MaxPendingTimers"/> timers armed, so
    /// <see cref="AsyncExecutable.DoAsyncAfter(TimeSpan, Action)"/> /
    /// <see cref="AsyncExecutable.DoAsyncEvery"/> refused to arm another.
    /// </summary>
    TimerQueueFull,
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

    /// <summary>
    /// Most timers this actor may have armed at once. <c>null</c> (the default) follows
    /// <see cref="MaxQueueSize"/>, and is unbounded when that is.
    ///
    /// <para>A timer holds no queue slot until it fires, so <see cref="MaxQueueSize"/> alone does
    /// not bound it: a client that arms a cooldown timer per packet grows the timer heap — an entry
    /// plus its payload job each time — for as long as it keeps sending, and the queue bound only
    /// starts dropping things once they come due. Refusals are reported as
    /// <see cref="DropReason.TimerQueueFull"/>. A repeating timer counts as one for its whole life.</para>
    /// </summary>
    public int? MaxPendingTimers { get; init; }

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
    /// When a job on a worker makes more than one idle actor ready, hand the extras to the worker
    /// pool instead of running them all on the flushing thread. Default <c>true</c>.
    ///
    /// <para>Waking one actor from another queues it on a thread-local list, which is the right
    /// trade for a single hop: no wake-up, no ready-queue round trip. But that list is thread-local,
    /// so no other worker can take from it — a zone actor broadcasting to a hundred player actors
    /// ran all hundred on the one worker while the rest of the pool sat idle. The first actor a
    /// flush makes ready still stays local; the rest go to the pool.</para>
    ///
    /// <para>Set it to <c>false</c> to keep the pre-fix behaviour on a workload where the extra hop
    /// costs more than the parallelism buys.</para>
    /// </summary>
    public bool FanOutToWorkers { get; init; } = true;

    /// <summary>
    /// Also report failures of <c>Ask</c> / <c>AskAsync</c> through
    /// <see cref="AsyncExecutable.OnJobError"/>. Default <c>false</c>.
    ///
    /// <para>Their exception already reaches the caller through the returned task, so calling the
    /// actor's error hook as well reports it twice. <c>RunAsync</c> is not covered by this flag: it
    /// returns no value, is commonly fire-and-forget, and staying silent there means the failure is
    /// recorded nowhere at all. Metrics and the <see cref="MaxConsecutiveFailures"/> streak see every
    /// failure either way.</para>
    /// </summary>
    public bool ReportAwaitedFailures { get; init; }

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
