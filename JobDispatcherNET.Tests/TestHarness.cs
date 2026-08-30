using JobDispatcherNET;

namespace JobDispatcherNET.Tests;

/// <summary>
/// A job system plus worker pool scoped to one test, so cases never share counters or workers.
/// </summary>
public sealed class TestSystem : IDisposable
{
    private readonly JobDispatcher? _dispatcher;

    public TestSystem(int workers = 4, JobSystemOptions? options = null, JobDispatcherOptions? dispatcherOptions = null)
    {
        System = new JobSystem(options ?? new JobSystemOptions
        {
            Name = "test",
            Logger = NullJobLogger.Instance,
            PublishMeter = false,
            DetectBlockingWaitOnWorker = false,
        });

        if (workers <= 0)
            return;

        _dispatcher = new JobDispatcher(workers, (dispatcherOptions ?? JobDispatcherOptions.Default) with
        {
            System = System,
            IdleWaitMs = 5,
        });
        _ = _dispatcher.RunWorkerThreadsAsync();

        // Wait for the pool to come up so tests do not race worker startup.
        SpinWaitFor(() => System.LiveWorkerCount == workers, TimeSpan.FromSeconds(5), "workers did not start");
    }

    public JobSystem System { get; }

    public JobOptions Options(int? maxQueue = null, ExecutionMode mode = ExecutionMode.LeaderFlush,
        Action<AsyncExecutable, DropReason>? onDropped = null) => new()
    {
        System = System,
        MaxQueueSize = maxQueue,
        Mode = mode,
        OnDropped = onDropped,
    };

    public void Dispose()
    {
        _dispatcher?.Dispose();
        System.Dispose();
    }

    public static void SpinWaitFor(Func<bool> condition, TimeSpan timeout, string message)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            Thread.Sleep(2);
        }
        throw new TimeoutException($"Timed out after {timeout.TotalSeconds:F1}s: {message}");
    }
}

/// <summary>Actor that records how many jobs ran and whether two ever ran at once.</summary>
public sealed class CountingActor : AsyncExecutable
{
    private int _concurrent;
    private int _maxConcurrent;
    private int _executed;
    private int _state;

    public CountingActor(JobOptions options) : base(options) { }

    public int Executed => Volatile.Read(ref _executed);
    public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);
    public int State => Volatile.Read(ref _state);

    public bool Bump() => DoAsync(static a => a.Run(), this);

    private void Run()
    {
        var now = Interlocked.Increment(ref _concurrent);
        if (now > Volatile.Read(ref _maxConcurrent))
            Volatile.Write(ref _maxConcurrent, now);

        // Non-atomic on purpose: if two threads ever run this actor at once the value drifts.
        _state = _state + 1;

        Interlocked.Increment(ref _executed);
        Interlocked.Decrement(ref _concurrent);
    }
}

/// <summary>
/// Holds its queue open: the first job parks on a gate, so later jobs pile up behind it and
/// bounded-queue behaviour can be observed deterministically.
/// </summary>
public sealed class BlockingActor : AsyncExecutable
{
    private readonly ManualResetEventSlim _gate = new(false);
    private readonly ManualResetEventSlim _entered = new(false);
    private int _done;

    public BlockingActor(JobOptions options) : base(options) { }

    public int Done => Volatile.Read(ref _done);

    /// <summary>Queue the blocking job and wait until it is actually running on a worker.</summary>
    public void BlockAndWait()
    {
        if (!DoAsync(static a => a.Park(), this))
            throw new InvalidOperationException("the blocking job was refused");

        if (!_entered.Wait(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("the blocking job never started");
    }

    public bool Enqueue() => DoAsync(static a => a.Tick(), this);

    public void Release() => _gate.Set();

    private void Park()
    {
        _entered.Set();
        _gate.Wait(TimeSpan.FromSeconds(30));
        Interlocked.Increment(ref _done);
    }

    private void Tick() => Interlocked.Increment(ref _done);
}
