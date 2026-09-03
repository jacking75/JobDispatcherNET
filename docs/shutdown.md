# Shutdown

## The recommended sequence is one call

```csharp
var drained = await system.StopAsync(TimeSpan.FromSeconds(10));
if (!drained)
    logger.Warn("some work was still in flight at shutdown");
system.Dispose();
```

`JobSystem.StopAsync(TimeSpan drainTimeout, bool refuseNewWork = false)` returns `Task<bool>`:
`true` if everything drained inside the timeout, `false` if it gave up and stopped anyway.

## What it does, in order

```csharp
public async Task<bool> StopAsync(TimeSpan drainTimeout, bool refuseNewWork = false)
{
    if (refuseNewWork)
        AcceptingWork = false;                      // 0. optional: slam the door first

    var drained = await DrainAsync(drainTimeout);   // 1. wait for quiescence

    AcceptingWork = false;                          // 2. close the gate

    Volatile.Read(ref _timers)?.Dispose();          // 3. stop the timer thread

    foreach (var dispatcher in attachedDispatchers) // 4. stop the workers
        dispatcher.Dispose();

    return drained;
}
```

**1. Drain, with the gate still open.** `DrainAsync` waits until `InFlightJobs == 0` **and**
`ReadyQueueDepth == 0` **and** `PendingTimerCount == 0` **and** `PendingAsyncJobs == 0`, pulsing idle
workers awake and re-checking every 2 ms. New work is *still accepted* during this phase,
deliberately: a job that enqueues follow-up work — an actor telling its neighbours to despawn, a
session flushing its last packet — completes normally instead of being cut off half-way. On timeout
it logs the exact backlog (`in-flight=…, ready=…, timers=…, async=…`) and returns `false`.

`PendingAsyncJobs` is the one that is not obvious. An `AsyncReentrancy.Interleaved` job parked on an
`await` has handed its queue slot back, so it appears in none of the other three counters — the
drain used to call such a system idle and stop the workers while a continuation was still on its way
back onto the actor. It is counted from the moment `RunAsync`/`AskAsync` returns an unfinished task
until that task completes. The corollary: a job awaiting something that never finishes (a socket
read with no timeout, say) now burns the whole drain timeout instead of being silently abandoned,
and `async=…` in the timeout line names it.

**2. Close the gate.** `AcceptingWork = false`. Every actor on the system now refuses new jobs with
`DropReason.ShuttingDown`; `DoAsync` returns `false`, `Ask`/`RunAsync` fault with
`JobRejectedException`.

**3. Stop the timer thread.** Pending timers are discarded, not fired, and counted in
`TimersDiscarded`. See [Timers](timers.md#shutdown).

**4. Stop the workers.** Every `JobDispatcher` that attached to this system (they attach in their
constructor) is disposed, which is `TryStop(TimeSpan.FromSeconds(5))`: cancel the token, pulse the
waiters, join each thread, and log the thread name of any worker that would not stop — almost always
a job stuck on a lock or a synchronous wait.

The three phases are ordered this way on purpose: the workers are the last thing to go, because they
are what performs the drain.

### `refuseNewWork: true`

Pass it when external producers must be cut off *before* draining — a crash-stop, a health-check
failure, or when the process is being killed and you only care about finishing what is already
queued. The cost is that cascading work is refused too, so shutdown logic that posts jobs (despawn
broadcasts, "player left" notices) will be dropped. For an orderly shutdown, leave it `false` and
stop your *external* input yourself first, as the sample below does.

### `DrainAsync` on its own

```csharp
await system.DrainAsync(TimeSpan.FromSeconds(5));
```

Use it when you want quiescence without stopping anything — after a wave of despawns, between test
phases, or before taking a consistent snapshot. It is the same wait `StopAsync` performs. It needs
workers running (or the calling thread to be an actor's leader) to make progress, and it never
completes while a repeating timer is still armed.

### `IAsyncDisposable`

```csharp
await using var system = new JobSystem(options);
```

`JobSystem.DisposeAsync()` is `StopAsync(TimeSpan.FromSeconds(5))` followed by `Dispose()`.
`Dispose()` alone skips the drain entirely: it closes the gate, kills timers and workers, and
disposes the metrics meter. Use synchronous `Dispose` only after a `StopAsync` you have already
awaited, or when you genuinely do not care about in-flight work.

### Per-actor `DisposeAsync`

```csharp
await player.DisposeAsync();   // wait for this one actor's queue, then retire it
```

`AsyncExecutable.DisposeAsync` waits on a `TaskCompletionSource` that the flush loop signals when the
actor's counter reaches zero — signal-based, no polling — and then marks the actor completed, so any
later `DoAsync` returns `false` with `DropReason.Disposed`
(`ShutdownTests.DisposeAsyncWaitsForTheQueueToDrain`).

Something must still be draining that actor while you await: with the dispatcher already gone and no
leader running, the wait can only time out at whatever level you wrap it in. Retire actors **before**
stopping the system, not after.

### `TryStop` on a dispatcher

```csharp
if (!dispatcher.TryStop(TimeSpan.FromSeconds(5)))
    logger.Error("a worker refused to stop — check the log for the thread name");
```

Same work as `Dispose()`, but it tells you whether every worker actually exited. Prefer it in
shutdown paths where a hang is a bug you want reported rather than swallowed. It is idempotent and
returns `true` on a second call.

## Worked example — `AdvancedMmorpgServer/GameServer.cs`

```csharp
public sealed class GameServer : IDisposable
{
    private readonly JobSystem _system;
    private JobDispatcher? _dispatcher;

    public GameServer(ServerConfig config)
    {
        // One system owns the workers, the timer thread and the metrics for this server.
        _system = new JobSystem(new JobSystemOptions
        {
            Name = "game",
            TimerPrecision = TimerPrecision.Coarse,
            MaxJobDuration = TimeSpan.FromMilliseconds(50),
        });
        ...
    }

    public void Start()
    {
        _dispatcher = new JobDispatcher(config.Server.WorkerThreads, new JobDispatcherOptions
        {
            System = _system,
            RestartFailedWorkers = true,
            MaxRestartsPerWorker = 5,
            RestartBackoff = TimeSpan.FromSeconds(1),
        });
        _ = _dispatcher.RunWorkerThreadsAsync();
        ...
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // 1. Stop external input. Internal shutdown work still needs the actors.
        _network.Stop();

        // 2. Despawn everything and cancel the timer chains, so the system can reach quiescence.
        _world.Stop();

        // 3. Drain what is left, then stop the timer thread and the workers.
        var drained = _system.StopAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        if (!drained)
            JobLog.Warn("[Server] some work was still in flight at shutdown");

        _system.Dispose();
    }
}
```

Three things to copy from it:

- **Stop your own input first.** `StopAsync` does not know about your listening socket. Closing it
  before draining is what makes the drain finite.
- **Cancel repeating timers before draining.** `GameWorld.Stop()` posts one job that despawns every
  session, NPC and player; each `Despawn()` cancels that entity's `ITimerHandle`s. Without this the
  drain waits on `PendingTimerCount` forever and burns the whole timeout.
- **Then one call.** `_world.Stop()` itself just awaits `System.DrainAsync(5s)` to let the despawn
  cascade settle; `StopAsync` then does everything else.

### Disposing an actor after the workers are gone

`await actor.DisposeAsync()` waits for that actor's queue to drain, and nothing drains a queue once
its workers have stopped — so disposing actors *after* `StopAsync` waits forever. Either dispose them
while the pool is still up, or give the wait a bound:

```csharp
if (!await session.DisposeAsync(TimeSpan.FromSeconds(2)))
    log.Warn($"{session.Name} still had {session.RemainingTaskCount} jobs queued");
```

`DisposeAsync(TimeSpan)` and `DisposeAsync(CancellationToken)` return `false` instead of throwing
when they give up. The actor stops accepting work either way.

## Why the old four-step sequence is gone

The v2.0 README documented this:

```csharp
AsyncExecutable.AcceptingWork = false;   // block new input
world.Stop();                            // drain remaining work
dispatcher.Dispose();                    // stop workers + join
TimerRegistry.DisposeAll();              // clean up non-worker timers
```

Every line of it was a problem:

- **`AcceptingWork` was a process-wide static.** Two job systems in one process (a game world and a
  background IO pool) shared one gate, so stopping either stopped both. The sample server had to set
  it back to `true` in its own `Dispose` to avoid poisoning the next test run. It is now an instance
  property, `system.AcceptingWork`; the static remains as `[Obsolete]` and forwards to
  `JobSystem.Default`.
- **Closing the gate *first* killed cascading shutdown work.** Every "tell my neighbours I'm gone"
  job posted during `world.Stop()` was refused. `StopAsync` drains before it closes the gate for
  exactly this reason, and `refuseNewWork: true` is there when you want the old behaviour.
- **"Drain" had no signal, so it was a sleep.** `GameWorld.Stop` ended with `Thread.Sleep(200)` and
  hoped. `DrainAsync` waits on the actual in-flight/ready/timer counters.
- **`TimerRegistry.DisposeAll()` was needed because timers lived on whichever thread created them.**
  Timers now live on the system's own timer thread and are disposed with it. `TimerRegistry` survives
  as an `[Obsolete]` no-op so v2.0 shutdown code still compiles; delete the call.
- **`dispatcher.Dispose()` ignored a failed join.** `TryStop` reports it and names the stuck thread.

Ordering was also easy to get wrong: disposing the dispatcher before the actors had drained left
nothing running to execute the remaining jobs, so `DisposeAsync` on an actor would hang. `StopAsync`
fixes the order in one place.
