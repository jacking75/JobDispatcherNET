using System.Threading.Channels;
using System.Threading.Tasks.Dataflow;
using BenchmarkDotNet.Attributes;

namespace JobDispatcherNET.Benchmarks;

/// <summary>
/// ROADMAP §3.3, comparison row — the same workload written three ways, so the numbers are about
/// the dispatch mechanism and nothing else.
///
/// The workload is fixed: <see cref="ActorCount"/> mailboxes, <see cref="Producers"/> producer
/// threads, <see cref="TotalMessages"/> messages sprayed round-robin, every mailbox serializing its
/// own messages. Each implementation gets the identical body (bump a non-atomic per-mailbox field,
/// then signal a shared completion counter) and the identical "wait until the last message ran"
/// finish line.
///
/// <list type="bullet">
/// <item><b>JobDispatcherNET</b> — dedicated worker threads, actors in
/// <see cref="ExecutionMode.Scheduled"/> because the producers are thread-pool threads.</item>
/// <item><b>Channel&lt;T&gt;</b> — one unbounded single-reader channel per mailbox, drained by a
/// thread-pool work item that is queued only on the idle-to-busy edge. This is the pattern people
/// hand-roll when they decide they do not need a library.</item>
/// <item><b>TPL Dataflow</b> — <see cref="ActionBlock{TInput}"/> with
/// <c>MaxDegreeOfParallelism = 1</c>, the in-box way to get per-mailbox serialization.</item>
/// </list>
///
/// TODO: Akka.NET and Proto.Actor belong in this table too (ROADMAP §3.3 names both). They are
/// deliberately left out for now so the benchmark project stays dependency-light — adding them
/// pulls in a large transitive graph and their own configuration surface (dispatchers, mailbox
/// types, actor-system startup) which needs its own tuning pass to be a fair comparison rather
/// than a strawman.
/// </summary>
[MemoryDiagnoser]
public class AlternativesComparison
{
    private const int ActorCount = 1_000;
    private const int Producers = 8;
    private const int TotalMessages = 100_000;
    private const int MessagesPerProducer = TotalMessages / Producers;
    private const int Workers = 8;

    private readonly Completion _done = new();
    private JobSystem _system = null!;
    private JobDispatcher _dispatcher = null!;
    private CounterActor[] _actors = null!;
    private ChannelMailbox[] _channels = null!;
    private ActionBlock<int>[] _blocks = null!;
    private Task[] _producers = null!;

    /// <summary>Build all three topologies up front so no setup cost lands inside a measurement.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _producers = new Task[Producers];

        _system = Bench.NewSystem("alternatives");
        _dispatcher = Bench.StartWorkers(_system, Workers);

        var options = new JobOptions
        {
            System = _system,
            Mode = ExecutionMode.Scheduled,
            Name = "mailbox",
        };

        _actors = new CounterActor[ActorCount];
        _channels = new ChannelMailbox[ActorCount];
        _blocks = new ActionBlock<int>[ActorCount];

        var blockOptions = new ExecutionDataflowBlockOptions
        {
            MaxDegreeOfParallelism = 1,     // the Dataflow way of saying "one message at a time"
            EnsureOrdered = true,
        };

        for (var i = 0; i < ActorCount; i++)
        {
            _actors[i] = new CounterActor(options);
            _channels[i] = new ChannelMailbox(_done);

            var mailbox = new BlockMailbox(_done);
            _blocks[i] = new ActionBlock<int>(mailbox.Handle, blockOptions);
        }
    }

    /// <summary>Tear everything down.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (var block in _blocks)
            block.Complete();
        Bench.Shutdown(_dispatcher, _system);
    }

    /// <summary>The library under test.</summary>
    [Benchmark(OperationsPerInvoke = TotalMessages, Baseline = true, Description = "JobDispatcherNET actors")]
    public int JobDispatcherActors() => RunWorkload(static (self, start) => self.ProduceJobDispatcher(start));

    /// <summary>Hand-rolled Channel-per-mailbox with a thread-pool drain loop.</summary>
    [Benchmark(OperationsPerInvoke = TotalMessages, Description = "Channel<T> + ThreadPool drain loop")]
    public int RawChannels() => RunWorkload(static (self, start) => self.ProduceChannel(start));

    /// <summary>TPL Dataflow ActionBlock with MaxDegreeOfParallelism = 1.</summary>
    [Benchmark(OperationsPerInvoke = TotalMessages, Description = "TPL Dataflow ActionBlock(MaxDOP=1)")]
    public int TplDataflow() => RunWorkload(static (self, start) => self.ProduceDataflow(start));

    private int RunWorkload(Action<AlternativesComparison, int> produce)
    {
        _done.Reset();

        for (var p = 0; p < Producers; p++)
        {
            var startIndex = p;
            _producers[p] = Task.Run(() => produce(this, startIndex));
        }

        Task.WaitAll(_producers);
        Bench.WaitFor(_done, TotalMessages);
        return _done.Count;
    }

    private void ProduceJobDispatcher(int startIndex)
    {
        var actors = _actors;
        var done = _done;
        var index = startIndex % actors.Length;

        for (var i = 0; i < MessagesPerProducer; i++)
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

    private void ProduceChannel(int startIndex)
    {
        var mailboxes = _channels;
        var index = startIndex % mailboxes.Length;

        for (var i = 0; i < MessagesPerProducer; i++)
        {
            mailboxes[index].Post(i);
            if (++index == mailboxes.Length)
                index = 0;
        }
    }

    private void ProduceDataflow(int startIndex)
    {
        var blocks = _blocks;
        var index = startIndex % blocks.Length;

        for (var i = 0; i < MessagesPerProducer; i++)
        {
            blocks[index].Post(i);
            if (++index == blocks.Length)
                index = 0;
        }
    }

    /// <summary>
    /// One mailbox: an unbounded single-reader channel plus a depth counter that keeps exactly one
    /// thread-pool work item draining it at a time.
    ///
    /// The counter — rather than the channel's own state — is what decides ownership, for the same
    /// reason <c>AsyncExecutable</c> uses one: the producer that takes the depth from 0 to 1 is the
    /// only one that schedules a drain, and the drain only lets go once the depth is back at 0, so
    /// no message can be stranded and no two drains can overlap. It also keeps the mailbox off
    /// <c>ChannelReader.Count</c>, which the single-reader channel does not implement.
    /// </summary>
    private sealed class ChannelMailbox
    {
        private static readonly Action<ChannelMailbox> DrainCallback = static m => m.Drain();

        private readonly Channel<int> _channel = Channel.CreateUnbounded<int>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

        private readonly Completion _done;
        private long _serialSum;
        private int _depth;

        public ChannelMailbox(Completion done) => _done = done;

        public void Post(int value)
        {
            // Write first, then publish the depth: a drain that sees depth N is guaranteed N reads.
            _channel.Writer.TryWrite(value);
            if (Interlocked.Increment(ref _depth) == 1)
                ThreadPool.UnsafeQueueUserWorkItem(DrainCallback, this, preferLocal: false);
        }

        private void Drain()
        {
            var claimed = Volatile.Read(ref _depth);

            while (true)
            {
                var processed = 0;
                while (processed < claimed && _channel.Reader.TryRead(out var value))
                {
                    _serialSum += value;        // safe only because one drain runs at a time
                    _done.Signal();
                    processed++;
                }

                claimed = Interlocked.Add(ref _depth, -processed);
                if (claimed == 0)
                    return;
            }
        }
    }

    /// <summary>Per-block state for the Dataflow variant, so its body matches the others exactly.</summary>
    private sealed class BlockMailbox
    {
        private readonly Completion _done;
        private long _serialSum;

        public BlockMailbox(Completion done) => _done = done;

        public void Handle(int value)
        {
            _serialSum += value;                // MaxDegreeOfParallelism = 1 makes this safe
            _done.Signal();
        }
    }
}
