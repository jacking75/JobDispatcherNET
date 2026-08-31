using BenchmarkDotNet.Attributes;

namespace JobDispatcherNET.Benchmarks;

/// <summary>
/// Actor to actor ping-pong: round-trip latency.
///
/// Two actors bounce one message back and forth. Three shapes are measured:
/// <list type="bullet">
/// <item><see cref="BounceInline"/> — the batch is kicked off from the host thread with
/// <see cref="ExecutionMode.LeaderFlush"/>, so the whole chain runs on that one thread. This is the
/// library's cheapest path and the number to quote for "actor calls actor inside a worker".</item>
/// <item><see cref="BounceOnWorker"/> — <see cref="ExecutionMode.Scheduled"/>, so the host thread
/// only hands the chain to the ready queue and a worker runs every hop.</item>
/// <item><see cref="RoundTripFromExternalThread"/> — one round trip at a time, each kicked and
/// awaited by the host thread. This includes the producer/worker handoff, so it is the honest
/// "message in, answer out" latency seen by a network thread.</item>
/// </list>
/// </summary>
[MemoryDiagnoser]
public class PingPongLatency
{
    private const int RoundTrips = 1_000;
    private const int SingleRoundTrips = 100;

    private JobSystem _system = null!;
    private JobDispatcher _dispatcher = null!;
    private Bouncer _inlineA = null!;
    private Bouncer _workerA = null!;
    private Bouncer _singleA = null!;

    /// <summary>Build the system and three independent actor pairs.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _system = Bench.NewSystem("ping-pong");
        _dispatcher = Bench.StartWorkers(_system, 2);

        _inlineA = CreatePair(ExecutionMode.LeaderFlush, "inline");
        _workerA = CreatePair(ExecutionMode.Scheduled, "worker");
        _singleA = CreatePair(ExecutionMode.Scheduled, "single");
    }

    /// <summary>Tear the system down.</summary>
    [GlobalCleanup]
    public void Cleanup() => Bench.Shutdown(_dispatcher, _system);

    private Bouncer CreatePair(ExecutionMode mode, string name)
    {
        var options = new JobOptions { System = _system, Mode = mode, Name = name };
        var a = new Bouncer(options);
        var b = new Bouncer(options);
        a.Peer = b;
        b.Peer = a;
        return a;
    }

    /// <summary>N round trips, all flushed inline on the calling thread.</summary>
    [Benchmark(OperationsPerInvoke = RoundTrips, Baseline = true, Description = "A<->B round trip, LeaderFlush (inline)")]
    public void BounceInline() => _inlineA.Run(RoundTrips);

    /// <summary>N round trips, every hop executed by a worker thread.</summary>
    [Benchmark(OperationsPerInvoke = RoundTrips, Description = "A<->B round trip, Scheduled (on worker)")]
    public void BounceOnWorker() => _workerA.Run(RoundTrips);

    /// <summary>One round trip per wait, so the producer/worker handoff is inside the measurement.</summary>
    [Benchmark(OperationsPerInvoke = SingleRoundTrips, Description = "single round trip kicked + awaited per op")]
    public void RoundTripFromExternalThread()
    {
        for (var i = 0; i < SingleRoundTrips; i++)
            _singleA.Run(1);
    }

    /// <summary>
    /// Half of a ping-pong pair. <c>A.Ping -&gt; B.Pong -&gt; A.Complete</c> is one round trip;
    /// <c>Complete</c> either re-arms or releases the waiter.
    /// </summary>
    private sealed class Bouncer : AsyncExecutable
    {
        private readonly ManualResetEventSlim _done = new(false);
        private int _remaining;

        public Bouncer(JobOptions options) : base(options) { }

        /// <summary>The actor on the other end. Set once during setup.</summary>
        public Bouncer Peer { get; set; } = null!;

        /// <summary>Bounce <paramref name="roundTrips"/> times and block until the last one lands.</summary>
        public void Run(int roundTrips)
        {
            _done.Reset();
            Volatile.Write(ref _remaining, roundTrips);

            if (!DoAsync(static b => b.Ping(), this))
                throw new InvalidOperationException("ping-pong kick was refused");

            if (!_done.Wait(Bench.WaitTimeout))
                throw new TimeoutException($"ping-pong stalled with {Volatile.Read(ref _remaining)} round trips left");
        }

        private void Ping() => Peer.DoAsync(static b => b.Pong(), Peer);

        // Runs on the peer; 'Peer' from here is the actor that started the round trip.
        private void Pong() => Peer.DoAsync(static b => b.Complete(), Peer);

        private void Complete()
        {
            if (Interlocked.Decrement(ref _remaining) > 0)
                Ping();
            else
                _done.Set();
        }
    }
}
