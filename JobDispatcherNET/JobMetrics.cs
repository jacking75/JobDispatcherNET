using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace JobDispatcherNET;

/// <summary>
/// Non-blocking snapshot of a <see cref="JobSystem"/>'s counters.
/// </summary>
/// <param name="TotalJobsExecuted">Jobs that ran to completion (including ones that threw).</param>
/// <param name="TotalJobsDropped">Jobs refused: queue full, shutting down, disposed or faulted actor.</param>
/// <param name="TotalJobsFailed">Jobs that threw.</param>
/// <param name="PendingTimerJobs">Timers scheduled and not yet fired.</param>
/// <param name="PendingTimerDispatch">Actors sitting on the ready queue waiting for a worker.</param>
/// <param name="ActiveJobPoolSize">Pooled <see cref="Job"/> instances.</param>
/// <param name="WorkerRestarts">Worker threads restarted by the supervisor.</param>
/// <param name="TimersFired">Timers that fired.</param>
/// <param name="TimersCancelled">Timers cancelled before firing.</param>
/// <param name="TimersDiscarded">Timers dropped because their system stopped.</param>
/// <param name="ActorsFaulted">Actors that tripped <see cref="JobOptions.MaxConsecutiveFailures"/>.</param>
/// <param name="LiveWorkers">Worker threads currently alive.</param>
/// <param name="ReadyQueueDepth">Current ready-queue depth (actors + posted actions).</param>
/// <param name="InFlightJobs">Jobs admitted to an actor queue and not yet retired.</param>
public readonly record struct JobMetricsSnapshot(
    long TotalJobsExecuted,
    long TotalJobsDropped,
    long TotalJobsFailed,
    long PendingTimerJobs,
    long PendingTimerDispatch,
    long ActiveJobPoolSize,
    long WorkerRestarts,
    long TimersFired,
    long TimersCancelled,
    long TimersDiscarded,
    long ActorsFaulted,
    int LiveWorkers,
    int ReadyQueueDepth,
    long InFlightJobs);

/// <summary>
/// Counters for one <see cref="JobSystem"/>.
///
/// Counters are striped across cache lines so many workers incrementing at once do not
/// ping-pong a single line. Everything is also published through
/// <see cref="System.Diagnostics.Metrics"/> under the meter name <c>JobDispatcherNET</c>,
/// so OpenTelemetry and <c>dotnet-counters</c> pick it up with no extra wiring.
///
/// <para>Each system's meter carries a <c>jobdispatcher.system</c> tag holding
/// <see cref="JobSystemOptions.Name"/>. Two systems in one process publish the same instrument
/// names, and without that tag a collector has no way to tell their values apart — it just sees two
/// series it cannot name, which usually ends up rendered as one.</para>
/// </summary>
public sealed class JobMetrics : IDisposable
{
    /// <summary>Meter name used for all instruments.</summary>
    public const string MeterName = "JobDispatcherNET";

    /// <summary>Meter-level tag carrying the owning system's <see cref="JobSystemOptions.Name"/>.</summary>
    public const string SystemTagName = "jobdispatcher.system";

    private readonly StripedCounter _executed = new();
    private readonly StripedCounter _dropped = new();
    private readonly StripedCounter _failed = new();
    private readonly StripedCounter _workerRestarts = new();
    private readonly StripedCounter _timersFired = new();
    private readonly StripedCounter _timersCancelled = new();
    private readonly StripedCounter _timersDiscarded = new();
    private readonly StripedCounter _actorsFaulted = new();

    private readonly JobSystem? _system;
    private readonly Meter? _meter;
    private readonly Histogram<double>? _jobDuration;
    private readonly Histogram<double>? _timerLag;
    private int _disposed;

    /// <summary>True when duration histograms are recorded (costs a timestamp read per job).</summary>
    public bool DetailedEnabled { get; }

    internal JobMetrics(JobSystem? system, bool detailed, bool publishMeter)
    {
        _system = system;
        DetailedEnabled = detailed;

        if (!publishMeter)
            return;

        _meter = new Meter(new MeterOptions(MeterName)
        {
            Tags = [new KeyValuePair<string, object?>(SystemTagName, system?.Name ?? "default")],
        });
        _meter.CreateObservableCounter("jobdispatcher.jobs.executed", () => _executed.Value, unit: "{job}",
            description: "Jobs that ran to completion.");
        _meter.CreateObservableCounter("jobdispatcher.jobs.dropped", () => _dropped.Value, unit: "{job}",
            description: "Jobs refused (queue full, shutting down, disposed, faulted).");
        _meter.CreateObservableCounter("jobdispatcher.jobs.failed", () => _failed.Value, unit: "{job}",
            description: "Jobs that threw.");
        _meter.CreateObservableCounter("jobdispatcher.worker.restarts", () => _workerRestarts.Value, unit: "{restart}",
            description: "Worker threads restarted by the supervisor.");
        _meter.CreateObservableCounter("jobdispatcher.timers.fired", () => _timersFired.Value, unit: "{timer}",
            description: "Timers that fired.");
        _meter.CreateObservableCounter("jobdispatcher.timers.cancelled", () => _timersCancelled.Value, unit: "{timer}",
            description: "Timers cancelled before firing.");
        _meter.CreateObservableCounter("jobdispatcher.timers.discarded", () => _timersDiscarded.Value, unit: "{timer}",
            description: "Timers dropped because the system stopped.");
        _meter.CreateObservableCounter("jobdispatcher.actors.faulted", () => _actorsFaulted.Value, unit: "{actor}",
            description: "Actors that tripped MaxConsecutiveFailures.");

        _meter.CreateObservableGauge("jobdispatcher.workers.live", () => _system?.LiveWorkerCount ?? 0, unit: "{thread}",
            description: "Worker threads currently alive.");
        _meter.CreateObservableGauge("jobdispatcher.ready.depth", () => _system?.ReadyQueueDepth ?? 0, unit: "{item}",
            description: "Actors and actions waiting for a worker.");
        _meter.CreateObservableGauge("jobdispatcher.timers.pending", () => _system?.PendingTimerCount ?? 0, unit: "{timer}",
            description: "Timers scheduled and not yet fired.");
        _meter.CreateObservableGauge("jobdispatcher.jobs.inflight", () => _system?.InFlightJobs ?? 0, unit: "{job}",
            description: "Jobs admitted to an actor queue and not yet retired.");
        _meter.CreateObservableGauge("jobdispatcher.pool.size", () => Job.PoolSize, unit: "{job}",
            description: "Pooled Job instances.");

        if (!detailed)
            return;

        _jobDuration = _meter.CreateHistogram<double>("jobdispatcher.job.duration", unit: "ms",
            description: "Wall-clock time spent inside a single job.");
        _timerLag = _meter.CreateHistogram<double>("jobdispatcher.timer.lag", unit: "ms",
            description: "Delay between a timer's due time and its actual dispatch.");
    }

    internal void OnExecuted() => _executed.Increment();
    internal void OnDropped() => _dropped.Increment();
    internal void OnFailed() => _failed.Increment();
    internal void OnWorkerRestart() => _workerRestarts.Increment();
    internal void OnTimerFired() => _timersFired.Increment();
    internal void OnTimerCancelled() => _timersCancelled.Increment();
    internal void OnTimerDiscarded() => _timersDiscarded.Increment();
    internal void OnActorFaulted() => _actorsFaulted.Increment();

    internal void RecordJobDuration(long startTimestamp)
    {
        if (_jobDuration is { } h)
            h.Record(Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
    }

    internal void RecordTimerLag(double lagMs) => _timerLag?.Record(lagMs);

    /// <summary>Total jobs that ran to completion.</summary>
    public long TotalJobsExecuted => _executed.Value;

    /// <summary>Total jobs refused.</summary>
    public long TotalJobsDropped => _dropped.Value;

    /// <summary>Total jobs that threw.</summary>
    public long TotalJobsFailed => _failed.Value;

    /// <summary>Non-blocking snapshot for monitoring.</summary>
    public JobMetricsSnapshot Snapshot() => new(
        TotalJobsExecuted: _executed.Value,
        TotalJobsDropped: _dropped.Value,
        TotalJobsFailed: _failed.Value,
        PendingTimerJobs: _system?.PendingTimerCount ?? 0,
        PendingTimerDispatch: _system?.ReadyQueueDepth ?? 0,
        ActiveJobPoolSize: Job.PoolSize,
        WorkerRestarts: _workerRestarts.Value,
        TimersFired: _timersFired.Value,
        TimersCancelled: _timersCancelled.Value,
        TimersDiscarded: _timersDiscarded.Value,
        ActorsFaulted: _actorsFaulted.Value,
        LiveWorkers: _system?.LiveWorkerCount ?? 0,
        ReadyQueueDepth: _system?.ReadyQueueDepth ?? 0,
        InFlightJobs: _system?.InFlightJobs ?? 0);

    /// <summary>Zero every counter. Test and benchmark helper.</summary>
    public void ResetCounters()
    {
        _executed.Reset();
        _dropped.Reset();
        _failed.Reset();
        _workerRestarts.Reset();
        _timersFired.Reset();
        _timersCancelled.Reset();
        _timersDiscarded.Reset();
        _actorsFaulted.Reset();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _meter?.Dispose();
    }

    // ── process-wide compatibility surface ──────────────────────────────────

    /// <summary>
    /// Snapshot of <see cref="JobSystem.Default"/>. Prefer <c>system.Metrics.Snapshot()</c>
    /// when the process hosts more than one job system.
    /// </summary>
    public static JobMetricsSnapshot GetSnapshot() => JobSystem.Default.Metrics.Snapshot();

    /// <summary>Zero <see cref="JobSystem.Default"/>'s counters. Test and benchmark helper.</summary>
    public static void Reset() => JobSystem.Default.Metrics.ResetCounters();
}
