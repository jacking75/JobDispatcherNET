# ADR 0003 — One timer thread per `JobSystem`

- **Status**: Accepted in v2.1. Supersedes the per-thread `TimerQueue` of v1 / v2.0.
- **Fixes**: P0-2 (timers lost when a worker restarts or exits), P0-3 (delayed jobs never fire
  without a dispatcher)
- **Affects**: `TimerService`, `ITimerHandle`, `AsyncExecutable.DoAsyncAfter` / `DoAsyncEvery`,
  `JobSystemOptions.TimerPrecision`; removes `TimerQueue` and `TimerDispatchQueue`, leaves
  `TimerRegistry` as an obsolete no-op.

## Context

In v2.0 timers were owned by whichever thread called `DoAsyncAfter`. `ThreadContext` lazily created a
`TimerQueue` per thread, and each queue ran its own `PeriodicTimer(1 ms)` plus a thread-pool task.
Four defects followed from that design.

**Timers died with their thread (P0-2).** The worker exit path called `ThreadContext.Timer.Dispose()`,
and `Dispose` simply cleared the queue. A worker killed by an unhandled exception and restarted by the
supervisor took every timer scheduled on it with it: in the sample MMORPG server that meant NPC AI
tick chains, respawn timers and AOI resync all stopped permanently, for the subset of entities that
happened to be on that worker. Nothing was logged and no counter moved.

**Delayed jobs never fired without a dispatcher (P0-3, reproduced).** A due timer was pushed onto a
`TimerDispatchQueue` that only the worker loop drained. With no dispatcher running, delayed work sat
there forever. `ExampleConsoleApp` printed `Test count: 26` where 41 was expected — the missing 15 was
a `DoAsyncAfter` that silently never ran — while the README, the example's own comments and chapter 9
of the book all claimed delayed execution worked without a dispatcher.

**Thread-pool cost scaled with thread count.** Eight workers meant eight 1 ms polling tasks resident
on the thread pool, plus one more for every non-worker thread that ever scheduled a timer.

**No cancellation and no periodic API.** `DoAsyncAfter` returned nothing, so users fired timers into
despawned entities and filtered with a `_despawned` flag — which also kept the dead actor alive, since
the pending timer held a reference to it. Every recurring tick was hand-written as a job that
re-scheduled itself, an idiom that dies permanently the first time the tick throws.

## Decision

Each `JobSystem` owns a single `TimerService`: a `PriorityQueue<TimerEntry, long>` under a monitor,
and one dedicated background thread (`JobTimer-{name}`, `AboveNormal` priority) that does
`Monitor.Wait` until the next due time. Both are created lazily on the first timer scheduled on that
system, and disposed with the system.

- **Timers belong to the system, not to a thread.** A worker crash, a restart, or a short-lived
  producer thread has no effect on them. (P0-2)
- **`ITimerHandle { bool Cancel(); bool IsPending { get; } }`** is returned by every scheduling call.
- **`DoAsyncEvery(period, action, initialDelay)`** replaces the self-rescheduling idiom. The next
  firing is armed by the service, from the *scheduled* due time rather than from now, so it does not
  drift and it survives a throwing tick. A repeating timer counts as one pending timer for its whole
  life.
- **Firing goes through normal admission**: the service calls `owner.DoTaskFromTimer(job)`, so a
  timer job is serialized with ordinary jobs on that actor exactly like anything else. If the actor
  is idle and workers exist, the actor is pushed onto the ready queue and a worker flushes it.
- **Fallback when no workers exist**: the timer thread flushes the actor itself and `JobSystem` logs
  one warning naming the system. This is option A of the two the roadmap offered for P0-3, chosen so
  that "just use an `AsyncExecutable`, no dispatcher" — the library's simplest documented usage —
  keeps working. (P0-3)
- **`TimerPrecision`**: `Coarse` (default) waits and is bounded by the OS timer resolution;
  `High` waits until `TimerSpinThresholdMs` before due and then spins. `RaiseSystemTimerResolution`
  is a separate, opt-in, Windows-only `timeBeginPeriod(1)` for systems that measure ~15.6 ms as too
  coarse.
- **Shutdown**: `Dispose` pulses and joins the thread with a 2-second budget, then discards every
  remaining entry, recycling its job and counting `TimersDiscarded`. Pending timers do not fire at
  shutdown.

## Consequences

**Good**

- Timers survive worker restarts, and P0-2's silent partial outage is impossible.
- Delayed work runs with or without a dispatcher, and the difference is announced rather than silent.
- One thread per system instead of one polling task per thread, and no thread-pool dependency at all
  — which also removes an obstacle to a future `netstandard2.1` (Unity) target, since `PeriodicTimer`
  is gone.
- Cancellation makes it possible to stop holding a despawned actor alive, and gives `StopAsync`
  something finite to wait for.
- Metrics: `TimersFired`, `TimersCancelled`, `TimersDiscarded`, `PendingTimerCount`, and a
  `jobdispatcher.timer.lag` histogram under `EnableDetailedMetrics`.

**Bad**

- One thread is a single point of serialization for dispatch. It only enqueues, so the work per
  firing is tiny — but a callback that runs *on* the timer thread (the no-worker fallback) delays
  every other timer on that system. Documented as "the fallback is for tests and tools, not servers".
- `TimerPrecision.High` burns CPU on that thread before each firing.
- `DrainAsync` waits on `PendingTimerCount`, so a live repeating timer makes `StopAsync` run out its
  whole timeout. Callers must cancel repeating timers during shutdown. This is a real sharp edge and
  is called out in [Timers](../timers.md#shutdown), [Shutdown](../shutdown.md) and
  [Pitfalls](../pitfalls.md).
- Cancelled entries are left in the priority queue and skipped when they come due, so a workload that
  schedules and cancels far-future timers in a loop holds them until their original due time.

## Alternatives considered

- **Short-term patch: `TimerQueue.Dispose(bool migrate)`** that hands pending entries to another live
  queue on worker restart. Fixes P0-2 only, keeps the per-thread proliferation, the thread-pool
  tasks, and P0-3. Rejected as a workaround for a design problem.
- **P0-3 option B: throw `InvalidOperationException` when no workers exist.** Simple and honest, but
  it deletes the single-actor, no-dispatcher usage the README leads with, and it would break existing
  code at runtime rather than at compile time.
- **Hierarchical timing wheel** instead of a priority queue. Better asymptotics for very large timer
  populations and cheap cancellation, at the cost of coarse buckets and much more code. The priority
  queue is `O(log n)` per operation with a tiny constant; revisit only if a benchmark shows the timer
  thread saturating.
- **`System.Threading.Timer` / `PeriodicTimer` per timer.** Puts every callback back on the thread
  pool, which is what ADR 0002 rejected, and gives no control over dispatch order or lag measurement.
