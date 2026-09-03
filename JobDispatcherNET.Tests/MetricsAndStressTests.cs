using System.Diagnostics.Metrics;
using Xunit;

namespace JobDispatcherNET.Tests;

public sealed class MetricsTests
{
    [Fact]
    public void EachSystemTagsItsMeterWithItsOwnName()
    {
        // A8: two systems publish identical instrument names. Without a meter tag a collector has
        // no way to tell their series apart and shows them as one.
        var names = new HashSet<string>(StringComparer.Ordinal);

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (instrument.Meter.Name != JobMetrics.MeterName || instrument.Meter.Tags is null)
                    return;

                foreach (var tag in instrument.Meter.Tags)
                {
                    if (tag.Key == JobMetrics.SystemTagName && tag.Value is string name)
                        lock (names) { names.Add(name); }
                }
            },
        };
        listener.Start();

        using var alpha = new JobSystem(new JobSystemOptions { Name = "meter-alpha", Logger = NullJobLogger.Instance });
        using var beta = new JobSystem(new JobSystemOptions { Name = "meter-beta", Logger = NullJobLogger.Instance });

        lock (names)
        {
            Assert.Contains("meter-alpha", names);
            Assert.Contains("meter-beta", names);
        }
    }

    [Fact]
    public void ExecutedPlusDroppedAccountsForEveryAttempt()
    {
        using var host = new TestSystem(workers: 2);
        var actor = new NoopActor(host.Options(maxQueue: 4));

        const int attempts = 20_000;
        var accepted = 0;
        for (var i = 0; i < attempts; i++)
            if (actor.Ping())
                accepted++;

        TestSystem.SpinWaitFor(() => actor.RemainingTaskCount == 0, TimeSpan.FromSeconds(30), "queue did not drain");

        var m = host.System.Metrics.Snapshot();
        Assert.Equal(accepted, m.TotalJobsExecuted);
        Assert.Equal(attempts - accepted, m.TotalJobsDropped);
        Assert.Equal(0, m.InFlightJobs);
    }

    [Fact]
    public void SnapshotReportsLiveWorkers()
    {
        using var host = new TestSystem(workers: 3);
        Assert.Equal(3, host.System.Metrics.Snapshot().LiveWorkers);
    }

    [Fact]
    public void ResetZeroesTheCounters()
    {
        using var host = new TestSystem(workers: 1);
        var actor = new NoopActor(host.Options());
        for (var i = 0; i < 100; i++)
            actor.Ping();

        TestSystem.SpinWaitFor(() => actor.RemainingTaskCount == 0, TimeSpan.FromSeconds(10), "queue did not drain");
        Assert.True(host.System.Metrics.Snapshot().TotalJobsExecuted > 0);

        host.System.Metrics.ResetCounters();
        Assert.Equal(0, host.System.Metrics.Snapshot().TotalJobsExecuted);
    }

    [Fact]
    public void SystemsDoNotShareCounters()
    {
        using var a = new TestSystem(workers: 1);
        using var b = new TestSystem(workers: 1);

        var actor = new NoopActor(a.Options());
        for (var i = 0; i < 50; i++)
            actor.Ping();

        TestSystem.SpinWaitFor(() => actor.RemainingTaskCount == 0, TimeSpan.FromSeconds(10), "queue did not drain");

        Assert.True(a.System.Metrics.Snapshot().TotalJobsExecuted >= 50);
        Assert.Equal(0, b.System.Metrics.Snapshot().TotalJobsExecuted);
    }

    private sealed class NoopActor(JobOptions options) : AsyncExecutable(options)
    {
        public bool Ping() => DoAsync(static _ => { }, 0);
    }
}

public sealed class ExecutionModeTests
{
    [Fact]
    public void LeaderFlushRunsOnTheCallingThread()
    {
        using var host = new TestSystem(workers: 2);
        var actor = new ThreadRecordingActor(host.Options(mode: ExecutionMode.LeaderFlush));

        var callerThread = Environment.CurrentManagedThreadId;
        actor.Record();

        TestSystem.SpinWaitFor(() => actor.LastThreadId != 0, TimeSpan.FromSeconds(5), "job never ran");
        Assert.Equal(callerThread, actor.LastThreadId);
    }

    [Fact]
    public void ScheduledModeHandsWorkToAWorker()
    {
        using var host = new TestSystem(workers: 2);
        var actor = new ThreadRecordingActor(host.Options(mode: ExecutionMode.Scheduled));

        var callerThread = Environment.CurrentManagedThreadId;
        actor.Record();

        TestSystem.SpinWaitFor(() => actor.LastThreadId != 0, TimeSpan.FromSeconds(5), "job never ran");
        Assert.NotEqual(callerThread, actor.LastThreadId);
        Assert.True(actor.RanOnWorker, "the job did not run on a worker thread");
    }

    [Fact]
    public void ScheduledModeFallsBackToInlineWhenThereAreNoWorkers()
    {
        using var host = new TestSystem(workers: 0);
        var actor = new ThreadRecordingActor(host.Options(mode: ExecutionMode.Scheduled));

        actor.Record();
        Assert.NotEqual(0, actor.LastThreadId);
    }

    [Fact]
    public void MaxJobsPerFlushYieldsTheActorBackToThePool()
    {
        using var host = new TestSystem(workers: 2);
        var actor = new CountingActor(new JobOptions
        {
            System = host.System,
            Mode = ExecutionMode.Scheduled,
            MaxJobsPerFlush = 4,
        });

        for (var i = 0; i < 500; i++)
            Assert.True(actor.Bump());

        TestSystem.SpinWaitFor(() => actor.Executed == 500, TimeSpan.FromSeconds(30),
            $"executed {actor.Executed} of 500 — the actor was not rescheduled after yielding");
        Assert.Equal(1, actor.MaxConcurrent);
        Assert.Equal(500, actor.State);
    }

    [Fact]
    public void PostRunsAnActionOnAWorker()
    {
        using var host = new TestSystem(workers: 2);
        var ran = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        host.System.Post(() => ran.TrySetResult(JobDiagnostics.IsWorkerThread));

        Assert.True(ran.Task.Wait(TimeSpan.FromSeconds(5)), "the posted action never ran");
        Assert.True(ran.Task.Result, "the posted action did not run on a worker thread");
    }

    private sealed class ThreadRecordingActor(JobOptions options) : AsyncExecutable(options)
    {
        private int _threadId;
        public int LastThreadId => Volatile.Read(ref _threadId);
        public bool RanOnWorker { get; private set; }

        public bool Record() => DoAsync(static a => a.Capture(), this);

        private void Capture()
        {
            RanOnWorker = JobDiagnostics.IsWorkerThread;
            Volatile.Write(ref _threadId, Environment.CurrentManagedThreadId);
        }
    }
}

[Trait("Category", "Stress")]
public sealed class StressTests
{
    [Fact]
    public void ManyActorsUnderLoadStayCorrectAndDrain()
    {
        using var host = new TestSystem(workers: Math.Max(4, Environment.ProcessorCount));

        const int actorCount = 2_000;
        const int perActor = 500;

        var actors = new CountingActor[actorCount];
        for (var i = 0; i < actorCount; i++)
            actors[i] = new CountingActor(host.Options(maxQueue: 1_024, mode: ExecutionMode.Scheduled));

        var accepted = 0;
        Parallel.For(0, 8, _ =>
        {
            var local = 0;
            var rng = new Random(Environment.CurrentManagedThreadId);
            for (var i = 0; i < actorCount * perActor / 8; i++)
            {
                if (actors[rng.Next(actorCount)].Bump())
                    local++;
            }
            Interlocked.Add(ref accepted, local);
        });

        TestSystem.SpinWaitFor(() => actors.Sum(a => a.Executed) == accepted,
            TimeSpan.FromSeconds(120), "the system did not drain under load");

        foreach (var actor in actors)
        {
            Assert.Equal(1, actor.MaxConcurrent);
            Assert.Equal(actor.Executed, actor.State);
            Assert.Equal(0, actor.RemainingTaskCount);
        }

        Assert.Equal(0, host.System.InFlightJobs);
    }

    [Fact]
    public void TimerChainsUnderLoadDoNotLeak()
    {
        using var host = new TestSystem(workers: 4);

        const int actorCount = 200;
        var actors = Enumerable.Range(0, actorCount)
            .Select(_ => new TickingActor(host.Options(mode: ExecutionMode.Scheduled)))
            .ToArray();

        var handles = actors.Select(a => a.Start(TimeSpan.FromMilliseconds(10))).ToArray();

        TestSystem.SpinWaitFor(() => actors.All(a => a.Ticks >= 10), TimeSpan.FromSeconds(60),
            "the ticks did not accumulate");

        foreach (var handle in handles)
            handle.Cancel();

        TestSystem.SpinWaitFor(() => host.System.PendingTimerCount == 0, TimeSpan.FromSeconds(15),
            $"{host.System.PendingTimerCount} timers left pending after cancellation");
    }

    private sealed class TickingActor(JobOptions options) : AsyncExecutable(options)
    {
        private int _ticks;
        public int Ticks => Volatile.Read(ref _ticks);

        public ITimerHandle Start(TimeSpan period) => DoAsyncEvery(period, Tick, period);

        private void Tick() => Interlocked.Increment(ref _ticks);
    }
}
