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
    /// If a worker slot stays up this long after a restart, its restart budget is refilled.
    /// Without this a server that hiccups five times over months is permanently down a worker.
    /// Default 5 minutes; <see cref="TimeSpan.Zero"/> disables the refill.
    /// </summary>
    public TimeSpan RestartCountResetAfter { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Ready-queue items a worker handles per iteration. Default 256.</summary>
    public int MaxReadyDrainPerTick { get; init; } = 256;

    /// <inheritdoc cref="MaxReadyDrainPerTick" />
    [Obsolete("Renamed to MaxReadyDrainPerTick; timer dispatch now flows through the shared ready queue. Removed in v4.0.")]
    public int MaxTimerDrainPerTick
    {
        get => MaxReadyDrainPerTick;
        init => MaxReadyDrainPerTick = value;
    }

    /// <summary>How long an idle worker blocks before re-checking. Only used by the non-generic dispatcher.</summary>
    public int IdleWaitMs { get; init; } = 20;

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

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        _allWorkersDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_lifecycleLock)
        {
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
        catch (OperationCanceledException)
        {
            exitedNormally = true;
        }
        catch (Exception ex)
        {
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
            if (TryRestart(slot))
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
        var backoff = TimeSpan.FromMilliseconds(
            Options.RestartBackoff.TotalMilliseconds * Math.Pow(2, attempts - 1));
        System.Logger.Warn(
            $"Restarting worker slot #{slot} (attempt {attempts}/{Options.MaxRestartsPerWorker}) after {backoff.TotalMilliseconds:F0}ms");

        Thread.Sleep(backoff);

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
    /// </summary>
    /// <param name="joinTimeout">How long to wait for each worker thread.</param>
    /// <returns><c>true</c> if every worker exited within the timeout.</returns>
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

        System.SignalWork();

        var allStopped = true;
        foreach (var thread in snapshot)
        {
            if (thread is not { IsAlive: true })
                continue;

            System.SignalWork();
            if (thread.Join(joinTimeout))
                continue;

            allStopped = false;
            System.Logger.Error(
                $"Worker thread '{thread.Name}' did not stop within {joinTimeout.TotalMilliseconds:F0}ms. " +
                "A job is probably blocking (a lock, a synchronous wait, or an infinite loop).");
        }

        // Only safe to dispose once every worker has genuinely left; a straggler still reads the
        // token, and an ObjectDisposedException there surfaces as a bogus "worker crashed" log.
        if (allStopped)
            _cts.Dispose();

        return allStopped;
    }

    /// <inheritdoc />
    public void Dispose() => TryStop(TimeSpan.FromSeconds(5));

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
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

        while (!cancellationToken.IsCancellationRequested)
        {
            if (PumpReadyQueue() == 0)
                System.WaitForWork(idleWait);
        }
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
