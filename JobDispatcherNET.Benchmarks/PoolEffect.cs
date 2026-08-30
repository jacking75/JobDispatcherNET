using BenchmarkDotNet.Attributes;

namespace JobDispatcherNET.Benchmarks;

/// <summary>
/// ROADMAP §3.3, row 6 — "pool on/off: gen0 collection count", and the evidence ROADMAP §4.1 asks
/// for before touching <c>JobEntry</c>'s <c>ConcurrentBag</c> pool.
///
/// <see cref="MaxPoolSize"/> = 16384 is the shipping default; 0 disables recycling entirely, so
/// every job is a fresh gen0 object left to the collector. Read the <c>Gen0</c> column: if the
/// no-pool row is not measurably worse, the pool (two interlocked ops and a
/// <c>ConcurrentBag</c> round trip per job) is not paying for itself.
///
/// Both benchmarks run in <see cref="ExecutionMode.LeaderFlush"/> so the jobs are rented, executed
/// and recycled on one thread — no worker handoff noise on top of the allocation signal.
/// </summary>
[MemoryDiagnoser]
public class PoolEffect
{
    private const int Batch = 50_000;

    private JobSystem _system = null!;
    private JobDispatcher _dispatcher = null!;
    private CounterActor _actor = null!;
    private int _previousJobPool;
    private int _previousStatePool;

    /// <summary>Cap for <see cref="Job.MaxPoolSize"/>; 0 turns pooling off.</summary>
    [Params(16 * 1024, 0)]
    public int MaxPoolSize { get; set; }

    /// <summary>Reconfigure the pools, empty them, and build a fresh system.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _previousJobPool = Job.MaxPoolSize;
        _previousStatePool = Job<CounterActor>.MaxPoolSize;

        Job.MaxPoolSize = MaxPoolSize;
        Job<CounterActor>.MaxPoolSize = MaxPoolSize;
        Job.ClearPool();
        Job<CounterActor>.ClearPool();

        _system = Bench.NewSystem("pool");
        _dispatcher = Bench.StartWorkers(_system, 2);
        _actor = new CounterActor(new JobOptions
        {
            System = _system,
            Mode = ExecutionMode.LeaderFlush,
            Name = "pool-target",
        });
    }

    /// <summary>Restore the process-wide pool caps, then tear the system down.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        Bench.Shutdown(_dispatcher, _system);
        Job.MaxPoolSize = _previousJobPool;
        Job<CounterActor>.MaxPoolSize = _previousStatePool;
    }

    /// <summary>Pooled <c>Job&lt;TState&gt;</c> only — nothing else should be allocated.</summary>
    [Benchmark(OperationsPerInvoke = Batch, Baseline = true, Description = "DoAsync<TState> (pooled Job<TState>)")]
    public int StateJobs()
    {
        var actor = _actor;
        actor.Reset();

        for (var i = 0; i < Batch; i++)
            actor.DoAsync(static a => a.Touch(), actor);

        Bench.WaitFor(actor, Batch);
        return actor.Count;
    }

    /// <summary>Pooled <c>Job</c> plus a closure the pool cannot help with.</summary>
    [Benchmark(OperationsPerInvoke = Batch, Description = "DoAsync(Action) (pooled Job + closure)")]
    public int ClosureJobs()
    {
        var actor = _actor;
        actor.Reset();

        for (var i = 0; i < Batch; i++)
        {
            var value = i;
            actor.DoAsync(() => actor.Add(value));
        }

        Bench.WaitFor(actor, Batch);
        return actor.Count;
    }
}
