using BenchmarkDotNet.Attributes;

namespace JobDispatcherNET.Benchmarks;

/// <summary>
/// ROADMAP §3.3, row 2 — "1,000 actors, 8 producers: throughput".
///
/// Eight producer threads spray a fixed batch of <see cref="TotalJobs"/> messages round-robin over
/// <see cref="ActorCount"/> actors while <see cref="Workers"/> worker threads drain them. One
/// invocation is the whole batch, so the reported per-operation time is the amortised end-to-end
/// cost of one message (enqueue + dispatch + execute).
///
/// The actors run in <see cref="ExecutionMode.Scheduled"/> because the producers are thread-pool
/// threads: under the default <see cref="ExecutionMode.LeaderFlush"/> the producers would run the
/// actor code themselves and the worker pool would sit idle.
///
/// <c>ActorCount = 1</c> is the deliberate worst case: every message funnels through one queue.
/// </summary>
[MemoryDiagnoser]
public class ManyActorsThroughput
{
    private const int TotalJobs = 100_000;
    private const int Producers = 8;
    private const int JobsPerProducer = TotalJobs / Producers;

    private readonly Completion _done = new();
    private JobSystem _system = null!;
    private JobDispatcher _dispatcher = null!;
    private CounterActor[] _actors = null!;
    private Task[] _producers = null!;

    /// <summary>How many actors the batch is spread over.</summary>
    [Params(1, 100, 1000)]
    public int ActorCount { get; set; }

    /// <summary>Size of the worker pool draining them.</summary>
    [Params(4, 8)]
    public int Workers { get; set; }

    /// <summary>Build the system, the pool and the actor set.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _system = Bench.NewSystem("many-actors");
        _dispatcher = Bench.StartWorkers(_system, Workers);

        var options = new JobOptions
        {
            System = _system,
            Mode = ExecutionMode.Scheduled,
            Name = "worker-actor",
        };

        _actors = new CounterActor[ActorCount];
        for (var i = 0; i < ActorCount; i++)
            _actors[i] = new CounterActor(options);

        _producers = new Task[Producers];
    }

    /// <summary>Tear the system down.</summary>
    [GlobalCleanup]
    public void Cleanup() => Bench.Shutdown(_dispatcher, _system);

    /// <summary>Post the whole batch from 8 threads and wait for the last message to run.</summary>
    [Benchmark(OperationsPerInvoke = TotalJobs, Description = "8 producers -> N actors, fixed batch")]
    public int PostBatch()
    {
        _done.Reset();

        for (var p = 0; p < Producers; p++)
        {
            var startIndex = p;
            _producers[p] = Task.Run(() => Produce(startIndex));
        }

        Task.WaitAll(_producers);
        Bench.WaitFor(_done, TotalJobs);
        return _done.Count;
    }

    private void Produce(int startIndex)
    {
        var actors = _actors;
        var done = _done;
        var index = startIndex % actors.Length;

        for (var i = 0; i < JobsPerProducer; i++)
        {
            var actor = actors[index];
            if (++index == actors.Length)
                index = 0;

            actor.DoAsync(
                static t =>
                {
                    t.Actor.Touch();
                    t.Done.Signal();
                },
                (Actor: actor, Done: done));
        }
    }
}
