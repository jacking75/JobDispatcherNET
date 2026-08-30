# Observability sample

Shows how a JobDispatcherNET server looks from the outside: hosted by the .NET Generic Host, logging
through `ILogger`, and exporting its metrics with OpenTelemetry.

## What it demonstrates

- **`AddJobDispatcher(...)`** (`JobDispatcherNET.Extensions.Hosting`) — one call registers the
  `JobSystem` and its `JobDispatcher` worker pool as singletons, starts the worker threads as an
  `IHostedService`, and drains in-flight jobs on shutdown.
- **`ILogger` routing** — the job system's own log lines (worker start, drain result, slow jobs,
  drops) go through `MicrosoftLoggerAdapter` into the host's logging pipeline under the
  `JobDispatcherNET` category, instead of straight to the console.
- **Actors with repeating timers** — two `AsyncExecutable` actors (`world` every 20 ms, `ai` every
  100 ms) driven by `DoAsyncEvery`, plus a third timer that prints a `JobMetrics.Snapshot()` so the
  in-process counters can be compared against the exported ones.
- **OpenTelemetry metrics** — `AddMeter(JobMetrics.MeterName)` subscribes to the meter the library
  publishes (`"JobDispatcherNET"`) and the console exporter prints it every 3 seconds. Swapping in
  `AddPrometheusExporter()` or `AddOtlpExporter()` is a one-line change.
- **`EnableDetailedMetrics`** — turns on the `jobdispatcher.job.duration` and
  `jobdispatcher.timer.lag` histograms.

## Run it

```bash
dotnet run --project samples/Observability
```

Runs until Ctrl+C. For a bounded run (smoke tests, CI):

```bash
dotnet run --project samples/Observability -- --seconds 12
```

## Expected output

Startup lines from the host, then every 3 seconds a snapshot log line followed by the exporter's
metric block:

```
info: JobDispatcherNET[0]
      JobSystem 'observability' started 2 worker thread(s).
info: JobDispatcherNET.Samples.Observability.WorldSimulation[0]
      ticks world=149 ai=30 | executed=180 dropped=0 failed=0 workers=2 ready=0 inflight=0 timersPending=3 timersFired=180
Metric Name: jobdispatcher.jobs.executed, Jobs that ran to completion., Unit: {job}
(...) LongSum
Value: 180
Metric Name: jobdispatcher.workers.live, Worker threads currently alive., Unit: {thread}
(...) LongGauge
Value: 2
Metric Name: jobdispatcher.job.duration, Wall-clock time spent inside a single job., Unit: ms
(...) Histogram
Value: Sum: 1.23 Count: 180 Min: 0 Max: 0.4
```

Metrics you should see move: `jobdispatcher.jobs.executed`, `jobdispatcher.timers.fired`,
`jobdispatcher.job.duration`, `jobdispatcher.timer.lag`, with `jobdispatcher.workers.live` pinned at
the configured worker count and `jobdispatcher.jobs.dropped` / `.failed` at zero.

## Health check

`JobDispatcherNET.Extensions.Hosting` also ships a health check. In an ASP.NET Core host:

```csharp
builder.Services.AddHealthChecks().AddJobDispatcher();
```

Healthy while every configured worker is alive and the system accepts work, degraded when some
workers are down or shutdown has begun, unhealthy when no worker is left.
