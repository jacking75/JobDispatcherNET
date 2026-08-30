# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

Nothing yet.

## [2.1.0] - 2026-08-30

The first release published to NuGet, and the first with a library test suite. It closes
the four P0 stability defects that made the previous code unsafe for a long-running
server, and adds the execution model, timer and observability work that a production game
server needs.

Upgrading from 2.0 is source-compatible except for the four items under **Changed**;
everything replaced rather than removed is marked `[Obsolete]` and still compiles.

### Added

- **`JobSystem` and `JobSystemOptions`** — an explicit, disposable owner for worker
  threads, the ready queue, the timer thread, metrics and the shutdown gate. Two job
  systems can now coexist in one process (a game world and a background-IO pool, say)
  without sharing timers, counters or shutdown state, and tests can run in parallel.
  `JobSystem.Default` keeps existing dependency-free code working unchanged.
  `StopAsync(drainTimeout, refuseNewWork)` and `DrainAsync(timeout)` replace the manual
  four-step shutdown sequence and the `Thread.Sleep` that went with it.
- **Non-generic `JobDispatcher`** with signal-based idle workers. Workers block on a
  monitor and are pulsed when work arrives, instead of polling with `Thread.Sleep(1)`.
  This removes the 1–15 ms inbound latency the old polling loop inherited from the system
  timer resolution, and the constant wake-ups on an idle server.
  `JobDispatcher<T>` and `IRunnable` are unchanged for callers who want their own loop.
- **`ExecutionMode.Scheduled`** — a producer on a non-worker thread hands the actor to the
  system's ready queue and returns, instead of running actor code on the socket or
  ThreadPool thread that happened to call `DoAsync`. `LeaderFlush` remains the default.
  `JobOptions.MaxJobsPerFlush` caps how long one hot actor can hold a worker before it
  goes back on the ready queue.
- **Cancellable and repeating timers.** `DoAsyncAfter` now returns an `ITimerHandle` with
  `Cancel()` and `IsPending`, so a despawned actor can drop its pending callbacks instead
  of firing them and checking a flag. `DoAsyncEvery(period, action, initialDelay)` replaces
  the hand-written self-rescheduling pattern every sample used to carry.
  `TimerPrecision`, `TimerSpinThresholdMs` and the opt-in, Windows-only
  `RaiseSystemTimerResolution` control how precisely due times are hit.
- **Request/response and async jobs**: `Ask<TResult>` and `Ask<TState, TResult>` return a
  `Task<TResult>` from work run on the actor; `AskSync` blocks with a timeout for
  non-actor callers; `RunAsync(Func<Task>)` and `AskAsync(Func<Task<TResult>>)` run a job
  that awaits and bring the continuation back onto the actor's queue.
  `JobOptions.AsyncReentrancy` chooses `Interleaved` (default) or `Exclusive` behaviour
  while a job awaits.
- **Per-actor error handling**: `protected virtual void OnJobError(Exception)` for local
  recovery — drop this session, not the process — alongside the existing global
  `AsyncExecutable.OnError`. `JobOptions.MaxConsecutiveFailures` moves an actor that keeps
  throwing to a faulted state where it refuses work (`DropReason.Faulted`) until
  `ClearFault()`, so one broken actor cannot fill the log.
- **`System.Diagnostics.Metrics` instruments** under the meter name `JobDispatcherNET`:
  counters for jobs executed / dropped / failed, worker restarts, timers fired /
  cancelled / discarded and actors faulted; observable gauges for live workers, ready
  queue depth, pending timers, in-flight jobs and job-pool size; and, with
  `EnableDetailedMetrics`, histograms for job duration and timer lag. OpenTelemetry and
  `dotnet-counters` pick these up with no extra wiring.
- **`JobDiagnostics`** with a blocking-wait guard. `GuardBlockingWait` throws instead of
  deadlocking when actor code blocks a worker waiting on another actor's result — the
  failure mode the leader-flush model otherwise turns into a silent hang. Armed by
  `JobSystemOptions.DetectBlockingWaitOnWorker`, which defaults to on in DEBUG.
  `IsInsideActorJob`, `CurrentActor` and `IsWorkerThread` are exposed for callers writing
  their own guards.
- **`JobSystem.Post(Action)`** — the supported way to hand work from a network or
  ThreadPool thread to a worker without becoming some actor's leader.
- **`Sequencer<T>.Abort()`**, which discards what is queued and returns how many items were
  thrown away, for a hard shutdown; and `Enqueue` now returns `bool` so a caller can tell
  "queued" from "refused".  A `Sequencer<T>(JobSystem, handler, onError)` constructor
  schedules drains onto the worker pool, so callers no longer need their own inbound
  command queue.
- **`net8.0` target.** The library now multi-targets `net8.0;net10.0`.
- **NuGet packaging**: package metadata, README in the package, XML documentation,
  SourceLink, deterministic CI builds, symbol packages (`.snupkg`), `IsAotCompatible` and
  `IsTrimmable`.
- Actor identity and introspection for logs and metric tags: `JobOptions.Name`,
  `AsyncExecutable.Name`, `MaxObservedQueueDepth`, `IsFaulted`, and a `ToString()` that
  shows the queue depth.
- `JobDispatcherOptions` gained `RestartCountResetAfter`, `ThreadPriority`,
  `BackgroundThreads`, `MaxStackSize` and `IdleWaitMs`; `JobDispatcherBase.TryStop(TimeSpan)`
  reports whether workers actually joined.
- `JobDispatcherNET.Tests` — the first test suite for the library itself, including one
  regression test per P0 defect, each of which fails against 2.0.

### Changed

- **`JobOptions.OnDropped` is now `Action<AsyncExecutable, DropReason>`** (was
  `Action<JobEntry>`). The rejected job is no longer handed to user code — holding onto it
  risked using an entry the library had already recycled — and the callback now says
  *why* the job was refused: `QueueFull`, `ShuttingDown`, `Disposed` or `Faulted`.
- **`JobMetrics` is an instance on `JobSystem`**, not a static class. Use
  `system.Metrics.Snapshot()` / `system.Metrics.ResetCounters()`. The process-wide
  shortcuts remain as `JobMetrics.GetSnapshot()` and `JobMetrics.Reset()`, which forward to
  `JobSystem.Default`. Counters are striped across cache lines, so eight workers
  incrementing at once no longer ping-pong a single line.
- **`Sequencer<T>.Stop()` now drains items it has already accepted.** Its meaning is
  exactly "refuse new items"; everything already enqueued is still handled, in order. This
  matches what the documentation always claimed. Use the new `Abort()` when the remaining
  items genuinely must not run.
- **The async overloads are named `RunAsync` and `AskAsync`** rather than being further
  overloads of `DoAsync` / `Ask`, so `DoAsync(() => SomeTaskReturningMethod())` can no
  longer bind to the synchronous overload and silently drop the returned task.
- `JobDispatcherOptions.MaxTimerDrainPerTick` was renamed to `MaxReadyDrainPerTick`, since
  timer dispatch now flows through the shared ready queue. The old name still works and
  forwards.

### Fixed

Six further defects came out of an adversarial concurrency review of the rewrite itself, before
release. They are listed first because they were introduced by the v2.1 work rather than inherited.

- **An `Exclusive` async job finishing on an idle actor could produce two flushers.** The
  suspension's reservation was released unconditionally by the continuation, so the pending count
  could fall to zero while the flushing thread still believed it held leadership. A producer
  arriving in that window claimed the actor, and two threads then ran the same actor's jobs — the
  one thing the actor model exists to prevent. The reservation is now released exactly once, by
  whichever party owns leadership when the handshake resolves.
- **A stale suspension could consume a later async job's token.** `BeginExclusiveSuspension` wrote
  the suspend state blindly, so a continuation that had not finished its own transition could
  claim the token of a second async job — losing exclusivity, and, with an empty queue, spinning a
  worker core for the whole duration of the second await. The state transition is a CAS from
  `None` now, so a stale completion cannot take a token that is not its own.
- **Shutdown could stop the workers with posted work still queued.** `JobSystem.Post` raised the
  ready-queue depth only after the item was already visible, and lowered it before the item ran,
  so `DrainAsync` could observe an empty system while work was outstanding. Every
  `Sequencer<T>` built on the system-aware constructor drains through `Post`, so a graceful
  shutdown could silently lose a session's packets — the P0-4 failure mode by another route. The
  depth is now raised before the item is visible and lowered only after it has run, so it can only
  over-estimate. `InFlightJobs` likewise reads its two striped counters in the order that makes the
  error conservative.
- **Cancelling a timer that was firing decremented the pending count twice.** `Cancel()` decided
  "already fired" by *reading* the job while the dispatch path *took* it, so both sides could claim
  the same one-shot. The count drifted negative, which then made the shutdown drain skip waiting
  for timers that really were still armed, and `Cancel()` returned `true` for a callback that ran
  anyway. Taking the job is now the single arbiter. (Covered by
  `CancelRacingAFiringTimerKeepsThePendingCountHonest`, which reproduces the negative count against
  the old code.)
- **`Sequencer` could strand its last accepted item on a weak memory model.** The drain released
  its scheduling claim with a release store and then loaded the queue; that pair is not ordered, so
  a producer could read the stale claim and skip scheduling while the item sat queued. For a
  closing session that item is the disconnect marker. The release is an interlocked exchange now.
- **A worker restart racing shutdown could outlive `TryStop`.** The supervisor could start a
  replacement after shutdown had already scanned the thread array, so `TryStop` reported success
  with a worker still coming up, and then disposed the cancellation source that worker was about to
  read — surfacing as a spurious "worker crashed" log. Starting and stopping are serialised now,
  and the cancellation source is only disposed once every worker has genuinely left.
- **`MaxObservedQueueDepth` could go backwards.** It was a read-then-write; it is a CAS loop now.

- **Bounded-queue rejection could strand the flush leader (P0-1).** `DoTask` incremented
  the pending count *before* attempting the channel write and decremented on failure,
  leaving a window in which the counter claimed a job the queue had never received. A
  leader that hit that window exited its read loop without seeing the count reach zero and
  spun forever, burning a core — and worse, a later producer would see a zero count, become
  a second leader, and run the same actor's jobs concurrently with the stuck one,
  destroying the serialization guarantee. Admission is now a CAS on the counter, which is
  authoritative, and the queue itself is unbounded, so the phantom entry cannot exist. The
  flush loop also rechecks the count on a failed read as a safety net.
- **Timers were lost when a worker restarted (P0-2).** Timers lived in a per-thread queue
  owned by the thread that scheduled them, and the supervisor's restart path disposed that
  queue and cleared it. Every pending timer on that thread — AI tick chains, respawns,
  interest-management resynchronisation — silently stopped forever, and nothing counted it.
  A `JobSystem` now owns a single timer thread whose lifetime is the system's, so a worker
  crash cannot take timers with it. Timers dropped at shutdown are counted as
  `TimersDiscarded`.
- **`DoAsyncAfter` never fired without a dispatcher (P0-3).** A due timer was only handed
  to a queue that worker threads drained, so in a process with no workers the callback was
  silently discarded — contradicting the documented "simplest possible use" of a bare
  `AsyncExecutable`. Timer callbacks now fall back to running on the timer thread when the
  system has no workers, with a one-time warning saying so.
- **`Sequencer.Stop()` lost items it had already accepted (P0-4).** The reschedule check in
  the drain's `finally` block also tested the stopped flag, so an item enqueued in the
  window between the last dequeue and the release of the drain claim was dropped if `Stop`
  arrived first. In the sample server this lost a session's final disconnect marker and
  left a ghost player in the world until process exit. The stopped flag is no longer part
  of that condition, and `Stop` itself rechecks the queue after flipping it.
- **Repeating timers leaked the pending count.** A repeating entry decremented and
  re-incremented the pending counter asymmetrically across a fire, so `PendingTimerCount`
  drifted upward and never settled — which in turn made `DrainAsync` and `StopAsync`
  believe work was outstanding and wait out their whole timeout on a clean shutdown.
- **Dispatcher lifecycle guards.** `RunWorkerThreadsAsync()` called twice used to overwrite
  the completion state and start a second full set of worker threads; it is now guarded and
  only starts once. `Dispose()` ignored a `Thread.Join` timeout — it now logs which worker
  failed to stop, and `TryStop(TimeSpan)` returns that result to the caller. Per-worker
  restart counts reset after `RestartCountResetAfter` of healthy operation, so a worker that
  exhausted its restart budget days earlier is not permanently disabled.
- `Job<TState>.Execute` no longer substitutes `default!` for a null state, which quietly
  changed the meaning of a legitimately null reference-type state.
- `ExampleConsoleApp` checks `Console.IsInputRedirected` before `Console.ReadKey`, so it no
  longer throws when run with redirected input (CI, `dotnet run < /dev/null`).
- Sample and test ports moved into the project's development range (25001–25199).

### Deprecated

- `AsyncExecutable.AcceptingWork` — the process-wide shutdown gate. Use
  `JobSystem.Default.AcceptingWork`, or `system.AcceptingWork` for a specific system.
  Removed in 4.0.
- `TimerRegistry` — timers are owned by the `JobSystem` and disposed with it, so there is
  nothing left for this to clean up. Its members are now no-ops kept for source
  compatibility. Removed in 4.0.
- `JobDispatcherOptions.MaxTimerDrainPerTick` — renamed to `MaxReadyDrainPerTick`.
  Removed in 4.0.

### Removed

- `TimerQueue`, the per-thread timer queue, and the internal `TimerDispatchQueue` that fed
  due callbacks to worker loops. Both are replaced by the `JobSystem`-owned timer thread,
  which is what makes P0-2 and P0-3 fixable rather than merely mitigated.
- `ThreadContext.Timer`. Timers are no longer thread-affine, so there is nothing per-thread
  to expose.

[Unreleased]: https://github.com/jacking75/JobDispatcherNET/compare/v2.1.0...HEAD
[2.1.0]: https://github.com/jacking75/JobDispatcherNET/releases/tag/v2.1.0
