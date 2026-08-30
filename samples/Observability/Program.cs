using System.Globalization;
using JobDispatcherNET.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace JobDispatcherNET.Samples.Observability;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // Optional: "--seconds 10" runs the host for a fixed time and exits. Handy for smoke tests
        // and CI; without it the sample runs until Ctrl+C.
        var runFor = ParseSeconds(args);

        var builder = Host.CreateApplicationBuilder(args);

        builder.Logging.SetMinimumLevel(LogLevel.Information);

        // One call: JobSystem + worker pool + IHostedService start/drain + ILogger routing.
        builder.Services.AddJobDispatcher(o =>
        {
            o.WorkerCount = 2;
            o.ShutdownDrainTimeout = TimeSpan.FromSeconds(5);
            o.SystemOptions = o.SystemOptions with
            {
                Name = "observability",
                // Turn on the job-duration and timer-lag histograms so the exporter has
                // something more interesting than counters to print.
                EnableDetailedMetrics = true,
            };
        });

        // The library publishes every counter, gauge and histogram through System.Diagnostics.Metrics
        // under the meter name "JobDispatcherNET" (JobMetrics.MeterName). Subscribing is all it takes;
        // swap AddConsoleExporter for AddPrometheusExporter / AddOtlpExporter in a real deployment.
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(
                serviceName: "jobdispatcher-observability-sample",
                serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0"))
            .WithMetrics(metrics => metrics
                .AddMeter(JobMetrics.MeterName)
                .AddConsoleExporter((_, readerOptions) =>
                    readerOptions.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 3000));

        // Registered after AddJobDispatcher, so its StartAsync runs with the workers already live.
        builder.Services.AddHostedService<WorldSimulation>();

        using var host = builder.Build();

        if (runFor is { } duration)
        {
            Console.WriteLine(FormattableString.Invariant(
                $"Running for {duration.TotalSeconds:F0}s, then shutting down."));
            using var cts = new CancellationTokenSource(duration);
            try
            {
                await host.RunAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: the timed cancellation is how this mode stops.
            }
        }
        else
        {
            Console.WriteLine("Press Ctrl+C to stop.");
            await host.RunAsync().ConfigureAwait(false);
        }

        return 0;
    }

    private static TimeSpan? ParseSeconds(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], "--seconds", StringComparison.OrdinalIgnoreCase))
                continue;

            if (double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                && seconds > 0)
            {
                return TimeSpan.FromSeconds(seconds);
            }
        }

        return null;
    }
}

/// <summary>
/// An actor that does a slice of periodic work. Jobs posted to one instance are serialized, so the
/// mutable state below needs no lock even though several worker threads are running.
/// </summary>
internal sealed class TickingActor : AsyncExecutable
{
    private readonly int _spinIterations;
    private long _ticks;

    public TickingActor(string name, JobSystem system, int spinIterations)
        : base(JobOptions.Default with { Name = name, System = system })
    {
        _spinIterations = spinIterations;
    }

    public long Ticks => Interlocked.Read(ref _ticks);

    /// <summary>Runs on a worker thread, one call at a time for this instance.</summary>
    public void Tick()
    {
        // Burn a little CPU so jobdispatcher.job.duration is not all zeros.
        Thread.SpinWait(_spinIterations);
        Interlocked.Increment(ref _ticks);
    }
}

/// <summary>
/// Creates the actors, gives each one a repeating timer, and prints a counter snapshot next to the
/// OpenTelemetry export so the two can be compared.
/// </summary>
internal sealed class WorldSimulation : IHostedService
{
    private readonly JobSystem _system;
    private readonly ILogger<WorldSimulation> _logger;
    private readonly List<ITimerHandle> _timers = [];

    private TickingActor? _world;
    private TickingActor? _ai;

    public WorldSimulation(JobSystem system, ILogger<WorldSimulation> logger)
    {
        _system = system;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _world = new TickingActor("world", _system, spinIterations: 2_000);
        _ai = new TickingActor("ai", _system, spinIterations: 20_000);

        // DoAsyncEvery keeps firing on the actor until the handle is cancelled, and survives an
        // exception in a single tick (unlike a job that re-schedules itself).
        var world = _world;
        var ai = _ai;
        _timers.Add(world.DoAsyncEvery(TimeSpan.FromMilliseconds(20), world.Tick));
        _timers.Add(ai.DoAsyncEvery(TimeSpan.FromMilliseconds(100), ai.Tick));

        // A third timer that reports. It runs on the world actor, so it observes a consistent view
        // of that actor's state without locking.
        _timers.Add(world.DoAsyncEvery(TimeSpan.FromSeconds(3), () =>
        {
            var snapshot = _system.Metrics.Snapshot();
            _logger.LogInformation(
                "ticks world={WorldTicks} ai={AiTicks} | executed={Executed} dropped={Dropped} failed={Failed} " +
                "workers={Workers} ready={Ready} inflight={InFlight} timersPending={TimersPending} timersFired={TimersFired}",
                world.Ticks, ai.Ticks,
                snapshot.TotalJobsExecuted, snapshot.TotalJobsDropped, snapshot.TotalJobsFailed,
                snapshot.LiveWorkers, snapshot.ReadyQueueDepth, snapshot.InFlightJobs,
                snapshot.PendingTimerJobs, snapshot.TimersFired);
        }));

        _logger.LogInformation("World simulation started on JobSystem '{System}'.", _system.Name);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var timer in _timers)
            timer.Cancel();
        _timers.Clear();

        if (_world is { } world)
            await world.DisposeAsync().ConfigureAwait(false);
        if (_ai is { } ai)
            await ai.DisposeAsync().ConfigureAwait(false);

        _logger.LogInformation("World simulation stopped.");
    }
}
