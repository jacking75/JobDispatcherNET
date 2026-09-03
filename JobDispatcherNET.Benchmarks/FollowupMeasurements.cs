using BenchmarkDotNet.Attributes;

namespace JobDispatcherNET.Benchmarks;

/// <summary>
/// Independent actor-to-actor rings, so the run records whether actor fan-out actually uses the
/// worker pool (S2 in <c>docs/review-followup-2026-09-03.md</c>).
///
/// <para><see cref="RingCount"/> rings pass a token round <see cref="RingSize"/> actors each. The
/// rings never touch one another, so throughput ought to rise with the pool. Before the fan-out fix
/// it was flat from 1 to 8 workers: waking the next actor queued it on a thread-local list nothing
/// else could take from, so a ring ran entirely on whichever thread started it.</para>
///
/// <para>Compare <c>FanOut = true</c> against <c>false</c> — the latter is the pre-fix behaviour,
/// kept behind <see cref="JobOptions.FanOutToWorkers"/> — across the worker counts.</para>
/// </summary>
[MemoryDiagnoser]
public class ActorRingThroughput
{
    private const int RingCount = 64;
    private const int RingSize = 8;
    private const int HopsPerRing = 2_000;
    private const int TotalHops = RingCount * HopsPerRing;

    private readonly Completion _done = new();
    private JobSystem _system = null!;
    private JobDispatcher _dispatcher = null!;
    private RingNode[] _entryPoints = null!;

    /// <summary>Size of the worker pool. The point of the fixture is the shape of this curve.</summary>
    [Params(1, 4, 8)]
    public int Workers { get; set; }

    /// <summary>Whether extra ready actors are handed to the pool or kept on the flushing thread.</summary>
    [Params(true, false)]
    public bool FanOut { get; set; }

    /// <summary>Build the rings.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _system = Bench.NewSystem("ring");
        _dispatcher = Bench.StartWorkers(_system, Workers);

        var options = new JobOptions
        {
            System = _system,
            Name = "ring-node",
            FanOutToWorkers = FanOut,
        };

        _entryPoints = new RingNode[RingCount];
        for (var r = 0; r < RingCount; r++)
        {
            var nodes = new RingNode[RingSize];
            for (var i = 0; i < RingSize; i++)
                nodes[i] = new RingNode(options, _done);
            for (var i = 0; i < RingSize; i++)
                nodes[i].Next = nodes[(i + 1) % RingSize];

            _entryPoints[r] = nodes[0];
        }
    }

    /// <summary>Tear the system down.</summary>
    [GlobalCleanup]
    public void Cleanup() => Bench.Shutdown(_dispatcher, _system);

    /// <summary>Send one token round every ring and wait for the last hop.</summary>
    [Benchmark(OperationsPerInvoke = TotalHops, Description = "64 independent actor rings")]
    public int Circulate()
    {
        _done.Reset();

        foreach (var entry in _entryPoints)
            entry.Send(HopsPerRing);

        Bench.WaitFor(_done, TotalHops);
        return _done.Count;
    }

    private sealed class RingNode(JobOptions options, Completion done) : AsyncExecutable(options)
    {
        public RingNode? Next { get; set; }

        public void Send(int remaining) =>
            DoAsync(static t => t.Node.Hop(t.Remaining), (Node: this, Remaining: remaining));

        private void Hop(int remaining)
        {
            done.Signal();
            if (remaining > 1)
                Next!.Send(remaining - 1);
        }
    }
}

/// <summary>
/// Cost per item through one sequencer, bounded against unbounded (S7).
///
/// <para>An unbounded sequencer used to keep the same <c>_pending</c> counter a bounded one needs:
/// raised by the producing IO thread, lowered by the worker that handles the item, one shared
/// read-modify-write per item on top of the queue's own CAS. The bounded cell is the control — it
/// should not move.</para>
/// </summary>
[MemoryDiagnoser]
public class SequencerThroughput
{
    private const int Producers = 8;
    private const int ItemsPerProducer = 25_000;
    private const int TotalItems = Producers * ItemsPerProducer;

    private readonly Completion _done = new();
    private JobSystem _system = null!;
    private JobDispatcher _dispatcher = null!;
    private Sequencer<int> _sequencer = null!;
    private Task[] _producers = null!;

    /// <summary>0 is unbounded; the bounded cell is generous enough never to refuse.</summary>
    [Params(0, 1_000_000)]
    public int MaxPending { get; set; }

    /// <summary>Build the sequencer and its worker pool.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _system = Bench.NewSystem("sequencer");
        _dispatcher = Bench.StartWorkers(_system, 4);
        _sequencer = new Sequencer<int>(_system, _ => _done.Signal(), maxPending: MaxPending);
        _producers = new Task[Producers];
    }

    /// <summary>Tear the system down.</summary>
    [GlobalCleanup]
    public void Cleanup() => Bench.Shutdown(_dispatcher, _system);

    /// <summary>Feed the sequencer from 8 threads and wait for the handler to catch up.</summary>
    [Benchmark(OperationsPerInvoke = TotalItems, Description = "8 producers -> 1 sequencer, 4 workers")]
    public int Feed()
    {
        _done.Reset();

        for (var p = 0; p < Producers; p++)
            _producers[p] = Task.Run(Produce);

        Task.WaitAll(_producers);
        Bench.WaitFor(_done, TotalItems);
        return _done.Count;
    }

    private void Produce()
    {
        var sequencer = _sequencer;
        for (var i = 0; i < ItemsPerProducer; i++)
            sequencer.Enqueue(i);
    }
}

/// <summary>
/// Arming and cancelling timers from many threads (S22).
///
/// <para>Every arm takes the timer service's single lock and does an O(log n) heap insert, and every
/// entry is a fresh allocation. This fixture is what a decision about pooling the entries or
/// splitting the heap should rest on: read the scaling across <see cref="Threads"/> for the lock,
/// and the allocation column for the entries.</para>
/// </summary>
[MemoryDiagnoser]
public class TimerArmAndCancel
{
    private const int TimersPerThread = 25_000;

    private JobSystem _system = null!;
    private JobDispatcher _dispatcher = null!;
    private CounterActor _actor = null!;
    private Task[] _arming = null!;

    /// <summary>Threads arming timers at once. The curve is the point.</summary>
    [Params(1, 4, 8)]
    public int Threads { get; set; }

    /// <summary>Build the system and one unbounded actor to hang the timers off.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _system = Bench.NewSystem("timer-arm");
        _dispatcher = Bench.StartWorkers(_system, 4);
        _actor = new CounterActor(new JobOptions
        {
            System = _system,
            Name = "timer-owner",
            MaxPendingTimers = 0,      // unbounded: measure the service, not the bound
        });
        _arming = new Task[Threads];
    }

    /// <summary>Tear the system down.</summary>
    [GlobalCleanup]
    public void Cleanup() => Bench.Shutdown(_dispatcher, _system);

    /// <summary>Arm a batch of far-future timers on every thread, then cancel them all.</summary>
    [Benchmark(Description = "N threads x 25,000 DoAsyncAfter(1h) then Cancel")]
    public int ArmThenCancel()
    {
        for (var t = 0; t < Threads; t++)
            _arming[t] = Task.Run(ArmBatch);

        Task.WaitAll(_arming);
        return Threads * TimersPerThread;
    }

    private void ArmBatch()
    {
        var handles = new ITimerHandle[TimersPerThread];
        var actor = _actor;

        for (var i = 0; i < TimersPerThread; i++)
            handles[i] = actor.DoAsyncAfter(TimeSpan.FromHours(1), static a => a.Touch(), actor);

        foreach (var handle in handles)
            handle.Cancel();
    }
}

/// <summary>
/// The same job through a reference-typed state and a value-typed one (S23).
///
/// <para><c>Job&lt;TState&gt;</c>'s pool keeps its per-thread stack in a <c>[ThreadStatic]</c>. When
/// <c>TState</c> is a reference type the JIT compiles one shared body for every such instantiation
/// and each access to that field goes through a generic dictionary lookup; a value-typed
/// <c>TState</c> gets its own specialised code and the inline TLS path. Both idioms are documented
/// side by side, so the gap between these two cells is the cost of choosing the first one.</para>
/// </summary>
[MemoryDiagnoser]
public class JobStateShape
{
    private const int TotalJobs = 200_000;

    private JobSystem _system = null!;
    private CounterActor _actor = null!;

    /// <summary>Build a worker-free system: the producer flushes inline, which is the path measured.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _system = Bench.NewSystem("state-shape");
        _actor = new CounterActor(new JobOptions { System = _system, Name = "state-shape-actor" });
    }

    /// <summary>Tear the system down.</summary>
    [GlobalCleanup]
    public void Cleanup() => Bench.Shutdown(null, _system);

    /// <summary>The <c>DoAsync(static a =&gt; a.Touch(), this)</c> idiom: TState is the actor.</summary>
    [Benchmark(OperationsPerInvoke = TotalJobs, Baseline = true, Description = "reference-typed TState")]
    public long ReferenceState()
    {
        _actor.Reset();
        for (var i = 0; i < TotalJobs; i++)
            _actor.DoAsync(static a => a.Touch(), _actor);
        return _actor.SerialSum;
    }

    /// <summary>The tuple idiom: TState is a value type, so the pool specialises for it.</summary>
    [Benchmark(OperationsPerInvoke = TotalJobs, Description = "value-typed TState")]
    public long ValueState()
    {
        _actor.Reset();
        for (var i = 0; i < TotalJobs; i++)
            _actor.DoAsync(static t => t.Self.Add(t.Value), (Self: _actor, Value: 1));
        return _actor.SerialSum;
    }
}
