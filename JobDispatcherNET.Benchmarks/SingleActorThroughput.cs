using BenchmarkDotNet.Attributes;

namespace JobDispatcherNET.Benchmarks;

/// <summary>
/// ROADMAP §3.3, row 1 — "single actor, single producer <c>DoAsync</c>: ops/s, alloc/op
/// (<c>Action</c> vs <c>TState</c>)".
///
/// Both benchmarks push the same fixed batch at one actor from one producer thread and wait for the
/// actor to drain. The only difference is how the job body is handed over:
/// <list type="bullet">
/// <item><see cref="ClosureAction"/> captures a local, so Roslyn allocates a display class plus a
/// delegate on every call — the allocation the library's docs warn about.</item>
/// <item><see cref="StateAction"/> passes a <c>static</c> lambda plus a value tuple, so the only
/// object in play is the pooled <c>Job&lt;TState&gt;</c>.</item>
/// </list>
/// <see cref="Mode"/> covers both dispatch paths: <see cref="ExecutionMode.LeaderFlush"/> runs the
/// jobs inline on the producer, <see cref="ExecutionMode.Scheduled"/> hands the actor to a worker.
/// </summary>
[MemoryDiagnoser]
public class SingleActorThroughput
{
    private const int Batch = 10_000;

    private JobSystem _system = null!;
    private JobDispatcher _dispatcher = null!;
    private CounterActor _actor = null!;

    /// <summary>Which thread ends up running the jobs.</summary>
    [Params(ExecutionMode.LeaderFlush, ExecutionMode.Scheduled)]
    public ExecutionMode Mode { get; set; }

    /// <summary>Build a private system with a small pool.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _system = Bench.NewSystem("single-actor");
        _dispatcher = Bench.StartWorkers(_system, 2);
        _actor = new CounterActor(new JobOptions
        {
            System = _system,
            Mode = Mode,
            Name = "counter",
        });
    }

    /// <summary>Tear the system down.</summary>
    [GlobalCleanup]
    public void Cleanup() => Bench.Shutdown(_dispatcher, _system);

    /// <summary><c>DoAsync(Action)</c> with a capturing closure.</summary>
    [Benchmark(OperationsPerInvoke = Batch, Baseline = true, Description = "DoAsync(Action) + capturing closure")]
    public int ClosureAction()
    {
        var actor = _actor;
        actor.Reset();

        for (var i = 0; i < Batch; i++)
        {
            var value = i;                              // captured -> display class + delegate
            actor.DoAsync(() => actor.Add(value));
        }

        Bench.WaitFor(actor, Batch);
        return actor.Count;
    }

    /// <summary><c>DoAsync&lt;TState&gt;</c> with a static lambda and an explicit state tuple.</summary>
    [Benchmark(OperationsPerInvoke = Batch, Description = "DoAsync<TState>(static, state)")]
    public int StateAction()
    {
        var actor = _actor;
        actor.Reset();

        for (var i = 0; i < Batch; i++)
            actor.DoAsync(static t => t.Actor.Add(t.Value), (Actor: actor, Value: i));

        Bench.WaitFor(actor, Batch);
        return actor.Count;
    }
}
