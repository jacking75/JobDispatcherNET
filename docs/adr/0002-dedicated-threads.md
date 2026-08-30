# ADR 0002 — Workers are dedicated OS threads, not thread-pool threads

- **Status**: Accepted. Recorded retroactively; the ready queue and the non-generic `JobDispatcher`
  were added in v2.1.
- **Affects**: `JobDispatcherBase`, `JobDispatcher`, `JobDispatcher<T>`, `ThreadContext`,
  `JobSystem`'s ready queue

## Context

Something has to run actor jobs that no producer picked up: timer firings, actors handed over by
`ExecutionMode.Scheduled`, and `JobSystem.Post` work. The .NET thread pool is the default answer, and
for this workload it is the wrong one:

- **Thread-pool threads are shared with everything else in the process** — ASP.NET request handling,
  `Task.Run`, library continuations. A game loop competing with the framework for the same threads
  has latency it cannot explain or control.
- **`[ThreadStatic]` state is not stable** on pool threads. The leader-flush model keeps
  `CurrentExecuter`, `IsWorkerThread` and the nested-actor `ExecuterQueue` in per-thread storage, and
  those must belong to a thread with a known lifetime.
- **The pool's hill-climbing injection is actively harmful** to a fixed-size simulation: it adds
  threads when work queues up, which is exactly when adding threads makes a serialized workload
  slower.
- **Long-running loops are anti-social on the pool.** A `IRunnable.Run` loop that owns its thread for
  the life of the process is precisely what the pool asks you not to do.
- Thread priority, stack size and naming are not controllable per pool thread; on a dedicated thread
  they are.

## Decision

`JobDispatcherBase` creates `workerCount` real `Thread` objects and supervises them.

- They are named `JobWorker-{system}-{slot}` (and `-r{n}` after a restart), so a stack dump and a
  profiler both say which slot is doing what.
- `ThreadPriority`, `BackgroundThreads` and `MaxStackSize` are options.
- Each worker sets `ThreadContext.IsWorkerThread = true` and `ThreadContext.CurrentSystem`, which is
  what makes `ExecutionMode.Scheduled` and the diagnostics work.
- A supervisor restarts a slot whose thread died from an unhandled exception, with exponential
  backoff, a per-slot restart budget, and a budget refill after `RestartCountResetAfter` of healthy
  running. The restart log names the actor that was running when the thread died.

Two dispatcher shapes:

| Type | Worker loop |
|---|---|
| `JobDispatcher` | Drain the system ready queue; if it was empty, block on a monitor until a producer pulses it (`IdleWaitMs` is a re-check bound, not the wake-up latency). |
| `JobDispatcher<T> where T : IRunnable, new()` | Drain the ready queue, then call the user's `Run(CancellationToken)` once, forever. For servers that need their own per-iteration work — a network poll, a fixed simulation step. |

The non-generic `JobDispatcher` was added in v2.1 to remove the `Thread.Sleep(1)` polling loop every
sample used to need. Eight workers polling at 1 ms woke 8,000 times a second to do nothing and, on
stock Windows, added 1–15 ms of latency to work that had already arrived — because `Thread.Sleep(1)`
is bounded by the same ~15.6 ms scheduler tick as everything else (see
[ADR 0003](0003-timer-service.md)). Blocking on a monitor that producers pulse costs nothing when
idle and wakes immediately when it should.

## Consequences

**Good**

- Predictable, isolated capacity: the pool size is what you configured, unaffected by whatever else
  the process is doing.
- `[ThreadStatic]` state is sound, which the leader-flush model depends on.
- Idle workers cost nothing, and the wake-up path is a monitor pulse.
- A crashed worker is restarted with an actionable log line rather than silently reducing capacity.

**Bad**

- Threads are a heavier resource than pool work items — one stack each, and oversubscription costs
  context switches. Sizing is now the user's problem; see [Tuning](../tuning.md#worker-count).
- The library has its own supervision policy to understand and configure, rather than deferring to
  the runtime.
- Work handed over with `JobSystem.Post` (and the `Sequencer(JobSystem, …)` overload) simply does not
  run when no dispatcher exists — unlike a timer firing, there is no fallback.

## Alternatives considered

- **Thread pool with `TaskCreationOptions.LongRunning`.** That already creates a dedicated thread per
  task, so it buys the drawbacks of both models without the naming, priority and supervision control.
- **A custom `TaskScheduler` over pool threads.** Keeps pool integration but not `[ThreadStatic]`
  stability, and it does not solve competing with the rest of the process for threads.
- **`System.Threading.Channels` as the ready queue.** A `ConcurrentQueue` plus a monitor is fewer
  moving parts for a queue whose consumers are a fixed set of dedicated threads, and it lets the same
  monitor serve timer firings, scheduled actors and posted actions.
