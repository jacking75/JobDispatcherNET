using System.Collections.Concurrent;
using System.Diagnostics;

namespace JobDispatcherNET;

/// <summary>
/// Thrown when a job could not be queued.
/// </summary>
public sealed class JobRejectedException : InvalidOperationException
{
    /// <summary>Why the job was refused.</summary>
    public DropReason Reason { get; }

    /// <summary>Create the exception.</summary>
    public JobRejectedException(DropReason reason, string message) : base(message) => Reason = reason;
}

/// <summary>
/// Base class for an actor: an object that owns a job queue.
///
/// Jobs posted to one instance run one at a time, in order, so the instance's own state needs no
/// locks. Different instances run fully in parallel.
///
/// <para><b>Which thread runs the job</b> depends on <see cref="JobOptions.Mode"/>. Under the
/// default <see cref="ExecutionMode.LeaderFlush"/> the producer that finds the actor idle runs it
/// inline — fast inside a worker, but it means a socket or thread-pool thread ends up running
/// actor code. Set <see cref="ExecutionMode.Scheduled"/> on actors reached from such threads and
/// the job system hands them to a worker instead.</para>
/// </summary>
public abstract class AsyncExecutable : IAsyncDisposable
{
    // suspend-state machine used only by AsyncReentrancy.Exclusive
    private const int SuspendNone = 0;
    private const int SuspendPending = 1;   // an async job is in flight
    private const int SuspendParked = 2;    // the flushing thread has released leadership
    private const int SuspendCompleted = 3; // the async job finished while the flusher was still running

    private static Action<Exception>? _globalOnError;

    private readonly ConcurrentQueue<JobEntry> _queue = new();
    private readonly JobSystem _system;
    private readonly JobOptions _options;
    private readonly int _maxQueueSize;
    private readonly ExecutionMode _mode;
    private readonly int _maxJobsPerFlush;
    private readonly int _maxConsecutiveFailures;
    private readonly AsyncReentrancy _reentrancy;
    private readonly ActorSynchronizationContext? _syncContext;

    private int _remainingTaskCount;
    private int _pendingAsync;
    private int _consecutiveFailures;
    private int _maxObservedQueueDepth;
    private int _faulted;
    private int _completed;
    private int _suspendState;

    // Not volatile: the drain handshake fences explicitly on both sides. See DisposeAsync()
    // and SignalDrainedIfIdle().
    private TaskCompletionSource? _drainTcs;

    /// <summary>Create an actor with default options on <see cref="JobSystem.Default"/>.</summary>
    protected AsyncExecutable() : this(JobOptions.Default) { }

    /// <summary>Create an actor with explicit options.</summary>
    protected AsyncExecutable(JobOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _system = options.System ?? JobSystem.Default;
        _maxQueueSize = options.MaxQueueSize is int max && max > 0 ? max : 0;
        _mode = options.Mode;
        _maxJobsPerFlush = options.MaxJobsPerFlush > 0 ? options.MaxJobsPerFlush : int.MaxValue;
        _maxConsecutiveFailures = Math.Max(0, options.MaxConsecutiveFailures);
        _reentrancy = options.AsyncReentrancy;
        Name = options.Name ?? GetType().Name;

        if (_reentrancy == AsyncReentrancy.Interleaved)
            _syncContext = new ActorSynchronizationContext(this);
    }

    /// <summary>Name used in logs and diagnostics. Defaults to the runtime type name.</summary>
    public string Name { get; }

    /// <summary>The job system this actor belongs to.</summary>
    public JobSystem System => _system;

    /// <summary>Queued plus in-flight jobs. Use for queue-depth monitoring.</summary>
    public int RemainingTaskCount => Volatile.Read(ref _remainingTaskCount);

    /// <summary>
    /// Async jobs that have started and are parked on an <c>await</c>.
    ///
    /// Under <see cref="AsyncReentrancy.Interleaved"/> such a job holds no queue slot — it is not
    /// part of <see cref="RemainingTaskCount"/> — yet the actor is not finished with it. Shutdown
    /// waits for this to reach zero as well.
    /// </summary>
    public int PendingAsyncJobs => Volatile.Read(ref _pendingAsync);

    /// <summary>Highest queue depth seen since construction.</summary>
    public int MaxObservedQueueDepth => Volatile.Read(ref _maxObservedQueueDepth);

    /// <summary>Nothing queued, nothing in flight, and no async job parked on an await.</summary>
    private bool IsIdle =>
        Volatile.Read(ref _remainingTaskCount) == 0 && Volatile.Read(ref _pendingAsync) == 0;

    /// <summary>
    /// True once <see cref="JobOptions.MaxConsecutiveFailures"/> consecutive jobs threw.
    /// A faulted actor refuses work until <see cref="ClearFault"/> is called.
    /// </summary>
    public bool IsFaulted => Volatile.Read(ref _faulted) != 0;

    /// <summary>Bring a faulted actor back into service.</summary>
    public void ClearFault()
    {
        Volatile.Write(ref _consecutiveFailures, 0);
        Volatile.Write(ref _faulted, 0);
    }

    /// <inheritdoc />
    public override string ToString() => $"{Name}(queue={RemainingTaskCount})";

    // ── posting work ────────────────────────────────────────────────────────

    /// <summary>
    /// Queue an <see cref="Action"/>. A closure that captures anything allocates on every call —
    /// prefer <see cref="DoAsync{TState}"/> on paths that run thousands of times a second.
    /// </summary>
    /// <returns><c>true</c> if queued, <c>false</c> if refused (queue full, shutting down, faulted, disposed).</returns>
    public bool DoAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!TryReserve(out var reason))
            return Refuse(reason);
        return Admit(Job.Rent(action), fromTimer: false);
    }

    /// <summary>
    /// Queue a job with explicit state, so no closure is allocated.
    /// Pass a <c>static</c> lambda and carry every captured value in <paramref name="state"/>.
    /// </summary>
    /// <returns><c>true</c> if queued, <c>false</c> if refused.</returns>
    public bool DoAsync<TState>(Action<TState> action, TState state)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!TryReserve(out var reason))
            return Refuse(reason);
        return Admit(Job<TState>.Rent(action, state), fromTimer: false);
    }

    /// <summary>
    /// Run <paramref name="action"/> on this actor after <paramref name="delay"/>.
    /// The returned handle cancels it.
    /// </summary>
    public ITimerHandle DoAsyncAfter(TimeSpan delay, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!TryReserve(out var reason))
        {
            Refuse(reason);
            return CancelledTimer.Instance;
        }
        return _system.Timers.Schedule(this, delay, Job.Rent(action));
    }

    /// <summary>Delayed execution with explicit state, so no closure is allocated.</summary>
    public ITimerHandle DoAsyncAfter<TState>(TimeSpan delay, Action<TState> action, TState state)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!TryReserve(out var reason))
        {
            Refuse(reason);
            return CancelledTimer.Instance;
        }
        return _system.Timers.Schedule(this, delay, Job<TState>.Rent(action, state));
    }

    /// <summary>
    /// Run <paramref name="action"/> on this actor every <paramref name="period"/> until the
    /// returned handle is cancelled. Replaces the "job re-schedules itself" idiom, and unlike that
    /// idiom it survives an exception in one tick.
    /// </summary>
    /// <param name="period">Interval between firings. Must be positive.</param>
    /// <param name="action">Work to run on this actor.</param>
    /// <param name="initialDelay">Delay before the first firing. Defaults to <paramref name="period"/>.</param>
    public ITimerHandle DoAsyncEvery(TimeSpan period, Action action, TimeSpan? initialDelay = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!TryReserve(out var reason))
        {
            Refuse(reason);
            return CancelledTimer.Instance;
        }
        return _system.Timers.ScheduleRepeating(this, period, initialDelay ?? period, action);
    }

    /// <summary>
    /// Queue a job and report why it was refused. Used by the request/response and async entry
    /// points so a rejection surfaces the real cause rather than always claiming a full queue.
    /// </summary>
    private bool TryEnqueue<TState>(Action<TState> action, TState state, out DropReason reason)
    {
        if (!TryReserve(out reason))
        {
            Refuse(reason);
            return false;
        }

        if (Admit(Job<TState>.Rent(action, state), fromTimer: false))
            return true;

        // Admit only refuses for one reason; TryReserve already covered the others.
        reason = DropReason.QueueFull;
        return false;
    }

    // ── request / response ──────────────────────────────────────────────────

    /// <summary>
    /// Run <paramref name="func"/> on this actor and await its result.
    /// Never block on the returned task from inside another actor's job — that deadlocks.
    /// </summary>
    public Task<TResult> Ask<TResult>(Func<TResult> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        var tcs = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = TryEnqueue(
            static t =>
            {
                try { t.Tcs.TrySetResult(t.Func()); }
                catch (Exception ex) { t.Tcs.TrySetException(ex); }
            },
            (Tcs: tcs, Func: func),
            out var reason);

        if (!queued)
            tcs.TrySetException(new JobRejectedException(reason, $"Actor '{Name}' refused the Ask job ({reason})."));

        return tcs.Task;
    }

    /// <summary>Run <paramref name="func"/> with explicit state on this actor and await its result.</summary>
    public Task<TResult> Ask<TState, TResult>(Func<TState, TResult> func, TState state)
    {
        ArgumentNullException.ThrowIfNull(func);
        var tcs = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = TryEnqueue(
            static t =>
            {
                try { t.Tcs.TrySetResult(t.Func(t.State)); }
                catch (Exception ex) { t.Tcs.TrySetException(ex); }
            },
            (Tcs: tcs, Func: func, State: state),
            out var reason);

        if (!queued)
            tcs.TrySetException(new JobRejectedException(reason, $"Actor '{Name}' refused the Ask job ({reason})."));

        return tcs.Task;
    }

    /// <summary>
    /// Blocking form of <see cref="Ask{TResult}(Func{TResult})"/> for callers that are not async —
    /// a console command loop, a health probe, <c>Main</c>.
    ///
    /// Throws if called from inside an actor job, because that is a guaranteed deadlock
    /// (see <see cref="JobSystemOptions.DetectBlockingWaitOnWorker"/>).
    /// </summary>
    public TResult AskSync<TResult>(Func<TResult> func, TimeSpan timeout)
    {
        JobDiagnostics.GuardBlockingWait(_system, nameof(AskSync));
        var task = Ask(func);

        // Waiting on the handle rather than Task.Wait: Wait rethrows a failed task's exception
        // wrapped in an AggregateException, so the caller saw that instead of the exception the job
        // actually threw and never reached the GetResult below. GetAwaiter().GetResult() rethrows
        // the original, stack trace intact. The handle is allocated lazily and only on the slow
        // path, which for a blocking API is not worth optimising away.
        if (!task.IsCompleted && !((IAsyncResult)task).AsyncWaitHandle.WaitOne(timeout))
            throw new TimeoutException($"Actor '{Name}' did not answer within {timeout.TotalMilliseconds:F0}ms.");

        return task.GetAwaiter().GetResult();
    }

    // ── async jobs ──────────────────────────────────────────────────────────

    /// <summary>
    /// Queue an asynchronous job. What happens at each <c>await</c> depends on
    /// <see cref="JobOptions.AsyncReentrancy"/>:
    /// <list type="bullet">
    /// <item><see cref="AsyncReentrancy.Interleaved"/> (default) — continuations come back onto this
    /// actor's queue and interleave with other jobs. Do not use <c>ConfigureAwait(false)</c>: it
    /// opts out of that and resumes on the thread pool.</item>
    /// <item><see cref="AsyncReentrancy.Exclusive"/> — the actor runs nothing else until the whole
    /// async job completes, so the continuation may safely run on the thread pool.</item>
    /// </list>
    /// </summary>
    public Task RunAsync(Func<Task> asyncAction)
    {
        ArgumentNullException.ThrowIfNull(asyncAction);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = TryEnqueue(
            static t => t.Self.StartAsyncJob(t.Fn, t.Tcs),
            (Self: this, Fn: asyncAction, Tcs: tcs),
            out var reason);

        if (!queued)
            tcs.TrySetException(new JobRejectedException(reason, $"Actor '{Name}' refused the async job ({reason})."));

        return tcs.Task;
    }

    /// <summary>Queue an asynchronous job that produces a result. See <see cref="RunAsync(Func{Task})"/>.</summary>
    public Task<TResult> AskAsync<TResult>(Func<Task<TResult>> asyncFunc)
    {
        ArgumentNullException.ThrowIfNull(asyncFunc);
        var tcs = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = TryEnqueue(
            static t => t.Self.StartAsyncJob(t.Fn, t.Tcs),
            (Self: this, Fn: asyncFunc, Tcs: tcs),
            out var reason);

        if (!queued)
            tcs.TrySetException(new JobRejectedException(reason, $"Actor '{Name}' refused the async job ({reason})."));

        return tcs.Task;
    }

    private void StartAsyncJob(Func<Task> fn, TaskCompletionSource tcs)
    {
        Task task;
        try
        {
            task = fn() ?? Task.CompletedTask;
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
            return;
        }

        if (task.IsCompleted)
        {
            Settle(task, tcs);
            return;
        }

        if (_reentrancy == AsyncReentrancy.Exclusive)
            BeginExclusiveSuspension();

        BeginAsyncTracking();

        task.ContinueWith(
            static (t, s) =>
            {
                var (self, completion, exclusive) = ((AsyncExecutable, TaskCompletionSource, bool))s!;
                if (exclusive)
                    self.EndExclusiveSuspension();
                Settle(t, completion);
                self.EndAsyncTracking();
            },
            (this, tcs, _reentrancy == AsyncReentrancy.Exclusive),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void StartAsyncJob<TResult>(Func<Task<TResult>> fn, TaskCompletionSource<TResult> tcs)
    {
        Task<TResult> task;
        try
        {
            task = fn() ?? Task.FromResult<TResult>(default!);
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
            return;
        }

        if (task.IsCompleted)
        {
            Settle(task, tcs);
            return;
        }

        if (_reentrancy == AsyncReentrancy.Exclusive)
            BeginExclusiveSuspension();

        BeginAsyncTracking();

        task.ContinueWith(
            static (t, s) =>
            {
                var (self, completion, exclusive) = ((AsyncExecutable, TaskCompletionSource<TResult>, bool))s!;
                if (exclusive)
                    self.EndExclusiveSuspension();
                Settle(t, completion);
                self.EndAsyncTracking();
            },
            (this, tcs, _reentrancy == AsyncReentrancy.Exclusive),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void Settle(Task task, TaskCompletionSource tcs)
    {
        if (task.IsFaulted) tcs.TrySetException(task.Exception!.InnerExceptions);
        else if (task.IsCanceled) tcs.TrySetCanceled();
        else tcs.TrySetResult();
    }

    private static void Settle<TResult>(Task<TResult> task, TaskCompletionSource<TResult> tcs)
    {
        if (task.IsFaulted) tcs.TrySetException(task.Exception!.InnerExceptions);
        else if (task.IsCanceled) tcs.TrySetCanceled();
        else tcs.TrySetResult(task.Result);
    }

    /// <summary>
    /// Register an async job that returned an unfinished task, i.e. one parked on an <c>await</c>.
    ///
    /// Under <see cref="AsyncReentrancy.Interleaved"/> such a job is invisible everywhere else: it
    /// holds no queue slot, sits in no ready queue and has no timer pending, so a shutdown drain
    /// used to see an idle system, stop the workers and leave the continuation with nothing to run
    /// on. Both the actor and the system count it until its task completes.
    /// </summary>
    private void BeginAsyncTracking()
    {
        Interlocked.Increment(ref _pendingAsync);
        _system.OnAsyncJobStarted();
    }

    /// <summary>
    /// Runs on whichever thread completed the async job's task, after the caller's task has been
    /// settled. The system counter is bumped first so <see cref="JobSystem.PendingAsyncJobs"/>,
    /// which reads completed-then-started, can read high but never spuriously low.
    /// </summary>
    private void EndAsyncTracking()
    {
        _system.OnAsyncJobCompleted();

        // Interlocked, then a re-read of both counters: one half of the Dekker handshake described
        // on SignalDrainedIfIdle. The other half is the flushing thread's decrement of
        // _remainingTaskCount, so exactly one of the two threads sees the actor fully idle.
        if (Interlocked.Decrement(ref _pendingAsync) == 0)
            SignalDrainedIfIdle();
    }

    /// <summary>
    /// Hold one extra reservation so the actor's count never reaches zero while the async job is
    /// pending. No other producer can become leader, and the flushing thread parks instead of
    /// draining the next job.
    ///
    /// The CAS (rather than a plain write) is what makes a stale <see cref="EndExclusiveSuspension"/>
    /// impossible: leadership is never handed on until a previous suspension has fully resolved, so
    /// the state must be <c>None</c> here. If it ever is not, two suspensions overlap and the token
    /// the old one is about to consume would belong to the new one.
    /// </summary>
    private void BeginExclusiveSuspension()
    {
        Interlocked.Increment(ref _remainingTaskCount);
        _system.OnJobAdmitted();

        var previous = Interlocked.CompareExchange(ref _suspendState, SuspendPending, SuspendNone);
        Debug.Assert(previous == SuspendNone,
            $"actor '{Name}': an exclusive suspension was already in progress (state {previous})");
    }

    /// <summary>
    /// Runs on whichever thread completed the async job's task.
    ///
    /// It deliberately does NOT release the reservation on the fast path. Releasing it here
    /// unconditionally would let <see cref="_remainingTaskCount"/> fall to zero while the flushing
    /// thread still believed it was the leader — the actor would look idle, a producer could CAS
    /// 0 to 1 and claim it, and two threads would end up inside <see cref="Flush"/> for the same
    /// actor. The reservation is instead released by whoever owns leadership when the dust settles.
    /// </summary>
    private void EndExclusiveSuspension()
    {
        // Exactly one of these two CAS attempts on the same word can win.
        if (Interlocked.CompareExchange(ref _suspendState, SuspendCompleted, SuspendPending) == SuspendPending)
        {
            // The flusher is still inside its loop and keeps leadership. It will observe
            // SuspendCompleted, release the reservation itself and carry on.
            return;
        }

        // The flusher parked, so leadership is ours. Clear the state before touching the counter:
        // while the reservation still stands nobody else can claim the actor.
        Volatile.Write(ref _suspendState, SuspendNone);
        _system.OnJobRetired();

        if (Interlocked.Decrement(ref _remainingTaskCount) == 0)
        {
            SignalDrainedIfIdle();
            return;
        }

        ScheduleOrFlush();
    }

    // ── admission ───────────────────────────────────────────────────────────

    private bool TryReserve(out DropReason reason)
    {
        if (!_system.AcceptingWork)
        {
            reason = DropReason.ShuttingDown;
            return false;
        }
        if (Volatile.Read(ref _completed) != 0)
        {
            reason = DropReason.Disposed;
            return false;
        }
        if (Volatile.Read(ref _faulted) != 0)
        {
            reason = DropReason.Faulted;
            return false;
        }
        reason = default;
        return true;
    }

    private bool Refuse(DropReason reason)
    {
        _system.Metrics.OnDropped();
        if (_options.DropPolicy == DropPolicy.Reject && _options.OnDropped is { } callback)
        {
            try { callback(this, reason); }
            catch (Exception ex) { _system.Logger.Error($"OnDropped callback for '{Name}' threw", ex); }
        }
        return false;
    }

    /// <summary>
    /// Reserve a queue slot, enqueue, and take leadership if the actor was idle.
    ///
    /// Admission is decided by a CAS on the counter, not by the queue, so the counter and the
    /// queue can never disagree. The v2.0 code incremented first and then tried to write, which
    /// left a window where the count claimed a job the queue did not have — a leader could then
    /// spin forever waiting for a job that was never written.
    /// </summary>
    private bool Admit(JobEntry task, bool fromTimer, bool bypassBound = false)
    {
        int current;
        while (true)
        {
            current = Volatile.Read(ref _remainingTaskCount);
            if (!bypassBound && _maxQueueSize != 0 && current >= _maxQueueSize)
            {
                task.Discard();
                return Refuse(DropReason.QueueFull);
            }
            if (Interlocked.CompareExchange(ref _remainingTaskCount, current + 1, current) == current)
                break;
        }

        // CAS loop: a plain read-then-write lets two producers both read the old peak and the
        // smaller one land last, so the reported maximum could go backwards.
        var depth = current + 1;
        var observed = Volatile.Read(ref _maxObservedQueueDepth);
        while (depth > observed)
        {
            var previous = Interlocked.CompareExchange(ref _maxObservedQueueDepth, depth, observed);
            if (previous == observed)
                break;
            observed = previous;
        }

        _system.OnJobAdmitted();
        _queue.Enqueue(task);

        if (current != 0)
            return true;    // somebody else already owns the flush

        // We are the leader for this actor.
        if (fromTimer)
        {
            if (_system.HasWorkers)
            {
                _system.Schedule(this);
                return true;
            }
            // No dispatcher is running. Rather than silently never firing (the v2.0 behaviour),
            // run the job here on the timer thread and say so once.
            _system.WarnTimerFallbackOnce();
            RunFlushLoop();
            return true;
        }

        if (_mode == ExecutionMode.Scheduled && !ThreadContext.IsWorkerThread && _system.HasWorkers)
        {
            _system.Schedule(this);
            return true;
        }

        if (ThreadContext.CurrentExecuter is not null)
        {
            // Already flushing another actor on this thread — queue up instead of recursing.
            ThreadContext.ExecuterQueue.Enqueue(this);
            return true;
        }

        RunFlushLoop();
        return true;
    }

    /// <summary>
    /// Queue a fired timer's job. The reason is reported because the timer service acts on it:
    /// a repeating timer whose actor has been disposed is retired rather than re-armed.
    /// </summary>
    internal bool DoTaskFromTimer(JobEntry task, out DropReason reason)
    {
        if (!TryReserve(out reason))
        {
            task.Discard();
            return Refuse(reason);
        }

        if (Admit(task, fromTimer: true))
            return true;

        // Admit only refuses for one reason; TryReserve already covered the others.
        reason = DropReason.QueueFull;
        return false;
    }

    /// <summary>Queue a pre-built job. Kept for callers that build <see cref="JobEntry"/> themselves.</summary>
    internal bool DoTask(JobEntry task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (!TryReserve(out var reason))
        {
            task.Discard();
            return Refuse(reason);
        }
        return Admit(task, fromTimer: false);
    }

    /// <summary>
    /// Entry point for the second half of an already-admitted async job: the continuation of an
    /// <see cref="AsyncReentrancy.Interleaved"/> <c>await</c>.
    ///
    /// It deliberately skips both <see cref="TryReserve"/> and the queue bound. A continuation is
    /// not new work, and refusing it strands the async state machine for good — the task handed
    /// back by <c>RunAsync</c>/<c>AskAsync</c> would never complete and neither would anything
    /// chained onto it. Since there is no safe way to say no, the actor does not ask: the
    /// continuation runs whatever the queue depth and whatever the actor's state.
    ///
    /// The price is that <see cref="RemainingTaskCount"/> may exceed
    /// <see cref="JobOptions.MaxQueueSize"/> by the number of jobs currently awaiting. Admission of
    /// genuinely new work still respects the bound, so the overshoot is capped by how many async
    /// jobs the bound let in to begin with.
    /// </summary>
    internal void AdmitContinuation(SendOrPostCallback callback, object? state) =>
        Admit(
            Job<(SendOrPostCallback Callback, object? State)>.Rent(
                static t => t.Callback(t.State), (callback, state)),
            fromTimer: false,
            bypassBound: true);

    private void ScheduleOrFlush()
    {
        if (_system.HasWorkers)
        {
            _system.Schedule(this);
            return;
        }
        if (ThreadContext.CurrentExecuter is not null)
        {
            ThreadContext.ExecuterQueue.Enqueue(this);
            return;
        }
        RunFlushLoop();
    }

    // ── flushing ────────────────────────────────────────────────────────────

    /// <summary>
    /// Legacy knob for the spin limit inside <see cref="Flush"/>. The loop now exits as soon as the
    /// counter says the queue is empty, so this only bounds the wait for a producer that is between
    /// its CAS and its enqueue.
    /// </summary>
    public static int MaxFlushSpinIterations { get; set; } = 1000;

    /// <summary>Entry point used by worker threads pulling this actor off the ready queue.</summary>
    internal void FlushAsLeader() => RunFlushLoop();

    private void RunFlushLoop()
    {
        try
        {
            ThreadContext.CurrentExecuter = this;
            Flush();

            // Actors that became ready while we were flushing. Re-point CurrentExecuter at each
            // one: it is what JobDiagnostics reports and what a worker crash log names, so it has
            // to say which actor is actually running, not just the one that started the loop.
            while (ThreadContext.ExecuterQueue.TryDequeue(out var next))
            {
                ThreadContext.CurrentExecuter = next;
                next.Flush();
            }
        }
        catch (Exception ex)
        {
            // Flush is not meant to throw — ExecuteJob catches anything a job can do — but if it
            // ever does, leadership dies with this thread while the counter still says the actor is
            // busy. No producer can claim an actor whose count is above zero, so it would stay
            // wedged for the life of the process. Hand it, and anything queued behind it on this
            // thread, back to the system before letting the exception continue on its way.
            var stuck = ThreadContext.CurrentExecuter ?? this;
            _system.Logger.Error($"Flush loop for actor '{stuck.Name}' failed; handing leadership back", ex);

            stuck.RescheduleAfterFailedFlush();
            while (ThreadContext.ExecuterQueue.TryDequeue(out var abandoned))
                abandoned.RescheduleAfterFailedFlush();

            throw;
        }
        finally
        {
            ThreadContext.CurrentExecuter = null;
        }
    }

    /// <summary>
    /// Put an actor whose flush blew up back on the ready queue, so a worker picks its leadership
    /// up. Deliberately not <see cref="ScheduleOrFlush"/>: flushing inline from here would re-enter
    /// the loop that just failed, on the same thread, with the same input.
    /// </summary>
    private void RescheduleAfterFailedFlush()
    {
        if (Volatile.Read(ref _remainingTaskCount) > 0)
            _system.Schedule(this);
    }

    internal void Flush()
    {
        var spinner = new SpinWait();
        var iterations = 0;
        var executed = 0;

        while (true)
        {
            if (_queue.TryDequeue(out var job))
            {
                spinner = new SpinWait();
                iterations = 0;

                int remaining;
                try
                {
                    ExecuteJob(job);
                }
                finally
                {
                    // In a finally so the accounting survives an exception from ExecuteJob itself
                    // (it catches everything the job can throw, but the metric and watchdog calls
                    // around it are not the library's code). Skipping it would leave the counter
                    // permanently one job above the truth, which pins the actor as busy forever and
                    // makes every later drain time out.
                    _system.OnJobRetired();
                    remaining = Interlocked.Decrement(ref _remainingTaskCount);
                }

                if (Volatile.Read(ref _suspendState) != SuspendNone)
                {
                    if (Interlocked.CompareExchange(ref _suspendState, SuspendParked, SuspendPending) == SuspendPending)
                        return;     // async job still running; its continuation resumes us

                    // The async job finished while we were still here, so we never lost leadership
                    // and we are the one that releases its reservation. Doing it here rather than in
                    // the continuation is what stops the counter from hitting zero while we still
                    // intend to keep flushing.
                    Volatile.Write(ref _suspendState, SuspendNone);
                    _system.OnJobRetired();
                    if (Interlocked.Decrement(ref _remainingTaskCount) == 0)
                    {
                        SignalDrainedIfIdle();
                        return;
                    }
                    continue;
                }

                if (remaining == 0)
                {
                    SignalDrainedIfIdle();
                    return;
                }

                if (++executed >= _maxJobsPerFlush && _system.HasWorkers)
                {
                    // Fairness: hand the actor back so one hot actor cannot own a worker forever.
                    _system.Schedule(this);
                    return;
                }
            }
            else
            {
                // The counter is the source of truth. Zero here means no producer holds a
                // reservation, so there is nothing left to wait for — the v2.0 loop had no such
                // exit and could spin a core forever after a rejected write.
                if (Volatile.Read(ref _remainingTaskCount) == 0)
                {
                    SignalDrainedIfIdle();
                    return;
                }

                if (++iterations >= MaxFlushSpinIterations)
                {
                    Thread.Yield();
                    iterations = 0;
                    spinner = new SpinWait();
                }
                else
                {
                    spinner.SpinOnce();
                }
            }
        }
    }

    private void ExecuteJob(JobEntry job)
    {
        var detailed = _system.Metrics.DetailedEnabled;
        var watchdog = _system.Options.MaxJobDuration;
        var timed = detailed || watchdog > TimeSpan.Zero;
        var start = timed ? Stopwatch.GetTimestamp() : 0L;

        var previousContext = _syncContext is null ? null : SynchronizationContext.Current;
        if (_syncContext is not null)
            SynchronizationContext.SetSynchronizationContext(_syncContext);

        try
        {
            job.Execute();
            _system.Metrics.OnExecuted();
            if (_maxConsecutiveFailures > 0)
                Volatile.Write(ref _consecutiveFailures, 0);
        }
        catch (Exception ex)
        {
            _system.Metrics.OnExecuted();
            _system.Metrics.OnFailed();
            HandleJobFailure(ex);
        }
        finally
        {
            if (_syncContext is not null)
                SynchronizationContext.SetSynchronizationContext(previousContext);

            if (timed)
            {
                if (detailed)
                    _system.Metrics.RecordJobDuration(start);
                if (watchdog > TimeSpan.Zero)
                {
                    var elapsed = Stopwatch.GetElapsedTime(start);
                    if (elapsed > watchdog)
                        _system.Logger.Warn($"Actor '{Name}' job ran {elapsed.TotalMilliseconds:F1}ms (limit {watchdog.TotalMilliseconds:F0}ms)");
                }
            }
        }
    }

    private void HandleJobFailure(Exception ex)
    {
        try
        {
            OnJobError(ex);
        }
        catch (Exception inner)
        {
            _system.Logger.Error($"OnJobError for '{Name}' threw", inner);
        }

        if (_maxConsecutiveFailures <= 0)
            return;

        if (Interlocked.Increment(ref _consecutiveFailures) < _maxConsecutiveFailures)
            return;

        if (Interlocked.Exchange(ref _faulted, 1) == 0)
        {
            _system.Metrics.OnActorFaulted();
            _system.Logger.Error(
                $"Actor '{Name}' faulted after {_maxConsecutiveFailures} consecutive failures; it will refuse work until ClearFault().");
        }
    }

    /// <summary>
    /// Called on the worker thread when one of this actor's jobs throws. Override to handle
    /// failures per actor — dropping the session that owns it, for instance. The default logs and
    /// forwards to the process-wide <see cref="OnError"/> hook if one is set.
    /// </summary>
    protected virtual void OnJobError(Exception exception)
    {
        if (_globalOnError is { } handler)
        {
            handler(exception);
            return;
        }
        _system.Logger.Error($"Unhandled error in actor '{Name}'", exception);
    }

    /// <summary>
    /// Complete a pending <see cref="DisposeAsync()"/> if the actor has gone fully idle.
    ///
    /// Every caller reaches here straight after an <c>Interlocked</c> decrement, which is a full
    /// fence, so that decrement is globally visible before the counters below are re-read. That is
    /// what makes the handshake with <see cref="DisposeAsync()"/> — which publishes
    /// <see cref="_drainTcs"/> with <see cref="Interlocked.Exchange{T}"/> before re-reading the same
    /// counters — sound. A release store followed by an acquire load is not enough on either side:
    /// store-load is the one reordering x64 still allows, and losing that race left the disposer
    /// waiting on a task nobody would ever complete.
    /// </summary>
    private void SignalDrainedIfIdle()
    {
        if (!IsIdle)
            return;
        Volatile.Read(ref _drainTcs)?.TrySetResult();
    }

    // ── shutdown ────────────────────────────────────────────────────────────

    /// <summary>
    /// Wait for the queue to drain, then refuse further work. Signal-based, no polling.
    ///
    /// A dispatcher must still be running (or the caller must be the actor's leader) for the queue
    /// to drain — disposing the dispatcher first leaves nothing to do the work, and this overload
    /// then waits forever. Use <see cref="DisposeAsync(TimeSpan)"/> or
    /// <see cref="DisposeAsync(CancellationToken)"/> when the caller cannot make that guarantee.
    /// </summary>
    public virtual async ValueTask DisposeAsync() =>
        await DrainThenCompleteAsync(Timeout.InfiniteTimeSpan, CancellationToken.None).ConfigureAwait(false);

    /// <summary>
    /// <see cref="DisposeAsync()"/> with an upper bound on the wait. Nothing can finish an actor's
    /// queued work once its workers are gone, so a caller disposing actors during or after shutdown
    /// needs a way to stop waiting.
    /// </summary>
    /// <param name="timeout">
    /// How long to wait for the drain. <see cref="Timeout.InfiniteTimeSpan"/> waits forever.
    /// </param>
    /// <returns><c>true</c> if the actor drained; <c>false</c> if the timeout expired first.</returns>
    /// <remarks>
    /// The actor stops accepting work either way. This overload does not route through an override
    /// of <see cref="DisposeAsync()"/>.
    /// </remarks>
    public ValueTask<bool> DisposeAsync(TimeSpan timeout) =>
        DrainThenCompleteAsync(timeout, CancellationToken.None);

    /// <summary>
    /// <see cref="DisposeAsync()"/> that gives up when <paramref name="cancellationToken"/> fires
    /// rather than throwing. See <see cref="DisposeAsync(TimeSpan)"/>.
    /// </summary>
    /// <returns><c>true</c> if the actor drained; <c>false</c> if it was cancelled first.</returns>
    public ValueTask<bool> DisposeAsync(CancellationToken cancellationToken) =>
        DrainThenCompleteAsync(Timeout.InfiniteTimeSpan, cancellationToken);

    private async ValueTask<bool> DrainThenCompleteAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var drained = true;

        if (!IsIdle)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            // Interlocked, not a plain or volatile store: this is one half of a Dekker handshake
            // with SignalDrainedIfIdle, and a release store does not order against the loads that
            // follow. Without the full fence the store can still sit in this core's store buffer
            // while the thread finishing the last job reads a null _drainTcs, signals nobody, and
            // leaves this await pending for the life of the process.
            Interlocked.Exchange(ref _drainTcs, tcs);

            if (!IsIdle)
                drained = await AwaitDrainAsync(tcs.Task, timeout, cancellationToken).ConfigureAwait(false);
        }

        Volatile.Write(ref _completed, 1);
        GC.SuppressFinalize(this);
        return drained;
    }

    private static async Task<bool> AwaitDrainAsync(Task drain, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout == Timeout.InfiniteTimeSpan && !cancellationToken.CanBeCanceled)
        {
            await drain.ConfigureAwait(false);
            return true;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout != Timeout.InfiniteTimeSpan)
            cts.CancelAfter(timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout);

        var abandoned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cts.Token.Register(static s => ((TaskCompletionSource)s!).TrySetResult(), abandoned))
        {
            return ReferenceEquals(await Task.WhenAny(drain, abandoned.Task).ConfigureAwait(false), drain);
        }
    }

    // ── process-wide compatibility surface ──────────────────────────────────

    /// <summary>
    /// Process-wide fallback for unhandled job errors. Prefer overriding <see cref="OnJobError"/>,
    /// which lets one misbehaving actor be handled without touching the rest.
    /// </summary>
    public static Action<Exception>? OnError
    {
        get => _globalOnError;
        set => _globalOnError = value;
    }

    internal static void RaiseGlobalError(Exception ex)
    {
        try { _globalOnError?.Invoke(ex); }
        catch { /* a failing error handler must not take the worker down */ }
    }

    /// <summary>
    /// Shutdown gate for <see cref="JobSystem.Default"/>.
    /// Prefer <c>system.AcceptingWork</c> or <see cref="JobSystem.StopAsync"/> when the process
    /// hosts more than one system.
    /// </summary>
    [Obsolete("Use JobSystem.Default.AcceptingWork, or system.AcceptingWork for a specific system. Removed in v1.0.")]
    public static bool AcceptingWork
    {
        get => JobSystem.Default.AcceptingWork;
        set => JobSystem.Default.AcceptingWork = value;
    }

    private sealed class CancelledTimer : ITimerHandle
    {
        public static readonly CancelledTimer Instance = new();
        public bool Cancel() => false;
        public bool IsPending => false;
    }

    /// <summary>
    /// Routes <c>await</c> continuations of an <see cref="AsyncReentrancy.Interleaved"/> async job
    /// back onto the owning actor's queue.
    /// </summary>
    private sealed class ActorSynchronizationContext(AsyncExecutable actor) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => actor.AdmitContinuation(d, state);

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (ReferenceEquals(ThreadContext.CurrentExecuter, actor))
            {
                d(state);
                return;
            }
            throw new InvalidOperationException(
                $"Synchronous Send onto actor '{actor.Name}' from another thread would deadlock.");
        }

        public override SynchronizationContext CreateCopy() => this;
    }
}
