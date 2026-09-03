# Timers

## One timer thread per `JobSystem`

Each `JobSystem` owns a single `TimerService`: a `PriorityQueue<TimerEntry, long>` guarded by a
monitor, and one dedicated background thread (`JobTimer-{system.Name}`, `ThreadPriority.AboveNormal`)
that does `Monitor.Wait` until the next due time. It is created lazily on the first timer scheduled
on that system, and the thread starts on that same first schedule — a process that never uses timers
never gets the thread.

There is no thread-pool dependency and no `PeriodicTimer`. In v1/v2.0 every thread that called
`DoAsyncAfter` lazily grew its *own* `TimerQueue` plus a 1 ms thread-pool polling task, and a worker
crash or a short-lived producer thread silently took every timer it owned down with it — defect
**P0-2** (`RegressionTests.P0_2_TimersSurviveAWorkerCrash`). Timers now outlive the thread that
scheduled them.

## API

```csharp
public interface ITimerHandle
{
    bool Cancel();      // true if the callback will not run; false once it has run
    bool IsPending { get; }
}

ITimerHandle DoAsyncAfter(TimeSpan delay, Action action);
ITimerHandle DoAsyncAfter<TState>(TimeSpan delay, Action<TState> action, TState state);
ITimerHandle DoAsyncEvery(TimeSpan period, Action action, TimeSpan? initialDelay = null);
```

All three are instance methods on `AsyncExecutable`, and all three deliver the callback **as a job on
that actor**, so a timer firing is serialized against ordinary jobs exactly like anything else
(`TimerTests.TimerJobsAreSerializedWithOrdinaryJobs`).

Keep the handle. A timer that fires into a despawned entity is a leak of both work and memory: the
entry holds a reference to the actor until it fires.

```csharp
public sealed class NpcActor : AsyncExecutable
{
    private ITimerHandle? _tick;

    public void Spawn() => _tick = DoAsyncEvery(TimeSpan.FromMilliseconds(200), Tick);

    public void Despawn() => _tick?.Cancel();
}
```

`Cancel()` returns `true` for the call that keeps the callback from running, and `false` once it has
run (or if the timer was already cancelled). Cancelling a repeating timer stops all further firings.
Cancellation is counted in `TimersCancelled`.

**"Fired" and "ran" are two different moments,** and `Cancel()` works right up to the second one. A
firing hands the callback to the actor as an ordinary job; if the actor is busy, that job waits its
turn like any other, and on a loaded actor the wait is easily hundreds of milliseconds. A cancel
landing inside that window claims the job and it never runs
(`TimerTests.CancellingAOneShotAfterItFiredButBeforeItRanStopsTheCallback`).

That is what makes the `Despawn()` above sufficient on its own. Because the actor runs one job at a
time, a `Cancel()` from inside one of its own jobs is committed before any queued tick can get its
turn, so **no tick runs after the despawn** — you do not need a `_despawned` flag as well
(`TimerTests.CancellingARepeatingTimerDropsATickAlreadyQueuedOnTheActor`).

## `DoAsyncEvery` versus the self-rescheduling idiom

The old idiom was a job that re-armed itself at the end:

```csharp
// Don't do this any more.
private void Tick()
{
    DoWork();
    DoAsyncAfter(_period, Tick);   // one throw from DoWork and the chain is dead forever
}
```

Three problems, all fixed by `DoAsyncEvery`:

| | Self-rescheduling chain | `DoAsyncEvery` |
|---|---|---|
| One tick throws | The re-arm never runs. The entity stops ticking silently, forever. | The next firing is already queued; ticking continues. Verified by `TimerTests.RepeatingTimerKeepsGoingAfterAThrowingTick`. |
| Cancellation | Needs your own `_despawned` flag checked at the top of every tick. | `handle.Cancel()`. |
| Drift | Each period is measured from *after* the work finished, so the interval is `period + work time` and slips forever. | Re-armed from the scheduled due time, not from now. |

`initialDelay` defaults to `period`; pass a small random jitter when spawning many entities on the
same period so they do not all land on the same tick.

A repeating timer counts as **one** pending timer for its whole life, not one per firing.

If you forget to cancel one, disposing its actor now retires it: the first firing that the disposed
actor refuses stops the timer instead of re-arming it, so it no longer fires into a closed door once
a period and no longer pins `PendingTimerCount` above zero
(`TimerTests.ARepeatingTimerRetiresItselfWhenItsActorIsDisposed`). That is a safety net, not a
substitute for cancelling — a timer on an actor you never dispose still pins the drain.

## Precision

`JobSystemOptions.TimerPrecision`:

| Mode | Behaviour | Cost |
|---|---|---|
| `Coarse` (default) | `Monitor.Wait` until due. Accuracy is bounded by the operating system's timer resolution. | None. |
| `High` | Waits until `TimerSpinThresholdMs` (default 16 ms) before due, then busy-spins with `SpinWait` to the exact tick. | One thread burning CPU for up to the threshold before each firing. |

The OS resolutions that bound `Coarse`:

- **Stock Windows: ~15.6 ms** (the default 64 Hz scheduler tick). A `Coarse` timer asked for 5 ms may
  well fire at 15 ms.
- **Linux: ~1 ms**, so `Coarse` is usually good enough there.

These are the platform's documented resolutions, not measurements of this library. Nothing in this
repository has measured the delivered lag yet — see [Benchmarks](benchmarks.md).

### `RaiseSystemTimerResolution` (Windows, opt-in)

```csharp
new JobSystemOptions { RaiseSystemTimerResolution = true }
```

Calls `timeBeginPeriod(1)` for the lifetime of the timer thread and `timeEndPeriod(1)` when it stops,
bringing `Coarse` accuracy to roughly 1 ms. The costs are real and are why it is off by default:

- It is **process-wide and, on older Windows, system-wide** — you are changing the scheduler tick for
  more than your timer thread.
- It raises power draw and hurts battery/idle-power behaviour measurably.
- Timer-driven wakeups get more frequent everywhere in the process, which can *reduce* throughput on
  a busy server.

Turn it on only after measuring that ~15.6 ms is genuinely too coarse for your tick rate. On non-Windows
platforms the option is ignored. If `winmm.dll` is unavailable the call degrades silently to a no-op.

Consider `TimerPrecision.High` first: it costs one thread's CPU for a few milliseconds per firing
rather than changing global state.

## Drift when re-arming

```csharp
// TimerService.DispatchDue — repeating branch
var next = entry.DueTick + ToMillis(entry.Period);
if (next <= now)
    next = now + ToMillis(entry.Period);
```

The next due time is computed from the **scheduled** time, so a firing that was late by 4 ms does not
push every subsequent firing 4 ms later — the schedule self-corrects and the long-run average period
stays exact.

If the system fell so far behind that the recomputed time is already in the past (a long GC pause, a
saturated worker pool), the timer **re-bases on now** instead. It does not fire a catch-up burst. A
tick that models elapsed time should read the clock itself rather than assume exactly one period
passed.

Delays are rounded **up** to whole milliseconds (`Math.Ceiling`), so a timer never fires early because
of rounding.

## Firing: which thread

The timer thread never runs your callback. It calls `owner.DoTaskFromTimer(job)`, which goes through
the normal admission path:

- Actor **busy** → the job joins the queue; the current leader runs it.
- Actor **idle** and `JobSystem.HasWorkers` → the actor is pushed onto the ready queue and a **worker**
  flushes it (`TimerTests.TimersRunOnWorkerThreadsWhenWorkersExist`).
- Actor **idle** and **no workers at all** → the timer thread flushes the actor itself, and
  `JobSystem` logs one warning for the lifetime of the system:

  > `JobSystem '<name>' has no worker threads, so timer callbacks run on the timer thread. Start a JobDispatcher to move them onto dedicated workers.`

  This fallback exists so that "just use an `AsyncExecutable`, no dispatcher" keeps working — in
  v2.0 the firing was queued somewhere only workers drained, so with no dispatcher it silently never
  ran at all. That was defect **P0-3**
  (`RegressionTests.P0_3_DelayedJobRunsWithNoDispatcher`). Note the consequence: a long callback
  blocks the timer thread and delays every other timer on that system. The fallback is for tests,
  tools and single-threaded console apps, not for a server.

## Metrics

| Counter | Meaning |
|---|---|
| `TimersFired` | Firings dispatched to an actor (a repeating timer contributes one per tick). Counts the hand-off, so a firing later claimed by `Cancel()` is counted here and in `TimersCancelled`. |
| `TimersCancelled` | `Cancel()` calls that kept a callback from running, whether or not it had already been dispatched. |
| `TimersDiscarded` | Timers thrown away because the service was stopping, scheduled after it stopped, or — for a repeating timer — retired because its actor had been disposed. |
| `PendingTimerJobs` / `JobSystem.PendingTimerCount` | Scheduled and not yet fired. Repeating timers count as 1 each. |

With `JobSystemOptions.EnableDetailedMetrics = true`, the `jobdispatcher.timer.lag` histogram records
the milliseconds between a timer's due time and its dispatch. Only positive lag is recorded.

## Shutdown

**Cancel your repeating timers before you call `StopAsync`.** `JobSystem.DrainAsync` — which
`StopAsync` runs first — waits for `PendingTimerCount` to reach zero as well as for in-flight jobs.
A live `DoAsyncEvery` handle never reaches zero, so the drain will burn its whole timeout and
`StopAsync` will return `false`. The sample server does this in `GameWorld.Stop()` by despawning
every entity, and each `Despawn` cancels its own handles.

Once draining is done, `StopAsync` disposes the timer service:

1. The thread is pulsed and joined, with a 2-second budget (a warning is logged if it overruns).
2. Every entry still on the queue is discarded: its job is recycled, `PendingCount` is decremented,
   and `TimersDiscarded` is incremented. **Pending timers do not fire at shutdown.**
3. Any `DoAsyncAfter`/`DoAsyncEvery` called after that point discards its job immediately, counts a
   discard, and returns a handle that is already `IsPending == false`.

See [Shutdown](shutdown.md) for the full sequence.
