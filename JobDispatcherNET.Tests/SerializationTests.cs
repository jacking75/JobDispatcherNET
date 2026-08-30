using Xunit;

namespace JobDispatcherNET.Tests;

public sealed class SerializationTests
{
    [Fact]
    public void SameActorNeverRunsOnTwoThreadsAtOnce()
    {
        using var host = new TestSystem(workers: 8);
        var actor = new CountingActor(host.Options());

        const int producers = 8;
        const int perProducer = 20_000;

        Parallel.For(0, producers, _ =>
        {
            for (var i = 0; i < perProducer; i++)
                Assert.True(actor.Bump());
        });

        TestSystem.SpinWaitFor(() => actor.Executed == producers * perProducer,
            TimeSpan.FromSeconds(60), $"executed {actor.Executed} of {producers * perProducer}");

        Assert.Equal(1, actor.MaxConcurrent);
        Assert.Equal(producers * perProducer, actor.State);
        Assert.Equal(0, actor.RemainingTaskCount);
    }

    [Fact]
    public void SingleProducerOrderIsPreserved()
    {
        using var host = new TestSystem(workers: 4);
        var seen = new List<int>();
        var actor = new OrderActor(host.Options(), seen);

        for (var i = 0; i < 5_000; i++)
            Assert.True(actor.Push(i));

        TestSystem.SpinWaitFor(() => actor.RemainingTaskCount == 0, TimeSpan.FromSeconds(30), "queue did not drain");

        Assert.Equal(5_000, seen.Count);
        for (var i = 0; i < seen.Count; i++)
            Assert.Equal(i, seen[i]);
    }

    [Fact]
    public void DifferentActorsRunInParallel()
    {
        using var host = new TestSystem(workers: 4);
        var barrier = new Barrier(4);
        var actors = Enumerable.Range(0, 4)
            .Select(_ => new BarrierActor(host.Options(mode: ExecutionMode.Scheduled), barrier))
            .ToArray();

        foreach (var actor in actors)
            Assert.True(actor.Enter());

        // Every actor must reach the barrier; if they were serialized this would time out.
        TestSystem.SpinWaitFor(() => actors.All(a => a.Passed), TimeSpan.FromSeconds(10),
            "actors did not run in parallel");
    }

    [Fact]
    public void NestedDispatchDoesNotRecurse()
    {
        using var host = new TestSystem(workers: 1);
        var inner = new CountingActor(host.Options());
        var outer = new NestedActor(host.Options(), inner);

        Assert.True(outer.Kick(depth: 500));

        TestSystem.SpinWaitFor(() => inner.Executed == 500, TimeSpan.FromSeconds(30),
            $"inner executed {inner.Executed}");
        Assert.Equal(1, inner.MaxConcurrent);
    }

    private sealed class OrderActor(JobOptions options, List<int> sink) : AsyncExecutable(options)
    {
        public bool Push(int value) => DoAsync(static t => t.Self.Record(t.Value), (Self: this, Value: value));

        private void Record(int value) => sink.Add(value);
    }

    private sealed class BarrierActor(JobOptions options, Barrier barrier) : AsyncExecutable(options)
    {
        private volatile bool _passed;
        public bool Passed => _passed;

        public bool Enter() => DoAsync(static a => a.Wait(), this);

        private void Wait()
        {
            barrier.SignalAndWait(TimeSpan.FromSeconds(8));
            _passed = true;
        }
    }

    private sealed class NestedActor(JobOptions options, CountingActor inner) : AsyncExecutable(options)
    {
        public bool Kick(int depth) => DoAsync(static t => t.Self.Fan(t.Depth), (Self: this, Depth: depth));

        private void Fan(int depth)
        {
            for (var i = 0; i < depth; i++)
                inner.Bump();
        }
    }
}
