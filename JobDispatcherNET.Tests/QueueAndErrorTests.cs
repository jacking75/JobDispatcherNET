using Xunit;

namespace JobDispatcherNET.Tests;

public sealed class BoundedQueueTests
{
    [Fact]
    public void QueueNeverExceedsTheConfiguredLimit()
    {
        using var host = new TestSystem(workers: 1);
        var actor = new BlockingActor(host.Options(maxQueue: 5, mode: ExecutionMode.Scheduled));

        // One job is parked on the worker and still counts against the limit; four more fit.
        actor.BlockAndWait();

        var accepted = 0;
        var observed = 0;
        for (var i = 0; i < 50; i++)
        {
            if (actor.Enqueue())
                accepted++;
            observed = Math.Max(observed, actor.RemainingTaskCount);
        }

        Assert.Equal(4, accepted);
        Assert.Equal(5, observed);

        actor.Release();
        TestSystem.SpinWaitFor(() => actor.RemainingTaskCount == 0, TimeSpan.FromSeconds(10), "queue did not drain");
        Assert.Equal(5, actor.Done);
    }

    [Fact]
    public void OnDroppedFiresOncePerRejectionWithTheReason()
    {
        using var host = new TestSystem(workers: 1);
        var reasons = new List<DropReason>();
        var actor = new BlockingActor(host.Options(maxQueue: 2, mode: ExecutionMode.Scheduled,
            onDropped: (_, reason) => reasons.Add(reason)));

        actor.BlockAndWait();   // fills 1 of the 2 slots

        for (var i = 0; i < 10; i++)
            actor.Enqueue();    // 1 accepted, 9 rejected

        Assert.Equal(9, reasons.Count);
        Assert.All(reasons, r => Assert.Equal(DropReason.QueueFull, r));
        Assert.Equal(9, host.System.Metrics.Snapshot().TotalJobsDropped);

        actor.Release();
    }

    [Fact]
    public void SilentPolicySuppressesTheCallbackButStillCounts()
    {
        using var host = new TestSystem(workers: 1);
        var called = 0;
        var actor = new BlockingActor(new JobOptions
        {
            System = host.System,
            MaxQueueSize = 1,
            Mode = ExecutionMode.Scheduled,
            DropPolicy = DropPolicy.Silent,
            OnDropped = (_, _) => called++,
        });

        actor.BlockAndWait();   // the single slot is taken

        for (var i = 0; i < 5; i++)
            actor.Enqueue();

        Assert.Equal(0, called);
        Assert.Equal(5, host.System.Metrics.Snapshot().TotalJobsDropped);

        actor.Release();
    }

    [Fact]
    public void ShutdownGateRefusesWithTheRightReason()
    {
        using var host = new TestSystem(workers: 1);
        DropReason? reason = null;
        var actor = new GateActor(host.Options(onDropped: (_, r) => reason = r));

        host.System.AcceptingWork = false;
        Assert.False(actor.Enqueue());
        Assert.Equal(DropReason.ShuttingDown, reason);

        host.System.AcceptingWork = true;
        Assert.True(actor.Enqueue());
    }

    [Fact]
    public void MaxObservedQueueDepthIsTracked()
    {
        using var host = new TestSystem(workers: 1);
        var actor = new BlockingActor(host.Options(maxQueue: 8, mode: ExecutionMode.Scheduled));

        actor.BlockAndWait();
        for (var i = 0; i < 4; i++)
            actor.Enqueue();

        Assert.Equal(5, actor.MaxObservedQueueDepth);
        actor.Release();
    }

    private sealed class GateActor(JobOptions options) : AsyncExecutable(options)
    {
        public bool Enqueue() => DoAsync(static _ => { }, 0);
    }
}

public sealed class ErrorIsolationTests
{
    [Fact]
    public void AFailingJobDoesNotStopTheNextOne()
    {
        using var host = new TestSystem(workers: 2);
        var actor = new ThrowingActor(host.Options());

        for (var i = 0; i < 20; i++)
            actor.Work(fail: i % 2 == 0);

        TestSystem.SpinWaitFor(() => actor.Succeeded == 10, TimeSpan.FromSeconds(10),
            $"only {actor.Succeeded} of 10 good jobs ran");

        var metrics = host.System.Metrics.Snapshot();
        Assert.Equal(10, metrics.TotalJobsFailed);
        Assert.Equal(20, metrics.TotalJobsExecuted);
    }

    [Fact]
    public void OnJobErrorOverrideSeesTheException()
    {
        using var host = new TestSystem(workers: 1);
        var actor = new ThrowingActor(host.Options());

        actor.Work(fail: true);

        TestSystem.SpinWaitFor(() => actor.Errors.Count == 1, TimeSpan.FromSeconds(5), "OnJobError not called");
        Assert.IsType<InvalidOperationException>(actor.Errors[0]);
    }

    [Fact]
    public void ActorFaultsAfterConsecutiveFailuresAndRecovers()
    {
        using var host = new TestSystem(workers: 1);
        DropReason? reason = null;
        var actor = new ThrowingActor(new JobOptions
        {
            System = host.System,
            MaxConsecutiveFailures = 3,
            OnDropped = (_, r) => reason = r,
        });

        for (var i = 0; i < 3; i++)
            actor.Work(fail: true);

        TestSystem.SpinWaitFor(() => actor.IsFaulted, TimeSpan.FromSeconds(5), "actor never faulted");

        Assert.False(actor.Work(fail: false));
        Assert.Equal(DropReason.Faulted, reason);
        Assert.Equal(1, host.System.Metrics.Snapshot().ActorsFaulted);

        actor.ClearFault();
        Assert.False(actor.IsFaulted);
        Assert.True(actor.Work(fail: false));
    }

    [Fact]
    public void SuccessResetsTheConsecutiveFailureCount()
    {
        using var host = new TestSystem(workers: 1);
        var actor = new ThrowingActor(new JobOptions { System = host.System, MaxConsecutiveFailures = 3 });

        for (var i = 0; i < 10; i++)
        {
            actor.Work(fail: true);
            actor.Work(fail: true);
            actor.Work(fail: false);
        }

        TestSystem.SpinWaitFor(() => actor.RemainingTaskCount == 0, TimeSpan.FromSeconds(10), "queue did not drain");
        Assert.False(actor.IsFaulted);
    }

    private sealed class ThrowingActor(JobOptions options) : AsyncExecutable(options)
    {
        private int _succeeded;
        public int Succeeded => Volatile.Read(ref _succeeded);
        public List<Exception> Errors { get; } = [];

        public bool Work(bool fail) => DoAsync(static t => t.Self.Run(t.Fail), (Self: this, Fail: fail));

        private void Run(bool fail)
        {
            if (fail)
                throw new InvalidOperationException("boom");
            Interlocked.Increment(ref _succeeded);
        }

        protected override void OnJobError(Exception exception)
        {
            lock (Errors)
            {
                Errors.Add(exception);
            }
        }
    }
}

public sealed class PoolTests
{
    [Fact]
    public void JobsAreReturnedToThePoolAfterRunning()
    {
        using var host = new TestSystem(workers: 1);
        var actor = new NoopActor(host.Options());

        for (var i = 0; i < 1_000; i++)
            actor.Ping();

        TestSystem.SpinWaitFor(() => actor.RemainingTaskCount == 0, TimeSpan.FromSeconds(10), "queue did not drain");
        Assert.True(Job.PoolSize > 0, "nothing was pooled");
    }

    [Fact]
    public void RejectedJobsAreRecycledNotLeaked()
    {
        using var host = new TestSystem(workers: 1);
        var actor = new BlockingActor(host.Options(maxQueue: 1, mode: ExecutionMode.Scheduled));
        actor.BlockAndWait();

        var before = Job.PoolSize;
        for (var i = 0; i < 500; i++)
            actor.Enqueue();

        // The rejected jobs go back to the pool instead of being handed to the GC.
        Assert.True(Job.PoolSize >= before, $"pool shrank from {before} to {Job.PoolSize}");
        actor.Release();
    }

    [Fact]
    public void PoolIsCappedByMaxPoolSize()
    {
        var original = Job.MaxPoolSize;
        try
        {
            Job.ClearPool();
            Job.MaxPoolSize = 8;

            using var host = new TestSystem(workers: 1);
            var actor = new NoopActor(host.Options());
            for (var i = 0; i < 500; i++)
                actor.Ping();

            TestSystem.SpinWaitFor(() => actor.RemainingTaskCount == 0, TimeSpan.FromSeconds(10), "queue did not drain");
            Assert.True(Job.PoolSize <= 8, $"pool grew to {Job.PoolSize}, above the cap of 8");
        }
        finally
        {
            Job.MaxPoolSize = original;
            Job.ClearPool();
        }
    }

    private sealed class NoopActor(JobOptions options) : AsyncExecutable(options)
    {
        public bool Ping() => DoAsync(() => { });
    }
}
