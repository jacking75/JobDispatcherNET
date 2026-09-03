using Xunit;

namespace JobDispatcherNET.Tests;

public sealed class BoundedQueueTests
{
    [Fact]
    public void AnActorWithNoBoundOfItsOwnInheritsTheSystemDefault()
    {
        // B5: the per-actor bound only helps on actors somebody remembered to configure.
        using var host = new TestSystem(workers: 1, options: new JobSystemOptions
        {
            Name = "default-bound",
            Logger = NullJobLogger.Instance,
            PublishMeter = false,
            DetectBlockingWaitOnWorker = false,
            DefaultMaxQueueSize = 2,
        });

        var actor = new BlockingActor(new JobOptions { System = host.System, Mode = ExecutionMode.Scheduled });
        Assert.Equal(2, actor.MaxQueueSize);

        actor.BlockAndWait();                       // one of the two slots is taken
        Assert.True(actor.Enqueue());
        Assert.False(actor.Enqueue(), "the system default bound was not applied");

        actor.Release();
        TestSystem.SpinWaitFor(() => actor.Done == 2, TimeSpan.FromSeconds(5), "the actor never drained");
    }

    [Fact]
    public void AnActorsOwnBoundBeatsTheSystemDefault()
    {
        using var host = new TestSystem(workers: 1, options: new JobSystemOptions
        {
            Name = "default-bound-override",
            Logger = NullJobLogger.Instance,
            PublishMeter = false,
            DetectBlockingWaitOnWorker = false,
            DefaultMaxQueueSize = 2,
        });

        var actor = new GateActor(new JobOptions { System = host.System, MaxQueueSize = 64 });
        Assert.Equal(64, actor.MaxQueueSize);
    }

    [Fact]
    public void AnActorNameCannotForgeALogLine()
    {
        // B3: a server that names actors after player nicknames would otherwise let a newline in a
        // nickname write a whole extra log entry.
        using var host = new TestSystem(workers: 0);

        var actor = new GateActor(host.Options() with
        {
            Name = "player\r\n[JobDispatcherNET][Error] forged" + new string('x', 500),
        });

        Assert.DoesNotContain(actor.Name, c => char.IsControl(c));
        Assert.True(actor.Name.Length <= 128, $"name is {actor.Name.Length} characters");
        Assert.StartsWith("player??", actor.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void PostReportsThatAStoppedSystemWillNotRunTheAction()
    {
        // B2: work posted past the shutdown door used to pile up on a queue with nothing left to
        // drain it, and the caller was never told.
        using var host = new TestSystem(workers: 1);

        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(host.System.Post(ran.SetResult));
        Assert.True(ran.Task.Wait(TimeSpan.FromSeconds(5)), "the posted action never ran");
        TestSystem.SpinWaitFor(() => host.System.ReadyQueueDepth == 0, TimeSpan.FromSeconds(5),
            "the ready queue never settled");

        host.System.AcceptingWork = false;
        Assert.False(host.System.Post(static () => { }), "Post accepted work after the gate closed");
        Assert.Equal(0, host.System.ReadyQueueDepth);
    }

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
    public void RentingAndRecyclingOnOneThreadStopsAllocating()
    {
        // The pool's whole job. Renting and recycling on the same thread is the LeaderFlush and
        // actor-to-actor path, and it must not touch the shared pool — or allocate — at all.
        using var host = new TestSystem(workers: 0);     // no workers: the caller flushes inline
        var actor = new NoopActor(host.Options());

        for (var i = 0; i < 1_000; i++)                  // warm this thread's local stack
            Assert.True(actor.Ping());

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
            actor.Ping();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 64 * 1024,
            $"{allocated} bytes for 10,000 jobs; unpooled this path allocates about 320,000");
    }

    [Fact]
    public void JobsRecycledOnAWorkerReachTheSharedPool()
    {
        // The asymmetric path: this thread only rents, the workers only recycle. They meet through
        // the shared pool, in batches.
        var original = Job.MaxPoolSize;
        try
        {
            Job.ClearPool();

            using var host = new TestSystem(workers: 2);
            var actor = new NoopActor(host.Options(mode: ExecutionMode.Scheduled));

            for (var i = 0; i < 50_000; i++)
                Assert.True(actor.Ping());

            TestSystem.SpinWaitFor(() => actor.RemainingTaskCount == 0, TimeSpan.FromSeconds(30),
                "queue did not drain");
            Assert.True(Job.PoolSize > 0, "the workers never handed a batch to the shared pool");
        }
        finally
        {
            Job.MaxPoolSize = original;
            Job.ClearPool();
        }
    }

    [Fact]
    public void RejectedJobsAreRecycledNotLeaked()
    {
        using var host = new TestSystem(workers: 1);
        var actor = new BlockingActor(host.Options(maxQueue: 1, mode: ExecutionMode.Scheduled));
        actor.BlockAndWait();

        for (var i = 0; i < 1_000; i++)
            Assert.False(actor.Enqueue(), "the bound let a job through, so nothing was refused");

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
            actor.Enqueue();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // A refused job is recycled rather than handed to the GC.
        Assert.True(allocated < 64 * 1024,
            $"{allocated} bytes for 10,000 refused jobs; unrecycled they cost about 320,000");

        actor.Release();
    }

    [Fact]
    public void TheSharedPoolIsCappedByMaxPoolSize()
    {
        var original = Job.MaxPoolSize;
        try
        {
            Job.ClearPool();
            Job.MaxPoolSize = 64;

            using var host = new TestSystem(workers: 2);
            var actor = new NoopActor(host.Options(mode: ExecutionMode.Scheduled));
            for (var i = 0; i < 50_000; i++)
                actor.Ping();

            TestSystem.SpinWaitFor(() => actor.RemainingTaskCount == 0, TimeSpan.FromSeconds(30),
                "queue did not drain");
            Assert.True(Job.PoolSize <= 64, $"shared pool grew to {Job.PoolSize}, above the cap of 64");
        }
        finally
        {
            Job.MaxPoolSize = original;
            Job.ClearPool();
        }
    }

    [Fact]
    public void MaxPoolSizeZeroTurnsPoolingOff()
    {
        var original = Job.MaxPoolSize;
        try
        {
            Job.MaxPoolSize = 0;
            Job.ClearPool();

            using var host = new TestSystem(workers: 0);
            var actor = new NoopActor(host.Options());

            for (var i = 0; i < 1_000; i++)
                actor.Ping();

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 10_000; i++)
                actor.Ping();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(allocated > 100_000,
                $"only {allocated} bytes for 10,000 jobs; pooling was supposed to be off");
            Assert.Equal(0, Job.PoolSize);
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
