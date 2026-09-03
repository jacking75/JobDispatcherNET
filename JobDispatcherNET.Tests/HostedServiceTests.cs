using JobDispatcherNET.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace JobDispatcherNET.Tests;

/// <summary>
/// S1 — the Generic Host wiring is the library's recommended setup and it shut down with
/// <c>refuseNewWork: true</c>, the opposite of the contract <c>JobSystem.StopAsync</c> and
/// <c>docs/shutdown.md</c> promise. Nothing tested it, which is how it got there.
/// </summary>
public sealed class HostedServiceTests
{
    [Fact]
    public async Task ShutdownDrainsCascadingWorkByDefault()
    {
        using var fixture = new HostFixture(refuseNewWork: false);

        // Held until the drain is under way, so the cascade lands squarely inside it.
        var stop = fixture.Hosted.StopAsync(CancellationToken.None);
        fixture.Release();
        await stop.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(HostFixture.JobCount, fixture.Sink.Done);
        Assert.Equal(0, fixture.System.Metrics.Snapshot().TotalJobsDropped);
        Assert.False(fixture.System.AcceptingWork, "the gate must be closed once the drain is done");
    }

    [Fact]
    public async Task RefuseNewWorkOnShutdownClosesTheGateFirst()
    {
        using var fixture = new HostFixture(refuseNewWork: true);

        var stop = fixture.Hosted.StopAsync(CancellationToken.None);
        Assert.False(fixture.System.AcceptingWork, "the opt-in must close the gate before draining");

        fixture.Release();
        await stop.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, fixture.Sink.Done);
        Assert.Equal(HostFixture.JobCount, fixture.System.Metrics.Snapshot().TotalJobsDropped);
    }

    /// <summary>
    /// A running hosted service with <see cref="JobCount"/> jobs parked on a gate, so a shutdown
    /// can be started with work reliably still in flight.
    /// </summary>
    private sealed class HostFixture : IDisposable
    {
        public const int JobCount = 20;

        private readonly JobDispatcher _dispatcher;
        private readonly ManualResetEventSlim _gate = new(false);

        public HostFixture(bool refuseNewWork)
        {
            System = new JobSystem(new JobSystemOptions
            {
                Name = refuseNewWork ? "hosted-refuse" : "hosted-drain",
                Logger = NullJobLogger.Instance,
                PublishMeter = false,
            });

            _dispatcher = new JobDispatcher(2, new JobDispatcherOptions { System = System, IdleWaitMs = 5 });

            Hosted = new JobSystemHostedService(System, _dispatcher, Options.Create(new JobDispatcherBuilderOptions
            {
                WorkerCount = 2,
                ShutdownDrainTimeout = TimeSpan.FromSeconds(20),
                RefuseNewWorkOnShutdown = refuseNewWork,
            }));

            Hosted.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            TestSystem.SpinWaitFor(() => System.LiveWorkerCount == 2, TimeSpan.FromSeconds(5),
                "workers did not start");

            Sink = new CascadeSink(new JobOptions { System = System });

            // Scheduled, so the jobs go to a worker rather than being flushed inline on this thread
            // — otherwise they would all be over before the shutdown started.
            var source = new CascadeSource(
                new JobOptions { System = System, Mode = ExecutionMode.Scheduled },
                Sink,
                _gate);

            for (var i = 0; i < JobCount; i++)
                Assert.True(source.WorkThenCascade());

            TestSystem.SpinWaitFor(() => source.Waiting, TimeSpan.FromSeconds(5),
                "no job reached the gate");
        }

        public JobSystem System { get; }

        public JobSystemHostedService Hosted { get; }

        public CascadeSink Sink { get; }

        public void Release() => _gate.Set();

        public void Dispose()
        {
            _gate.Set();
            _dispatcher.Dispose();
            System.Dispose();
            _gate.Dispose();
        }
    }

    /// <summary>Its jobs run during the drain and each one wakes a peer — the despawn cascade.</summary>
    private sealed class CascadeSource(JobOptions options, CascadeSink sink, ManualResetEventSlim gate)
        : AsyncExecutable(options)
    {
        private int _waiting;

        public bool Waiting => Volatile.Read(ref _waiting) != 0;

        public bool WorkThenCascade() => DoAsync(static a => a.Step(), this);

        private void Step()
        {
            Volatile.Write(ref _waiting, 1);
            gate.Wait(TimeSpan.FromSeconds(30));
            sink.Accept();
        }
    }

    private sealed class CascadeSink(JobOptions options) : AsyncExecutable(options)
    {
        private int _done;

        public int Done => Volatile.Read(ref _done);

        public bool Accept() => DoAsync(static a => a.Tick(), this);

        private void Tick() => Interlocked.Increment(ref _done);
    }
}
