# Tuning guide

Nothing here should be changed without a measurement. The counters in
[find the bottleneck](#find-the-bottleneck-from-the-metrics) are the measurement.

## Worker count

`new JobDispatcher(workerCount)` creates that many **real OS threads**, not thread-pool threads. They
are background threads by default, they park on a monitor when idle, and they cost a stack each
(`JobDispatcherOptions.MaxStackSize`, 0 = platform default).

- **Start at `Environment.ProcessorCount`.** Actor jobs are meant to be short and CPU-bound; the pool
  exists to use the cores, not to hide blocking.
- **Do not add workers to hide a blocking job.** A worker parked in a lock or a synchronous wait is a
  worker that cannot flush any actor, and under leader-flush it may be the only thread that *could*
  have run the work being waited on. Fix the blocking instead — see [Pitfalls](pitfalls.md).
- **More workers than cores** only helps when jobs genuinely wait on IO you cannot make async, and it
  costs context switches on a hot ready queue.
- **Fewer workers than cores** is right when the job system is one subsystem among several. Two
  systems in one process (`new JobSystem(...)` each, with their own dispatchers) is a supported way
  to keep a background pool from starving the game loop.
- **Zero workers is a valid configuration** for tools and tests, but then every actor is flushed by
  its producer and timers fire on the timer thread. Do not ship a server that way.

Related `JobDispatcherOptions`: `ThreadPriority` (default `Normal`), `BackgroundThreads` (default
true — set false if the pool must keep the process alive), `MaxStackSize`.

> The worker loop reads `MaxReadyDrainPerTick` and `IdleWaitMs` from **`JobDispatcherOptions`**.
> `JobSystemOptions` carries same-named properties, but the dispatcher does not read them — set them
> on the dispatcher.

## `MaxQueueSize`

`JobOptions.MaxQueueSize` bounds **queued + in-flight** jobs for one actor (it is the same counter the
admission CAS guards). `null` means unbounded, which in a long-running server is an OOM vector: a
producer faster than the actor will grow the queue until the process dies.

One thing is exempt: the continuation of an `AsyncReentrancy.Interleaved` `await`, which belongs to a
job the bound already admitted and would strand its caller's task if refused. `RemainingTaskCount`
can therefore exceed the bound by the number of jobs currently awaiting — bounded in turn by how many
async jobs the bound let in.


Sizing it:

1. Decide the worst latency you will tolerate for that actor: `L`.
2. Measure or estimate the actor's service rate `R` (jobs/second) under load.
3. `MaxQueueSize ≈ L × R`, rounded to something memorable.

A player actor servicing 2,000 jobs/second that must never be more than 100 ms behind gets ~200. Once
the queue is that deep the actor is already failing its latency target, so admitting more only turns
a latency problem into a memory problem.

Handle the refusal. `DoAsync` returns `false`; with `DropPolicy.Reject` (default)
`OnDropped(actor, reason)` also fires, on the producer's thread, so keep it cheap — a counter bump,
not a log line per drop. `DropPolicy.Silent` skips the callback and keeps the counter.

`JobOptions.MaxConsecutiveFailures` is the other bound worth setting on actors driven by untrusted
input: after N consecutive throws the actor goes `IsFaulted` and refuses everything until
`ClearFault()`, which stops one broken entity from filling the log at line rate.

## `MaxJobsPerFlush` — fairness

Default `int.MaxValue`: a leader drains the actor to empty before letting go. One actor receiving
work faster than it can process it therefore owns a worker indefinitely.

Set `MaxJobsPerFlush` (say 64–256) and after that many jobs the flushing thread hands the actor back
to the system ready queue and returns, so the worker can serve other actors. The actor's remaining
jobs run on whichever worker picks it up next — still strictly one at a time, so no guarantee is
weakened; only latency distribution changes.

Two caveats:

- The yield is **ignored when no dispatcher is running** (`JobSystem.HasWorkers == false`) — there
  would be nobody to pick the actor up.
- Every yield costs a ready-queue round trip. Too small a value trades throughput for fairness. Only
  set it once `MaxObservedQueueDepth` shows an actor that actually monopolises.

Symptom that you need it: a few actors with very deep queues while `ReadyQueueDepth` also stays high
and other actors are visibly starved.

## Job pooling

`Job` and `Job<TState>` are pooled in a `ConcurrentBag` capped by a static `MaxPoolSize`
(default 16384 **each**, and `Job<TState>` has its own pool and its own cap per closed generic type).
Anything beyond the cap is left to the GC rather than growing memory without bound.

- Size it to your steady-state peak of concurrent in-flight jobs, not to your throughput. Pooling
  more than you ever hold at once is wasted working set.
- The `ActiveJobPoolSize` metric (and the `jobdispatcher.pool.size` gauge) reports the **non-generic**
  `Job` pool only. `Job<TState>.PoolSize` is per state type and is not aggregated.
- Rented jobs are recycled in a `finally`, so a throwing job still returns its entry, and a *refused*
  job is discarded straight back into the pool.

The bigger allocation win is not the pool at all: use `DoAsync<TState>` with a `static` lambda on hot
paths so no closure object is allocated per call.

```csharp
// allocates a closure every call
DoAsync(() => ProcessMove(x, y));

// allocates nothing beyond the pooled job entry
DoAsync(static t => t.Self.ProcessMove(t.X, t.Y), (Self: this, X: x, Y: y));
```

## `LeaderFlush` vs `Scheduled`

| | `LeaderFlush` (default) | `Scheduled` |
|---|---|---|
| Non-worker producer | runs the actor's jobs itself | hands the actor to the ready queue, returns immediately |
| Worker producer | runs inline | runs inline (identical) |
| Latency | lowest — no queue hop | one ready-queue hop |
| Risk | a socket/thread-pool/request thread executes actor code for an unbounded time | none, but see the fallback below |

The rule of thumb: **`Scheduled` on every actor reachable directly from a non-worker thread**
(network handlers, ASP.NET requests, `async` continuations from outside the system), `LeaderFlush`
everywhere else — actors only ever touched from inside other actors' jobs get nothing from
`Scheduled` but the extra hop.

`Scheduled` falls back to an inline flush when `JobSystem.HasWorkers` is false, so it is not a
guarantee on a system with no dispatcher. Start the dispatcher before you accept connections.

## `EnableDetailedMetrics`

Off by default. On, every job pays:

- one `Stopwatch.GetTimestamp()` before and one `Stopwatch.GetElapsedTime` after, and
- one `Histogram<double>.Record` into `jobdispatcher.job.duration`.

Plus `jobdispatcher.timer.lag` on every timer firing that was late. The counters themselves
(`executed`, `dropped`, `failed`, …) are always on and are cheap: `StripedCounter` spreads them
across cache lines by thread id, so eight workers incrementing do not ping-pong one line. Reads sum
the stripes, so they are for snapshots, not for the hot path.

Leave it off in production unless you are chasing a latency question; turn it on for a window, take
the histogram, turn it off. `JobSystemOptions.MaxJobDuration` (default `TimeSpan.Zero` = off) is the
cheaper always-on alternative: it costs the same timestamp pair but only logs a warning when a single
job overruns the limit, which is usually the question you actually have.

`PublishMeter` (default true) controls whether the `System.Diagnostics.Metrics` meter
`JobDispatcherNET` is created at all — turn it off in benchmarks and tight unit tests.

## Spin and idle knobs

| Knob | Default | What it does |
|---|---|---|
| `AsyncExecutable.MaxFlushSpinIterations` (static) | 1000 | Spins before yielding while the flush loop waits for a producer that is between its CAS and its enqueue. The loop now exits immediately when the counter reads zero, so this only bounds that narrow window. Leave it alone. |
| `JobDispatcherOptions.IdleWaitMs` | 20 | How long a parked worker blocks before re-checking. Read only by the non-generic `JobDispatcher`; a `JobDispatcher<T>` idles inside your own `IRunnable.Run`. Producers pulse the monitor, so this is a safety net, not the wake-up latency. |
| `JobDispatcherOptions.MaxReadyDrainPerTick` | 256 | Ready-queue items one worker handles per iteration. Lower it only if a `JobDispatcher<T>`'s own `IRunnable.Run` loop is being starved. |
| `JobSystemOptions.TimerSpinThresholdMs` | 16 | With `TimerPrecision.High`, how long before due the timer thread starts spinning. |

## Worker supervision

`JobDispatcherOptions.RestartFailedWorkers` (default true) restarts a worker slot whose thread died
from an unhandled exception — which for `JobDispatcher<T>` means an exception escaping your
`IRunnable.Run`, since job exceptions are already contained. `MaxRestartsPerWorker` (5),
`RestartBackoff` (1 s, doubling) and `MaxRestartBackoff` (1 minute, the ceiling on that doubling)
bound the retries, and `RestartCountResetAfter` (5 minutes) refills the budget for a slot that has
been healthy since its last restart — without it a server that hiccups five times over months is
permanently down a worker.

Raise `MaxRestartsPerWorker` freely: the backoff is clamped, so a large budget no longer means a slot
that waits days between attempts. The wait is also interruptible, so `TryStop` does not have to sit
through it.

An `OperationCanceledException` escaping `Run` counts as a clean exit only while the dispatcher is
stopping. At any other time — an inner `Task.Wait` whose own token fired, say — it is a crash, and
the slot is logged and restarted like any other.

The restart log names the actor that was running when the thread died, so a rising `WorkerRestarts`
is directly actionable.

`TryStop(joinTimeout)` spends that timeout as **one budget across the whole pool**, not per thread,
so a pool with several stuck workers gives up when you asked it to. It skips joining the calling
thread when a job stops its own pool — that join could only ever time out. `TryStopAsync` is the same
thing without blocking the caller, and is what `JobSystem.StopAsync` uses.

## Find the bottleneck from the metrics

`system.Metrics.Snapshot()` returns a `JobMetricsSnapshot`; the same values are published as
OpenTelemetry / `dotnet-counters` instruments under the meter `JobDispatcherNET`. Every instrument
carries a meter-level `jobdispatcher.system` tag holding `JobSystemOptions.Name`, so two systems in
one process stay distinguishable — group or filter on it.

| Snapshot field | Meter instrument | Rising means | Do this |
|---|---|---|---|
| `TotalJobsDropped` | `jobdispatcher.jobs.dropped` | Producers outrun an actor and hit `MaxQueueSize`, or the system is shutting down, or an actor is faulted. | Check the `DropReason` in your `OnDropped`. `QueueFull` → the actor is the bottleneck: split it, make its jobs cheaper, or accept the back-pressure. |
| `TotalJobsFailed` | `jobdispatcher.jobs.failed` | Jobs are throwing. Serialization is intact but work is being lost. | Override `OnJobError` per actor; consider `MaxConsecutiveFailures` to quarantine the offender. |
| `ActorsFaulted` | `jobdispatcher.actors.faulted` | Actors tripped `MaxConsecutiveFailures` and are now refusing everything. | Fix the cause, then `ClearFault()`. Until then every job to that actor is a `Faulted` drop. |
| `ReadyQueueDepth` | `jobdispatcher.ready.depth` | Work is queued for workers faster than they take it. | Too few workers, or workers are blocked. Cross-check `LiveWorkers` and `jobdispatcher.job.duration`. |
| `InFlightJobs` | `jobdispatcher.jobs.inflight` | Total admitted-but-not-finished work is growing across the whole system. | System-wide saturation. If `ReadyQueueDepth` is flat while this climbs, the backlog is inside a few deep actor queues — look at each actor's `MaxObservedQueueDepth`. |
| `LiveWorkers` | `jobdispatcher.workers.live` | *Falling* below the configured count. | A slot exceeded `MaxRestartsPerWorker` and is permanently down; the log names it. Restart the process or raise the budget. |
| `WorkerRestarts` | `jobdispatcher.worker.restarts` | Worker threads are dying. | An exception is escaping `IRunnable.Run`. The log line names the actor that was running. |
| `PendingTimerJobs` | `jobdispatcher.timers.pending` | Timers are being scheduled faster than they fire, or handles are never cancelled. | Usually leaked `DoAsyncEvery` handles on despawned entities — this is also what makes `StopAsync` time out. |
| `TimersDiscarded` | `jobdispatcher.timers.discarded` | Timers were dropped because the system was stopping, or because a repeating timer's actor had been disposed. | Expected at shutdown. At runtime it usually means a repeating timer outlived the actor that owned it — harmless now that it retires itself, but a sign of a handle nobody cancelled. |
| `TimersFired` vs `TotalJobsExecuted` | — | Firings climb while executions do not. | The timer thread is dispatching but the actors are not draining — see `ReadyQueueDepth`. |
| `ActiveJobPoolSize` | `jobdispatcher.pool.size` | Pinned at `Job.MaxPoolSize`. | The pool is saturated and the overflow is going to the GC. Raise `MaxPoolSize` only if gen-0 pressure actually shows in a profile. |
| `jobdispatcher.job.duration` (detailed) | histogram | p99 far above p50. | A few slow jobs are holding leaders. Set `MaxJobDuration` to get them named in the log. |
| `jobdispatcher.timer.lag` (detailed) | histogram | Consistently ~15 ms on Windows. | That is the OS timer resolution, not a bug — see [Timers](timers.md#precision). |

Sanity check on the counters: `TotalJobsExecuted` counts every job that ran, *including* the ones
that threw, so `TotalJobsFailed ≤ TotalJobsExecuted`, and refused jobs appear only in
`TotalJobsDropped`.
