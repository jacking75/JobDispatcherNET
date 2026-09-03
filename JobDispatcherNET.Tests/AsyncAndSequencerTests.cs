using Xunit;

namespace JobDispatcherNET.Tests;

public sealed class AskTests
{
    [Fact]
    public async Task AskReturnsTheValueComputedOnTheActor()
    {
        using var host = new TestSystem(workers: 2);
        var actor = new CounterActor(host.Options());

        for (var i = 0; i < 100; i++)
            actor.Add(1);

        var value = await actor.Ask(() => actor.Value);
        Assert.Equal(100, value);
    }

    [Fact]
    public async Task AskPropagatesExceptions()
    {
        using var host = new TestSystem(workers: 1);
        var actor = new CounterActor(host.Options());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => actor.Ask<int>(() => throw new InvalidOperationException("nope")));
    }

    [Fact]
    public async Task AskOnAFullQueueFaultsInsteadOfHanging()
    {
        using var host = new TestSystem(workers: 1);
        var blocker = new BlockingActor(host.Options(maxQueue: 1, mode: ExecutionMode.Scheduled));
        blocker.BlockAndWait();   // the single slot is taken until we release it

        await Assert.ThrowsAsync<JobRejectedException>(() => blocker.Ask(() => 1));
        blocker.Release();
    }

    [Fact]
    public void AskSyncFromInsideAJobThrowsInsteadOfDeadlocking()
    {
        using var system = new JobSystem(new JobSystemOptions
        {
            Name = "guard",
            Logger = NullJobLogger.Instance,
            PublishMeter = false,
            DetectBlockingWaitOnWorker = true,
        });
        using var dispatcher = new JobDispatcher(2, new JobDispatcherOptions { System = system, IdleWaitMs = 5 });
        _ = dispatcher.RunWorkerThreadsAsync();

        var target = new CounterActor(new JobOptions { System = system });
        var caller = new ReentrantActor(new JobOptions { System = system }, target);

        caller.TryBlockingAsk();

        TestSystem.SpinWaitFor(() => caller.Caught is not null, TimeSpan.FromSeconds(5),
            "the blocking-wait guard never fired");
        Assert.IsType<InvalidOperationException>(caller.Caught);
        Assert.Contains("deadlock", caller.Caught!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AskSyncFromOutsideAnActorWorks()
    {
        using var host = new TestSystem(workers: 2);
        var actor = new CounterActor(host.Options());
        actor.Add(7);

        Assert.Equal(7, actor.AskSync(() => actor.Value, TimeSpan.FromSeconds(5)));
    }

    private sealed class CounterActor(JobOptions options) : AsyncExecutable(options)
    {
        private int _value;
        public int Value => _value;
        public bool Add(int amount) => DoAsync(static t => t.Self.Apply(t.Amount), (Self: this, Amount: amount));
        private void Apply(int amount) => _value += amount;
    }

    private sealed class ReentrantActor(JobOptions options, AsyncExecutable target) : AsyncExecutable(options)
    {
        public Exception? Caught { get; private set; }

        public bool TryBlockingAsk() => DoAsync(static a => a.Run(), this);

        private void Run()
        {
            try
            {
                JobDiagnostics.GuardBlockingWait(target.System, "test");
            }
            catch (Exception ex)
            {
                Caught = ex;
            }
        }
    }
}

public sealed class AsyncJobTests
{
    [Fact]
    public async Task InterleavedContinuationsComeBackOntoTheActor()
    {
        using var host = new TestSystem(workers: 4);
        var actor = new AsyncActor(host.Options());

        await actor.RunAsync();

        Assert.True(actor.ResumedOnActor, "the continuation did not resume on the actor");
        Assert.Equal(1, actor.MaxConcurrent);
    }

    [Fact]
    public async Task AsyncAskReturnsTheAwaitedResult()
    {
        using var host = new TestSystem(workers: 2);
        var actor = new AsyncActor(host.Options());

        var value = await actor.AskAsync(async () =>
        {
            await Task.Delay(20);
            return 42;
        });

        Assert.Equal(42, value);
    }

    [Fact]
    public async Task AsyncJobExceptionsSurfaceOnTheReturnedTask()
    {
        using var host = new TestSystem(workers: 2);
        var actor = new AsyncActor(host.Options());

        await Assert.ThrowsAsync<InvalidOperationException>(() => actor.RunAsync(async () =>
        {
            await Task.Delay(10);
            throw new InvalidOperationException("async boom");
        }));
    }

    [Fact]
    public async Task ExclusiveReentrancyBlocksOtherJobsUntilTheAwaitCompletes()
    {
        using var host = new TestSystem(workers: 4);
        var actor = new ExclusiveActor(new JobOptions
        {
            System = host.System,
            AsyncReentrancy = AsyncReentrancy.Exclusive,
        });

        var pending = actor.SlowAsync(TimeSpan.FromMilliseconds(300));

        // Queue plain jobs while the async one is still awaiting.
        for (var i = 0; i < 20; i++)
            Assert.True(actor.Quick());

        Assert.Equal(0, actor.QuickDone);   // nothing may run yet

        await pending;

        TestSystem.SpinWaitFor(() => actor.QuickDone == 20, TimeSpan.FromSeconds(10),
            $"only {actor.QuickDone} of 20 queued jobs ran after the await finished");
        Assert.Equal(0, actor.RemainingTaskCount);
    }

    [Fact]
    public async Task ExclusiveActorRecoversWhenTheAsyncJobThrows()
    {
        using var host = new TestSystem(workers: 2);
        var actor = new ExclusiveActor(new JobOptions
        {
            System = host.System,
            AsyncReentrancy = AsyncReentrancy.Exclusive,
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => actor.FailAsync());

        Assert.True(actor.Quick());
        TestSystem.SpinWaitFor(() => actor.QuickDone == 1, TimeSpan.FromSeconds(5),
            "the actor stayed suspended after a failed async job");
    }

    [Fact]
    public async Task InterleavedContinuationIsNeverRefusedByQueueBound()
    {
        // A2: the continuation of an await re-enters the actor as an ordinary job. When the bound
        // rejected it the async state machine stopped for good and RunAsync's task never completed.
        using var host = new TestSystem(workers: 2);
        var actor = new BlockingActor(host.Options(maxQueue: 1, mode: ExecutionMode.Scheduled));

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = actor.RunAsync(async () => await gate.Task);

        // Parking on the await hands the queue slot back, so the actor looks idle again.
        TestSystem.SpinWaitFor(() => actor.RemainingTaskCount == 0, TimeSpan.FromSeconds(5),
            "the async job never reached its await");

        actor.BlockAndWait();                                       // the one allowed slot is taken
        Assert.False(actor.Enqueue(), "the queue bound was not in force");

        gate.SetResult();                                           // continuation posted onto a full queue

        TestSystem.SpinWaitFor(() => actor.RemainingTaskCount == 2, TimeSpan.FromSeconds(5),
            "the continuation was refused instead of being let past the bound");

        actor.Release();
        await pending.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task InterleavedContinuationRunsEvenAfterTheActorFaults()
    {
        using var host = new TestSystem(workers: 2);
        var actor = new FaultingActor(new JobOptions
        {
            System = host.System,
            Mode = ExecutionMode.Scheduled,
            MaxConsecutiveFailures = 1,
        });

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = actor.RunAsync(async () => await gate.Task);

        TestSystem.SpinWaitFor(() => actor.RemainingTaskCount == 0, TimeSpan.FromSeconds(5),
            "the async job never reached its await");

        Assert.True(actor.Boom());
        TestSystem.SpinWaitFor(() => actor.IsFaulted, TimeSpan.FromSeconds(5), "the actor never faulted");
        Assert.False(actor.Boom(), "a faulted actor must still refuse new work");

        gate.SetResult();
        await pending.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private sealed class FaultingActor(JobOptions options) : AsyncExecutable(options)
    {
        public bool Boom() => DoAsync(static () => throw new InvalidOperationException("boom"));

        protected override void OnJobError(Exception exception)
        {
            // The failure is the point of the test; swallow it.
        }
    }

    private sealed class AsyncActor(JobOptions options) : AsyncExecutable(options)
    {
        private int _concurrent;
        private int _maxConcurrent;

        public bool ResumedOnActor { get; private set; }
        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

        public Task RunAsync() => base.RunAsync(async () =>
        {
            Track();
            await Task.Delay(30);
            ResumedOnActor = ReferenceEquals(JobDiagnostics.CurrentActor, this);
            Track();
        });

        private void Track()
        {
            var now = Interlocked.Increment(ref _concurrent);
            if (now > Volatile.Read(ref _maxConcurrent))
                Volatile.Write(ref _maxConcurrent, now);
            Interlocked.Decrement(ref _concurrent);
        }
    }

    private sealed class ExclusiveActor(JobOptions options) : AsyncExecutable(options)
    {
        private int _quickDone;
        public int QuickDone => Volatile.Read(ref _quickDone);

        public Task SlowAsync(TimeSpan delay) => RunAsync(async () => await Task.Delay(delay));

        public Task FailAsync() => RunAsync(async () =>
        {
            await Task.Delay(20);
            throw new InvalidOperationException("exclusive boom");
        });

        public bool Quick() => DoAsync(static a => a.Bump(), this);

        private void Bump() => Interlocked.Increment(ref _quickDone);
    }
}

public sealed class SequencerTests
{
    [Fact]
    public void ItemsAreHandledInArrivalOrderByOneThreadAtATime()
    {
        using var host = new TestSystem(workers: 8);

        var seen = new List<int>();
        var concurrent = 0;
        var maxConcurrent = 0;
        var done = new ManualResetEventSlim(false);

        var sequencer = new Sequencer<int>(host.System, item =>
        {
            var now = Interlocked.Increment(ref concurrent);
            if (now > Volatile.Read(ref maxConcurrent))
                Volatile.Write(ref maxConcurrent, now);

            seen.Add(item);
            if (item == 9_999)
                done.Set();

            Interlocked.Decrement(ref concurrent);
        });

        for (var i = 0; i < 10_000; i++)
            Assert.True(sequencer.Enqueue(i));

        Assert.True(done.Wait(TimeSpan.FromSeconds(30)), "the sequencer never finished");

        Assert.Equal(1, maxConcurrent);
        Assert.Equal(10_000, seen.Count);
        for (var i = 0; i < seen.Count; i++)
            Assert.Equal(i, seen[i]);
    }

    [Fact]
    public void EnqueueAfterStopIsRefused()
    {
        using var host = new TestSystem(workers: 2);
        var sequencer = new Sequencer<int>(host.System, _ => { });

        sequencer.Stop();

        Assert.False(sequencer.Enqueue(1));
        Assert.True(sequencer.IsStopped);
    }

    [Fact]
    public void AbortDiscardsWhatIsLeft()
    {
        using var host = new TestSystem(workers: 0);   // nothing drains
        var sequencer = new Sequencer<int>(host.System, _ => { });

        for (var i = 0; i < 10; i++)
            sequencer.Enqueue(i);

        var discarded = sequencer.Abort();

        Assert.Equal(10, discarded);
        Assert.Equal(0, sequencer.PendingCount);
        Assert.False(sequencer.Enqueue(99));
    }

    [Fact]
    public void MultipleProducersStillProduceOneSerialStream()
    {
        using var host = new TestSystem(workers: 8);

        var handled = 0;
        var concurrent = 0;
        var maxConcurrent = 0;

        var sequencer = new Sequencer<int>(host.System, _ =>
        {
            var now = Interlocked.Increment(ref concurrent);
            if (now > Volatile.Read(ref maxConcurrent))
                Volatile.Write(ref maxConcurrent, now);
            Interlocked.Increment(ref handled);
            Interlocked.Decrement(ref concurrent);
        });

        Parallel.For(0, 8, p =>
        {
            for (var i = 0; i < 5_000; i++)
                sequencer.Enqueue((p * 5_000) + i);
        });

        TestSystem.SpinWaitFor(() => Volatile.Read(ref handled) == 40_000, TimeSpan.FromSeconds(60),
            $"handled {handled} of 40000");
        Assert.Equal(1, maxConcurrent);
    }
}
