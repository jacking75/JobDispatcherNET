using BenchmarkDotNet.Attributes;

namespace JobDispatcherNET.Benchmarks;

/// <summary>
/// ROADMAP §3.3, row 5 — "bounded rejection path: cost of a refusal".
///
/// Back-pressure only helps if saying "no" is cheap: when a queue is full the caller is, by
/// definition, already in trouble. Both rejecting actors are bounded at one job and hold a parked
/// job on a worker for the whole run, so every <c>DoAsync</c> against them is refused with
/// <see cref="DropReason.QueueFull"/> and the measurement is the refusal path only — the reservation
/// check, the dropped-jobs counter, and (for one of them) the <see cref="JobOptions.OnDropped"/>
/// callback.
///
/// <see cref="AcceptedJob"/> is the baseline: the same call against an actor with room, so the
/// tables show refusal cost next to admission cost rather than in isolation.
/// </summary>
[MemoryDiagnoser]
public class RejectionCost
{
    private JobSystem _system = null!;
    private JobDispatcher _dispatcher = null!;
    private CounterActor _open = null!;
    private BlockingActor _full = null!;
    private BlockingActor _fullWithCallback = null!;
    private int _dropCallbacks;

    /// <summary>Number of <see cref="JobOptions.OnDropped"/> invocations observed (sanity check).</summary>
    public int DropCallbacks => Volatile.Read(ref _dropCallbacks);

    /// <summary>Build the system and park a job on each bounded actor so its queue stays full.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _system = Bench.NewSystem("rejection");
        _dispatcher = Bench.StartWorkers(_system, 4);

        _open = new CounterActor(new JobOptions
        {
            System = _system,
            Name = "open",
        });

        _full = new BlockingActor(new JobOptions
        {
            System = _system,
            MaxQueueSize = 1,
            DropPolicy = DropPolicy.Silent,
            Mode = ExecutionMode.Scheduled,
            Name = "full-silent",
        });

        _fullWithCallback = new BlockingActor(new JobOptions
        {
            System = _system,
            MaxQueueSize = 1,
            DropPolicy = DropPolicy.Reject,
            OnDropped = (_, _) => Interlocked.Increment(ref _dropCallbacks),
            Mode = ExecutionMode.Scheduled,
            Name = "full-callback",
        });

        // Each parked job occupies its actor's single queue slot (and one worker) until cleanup.
        _full.BlockAndWait();
        _fullWithCallback.BlockAndWait();
    }

    /// <summary>Release the parked jobs, then tear the system down.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _full.Release();
        _fullWithCallback.Release();
        Bench.Shutdown(_dispatcher, _system);
    }

    /// <summary>Baseline: a job that is accepted and run inline.</summary>
    [Benchmark(Baseline = true, Description = "accepted DoAsync<TState> (queue has room)")]
    public bool AcceptedJob() => _open.DoAsync(static a => a.Touch(), _open);

    /// <summary>Refusal with <see cref="DropPolicy.Silent"/> — counter bump only.</summary>
    [Benchmark(Description = "refused DoAsync<TState> (queue full, silent)")]
    public bool RefusedJob() => _full.DoAsync(static a => a.Noop(), _full);

    /// <summary>Refusal with <see cref="DropPolicy.Reject"/> and an <c>OnDropped</c> callback.</summary>
    [Benchmark(Description = "refused DoAsync<TState> (queue full, OnDropped callback)")]
    public bool RefusedJobWithCallback() => _fullWithCallback.DoAsync(static a => a.Noop(), _fullWithCallback);

    /// <summary>Refusal of the closure overload: the closure is allocated before the refusal.</summary>
    [Benchmark(Description = "refused DoAsync(Action) with a capturing closure")]
    public bool RefusedClosure()
    {
        var actor = _full;
        return actor.DoAsync(() => actor.Noop());
    }
}
