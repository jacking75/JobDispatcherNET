using System.Collections.Concurrent;
using System.Diagnostics;

namespace JobDispatcherNET;

/// <summary>
/// Options for a <see cref="JobSystem"/>.
/// </summary>
public sealed record JobSystemOptions
{
    /// <summary>Default options.</summary>
    public static readonly JobSystemOptions Default = new();

    /// <summary>Name used in thread names, log lines and diagnostics.</summary>
    public string Name { get; init; } = "default";

    /// <summary>How precisely the timer thread hits due times. See <see cref="TimerPrecision"/>.</summary>
    public TimerPrecision TimerPrecision { get; init; } = TimerPrecision.Coarse;

    /// <summary>With <see cref="TimerPrecision.High"/>, start spinning this many ms before due.</summary>
    public int TimerSpinThresholdMs { get; init; } = 16;

    /// <summary>
    /// Shortest period <see cref="AsyncExecutable.DoAsyncEvery"/> will accept. Default 1 ms;
    /// <see cref="TimeSpan.Zero"/> disables the check (any positive period is then allowed).
    ///
    /// A server that derives tick periods from client input — a skill cooldown, a configurable
    /// poll interval — would otherwise accept a one-tick period, re-arm the timer every
    /// millisecond and, under <see cref="TimerPrecision.High"/>, spin the timer thread at 100%.
    /// </summary>
    public TimeSpan MinTimerPeriod { get; init; } = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// Windows only, opt-in: raise the global system timer resolution to 1 ms for the lifetime of
    /// the timer thread. Process-wide and increases power draw — enable only if measurements show
    /// the default ~15.6 ms resolution is too coarse for your tick rate.
    /// </summary>
    public bool RaiseSystemTimerResolution { get; init; }

    /// <summary>Record job-duration and timer-lag histograms. Costs a timestamp read per job.</summary>
    public bool EnableDetailedMetrics { get; init; }

    /// <summary>Publish counters through <see cref="System.Diagnostics.Metrics"/>. Default true.</summary>
    public bool PublishMeter { get; init; } = true;

    /// <summary>
    /// Throw when actor code sets up a guaranteed deadlock: blocking a worker thread while waiting
    /// on another actor's result, or an <see cref="AsyncReentrancy.Exclusive"/> actor asking itself
    /// for something from inside one of its own jobs. Defaults to true in DEBUG builds.
    /// </summary>
    public bool DetectBlockingWaitOnWorker { get; init; } = IsDebugBuild;

    /// <summary>Log a warning when a single job runs longer than this. <see cref="TimeSpan.Zero"/> disables.</summary>
    public TimeSpan MaxJobDuration { get; init; } = TimeSpan.Zero;

    /// <summary>Logger for this system. <c>null</c> falls back to <see cref="JobLog.Current"/>.</summary>
    public IJobLogger? Logger { get; init; }

    /// <summary>
    /// Bound applied to actors on this system that do not set <see cref="JobOptions.MaxQueueSize"/>
    /// themselves. <c>null</c> (the default) keeps the historical unbounded behaviour.
    ///
    /// An unbounded actor queue is an OOM vector, and the per-actor setting only helps on actors
    /// somebody remembered to configure. Setting this puts a ceiling under every actor at once;
    /// an actor that names its own <see cref="JobOptions.MaxQueueSize"/> still wins.
    /// </summary>
    public int? DefaultMaxQueueSize { get; init; }

    private static bool IsDebugBuild
    {
        get
        {
#if DEBUG
            return true;
#else
            return false;
#endif
        }
    }
}

/// <summary>
/// Owns everything a set of actors needs: worker threads, the ready queue that feeds them,
/// the timer thread, metrics and the shutdown gate.
///
/// Most processes need exactly one and can use <see cref="Default"/> implicitly — every
/// <see cref="AsyncExecutable"/> and <see cref="JobDispatcher"/> attaches to it unless told
/// otherwise. Create explicit instances when a process needs isolated pools (a game world and
/// a background-IO pool, say), or in tests so cases can run in parallel without sharing counters.
/// </summary>
public sealed class JobSystem : IDisposable, IAsyncDisposable
{
    private static JobSystem? _default;
    private static readonly object DefaultLock = new();

    /// <summary>
    /// The implicit process-wide system. Created on first use.
    /// </summary>
    public static JobSystem Default
    {
        get
        {
            var existing = Volatile.Read(ref _default);
            if (existing is not null)
                return existing;

            lock (DefaultLock)
            {
                return _default ??= new JobSystem(JobSystemOptions.Default);
            }
        }
    }

    private readonly ConcurrentQueue<ReadyItem> _readyQueue = new();
    private readonly object _signal = new();
    private readonly StripedCounter _admitted = new();
    private readonly StripedCounter _retired = new();
    private readonly StripedCounter _asyncStarted = new();
    private readonly StripedCounter _asyncCompleted = new();
    private readonly StripedCounter _readyEnqueued = new();
    private readonly StripedCounter _readyHandled = new();
    private readonly List<IDisposable> _dispatchers = [];
    private readonly object _dispatcherLock = new();
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();

    private TimerService? _timers;
    private readonly object _timerLock = new();
    private readonly IJobLogger _logger;

    private int _waiters;
    private int _liveWorkers;
    private int _acceptingWork = 1;
    private int _disposed;
    private int _timerFallbackWarned;

    /// <summary>Create a system with the default options.</summary>
    public JobSystem() : this(JobSystemOptions.Default) { }

    /// <summary>Create a system with explicit options.</summary>
    public JobSystem(JobSystemOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
        _logger = options.Logger is null ? JobLog.Safe : new SafeJobLogger(options.Logger);
        Metrics = new JobMetrics(this, options.EnableDetailedMetrics, options.PublishMeter);
    }

    /// <summary>The options this system was created with.</summary>
    public JobSystemOptions Options { get; }

    /// <summary>Counters for this system.</summary>
    public JobMetrics Metrics { get; }

    /// <summary>Name from <see cref="JobSystemOptions.Name"/>.</summary>
    public string Name => Options.Name;

    /// <summary>
    /// Logger for this system, wrapped so that a throwing implementation cannot kill the thread
    /// that was logging. The library logs from the timer thread and from workers, neither of which
    /// has anything to catch an escaping exception. Read <see cref="JobSystemOptions.Logger"/> for
    /// the instance that was configured.
    /// </summary>
    public IJobLogger Logger => _logger;

    /// <summary>
    /// Shutdown gate. Setting this to <c>false</c> makes every actor on this system refuse new
    /// jobs with <see cref="DropReason.ShuttingDown"/>. <see cref="StopAsync"/> manages it for you.
    /// </summary>
    public bool AcceptingWork
    {
        get => Volatile.Read(ref _acceptingWork) != 0;
        set => Volatile.Write(ref _acceptingWork, value ? 1 : 0);
    }

    /// <summary>Worker threads currently alive on this system.</summary>
    public int LiveWorkerCount => Volatile.Read(ref _liveWorkers);

    /// <summary>True when at least one worker thread is running.</summary>
    public bool HasWorkers => LiveWorkerCount > 0;

    /// <summary>
    /// Actors and posted actions waiting for a worker.
    ///
    /// Striped for the same reason <see cref="InFlightJobs"/> is: a plain shared counter put two
    /// read-modify-writes on one cache line into every ready item, which every worker and every
    /// producer then fought over. Read handled-then-enqueued so the depth can read high but never
    /// spuriously low — a drain gate must not be fooled into stopping early.
    /// </summary>
    public int ReadyQueueDepth
    {
        get
        {
            var handled = _readyHandled.Value;
            return (int)Math.Clamp(_readyEnqueued.Value - handled, 0, int.MaxValue);
        }
    }

    /// <summary>Cheap emptiness check for the worker spin, with none of the counter arithmetic.</summary>
    internal bool ReadyQueueIsEmpty => _readyQueue.IsEmpty;

    /// <summary>Timers scheduled and not yet fired.</summary>
    public long PendingTimerCount => Volatile.Read(ref _timers)?.PendingCount ?? 0;

    /// <summary>
    /// Jobs admitted to an actor queue and not yet retired.
    ///
    /// The two striped counters are summed one after the other, so the result is never exact.
    /// Reading <c>retired</c> first makes the error conservative — the count can read high but
    /// never spuriously low, so a drain gate cannot be fooled into stopping early.
    /// </summary>
    public long InFlightJobs
    {
        get
        {
            var retired = _retired.Value;
            return Math.Max(0, _admitted.Value - retired);
        }
    }

    /// <summary>
    /// Async jobs that have started and are parked on an <c>await</c>.
    ///
    /// Under <see cref="AsyncReentrancy.Interleaved"/> an awaiting job releases its queue slot, so
    /// it appears in neither <see cref="InFlightJobs"/> nor <see cref="ReadyQueueDepth"/> nor
    /// <see cref="PendingTimerCount"/>. <see cref="DrainAsync"/> waits on this as well, otherwise a
    /// shutdown could stop the workers while a continuation was still on its way back.
    ///
    /// Read in the same order as <see cref="InFlightJobs"/> — completed first — so the count can
    /// read high but never spuriously low.
    /// </summary>
    public long PendingAsyncJobs
    {
        get
        {
            var completed = _asyncCompleted.Value;
            return Math.Max(0, _asyncStarted.Value - completed);
        }
    }

    /// <summary>Monotonic milliseconds since this system was created.</summary>
    public long CurrentTick => (long)Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;

    internal TimerService Timers
    {
        get
        {
            var existing = Volatile.Read(ref _timers);
            if (existing is not null)
                return existing;

            lock (_timerLock)
            {
                return _timers ??= new TimerService(this, Options.TimerPrecision, Options.TimerSpinThresholdMs);
            }
        }
    }

    // ── work admission accounting ───────────────────────────────────────────

    internal void OnJobAdmitted() => _admitted.Increment();

    internal void OnJobRetired() => _retired.Increment();

    internal void OnAsyncJobStarted() => _asyncStarted.Increment();

    internal void OnAsyncJobCompleted() => _asyncCompleted.Increment();

    // ── ready queue ─────────────────────────────────────────────────────────

    /// <summary>
    /// Run an action on a worker thread. This is the supported way to hand work from a network
    /// or thread-pool thread into the worker pool without becoming an actor's leader yourself.
    /// </summary>
    /// <returns>
    /// <c>false</c> when the system has stopped accepting work or been disposed, in which case
    /// nothing was queued. Posting is not gated on workers existing — they may start later — but it
    /// is gated on the shutdown door, because work posted past it piles up on a queue with nothing
    /// left to drain it.
    /// </returns>
    public bool Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (!AcceptingWork || Volatile.Read(ref _disposed) != 0)
            return false;

        Enqueue(new ReadyItem(null, action));
        return true;
    }

    internal void Schedule(AsyncExecutable actor) => Enqueue(new ReadyItem(actor, null));

    private void Enqueue(ReadyItem item)
    {
        // Count the item BEFORE it is visible on the queue, and only stop counting it AFTER it has
        // run (see DrainReady). The depth then always over-estimates rather than under-estimates,
        // which is what a shutdown drain needs: incrementing after the enqueue left a window where
        // DrainAsync saw an empty system and stopped the workers out from under queued work.
        _readyEnqueued.Increment();
        _readyQueue.Enqueue(item);
        SignalWork();
    }

    /// <summary>
    /// Drain up to <paramref name="maxItems"/> ready items. Called by worker threads.
    /// Returns the number of items handled.
    /// </summary>
    internal int DrainReady(int maxItems)
    {
        var handled = 0;
        while (handled < maxItems && _readyQueue.TryDequeue(out var item))
        {
            handled++;
            try
            {
                if (item.Actor is { } actor)
                    actor.FlushAsLeader();
                else
                    item.Action?.Invoke();
            }
            catch (Exception ex)
            {
                Logger.Error("Ready-queue item failed", ex);
                AsyncExecutable.RaiseGlobalError(ex);
            }
            finally
            {
                _readyHandled.Increment();
            }
        }
        return handled;
    }

    /// <summary>
    /// Wake one idle worker. Used when a single item arrives, where one waiter is exactly the
    /// number needed.
    /// </summary>
    internal void SignalWork()
    {
        if (Volatile.Read(ref _waiters) == 0)
            return;
        lock (_signal)
        {
            Monitor.Pulse(_signal);
        }
    }

    /// <summary>
    /// Wake every idle worker. Used where the point is "everybody re-check your exit condition" —
    /// shutdown and the drain loop — and where waking the wrong single waiter costs a whole idle
    /// timeout. Unconditional: the caller cannot afford to skip on a stale <c>_waiters</c> read.
    /// </summary>
    internal void SignalAllWork()
    {
        lock (_signal)
        {
            Monitor.PulseAll(_signal);
        }
    }

    /// <summary>
    /// Block until work arrives or <paramref name="timeoutMs"/> elapses.
    /// <see cref="_waiters"/> is incremented before the queue is inspected so a producer can
    /// never observe "no waiters" and skip the pulse while a worker is on its way into the wait.
    /// </summary>
    internal void WaitForWork(int timeoutMs)
    {
        Interlocked.Increment(ref _waiters);
        try
        {
            lock (_signal)
            {
                if (!_readyQueue.IsEmpty)
                    return;
                Monitor.Wait(_signal, timeoutMs);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _waiters);
        }
    }

    // ── timers ──────────────────────────────────────────────────────────────

    /// <summary>Hand a fired timer's job to its actor. False when the actor refused it.</summary>
    internal bool DispatchTimerJob(AsyncExecutable owner, JobEntry job, out DropReason reason) =>
        owner.DoTaskFromTimer(job, out reason);

    internal void WarnTimerFallbackOnce()
    {
        if (Interlocked.Exchange(ref _timerFallbackWarned, 1) != 0)
            return;
        Logger.Warn(
            $"JobSystem '{Name}' has no worker threads, so timer callbacks run on the timer thread. " +
            "Start a JobDispatcher to move them onto dedicated workers.");
    }

    // ── worker registration ─────────────────────────────────────────────────

    internal void RegisterWorker()
    {
        if (Interlocked.Increment(ref _liveWorkers) != 1)
            return;

        // Arm the timer-fallback warning again. It is a once-per-process latch, and a restart
        // backoff that briefly empties the pool used to spend it: a system genuinely deployed
        // without a dispatcher afterwards would then never say so.
        Volatile.Write(ref _timerFallbackWarned, 0);
    }

    internal void UnregisterWorker()
    {
        if (Interlocked.Decrement(ref _liveWorkers) != 0)
            return;

        // Actors already on the ready queue are the ones with no way out: their counters say a
        // leader exists, so further posts only pile up behind them and their DisposeAsync never
        // returns. Meanwhile brand-new actors run inline and the system looks healthy, which makes
        // this exactly the kind of partial failure nobody notices. Say it out loud.
        var stranded = ReadyQueueDepth;
        if (stranded > 0)
        {
            Logger.Error(
                $"JobSystem '{Name}' has no worker threads left and {stranded} ready item(s) have nobody " +
                "to run them. Actors already queued there are stranded — new posts will queue behind them " +
                "and their DisposeAsync will not complete.");
        }
    }

    internal void AttachDispatcher(IDisposable dispatcher)
    {
        lock (_dispatcherLock)
        {
            _dispatchers.Add(dispatcher);
        }
    }

    internal void DetachDispatcher(IDisposable dispatcher)
    {
        lock (_dispatcherLock)
        {
            _dispatchers.Remove(dispatcher);
        }
    }

    // ── shutdown ────────────────────────────────────────────────────────────

    /// <summary>
    /// Graceful shutdown: wait for every admitted job to finish, then stop timers and workers.
    ///
    /// New work is still accepted while draining, so a job that enqueues follow-up work (an actor
    /// telling its peers to despawn, say) completes normally. Call
    /// <c>AcceptingWork = false</c> yourself first if you also need to slam the door on external
    /// producers, or pass <paramref name="refuseNewWork"/>.
    /// </summary>
    /// <param name="drainTimeout">How long to wait for in-flight work before stopping anyway.</param>
    /// <param name="refuseNewWork">Close the gate before draining instead of after.</param>
    /// <returns>True if everything drained within the timeout.</returns>
    public async Task<bool> StopAsync(TimeSpan drainTimeout, bool refuseNewWork = false)
    {
        if (refuseNewWork)
            AcceptingWork = false;

        var drained = await DrainAsync(drainTimeout).ConfigureAwait(false);

        AcceptingWork = false;

        Volatile.Read(ref _timers)?.Dispose();

        IDisposable[] dispatchers;
        lock (_dispatcherLock)
        {
            dispatchers = _dispatchers.ToArray();
        }
        foreach (var dispatcher in dispatchers)
        {
            try
            {
                // Joining worker threads is a blocking operation, and there is no reason for an
                // async caller to hold its own thread for the whole budget while it happens.
                if (dispatcher is JobDispatcherBase pool)
                    await pool.TryStopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                else
                    dispatcher.Dispose();
            }
            catch (Exception ex) { Logger.Error("Dispatcher shutdown failed", ex); }
        }

        return drained;
    }

    /// <summary>
    /// Wait until no jobs are in flight and the ready queue is empty, or the timeout expires.
    /// </summary>
    public async Task<bool> DrainAsync(TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp();
        var limit = timeout <= TimeSpan.Zero ? TimeSpan.Zero : timeout;

        // If the caller is itself one of this system's async jobs — an admin command actor asking
        // the process to stop, say — then it is one of the jobs this loop is counting, and waiting
        // for it would burn the whole timeout and report a failed drain every single shutdown. Its
        // own job does not count against it; everything else still does.
        var self = AsyncExecutable.AsyncFlowOwner is { } owner && ReferenceEquals(owner.System, this) ? 1 : 0;

        if (self != 0 && Options.DetectBlockingWaitOnWorker)
        {
            Logger.Warn(
                $"JobSystem '{Name}' is being drained from inside one of its own async jobs. The drain " +
                "excludes that job, but shutdown is better started from outside the system (the host, a " +
                "signal handler, a console loop) — or with `_ = system.StopAsync(...)` so the job can return.");
        }

        while (InFlightJobs > 0 || ReadyQueueDepth > 0 || PendingTimerCount > 0 || PendingAsyncJobs > self)
        {
            if (Stopwatch.GetElapsedTime(deadline) >= limit)
            {
                Logger.Warn(
                    $"JobSystem '{Name}' drain timed out after {limit.TotalMilliseconds:F0}ms " +
                    $"(in-flight={InFlightJobs}, ready={ReadyQueueDepth}, timers={PendingTimerCount}, " +
                    $"async={PendingAsyncJobs})");
                return false;
            }

            // Pulse one waiter, and only when there is actually something to take. This used to be
            // SignalAllWork, which is right for the shutdown join but wrong here: a drain that is
            // blocked — an uncancelled repeating timer, a long drain timeout — then woke every idle
            // worker 500 times a second for as long as it stayed blocked.
            if (ReadyQueueDepth > 0)
                SignalWork();

            await Task.Delay(2).ConfigureAwait(false);
        }

        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        AcceptingWork = false;
        Volatile.Read(ref _timers)?.Dispose();

        IDisposable[] dispatchers;
        lock (_dispatcherLock)
        {
            dispatchers = _dispatchers.ToArray();
            _dispatchers.Clear();
        }
        foreach (var dispatcher in dispatchers)
        {
            try { dispatcher.Dispose(); }
            catch (Exception ex) { Logger.Error("Dispatcher shutdown failed", ex); }
        }

        Metrics.Dispose();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _disposed) == 0)
            await StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Dispose();
    }

    private readonly struct ReadyItem(AsyncExecutable? actor, Action? action)
    {
        public AsyncExecutable? Actor { get; } = actor;
        public Action? Action { get; } = action;
    }
}
