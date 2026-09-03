# Pitfalls & FAQ

The leader-flush model turns several ordinary mistakes into hangs rather than errors. These are the
ones that actually bite people, in roughly the order they bite.

---

## 1. "My actor runs on a worker thread" — not necessarily

**Symptom.** A socket receive loop or an ASP.NET request slows to a crawl. A profiler shows game
logic on the IO thread. Latency spikes correlate with actor queue depth, not with network traffic.

**Cause.** Under the default `ExecutionMode.LeaderFlush`, the producer that finds an actor idle
becomes its leader and drains the whole queue inline, on its own thread. If that producer is a socket
thread, a thread-pool continuation or `Main`, that thread runs your actor code — for as long as the
queue keeps refilling.

```csharp
// on the IO thread
player.Move(x, y);   // ← may not return until the player's entire queue is drained
```

**Fix.** Mark actors reached from non-worker threads as `Scheduled`:

```csharp
public sealed class PlayerActor : AsyncExecutable
{
    public PlayerActor(JobSystem system) : base(new JobOptions
    {
        System = system,
        Mode = ExecutionMode.Scheduled,   // non-worker producers only enqueue
        MaxQueueSize = 256,
    }) { }
}
```

Or hand the work over explicitly with `system.Post(() => ...)`, which always runs on a worker.

**Do not assume `Scheduled` is a guarantee.** It falls back to an inline flush when the system has no
live workers, so start the dispatcher before you accept connections. `JobDiagnostics.IsWorkerThread`
tells you where you actually are.

---

## 2. Blocking inside a job to wait for another actor

**Symptom.** The server stops. No exception, no CPU. `TryStop` reports a worker that would not join.

**Cause.** This is a guaranteed deadlock, not a race:

```csharp
private void OnAttack(PlayerActor target)
{
    var hp = target.Ask(() => target.Hp).Result;   // ☠
}
```

The thread that would run `target`'s job is the thread you just parked. With `LeaderFlush` there is
no separate pool waiting to rescue you — and even with workers to spare, one worker per blocked job
is how a pool of eight dies under load.

**Fix.** Never block inside a job. Either send a message and let the other actor call back, or make
the job async:

```csharp
// message passing — no waiting at all
target.DoAsync(static t => t.Self.ReportHpTo(t.Caller), (Self: target, Caller: this));

// or an async job (Interleaved: the continuation comes back onto this actor)
RunAsync(async () =>
{
    var hp = await target.Ask(() => target.Hp);
    ApplyDamage(hp);
});
```

**The guard.** `JobSystemOptions.DetectBlockingWaitOnWorker` (default: on in DEBUG builds) turns the
deadlock into an immediate exception. `AskSync` checks it for you, and you should call it at the top
of any blocking helper you write:

```csharp
public PlayerSnapshot GetSnapshot()
{
    JobDiagnostics.GuardBlockingWait(System, nameof(GetSnapshot));
    return this.AskSync(() => Snapshot(), TimeSpan.FromSeconds(2));
}
```

Called from inside a job, that throws:

> `GetSnapshot was called from inside actor 'PlayerActor'. Blocking there deadlocks: this thread is
> the one that would run the work being waited on. Use await, or send a message to the other actor
> and let it call back.`

Called from `Main`, a console command loop or a health probe — all fine, that is what `AskSync` is
for.

Turn the guard on in Release too (`DetectBlockingWaitOnWorker = true`) if you would rather find these
in staging than in production.

---

## 3. `ConfigureAwait(false)` in an interleaved async job

**Symptom.** Intermittent corruption of actor state that "cannot happen" — counters off by a few,
collections throwing `InvalidOperationException` from concurrent modification, under load only.

**Cause.** With the default `AsyncReentrancy.Interleaved`, the actor installs its own
`SynchronizationContext` around each job so that `await` continuations are posted back onto the
actor's queue. `ConfigureAwait(false)` explicitly discards that context:

```csharp
RunAsync(async () =>
{
    var row = await db.LoadAsync().ConfigureAwait(false);   // ☠
    _cache[row.Id] = row;   // now on the thread pool, racing every other job on this actor
});
```

The serialization guarantee ends at that `await`.

**Fix.** Drop `ConfigureAwait(false)` inside actor jobs. The habitual "always ConfigureAwait(false) in
library code" rule exists to avoid capturing a UI or ASP.NET context — here the captured context is
exactly what you want.

If you genuinely want the continuation off the actor, say so with `AsyncReentrancy.Exclusive`: the
actor is suspended for the whole async job, so the continuation runs on the thread pool *and* nothing
else on that actor runs concurrently with it.

Watch for the same mistake by proxy: a helper library that internally uses `ConfigureAwait(false)`
returns you a task whose continuation is already off-context. Awaiting *that* task from your job is
fine — your continuation still comes back — but code the helper runs after its own await is not on
your actor.

---

## 4. Sharing a mutable collection between actors

**Symptom.** `InvalidOperationException: Collection was modified`, or silently wrong reads, from code
that has no `lock` anywhere because "actors don't need locks".

**Cause.** The guarantee is *per actor*. Different actors run in parallel, by design. A
`List<Player>` or `Dictionary<int, Session>` reachable from two actors is ordinary shared mutable
state with no protection at all.

```csharp
// world._players is touched by every PlayerActor's job ☠
public void OnLogout() => DoAsync(static a => a.World.Players.Remove(a.Id), this);
```

**Fix — pick one:**

- **Give the collection an owner.** The world is an actor too; post to it.
  ```csharp
  world.DoAsync(static t => t.World.RemovePlayer(t.Id), (World: world, Id: id));
  ```
- **Make it immutable.** Publish a new snapshot object with `Volatile.Write` and let readers take
  whatever they see.
- **Use a concurrent collection**, and remember that concurrent collections give you safe *operations*,
  not safe *invariants*: `if (!dict.ContainsKey(k)) dict.Add(k, v)` is still a race.

The same applies to anything else two actors can reach — a shared `Random`, a `StringBuilder`, a
non-thread-safe logger, a cached `MemoryStream`.

---

## 5. Forgetting `static` on a `DoAsync<TState>` lambda

**Symptom.** You migrated a hot path to `DoAsync<TState>` to remove allocations, and the allocation
profile did not change.

**Cause.** The whole point of the `TState` overload is that no closure object is allocated. A
non-`static` lambda that captures anything defeats it — it compiles fine and behaves correctly, it
just allocates a display class per call, which is what you were trying to avoid.

```csharp
// still allocates: the lambda captures `this`
DoAsync(t => ProcessMove(t.X, t.Y), (X: x, Y: y));

// allocates nothing beyond the pooled job entry
DoAsync(static t => t.Self.ProcessMove(t.X, t.Y), (Self: this, X: x, Y: y));
```

**Fix.** Write `static` on every such lambda. That is the real value of the keyword here: the compiler
*errors* if the lambda captures anything, so the mistake cannot survive a build. Carry everything the
job needs — including `this` — inside the tuple.

---

## 6. Ignoring the `bool` from `DoAsync`

**Symptom.** With `MaxQueueSize` set, work silently disappears under load. Players stop moving.
Nothing is logged, and the job never ran.

**Cause.** `DoAsync` returns `false` when the job was refused: queue full, system shutting down, actor
disposed, actor faulted. Most callers discard it.

```csharp
player.DoAsync(...);   // ☠ was that accepted?
```

**Fix.** Decide what a refusal means for your protocol, per call site:

```csharp
if (!player.DoAsync(static t => t.Self.Move(t.X, t.Y), (Self: player, X: x, Y: y)))
    session.Kick("overloaded");   // back-pressure, not silence
```

For a system-wide view, set `JobOptions.OnDropped` (it receives the actor and the `DropReason`) and
watch `TotalJobsDropped`. Keep the callback cheap — it runs on the producer's thread, once per drop.

`Ask`, `AskAsync` and `RunAsync` do not return `bool`; they fault the returned task with
`JobRejectedException`. That is only visible if you await it, so do not fire and forget them either.

---

## 7. A self-rescheduling timer chain that dies on one exception

**Symptom.** One NPC stops ticking. The rest are fine. Nothing in the log except the original
exception, hours earlier.

**Cause.** The classic idiom re-arms itself at the *end* of the tick, so any throw skips the re-arm
and the chain is over — permanently, silently:

```csharp
private void Tick()
{
    UpdateAi();                    // throws once → nothing below ever runs again
    DoAsyncAfter(_period, Tick);   // ☠
}
```

Job exceptions are caught by the library and the actor keeps working, which makes this *worse*: the
actor looks healthy, it just never ticks again.

**Fix.** Use `DoAsyncEvery`, which schedules the next firing independently of what the tick did:

```csharp
_tick = DoAsyncEvery(TimeSpan.FromMilliseconds(200), Tick, initialDelay: jitter);
...
_tick?.Cancel();   // in Despawn
```

Verified by `TimerTests.RepeatingTimerKeepsGoingAfterAThrowingTick`. It also fixes drift and gives you
a cancellation handle, so you no longer need a `_despawned` flag checked at the top of every tick.

---

## 8. Disposing the dispatcher before draining the actors

**Symptom.** Shutdown hangs, or the last few actions of every session are lost — the disconnect
never gets recorded, the player is still in the world after the process restarts.

**Cause.** The workers *are* what drains the actors. Stop them first and the remaining jobs have
nobody to run them: `await actor.DisposeAsync()` waits forever, and `DrainAsync` cannot make progress.

```csharp
dispatcher.Dispose();          // ☠ workers gone
await world.DisposeAsync();    // ...nothing left to drain the queue
```

**Fix.** One call, in this order:

```csharp
network.Stop();                                     // 1. stop external input yourself
world.Stop();                                       // 2. despawn + cancel repeating timers
await system.StopAsync(TimeSpan.FromSeconds(10));   // 3. drain → close gate → timers → workers
system.Dispose();
```

`StopAsync` drains *before* it closes the gate, so cascading shutdown work still runs, and it stops
the workers last. See [Shutdown](shutdown.md).

---

## 9. `StopAsync` always times out

**Symptom.** `StopAsync` returns `false` every time and logs
`drain timed out … (in-flight=0, ready=0, timers=12, async=0)`.

**Cause.** `DrainAsync` waits for `PendingTimerCount` to reach zero as well as for jobs. A live
`DoAsyncEvery` handle counts as one pending timer *forever*, so the drain can never finish.

**Fix.** Cancel repeating timers as part of your shutdown — normally by despawning the entities that
own them, since each `Despawn()` should cancel its own handles. The message names the culprit: a
non-zero `timers=` with `in-flight=0` is always this. Disposing the owning actor also works as a
backstop — a repeating timer whose actor refuses a firing with `Disposed` retires itself — but only
for actors you actually dispose.

**The other shape of the same symptom** is `async=` non-zero with everything else at zero: an
`AsyncReentrancy.Interleaved` job is parked on an `await` that never completes — a socket read or an
HTTP call with no timeout is the usual one. The drain waits for it on purpose (stopping the workers
underneath a pending continuation is worse), so the fix is to give the awaited operation a
`CancellationToken` and cancel it before shutting down.

**A third shape** used to be self-inflicted: `await system.StopAsync(t)` from *inside* an async job
counted that job among the ones it was waiting for, so every shutdown spent the full timeout and
reported failure. The drain now excludes its own caller. Prefer starting the shutdown from outside
the job system anyway — see [Shutdown](shutdown.md#starting-a-shutdown-from-inside-a-job).

---

## 10. A timer tick runs after the entity despawned

**Symptom.** An AI tick, a regen tick or a save tick runs once against an entity that has already
been removed — an NRE on a nulled field, or a write into a sector that no longer exists — even though
`Despawn()` cancelled the handle.

**Cause.** Historical. A firing handed the callback to the actor as an ordinary job, and from that
moment `Cancel()` reported `false` and the queued job ran anyway. On a busy actor the gap between
"fired" and "ran" is easily hundreds of milliseconds, so this was not a narrow race.

**Fix.** Nothing to do: `Cancel()` now claims a callback right up until it starts running, so
cancelling from inside the actor's own `Despawn()` job guarantees no further tick. If you carry a
`_despawned` flag checked at the top of every tick purely for this, it is no longer needed.

---

## 11. `JobSystem.Post` with no workers running

**Symptom.** Work handed over with `system.Post(...)` never runs, and neither does a `Sequencer<T>`
built with the `Sequencer(JobSystem, handler)` overload.

**Cause.** `Post` puts an item on the ready queue. Only worker threads drain that queue. With no
dispatcher there is nothing to drain it, and unlike a timer firing there is no fallback.

**Fix.** Start a dispatcher before posting. In tests, either start one or use the actor APIs directly
(a `DoAsync` on an idle actor is flushed by the caller, so it works with zero workers).

`Post` does return `false` once the system has stopped accepting work or been disposed — check it, so
work handed over during a shutdown is not silently lost. It deliberately does *not* return `false`
merely because no worker is running yet: workers can start later, and refusing would break the normal
"build the pool, then start it" order.

---

## 12. An `Exclusive` actor asking itself

**Symptom.** A job on an `AsyncReentrancy.Exclusive` actor awaits `this.Ask(...)` or
`this.AskAsync(...)` and the task never completes. Nothing is blocked, nothing is logged, and the
actor sits there looking idle.

**Cause.** The answer is queued behind the job that is asking, and an Exclusive actor runs nothing
else until that job finishes. The job is waiting for a queue it has itself stopped.
`GuardBlockingWait` cannot see this one: no thread is parked, the await simply never returns.

**Fix.** Split the work into two jobs and let the actor's queue carry the state between them, or use
`AsyncReentrancy.Interleaved`, whose queue keeps moving during an await. With
`JobSystemOptions.DetectBlockingWaitOnWorker` on (the default in DEBUG builds) the library throws an
`InvalidOperationException` at the `Ask` instead of letting you find out in production
(`AsyncJobTests.AnExclusiveActorAskingItselfIsRefusedInsteadOfDeadlocking`,
`SelfDrainTests.SelfAskIsCaughtAfterTheFirstAwaitToo`). `RunAsync` on yourself has the same shape,
but is not guarded — a fire-and-forget self-`RunAsync` is legitimate.

The guard asks two questions, because for a while it only asked one. "Is this actor running on this
thread" is thread-local and goes blank at the first `await`, so a job that awaited anything and *then*
asked itself walked straight past it. It now also follows the async flow, so the self-`Ask` below is
caught as well:

```csharp
RunAsync(async () =>
{
    await Task.Delay(1);        // the old guard stopped seeing the actor here
    await Ask(() => 1);         // …and this hung silently
});
```

---

## 12a. `async void` inside a job

**Symptom.** Shutdown reports a clean drain, the workers stop, and *then* a continuation runs — on a
thread-pool thread, against an actor that has already been disposed.

**Cause.** An interleaved actor installs a `SynchronizationContext` around every job, so an
`async void` method a job calls resumes back on the actor. `RunAsync`/`AskAsync` hand the drain a
task to wait on; an `async void` hands it nothing.

**Fix.** The library now hooks `OperationStarted`/`OperationCompleted`, which the compiler's
`async void` machinery calls, so a drain does wait for those
(`SelfDrainTests.DrainWaitsForAsyncVoidContinuations`). What it still cannot see is an async lambda
nobody awaits:

```csharp
actor.DoAsync(() => _ = SaveAsync());   // invisible to the drain — nothing reports it
actor.RunAsync(() => SaveAsync());      // do this instead
```

`async void` also swallows its own exceptions, so `RunAsync` is the better shape regardless. Keep it
for event-handler signatures that leave you no choice.

---

## 13. Two `JobSystem`s and the process-wide statics

**Symptom.** Stopping one subsystem stops the other. Parallel tests interfere.

**Cause.** `AsyncExecutable.AcceptingWork`, `AsyncExecutable.OnError`, `JobMetrics.GetSnapshot()`,
`JobLog.Current`, `Job.MaxPoolSize` and `TimerRegistry` are process-wide. The first three are
`[Obsolete]` shims that forward to `JobSystem.Default`, which is *not* your system if you constructed
your own.

**Fix.** Pass `System = mySystem` in `JobOptions` and `JobDispatcherOptions`, and use the instance
members: `system.AcceptingWork`, `system.Metrics.Snapshot()`, `JobSystemOptions.Logger`, and
`OnJobError` overridden per actor instead of the static `OnError`. In tests, construct a
`JobSystem` per test with `PublishMeter = false` and `Logger = NullJobLogger.Instance` so cases can
run in parallel without sharing counters.
