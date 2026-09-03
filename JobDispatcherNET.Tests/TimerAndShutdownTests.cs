using System.Diagnostics;
using Xunit;

namespace JobDispatcherNET.Tests;

public sealed class TimerTests
{
    [Fact]
    public void DelayedJobFiresAfterRoughlyTheRequestedDelay()
    {
        using var host = new TestSystem(workers: 2);
        var fired = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var actor = new TimerActor(host.Options(), fired);

        var started = Stopwatch.GetTimestamp();
        actor.After(TimeSpan.FromMilliseconds(200));

        Assert.True(fired.Task.Wait(TimeSpan.FromSeconds(5)), "timer never fired");
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        Assert.True(elapsed >= 150, $"fired {elapsed:F0}ms after scheduling, too early");
        Assert.True(elapsed < 2_000, $"fired {elapsed:F0}ms after scheduling, far too late");
    }

    [Fact]
    public void CancelledTimerNeverFires()
    {
        using var host = new TestSystem(workers: 2);
        var fired = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var actor = new TimerActor(host.Options(), fired);

        var handle = actor.After(TimeSpan.FromMilliseconds(300));
        Assert.True(handle.IsPending);
        Assert.True(handle.Cancel());
        Assert.False(handle.IsPending);
        Assert.False(handle.Cancel(), "a second Cancel must report that it did nothing");

        Assert.False(fired.Task.Wait(TimeSpan.FromMilliseconds(900)), "a cancelled timer fired anyway");
        Assert.Equal(1, host.System.Metrics.Snapshot().TimersCancelled);
    }

    [Fact]
    public void RepeatingTimerFiresRepeatedlyUntilCancelled()
    {
        using var host = new TestSystem(workers: 2);
        var actor = new TickActor(host.Options());

        var handle = actor.Every(TimeSpan.FromMilliseconds(50));
        TestSystem.SpinWaitFor(() => actor.Ticks >= 5, TimeSpan.FromSeconds(10),
            $"only {actor.Ticks} ticks");

        Assert.True(handle.Cancel());
        var afterCancel = actor.Ticks;
        Thread.Sleep(300);
        Assert.True(actor.Ticks <= afterCancel + 1, "the timer kept firing after cancellation");
    }

    [Fact]
    public void RepeatingTimerKeepsGoingAfterAThrowingTick()
    {
        using var host = new TestSystem(workers: 2);
        var actor = new TickActor(host.Options()) { ThrowEveryTick = true };

        using var handle = new TimerScope(actor.Every(TimeSpan.FromMilliseconds(30)));
        TestSystem.SpinWaitFor(() => actor.Attempts >= 5, TimeSpan.FromSeconds(10),
            $"timer stopped after {actor.Attempts} attempts");
    }

    [Fact]
    public void TimerJobsAreSerializedWithOrdinaryJobs()
    {
        using var host = new TestSystem(workers: 4);
        var actor = new CountingActor(host.Options());

        for (var i = 0; i < 200; i++)
            actor.DoAsyncAfter(TimeSpan.FromMilliseconds(20), static a => a.Bump(), actor);

        for (var i = 0; i < 200; i++)
            actor.Bump();

        TestSystem.SpinWaitFor(() => actor.Executed == 400, TimeSpan.FromSeconds(20),
            $"executed {actor.Executed} of 400");
        Assert.Equal(1, actor.MaxConcurrent);
        Assert.Equal(400, actor.State);
    }

    [Fact]
    public void TimersRunOnWorkerThreadsWhenWorkersExist()
    {
        using var host = new TestSystem(workers: 2);
        var fired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var actor = new ThreadProbeActor(host.Options(), fired);

        actor.After(TimeSpan.FromMilliseconds(50));

        Assert.True(fired.Task.Wait(TimeSpan.FromSeconds(5)), "timer never fired");
        Assert.True(fired.Task.Result, "the timer job did not run on a worker thread");
    }

    [Fact]
    public void CancellingAOneShotAfterItFiredButBeforeItRanStopsTheCallback()
    {
        // A6: the timer thread hands the callback to the actor as an ordinary job. While that job
        // waits its turn the callback has not run, so a cancel must still be able to claim it.
        using var host = new TestSystem(workers: 1);
        var actor = new BlockingActor(host.Options(mode: ExecutionMode.Scheduled));
        actor.BlockAndWait();                       // nothing behind the blocked job can run

        var ran = 0;
        var handle = actor.DoAsyncAfter(TimeSpan.FromMilliseconds(20), () => Interlocked.Increment(ref ran));

        TestSystem.SpinWaitFor(() => host.System.PendingTimerCount == 0, TimeSpan.FromSeconds(5),
            "the timer never fired");
        Assert.True(handle.IsPending, "a fired-but-not-yet-run timer is still pending");

        Assert.True(handle.Cancel(), "a fired-but-not-yet-run timer must still be cancellable");
        Assert.False(handle.IsPending);
        Assert.False(handle.Cancel(), "a second Cancel must report that it did nothing");

        actor.Release();
        TestSystem.SpinWaitFor(() => actor.RemainingTaskCount == 0, TimeSpan.FromSeconds(5),
            "the actor never drained");

        Assert.Equal(0, Volatile.Read(ref ran));
    }

    [Fact]
    public void CancellingARepeatingTimerDropsATickAlreadyQueuedOnTheActor()
    {
        // A6 for repeating timers: this is the despawn case. An entity cancels its AI tick and the
        // tick already sitting on its queue used to run anyway, against a dead entity.
        using var host = new TestSystem(workers: 1);
        var actor = new BlockingActor(host.Options(mode: ExecutionMode.Scheduled));
        actor.BlockAndWait();

        var ticks = 0;
        var handle = actor.DoAsyncEvery(TimeSpan.FromMilliseconds(10), () => Interlocked.Increment(ref ticks));

        TestSystem.SpinWaitFor(() => actor.RemainingTaskCount >= 2, TimeSpan.FromSeconds(5),
            "no tick ever queued up behind the blocked job");

        Assert.True(handle.Cancel());

        actor.Release();
        TestSystem.SpinWaitFor(() => actor.RemainingTaskCount == 0, TimeSpan.FromSeconds(5),
            "the actor never drained");

        Assert.Equal(0, Volatile.Read(ref ticks));
        Assert.Equal(0, host.System.PendingTimerCount);
    }

    [Fact]
    public async Task ARepeatingTimerRetiresItselfWhenItsActorIsDisposed()
    {
        // A12: a repeating timer on a disposed actor fired into a closed door once a period, adding
        // a drop each time and holding PendingTimerCount above zero — which is what made StopAsync
        // burn its whole drain timeout.
        using var host = new TestSystem(workers: 2);
        var actor = new TickActor(host.Options());
        var handle = actor.Every(TimeSpan.FromMilliseconds(10));

        TestSystem.SpinWaitFor(() => actor.Ticks >= 2, TimeSpan.FromSeconds(5), "the timer never ticked");

        await actor.DisposeAsync(TimeSpan.FromSeconds(5));

        TestSystem.SpinWaitFor(() => host.System.PendingTimerCount == 0, TimeSpan.FromSeconds(5),
            "the repeating timer kept firing into the disposed actor");

        Assert.False(handle.IsPending);
        Assert.True(await host.System.DrainAsync(TimeSpan.FromSeconds(2)),
            "the drain was still waiting on the retired timer");
    }

    [Fact]
    public void ABrokenLoggerDoesNotStopTheTimerThread()
    {
        // A4: with no workers the timer thread also runs the jobs, so both the watchdog warning and
        // the timer-fallback warning are logged from it. One throw used to kill it silently, and
        // with it every timer on the system for the life of the process.
        var system = new JobSystem(new JobSystemOptions
        {
            Name = "broken-logger",
            Logger = new ThrowingJobLogger(),
            PublishMeter = false,
            MaxJobDuration = TimeSpan.FromTicks(1),      // every job trips the watchdog -> Logger.Warn
        });

        var actor = new TickActor(new JobOptions { System = system });
        var handle = actor.Every(TimeSpan.FromMilliseconds(5));

        TestSystem.SpinWaitFor(() => actor.Ticks >= 5, TimeSpan.FromSeconds(10),
            $"the timer thread stopped after {actor.Ticks} ticks; the logger took it down");

        Assert.True(ThrowingJobLogger.Calls > 0, "the logger was never reached, so nothing was proven");

        handle.Cancel();
        system.Dispose();
    }

    [Fact]
    public void ABrokenLoggerDoesNotWedgeAnActor()
    {
        // The same throw inside a flush used to escape before the counter was decremented, leaving
        // the actor stuck at "somebody is flushing me" with no leader.
        using var host = new TestSystem(workers: 2, options: new JobSystemOptions
        {
            Name = "broken-logger-flush",
            Logger = new ThrowingJobLogger(),
            PublishMeter = false,
            DetectBlockingWaitOnWorker = false,
            MaxJobDuration = TimeSpan.FromTicks(1),
        });

        var actor = new CountingActor(host.Options());
        for (var i = 0; i < 200; i++)
            Assert.True(actor.Bump());

        TestSystem.SpinWaitFor(() => actor.Executed == 200, TimeSpan.FromSeconds(10),
            $"only {actor.Executed} of 200 jobs ran");
        TestSystem.SpinWaitFor(() => actor.RemainingTaskCount == 0, TimeSpan.FromSeconds(10),
            $"the actor stayed at depth {actor.RemainingTaskCount} after draining");
    }

    private sealed class ThrowingJobLogger : IJobLogger
    {
        private static int _calls;

        public static int Calls => Volatile.Read(ref _calls);

        public bool IsEnabled(JobLogLevel level) => true;

        public void Log(JobLogLevel level, string message, Exception? exception = null)
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException("the log sink is down");
        }
    }

    private sealed class TimerActor(JobOptions options, TaskCompletionSource<long> sink) : AsyncExecutable(options)
    {
        public ITimerHandle After(TimeSpan delay) => DoAsyncAfter(delay, static a => a.Fire(), this);
        private void Fire() => sink.TrySetResult(Environment.TickCount64);
    }

    private sealed class ThreadProbeActor(JobOptions options, TaskCompletionSource<bool> sink) : AsyncExecutable(options)
    {
        public ITimerHandle After(TimeSpan delay) => DoAsyncAfter(delay, static a => a.Fire(), this);
        private void Fire() => sink.TrySetResult(JobDiagnostics.IsWorkerThread);
    }

    private sealed class TickActor(JobOptions options) : AsyncExecutable(options)
    {
        private int _ticks;
        private int _attempts;

        public bool ThrowEveryTick { get; init; }
        public int Ticks => Volatile.Read(ref _ticks);
        public int Attempts => Volatile.Read(ref _attempts);

        public ITimerHandle Every(TimeSpan period) => DoAsyncEvery(period, Tick, period);

        private void Tick()
        {
            Interlocked.Increment(ref _attempts);
            if (ThrowEveryTick)
                throw new InvalidOperationException("tick failed");
            Interlocked.Increment(ref _ticks);
        }
    }

    private sealed class TimerScope(ITimerHandle handle) : IDisposable
    {
        public void Dispose() => handle.Cancel();
    }
}

public sealed class ShutdownTests
{
    [Fact]
    public async Task DisposeAsyncWaitsForTheQueueToDrain()
    {
        using var host = new TestSystem(workers: 2);
        var actor = new SlowActor(host.Options());

        for (var i = 0; i < 50; i++)
            Assert.True(actor.Work());

        await actor.DisposeAsync();

        Assert.Equal(50, actor.Done);
        Assert.Equal(0, actor.RemainingTaskCount);
        Assert.False(actor.Work(), "a disposed actor must refuse new work");
    }

    [Fact]
    public async Task StopAsyncDrainsCascadingWorkThenStopsWorkers()
    {
        var system = new JobSystem(new JobSystemOptions
        {
            Name = "stop",
            Logger = NullJobLogger.Instance,
            PublishMeter = false,
        });

        var dispatcher = new JobDispatcher(4, new JobDispatcherOptions { System = system, IdleWaitMs = 5 });
        _ = dispatcher.RunWorkerThreadsAsync();
        TestSystem.SpinWaitFor(() => system.LiveWorkerCount == 4, TimeSpan.FromSeconds(5), "workers did not start");

        var sink = new SlowActor(new JobOptions { System = system });
        var fan = new FanOutActor(new JobOptions { System = system }, sink);
        Assert.True(fan.Start(200));

        var drained = await system.StopAsync(TimeSpan.FromSeconds(30));

        Assert.True(drained, "shutdown timed out instead of draining");
        Assert.Equal(200, sink.Done);
        Assert.Equal(0, system.Metrics.Snapshot().TotalJobsDropped);
        Assert.False(system.AcceptingWork);
        TestSystem.SpinWaitFor(() => system.LiveWorkerCount == 0, TimeSpan.FromSeconds(5), "workers did not stop");

        system.Dispose();
    }

    [Fact]
    public async Task StopAsyncWithRefuseNewWorkClosesTheDoorFirst()
    {
        var system = new JobSystem(new JobSystemOptions
        {
            Name = "stop-refuse",
            Logger = NullJobLogger.Instance,
            PublishMeter = false,
        });
        using var dispatcher = new JobDispatcher(2, new JobDispatcherOptions { System = system, IdleWaitMs = 5 });
        _ = dispatcher.RunWorkerThreadsAsync();

        var actor = new SlowActor(new JobOptions { System = system });
        await system.StopAsync(TimeSpan.FromSeconds(5), refuseNewWork: true);

        Assert.False(actor.Work());
        system.Dispose();
    }

    [Fact]
    public void TryStopReportsWhetherWorkersActuallyStopped()
    {
        var system = new JobSystem(new JobSystemOptions
        {
            Name = "trystop",
            Logger = NullJobLogger.Instance,
            PublishMeter = false,
        });
        var dispatcher = new JobDispatcher(2, new JobDispatcherOptions { System = system, IdleWaitMs = 5 });
        _ = dispatcher.RunWorkerThreadsAsync();
        TestSystem.SpinWaitFor(() => system.LiveWorkerCount == 2, TimeSpan.FromSeconds(5), "workers did not start");

        Assert.True(dispatcher.TryStop(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, dispatcher.LiveWorkerCount);
        system.Dispose();
    }

    [Fact]
    public async Task DisposeAsyncNeverMissesTheDrainSignal()
    {
        // A1: the disposer publishes _drainTcs and then reads the job counter, while the worker
        // decrements the counter and then reads _drainTcs. Without a full fence on the disposer's
        // side the store-load reordering lets both sides miss each other and the await never ends.
        using var host = new TestSystem(workers: 4);

        var racers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            for (var round = 0; round < 500; round++)
            {
                var actor = new SlowActor(host.Options(mode: ExecutionMode.Scheduled));
                Assert.True(actor.Work());

                Assert.True(await actor.DisposeAsync(TimeSpan.FromSeconds(10)),
                    $"round {round}: DisposeAsync did not observe the drain signal");
            }
        })).ToArray();

        await Task.WhenAll(racers);
    }

    [Fact]
    public async Task DisposeAsyncWithATimeoutGivesUpInsteadOfHanging()
    {
        using var host = new TestSystem(workers: 1);
        var actor = new BlockingActor(host.Options(mode: ExecutionMode.Scheduled));
        actor.BlockAndWait();

        Assert.False(await actor.DisposeAsync(TimeSpan.FromMilliseconds(200)),
            "DisposeAsync claimed a clean drain while a job was still running");
        Assert.False(actor.Enqueue(), "a disposed actor must refuse new work");

        actor.Release();
        TestSystem.SpinWaitFor(() => actor.Done == 1, TimeSpan.FromSeconds(5),
            "the blocked job never finished");
    }

    [Fact]
    public async Task DrainWaitsForInterleavedAsyncJobsThatAreStillAwaiting()
    {
        // A3: an interleaved job parked on an await holds no queue slot, no ready-queue entry and
        // no timer, so the drain used to report success and stop the workers under it.
        using var host = new TestSystem(workers: 2);
        var actor = new SlowActor(host.Options());

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = actor.RunAsync(async () => await gate.Task);

        TestSystem.SpinWaitFor(() => actor.RemainingTaskCount == 0, TimeSpan.FromSeconds(5),
            "the async job never reached its await");

        Assert.Equal(1, host.System.PendingAsyncJobs);
        Assert.Equal(1, actor.PendingAsyncJobs);
        Assert.Equal(0, host.System.InFlightJobs);      // invisible to every other counter

        Assert.False(await host.System.DrainAsync(TimeSpan.FromMilliseconds(300)),
            "the drain reported success while an async job was still awaiting");

        gate.SetResult();
        await pending.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(await host.System.DrainAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, host.System.PendingAsyncJobs);
    }

    [Fact]
    public async Task DisposeAsyncWaitsForAnAsyncJobThatIsStillAwaiting()
    {
        using var host = new TestSystem(workers: 2);
        var actor = new SlowActor(host.Options());

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = actor.RunAsync(async () => await gate.Task);

        TestSystem.SpinWaitFor(() => actor.RemainingTaskCount == 0, TimeSpan.FromSeconds(5),
            "the async job never reached its await");

        var dispose = actor.DisposeAsync(TimeSpan.FromSeconds(10)).AsTask();
        await Task.Delay(100);
        Assert.False(dispose.IsCompleted, "DisposeAsync returned while an async job was still awaiting");

        gate.SetResult();
        await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(await dispose.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    private sealed class SlowActor(JobOptions options) : AsyncExecutable(options)
    {
        private int _done;
        public int Done => Volatile.Read(ref _done);

        public bool Work() => DoAsync(static a => a.Run(), this);

        private void Run()
        {
            Thread.SpinWait(2_000);
            Interlocked.Increment(ref _done);
        }
    }

    private sealed class FanOutActor(JobOptions options, SlowActor sink) : AsyncExecutable(options)
    {
        public bool Start(int count) => DoAsync(static t => t.Self.Fan(t.Count), (Self: this, Count: count));

        private void Fan(int count)
        {
            for (var i = 0; i < count; i++)
                sink.Work();
        }
    }
}
