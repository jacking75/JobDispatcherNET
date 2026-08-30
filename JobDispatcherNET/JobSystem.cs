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
    /// Throw when actor code blocks a worker thread waiting on another actor's result — a
    /// guaranteed deadlock under the leader-flush model. Defaults to true in DEBUG builds.
    /// </summary>
    public bool DetectBlockingWaitOnWorker { get; init; } = IsDebugBuild;

    /// <summary>Log a warning when a single job runs longer than this. <see cref="TimeSpan.Zero"/> disables.</summary>
    public TimeSpan MaxJobDuration { get; init; } = TimeSpan.Zero;

    /// <summary>Logger for this system. <c>null</c> falls back to <see cref="JobLog.Current"/>.</summary>
    public IJobLogger? Logger { get; init; }

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
    private readonly List<IDisposable> _dispatchers = [];
    private readonly object _dispatcherLock = new();
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();

    private TimerService? _timers;
    private readonly object _timerLock = new();

    private int _readyDepth;
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
        Metrics = new JobMetrics(this, options.EnableDetailedMetrics, options.PublishMeter);
    }

    /// <summary>The options this system was created with.</summary>
    public JobSystemOptions Options { get; }

    /// <summary>Counters for this system.</summary>
    public JobMetrics Metrics { get; }

    /// <summary>Name from <see cref="JobSystemOptions.Name"/>.</summary>
    public string Name => Options.Name;

    /// <summary>Logger for this system.</summary>
    public IJobLogger Logger => Options.Logger ?? JobLog.Current;

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

    /// <summary>Actors and posted actions waiting for a worker.</summary>
    public int ReadyQueueDepth => Volatile.Read(ref _readyDepth);

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

    // ── ready queue ─────────────────────────────────────────────────────────

    /// <summary>
    /// Run an action on a worker thread. This is the supported way to hand work from a network
    /// or thread-pool thread into the worker pool without becoming an actor's leader yourself.
    /// </summary>
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Enqueue(new ReadyItem(null, action));
    }

    internal void Schedule(AsyncExecutable actor) => Enqueue(new ReadyItem(actor, null));

    private void Enqueue(ReadyItem item)
    {
        // Count the item BEFORE it is visible on the queue, and only stop counting it AFTER it has
        // run (see DrainReady). The depth then always over-estimates rather than under-estimates,
        // which is what a shutdown drain needs: incrementing after the enqueue left a window where
        // DrainAsync saw an empty system and stopped the workers out from under queued work.
        Interlocked.Increment(ref _readyDepth);
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
                Interlocked.Decrement(ref _readyDepth);
            }
        }
        return handled;
    }

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

    internal void DispatchTimerJob(AsyncExecutable owner, JobEntry job) =>
        owner.DoTaskFromTimer(job);

    internal void WarnTimerFallbackOnce()
    {
        if (Interlocked.Exchange(ref _timerFallbackWarned, 1) != 0)
            return;
        Logger.Warn(
            $"JobSystem '{Name}' has no worker threads, so timer callbacks run on the timer thread. " +
            "Start a JobDispatcher to move them onto dedicated workers.");
    }

    // ── worker registration ─────────────────────────────────────────────────

    internal void RegisterWorker() => Interlocked.Increment(ref _liveWorkers);

    internal void UnregisterWorker() => Interlocked.Decrement(ref _liveWorkers);

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
            try { dispatcher.Dispose(); }
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

        while (InFlightJobs > 0 || ReadyQueueDepth > 0 || PendingTimerCount > 0)
        {
            if (Stopwatch.GetElapsedTime(deadline) >= limit)
            {
                Logger.Warn(
                    $"JobSystem '{Name}' drain timed out after {limit.TotalMilliseconds:F0}ms " +
                    $"(in-flight={InFlightJobs}, ready={ReadyQueueDepth}, timers={PendingTimerCount})");
                return false;
            }

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
