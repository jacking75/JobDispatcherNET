using System.Diagnostics;

namespace JobDispatcherNET.Benchmarks;

/// <summary>
/// Shared setup helpers. Every benchmark owns a private <see cref="JobSystem"/> and worker pool so
/// nothing leaks between fixtures and <see cref="JobSystem.Default"/> is never touched.
/// </summary>
internal static class Bench
{
    /// <summary>How long a wait helper spins before declaring the run broken.</summary>
    public static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    /// <summary>A quiet, meter-free system suitable for measurement.</summary>
    public static JobSystem NewSystem(string name) => new(new JobSystemOptions
    {
        Name = name,
        Logger = NullJobLogger.Instance,
        PublishMeter = false,                   // no observable-counter callbacks during a run
        EnableDetailedMetrics = false,          // no Stopwatch read per job
        DetectBlockingWaitOnWorker = false,     // benchmarks block on the (non-worker) host thread
    });

    /// <summary>Start a worker pool and wait until every thread is actually up.</summary>
    public static JobDispatcher StartWorkers(JobSystem system, int workerCount)
    {
        var dispatcher = new JobDispatcher(workerCount, new JobDispatcherOptions
        {
            System = system,
            IdleWaitMs = 5,
        });
        _ = dispatcher.RunWorkerThreadsAsync();

        var deadline = Stopwatch.GetTimestamp();
        while (system.LiveWorkerCount < workerCount)
        {
            if (Stopwatch.GetElapsedTime(deadline) > WaitTimeout)
                throw new TimeoutException($"only {system.LiveWorkerCount}/{workerCount} workers started");
            Thread.Sleep(1);
        }
        return dispatcher;
    }

    /// <summary>Stop the pool and release the system.</summary>
    public static void Shutdown(JobDispatcher? dispatcher, JobSystem? system)
    {
        dispatcher?.Dispose();
        system?.Dispose();
    }

    /// <summary>Spin until <paramref name="counter"/> reaches <paramref name="target"/>.</summary>
    /// <remarks>Allocation-free on purpose: it runs inside the measured region.</remarks>
    public static void WaitFor(Completion counter, int target)
    {
        var deadline = Stopwatch.GetTimestamp();
        var spinner = new SpinWait();
        while (counter.Count < target)
        {
            spinner.SpinOnce();
            if (Stopwatch.GetElapsedTime(deadline) > WaitTimeout)
                throw new TimeoutException($"only {counter.Count}/{target} jobs completed");
        }
    }

    /// <inheritdoc cref="WaitFor(Completion, int)" />
    public static void WaitFor(CounterActor actor, int target)
    {
        var deadline = Stopwatch.GetTimestamp();
        var spinner = new SpinWait();
        while (actor.Count < target)
        {
            spinner.SpinOnce();
            if (Stopwatch.GetElapsedTime(deadline) > WaitTimeout)
                throw new TimeoutException($"actor '{actor.Name}' ran only {actor.Count}/{target} jobs");
        }
    }
}

/// <summary>A shared "how many messages have landed" counter.</summary>
internal sealed class Completion
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public void Signal() => Interlocked.Increment(ref _count);

    public void Reset() => Volatile.Write(ref _count, 0);
}

/// <summary>
/// The workload actor used by most fixtures: counts what it ran, and keeps a deliberately
/// non-atomic field so a broken serialization guarantee would show up as a wrong sum.
/// </summary>
internal sealed class CounterActor : AsyncExecutable
{
    private long _serialSum;
    private int _count;

    public CounterActor(JobOptions options) : base(options) { }

    public int Count => Volatile.Read(ref _count);

    /// <summary>Non-atomic accumulator — only correct because the actor serializes its jobs.</summary>
    public long SerialSum => Interlocked.Read(ref _serialSum);

    public void Reset()
    {
        Interlocked.Exchange(ref _serialSum, 0);
        Volatile.Write(ref _count, 0);
    }

    /// <summary>One unit of work.</summary>
    public void Touch()
    {
        _serialSum++;
        Interlocked.Increment(ref _count);
    }

    /// <summary>One unit of work that consumes a value, for closure-vs-state comparisons.</summary>
    public void Add(int value)
    {
        _serialSum += value;
        Interlocked.Increment(ref _count);
    }
}

/// <summary>
/// Parks its first job on a gate so the queue behind it stays full for the whole run. Mirrors
/// <c>JobDispatcherNET.Tests.BlockingActor</c>; used to make the rejection path deterministic.
/// </summary>
internal sealed class BlockingActor : AsyncExecutable
{
    private readonly ManualResetEventSlim _gate = new(false);
    private readonly ManualResetEventSlim _entered = new(false);

    public BlockingActor(JobOptions options) : base(options) { }

    /// <summary>Queue the parking job and return once it is genuinely running on a worker.</summary>
    public void BlockAndWait()
    {
        if (!DoAsync(static a => a.Park(), this))
            throw new InvalidOperationException("the blocking job was refused");
        if (!_entered.Wait(Bench.WaitTimeout))
            throw new TimeoutException("the blocking job never started");
    }

    /// <summary>Let the parked job finish. Must be called before shutting the system down.</summary>
    public void Release() => _gate.Set();

    /// <summary>A job that will only ever be accepted when the queue has room.</summary>
    public void Noop() { }

    private void Park()
    {
        _entered.Set();
        _gate.Wait(Bench.WaitTimeout);
    }
}
