using System.Diagnostics;

namespace JobDispatcherNET;

/// <summary>
/// Worker-pool options.
/// </summary>
public sealed record JobDispatcherOptions
{
    /// <summary>Default options.</summary>
    public static readonly JobDispatcherOptions Default = new();

    /// <summary>Restart a worker that died from an unhandled exception. Default true.</summary>
    public bool RestartFailedWorkers { get; init; } = true;

    /// <summary>Restarts allowed per worker slot before it is left down. Default 5.</summary>
    public int MaxRestartsPerWorker { get; init; } = 5;

    /// <summary>Base delay between restarts; doubles with each attempt. Default 1s.</summary>
    public TimeSpan RestartBackoff { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Ceiling for the doubling in <see cref="RestartBackoff"/>. Default 1 minute.
    ///
    /// Not cosmetic: unbounded doubling overflows long before a generous
    /// <see cref="MaxRestartsPerWorker"/> runs out — <c>Thread.Sleep(TimeSpan)</c> rejects anything
    /// past ~24.8 days — and that throw would happen on the worker's own thread with nothing left
    /// to catch it, taking the process down. Values at or below <see cref="TimeSpan.Zero"/> mean
    /// "never back off further than <see cref="RestartBackoff"/>".
    /// </summary>
    public TimeSpan MaxRestartBackoff { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// If a worker slot stays up this long after a restart, its restart budget is refilled.
    /// Without this a server that hiccups five times over months is permanently down a worker.
    /// Default 5 minutes; <see cref="TimeSpan.Zero"/> disables the refill.
    /// </summary>
    public TimeSpan RestartCountResetAfter { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Ready-queue items a worker handles per iteration. Default 256.</summary>
    public int MaxReadyDrainPerTick { get; init; } = 256;

    /// <inheritdoc cref="MaxReadyDrainPerTick" />
    [Obsolete("Renamed to MaxReadyDrainPerTick; timer dispatch now flows through the shared ready queue. Removed in v1.0.")]
    public int MaxTimerDrainPerTick
    {
        get => MaxReadyDrainPerTick;
        init => MaxReadyDrainPerTick = value;
    }

    /// <summary>How long an idle worker blocks before re-checking. Only used by the non-generic dispatcher.</summary>
    public int IdleWaitMs { get; init; } = 20;

    /// <summary>
    /// Times a worker spins looking for work before parking on the monitor. Default 10;
    /// <c>0</c> parks immediately.
    ///
    /// A parked worker registers itself as a waiter, and from that moment every producer's enqueue
    /// takes the signal lock and pulses — and the worker it wakes pays a context switch. With short
    /// jobs a pool empties its queue constantly, so under load the workers park and unpark
    /// continuously and the cost grows with the pool size. A few microseconds of spinning keeps the
    /// waiter count at zero while work is actually flowing, and producers skip the lock entirely.
    /// Idle costs nothing extra: the spin runs once and then the worker parks as before.
    /// </summary>
    public int SpinBeforeParkIterations { get; init; } = 10;

    /// <summary>Priority for worker threads. Default <see cref="ThreadPriority.Normal"/>.</summary>
    public ThreadPriority ThreadPriority { get; init; } = ThreadPriority.Normal;

    /// <summary>Worker threads run as background threads (do not keep the process alive). Default true.</summary>
    public bool BackgroundThreads { get; init; } = true;

    /// <summary>Stack size in bytes for worker threads. 0 uses the platform default.</summary>
    public int MaxStackSize { get; init; }

    /// <summary>System the workers serve. <c>null</c> uses <see cref="JobSystem.Default"/>.</summary>
    public JobSystem? System { get; init; }
}

/// <summary>
/// Shared worker-thread lifecycle: start, supervise, restart, stop.
/// </summary>
public abstract class JobDispatcherBase : IDisposable, IAsyncDisposable
{
    private readonly Thread[] _threads;
    private readonly int[] _restartCounts;
    private readonly long[] _lastStartTimestamps;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// Serialises starting a worker against stopping the pool. Without it a supervisor restart
    /// could slip in after <see cref="TryStop"/> had already scanned <see cref="_threads"/>, so
    /// shutdown reported success while a fresh worker was coming up — and then disposed the
    /// cancellation source that worker was about to read.
    /// </summary>
    private readonly object _lifecycleLock = new();
    private TaskCompletionSource? _allWorkersDone;
    private int _completedWorkers;
    private int _disposed;
    private int _started;

    /// <summary>Create a dispatcher.</summary>
    /// <param name="workerCount">Number of worker threads. Must be at least 1.</param>
    /// <param name="options">Worker options. <c>null</c> uses <see cref="JobDispatcherOptions.Default"/>.</param>
    protected JobDispatcherBase(int workerCount, JobDispatcherOptions? options)
    {
        if (workerCount < 1)
            throw new ArgumentOutOfRangeException(nameof(workerCount), "must be >= 1");

        Options = options ?? JobDispatcherOptions.Default;
        System = Options.System ?? JobSystem.Default;
        WorkerCount = workerCount;
        _threads = new Thread[workerCount];
        _restartCounts = new int[workerCount];
        _lastStartTimestamps = new long[workerCount];
        System.AttachDispatcher(this);
    }

    /// <summary>Options this dispatcher was created with.</summary>
    public JobDispatcherOptions Options { get; }

    /// <summary>The system these workers serve.</summary>
    public JobSystem System { get; }

    /// <summary>Configured number of worker threads.</summary>
    public int WorkerCount { get; }

    /// <summary>Cancellation token signalled when the dispatcher stops.</summary>
    protected CancellationToken StoppingToken => _cts.Token;

    /// <summary>Worker threads currently alive.</summary>
    public int LiveWorkerCount
    {
        get
        {
            var alive = 0;
            foreach (var t in _threads)
                if (t is { IsAlive: true }) alive++;
            return alive;
        }
    }

    /// <summary>
    /// Start the worker threads. The returned task completes when every worker has stopped.
    /// Calling this more than once throws.
    /// </summary>
    public Task RunWorkerThreadsAsync()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("RunWorkerThreadsAsync has already been called on this dispatcher.");

        _allWorkersDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_lifecycleLock)
        {
            // Inside the lock, with the same reasoning as the re-check in TryRestart: a check
            // outside it could be overtaken by a concurrent TryStop, and the threads we then
            // started would read a cancellation source TryStop had already disposed. That surfaced
            // as an ObjectDisposedException logged as a bogus "worker crashed".
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            for (var slot = 0; slot < WorkerCount; slot++)
                StartWorkerOnSlot(slot, isRestart: false);
        }

        return _allWorkersDone.Task;
    }

    private void StartWorkerOnSlot(int slot, bool isRestart)
    {
        var name = isRestart
            ? $"JobWorker-{System.Name}-{slot}-r{_restartCounts[slot]}"
            : $"JobWorker-{System.Name}-{slot}";

        var thread = Options.MaxStackSize > 0
            ? new Thread(() => RunWorker(slot), Options.MaxStackSize)
            : new Thread(() => RunWorker(slot));

        thread.IsBackground = Options.BackgroundThreads;
        thread.Name = name;
        thread.Priority = Options.ThreadPriority;

        _threads[slot] = thread;
        Interlocked.Exchange(ref _lastStartTimestamps[slot], Stopwatch.GetTimestamp());
        thread.Start();
    }

    private void RunWorker(int slot)
    {
        var exitedNormally = false;

        ThreadContext.IsWorkerThread = true;
        ThreadContext.CurrentSystem = System;
        System.RegisterWorker();

        try
        {
            WorkerLoop(slot, _cts.Token);
            exitedNormally = true;
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            exitedNormally = true;
        }
        catch (Exception ex)
        {
            // Note the filter above: an OperationCanceledException with no stop in progress is a
            // crash like any other — a Task.Wait inside IRunnable.Run being cancelled, say. Treating
            // every OCE as a clean exit made the worker slot vanish with neither a log line nor a
            // restart.
            var running = ThreadContext.CurrentExecuter;
            var where = running is null ? string.Empty : $" while running actor '{running.Name}'";
            System.Logger.Error($"Worker slot #{slot} crashed{where}", ex);
            AsyncExecutable.RaiseGlobalError(ex);
        }
        finally
        {
            ThreadContext.CurrentExecuter = null;
            ThreadContext.IsWorkerThread = false;
            ThreadContext.CurrentSystem = null;
            System.UnregisterWorker();
        }

        if (!exitedNormally
            && Options.RestartFailedWorkers
            && Volatile.Read(ref _disposed) == 0
            && !_cts.IsCancellationRequested)
        {
            // Guarded because this runs *outside* the try/catch above, on a dedicated thread: an
            // exception escaping here is an unhandled exception on a thread nobody owns, which ends
            // the process. Losing one worker slot is not worth that.
            var restarted = false;
            try
            {
                restarted = TryRestart(slot);
            }
            catch (Exception ex)
            {
                System.Logger.Error($"Worker slot #{slot} could not be restarted; slot is down", ex);
            }

            if (restarted)
                return;
        }

        if (Interlocked.Increment(ref _completedWorkers) == WorkerCount)
            _allWorkersDone?.TrySetResult();
    }

    private bool TryRestart(int slot)
    {
        // A slot that has been healthy for a while gets its budget back.
        if (Options.RestartCountResetAfter > TimeSpan.Zero
            && Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastStartTimestamps[slot])) >= Options.RestartCountResetAfter)
        {
            Interlocked.Exchange(ref _restartCounts[slot], 0);
        }

        var attempts = Interlocked.Increment(ref _restartCounts[slot]);
        if (attempts > Options.MaxRestartsPerWorker)
        {
            System.Logger.Error(
                $"Worker slot #{slot} exceeded max restarts ({Options.MaxRestartsPerWorker}) — permanently down");
            return false;
        }

        System.Metrics.OnWorkerRestart();
        var backoff = RestartBackoffFor(attempts);
        System.Logger.Warn(
            $"Restarting worker slot #{slot} (attempt {attempts}/{Options.MaxRestartsPerWorker}) after {backoff.TotalMilliseconds:F0}ms");

        // Waiting on the token rather than sleeping: this thread is still the one TryStop will try
        // to join for this slot, so a plain sleep through a long backoff would make shutdown report
        // a straggler that is only waiting to be replaced.
        if (backoff > TimeSpan.Zero && _cts.Token.WaitHandle.WaitOne(backoff))
            return false;

        // Re-check and start under the lifecycle lock so the decision cannot be overtaken by a
        // TryStop that has already passed its own scan of the thread array.
        lock (_lifecycleLock)
        {
            if (Volatile.Read(ref _disposed) != 0 || _cts.IsCancellationRequested)
                return false;

            StartWorkerOnSlot(slot, isRestart: true);
            return true;
        }
    }

    /// <summary>
    /// Exponential backoff, clamped to <see cref="JobDispatcherOptions.MaxRestartBackoff"/> and to
    /// what <c>WaitHandle.WaitOne</c> accepts. Both clamps matter: the doubling reaches infinity by
    /// attempt 1025 and overflows a sleep long before that, and this runs where a throw would kill
    /// the process rather than a worker.
    /// </summary>
    private TimeSpan RestartBackoffFor(int attempts)
    {
        var baseMs = Options.RestartBackoff.TotalMilliseconds;
        if (baseMs <= 0 || double.IsNaN(baseMs))
            return TimeSpan.Zero;

        var capMs = Options.MaxRestartBackoff > TimeSpan.Zero
            ? Options.MaxRestartBackoff.TotalMilliseconds
            : baseMs;
        capMs = Math.Min(capMs, int.MaxValue);

        // 62 is already far past any cap a caller could set; it only keeps Math.Pow finite.
        var scaled = baseMs * Math.Pow(2, Math.Min(attempts - 1, 62));
        return TimeSpan.FromMilliseconds(Math.Clamp(scaled, 0, capMs));
    }

    /// <summary>The body of one worker thread. Runs until the token is cancelled.</summary>
    protected abstract void WorkerLoop(int slot, CancellationToken cancellationToken);

    /// <summary>
    /// Do one pass over the system ready queue. Returns the number of items handled.
    /// Worker loops should call this before their own work so timers and scheduled actors
    /// are not starved.
    /// </summary>
    protected int PumpReadyQueue()
    {
        ThreadContext.TickCount = System.CurrentTick;
        return System.DrainReady(Options.MaxReadyDrainPerTick);
    }

    /// <summary>
    /// Stop the workers and wait for them to exit.
    ///
    /// <para>Blocks the calling thread. Prefer <see cref="TryStopAsync"/> from async code, and note
    /// that calling this from inside a job — a job that disposes its own system — is allowed but
    /// cannot wait for the worker it is running on.</para>
    /// </summary>
    /// <param name="joinTimeout">
    /// Total budget for joining the workers, not a budget per thread. Spending the full timeout on
    /// each of N threads in turn meant a pool with one stuck worker took N × timeout to give up.
    /// </param>
    /// <returns><c>true</c> if every worker that could be waited for exited within the timeout.</returns>
    public bool TryStop(TimeSpan joinTimeout)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return true;

        System.DetachDispatcher(this);

        Thread[] snapshot;
        lock (_lifecycleLock)
        {
            // Cancelling inside the lock means any restart still deciding either lands before this
            // (and is therefore in the snapshot) or sees the cancellation and gives up.
            _cts.Cancel();
            snapshot = (Thread[])_threads.Clone();
        }

        // PulseAll, not Pulse: a single pulse wakes one waiter, which on a pool of idle workers is
        // very likely not the one being joined right now. They would each still time out of their
        // own idle wait, but only after IdleWaitMs, and shutdown would crawl.
        System.SignalAllWork();

        var started = Stopwatch.GetTimestamp();
        var allStopped = true;
        var joinedSelf = false;

        foreach (var thread in snapshot)
        {
            if (thread is not { IsAlive: true })
                continue;

            if (ReferenceEquals(thread, Thread.CurrentThread))
            {
                // A job running on this very worker asked for the stop. Joining ourselves can only
                // time out, so it used to log a straggler on every such shutdown. The thread leaves
                // its loop as soon as this job returns.
                joinedSelf = true;
                continue;
            }

            System.SignalAllWork();

            var remaining = joinTimeout - Stopwatch.GetElapsedTime(started);
            if (remaining < TimeSpan.Zero)
                remaining = TimeSpan.Zero;

            if (thread.Join(remaining))
                continue;

            allStopped = false;
            System.Logger.Error(
                $"Worker thread '{thread.Name}' did not stop within the {joinTimeout.TotalMilliseconds:F0}ms " +
                "shutdown budget. A job is probably blocking (a lock, a synchronous wait, or an infinite loop).");
        }

        // Only safe to dispose once every worker has genuinely left; a straggler still reads the
        // token, and an ObjectDisposedException there surfaces as a bogus "worker crashed" log.
        // That includes the caller's own worker thread when it stopped its own pool.
        if (allStopped && !joinedSelf)
            _cts.Dispose();

        return allStopped;
    }

    /// <summary>
    /// <see cref="TryStop"/> without blocking the caller. The joins still happen on a thread, just
    /// not this one — which matters inside <c>StopAsync</c>, where the alternative was holding an
    /// async caller for the whole shutdown budget.
    /// </summary>
    /// <param name="joinTimeout">Total budget for joining the workers.</param>
    /// <returns><c>true</c> if every worker that could be waited for exited within the timeout.</returns>
    public Task<bool> TryStopAsync(TimeSpan joinTimeout) => Task.Run(() => TryStop(joinTimeout));

    /// <inheritdoc />
    public void Dispose() => TryStop(TimeSpan.FromSeconds(5));

    /// <inheritdoc />
    public async ValueTask DisposeAsync() =>
        await TryStopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
}

/// <summary>
/// Worker pool with no user loop: every worker blocks until the job system has work, runs it, and
/// blocks again. No polling, no <c>Thread.Sleep(1)</c>, and idle workers cost nothing.
///
/// This is the right dispatcher for a server whose work all arrives as actor jobs, timer firings
/// and <see cref="JobSystem.Post(Action)"/> calls. Use <see cref="JobDispatcher{T}"/> instead when
/// workers need their own per-iteration loop.
/// </summary>
public sealed class JobDispatcher : JobDispatcherBase
{
    /// <summary>Create a pool of <paramref name="workerCount"/> workers on <see cref="JobSystem.Default"/>.</summary>
    public JobDispatcher(int workerCount) : this(workerCount, null) { }

    /// <summary>Create a pool with explicit options.</summary>
    public JobDispatcher(int workerCount, JobDispatcherOptions? options) : base(workerCount, options) { }

    /// <inheritdoc />
    protected override void WorkerLoop(int slot, CancellationToken cancellationToken)
    {
        var idleWait = Math.Max(1, Options.IdleWaitMs);
        var spins = Math.Max(0, Options.SpinBeforeParkIterations);

        while (!cancellationToken.IsCancellationRequested)
        {
            if (PumpReadyQueue() != 0)
                continue;

            if (SpinForWork(spins))
                continue;

            System.WaitForWork(idleWait);
        }
    }

    /// <summary>
    /// Look for work for a few microseconds before parking. See
    /// <see cref="JobDispatcherOptions.SpinBeforeParkIterations"/> for why this is worth doing.
    /// </summary>
    private bool SpinForWork(int iterations)
    {
        if (iterations == 0)
            return false;

        var spinner = new SpinWait();
        for (var i = 0; i < iterations; i++)
        {
            // sleep1Threshold: -1 keeps this out of Thread.Sleep(1), whose 15 ms granularity on
            // stock Windows is far longer than anything worth spinning for.
            spinner.SpinOnce(sleep1Threshold: -1);
            if (!System.ReadyQueueIsEmpty)
                return true;
        }

        return false;
    }
}

/// <summary>
/// Worker pool that runs a user-supplied <see cref="IRunnable"/> loop on each dedicated OS thread,
/// draining the job system's ready queue before every iteration.
///
/// Threads are real OS threads, not thread-pool threads, so per-thread state and long-running loops
/// are safe.
/// </summary>
public sealed class JobDispatcher<T> : JobDispatcherBase where T : IRunnable, new()
{
    /// <summary>Create a pool of <paramref name="workerCount"/> workers on <see cref="JobSystem.Default"/>.</summary>
    public JobDispatcher(int workerCount) : this(workerCount, null) { }

    /// <summary>Create a pool with explicit options.</summary>
    public JobDispatcher(int workerCount, JobDispatcherOptions? options) : base(workerCount, options) { }

    /// <inheritdoc />
    protected override void WorkerLoop(int slot, CancellationToken cancellationToken)
    {
        var runner = new T();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                PumpReadyQueue();

                if (!runner.Run(cancellationToken))
                    break;
            }
        }
        finally
        {
            try { runner.Dispose(); }
            catch (Exception ex) { System.Logger.Error($"Worker slot #{slot} runner disposal failed", ex); }
        }
    }
}
