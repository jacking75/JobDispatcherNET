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
