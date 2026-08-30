# Concepts — the threading model

This is the page to read before anything else. Everything JobDispatcherNET guarantees, and every
way it surprises people, follows from the rules below.

## The one-sentence model

An **actor** (a subclass of `AsyncExecutable`) owns a queue. Jobs posted to one actor run **one at a
time, in queue order**, so the actor's own fields need no locks. Different actors run fully in
parallel. Nobody dedicates a thread to an actor — instead, **whichever producer finds the actor idle
becomes its leader** and drains the queue.

## The pieces

| Type | Role |
|---|---|
| `JobSystem` | Owns the worker threads' ready queue, the timer thread, the metrics and the shutdown gate. `JobSystem.Default` is created on first use. |
| `AsyncExecutable` | The actor base class. Owns a `ConcurrentQueue<JobEntry>` and an `int` reservation counter. |
| `JobDispatcher` / `JobDispatcher<T>` | A pool of real OS threads (not thread-pool threads) that drain the system's ready queue. |
| `TimerService` | One dedicated thread per `JobSystem`. Not exposed publicly; reached through `DoAsyncAfter` / `DoAsyncEvery`. |
| `ThreadContext` | `[ThreadStatic]` per-thread state: which actor this thread is currently flushing, whether this thread is a worker, and the pending nested-actor queue. |

## Leader flush

`AsyncExecutable` has no thread of its own. Posting a job does two things:

1. **Reserve a slot** in the actor's counter (`_remainingTaskCount`).
2. **Enqueue** the job.

If the reservation moved the counter from `0` to `1`, the caller has just found the actor idle and
becomes its **leader**: it runs the flush loop, which dequeues and executes jobs until the counter
falls back to zero. If the counter was already non-zero, some other thread is already the leader and
will pick this job up; the producer returns immediately.

The consequence is that "actor code runs on a worker thread" is only true when the producer that
found the actor idle *was* a worker thread. A socket thread, an `async` continuation on the thread
pool, or `Main` will otherwise run the actor's jobs itself. That is the **hijack**, and
`ExecutionMode.Scheduled` is the supported cure — see the table below.

## The admission CAS — why the counter, not the queue, is authoritative

```csharp
// AsyncExecutable.Admit — simplified
int current;
while (true)
{
    current = Volatile.Read(ref _remainingTaskCount);
    if (_maxQueueSize != 0 && current >= _maxQueueSize)
    {
        task.Discard();                       // recycled by the library
        return Refuse(DropReason.QueueFull);  // DoAsync returns false
    }
    if (Interlocked.CompareExchange(ref _remainingTaskCount, current + 1, current) == current)
        break;
}

_queue.Enqueue(task);

if (current != 0)
    return true;    // somebody else already owns the flush

// current == 0 → we are the leader for this actor
```

Two invariants come out of this:

- **Exactly one thread observes `current == 0`** for a given idle→busy transition, so exactly one
  leader exists at a time. Serialization is not a lock; it is this counter.
- **The counter never claims fewer jobs than the queue holds.** A slot is reserved *before* the
  enqueue and released *after* the job has run. So the leader may briefly see an empty queue while
  the counter says `> 0` (a producer is between its CAS and its `Enqueue`) — it spins for that.
  It can never see `counter == 0` while a job is still pending.

The flush loop therefore has two exits, and the second one is the important one:

```csharp
// AsyncExecutable.Flush — simplified
if (_queue.TryDequeue(out var job))
{
    ExecuteJob(job);
    if (Interlocked.Decrement(ref _remainingTaskCount) == 0) return;   // drained
}
else if (Volatile.Read(ref _remainingTaskCount) == 0)
{
    return;   // nothing reserved → nothing is coming
}
else
{
    spinner.SpinOnce();   // a producer is mid-admission; wait for its Enqueue
}
```

In v2.0 the code incremented the counter first and only then tried to write to a bounded channel. A
rejected write left the counter claiming a job the queue never received, and the flush loop had no
"counter is zero" exit — so the leader spun a core forever, and the next producer became a *second*
concurrent leader for the same actor. That is defect **P0-1**
(`JobDispatcherNET.Tests/RegressionTests.cs::P0_1_BoundedRejectionNeverStrandsTheLeader`). The queue
is now always unbounded; back-pressure lives entirely in the CAS.

## Which thread runs the job

`ThreadContext.IsWorkerThread` is true only on threads created by a `JobDispatcher`/`JobDispatcher<T>`.
`ThreadContext.CurrentExecuter` is non-null while this thread is inside a flush loop.

| Call site | Thread that runs the job | Guarantee |
|---|---|---|
| Worker thread → **idle** actor, not already flushing another actor (either mode) | the calling worker, inline, before `DoAsync` returns | Serialized. Lowest latency path. `Scheduled` changes nothing for a worker producer. |
| Worker thread → **busy** actor | the thread that is currently the actor's leader | Serialized; FIFO relative to this producer's other jobs. |
| Inside another actor's job (`ThreadContext.CurrentExecuter != null`) → **idle** actor | the same thread, **after** the current flush finishes, via `ThreadContext.ExecuterQueue` | Serialized. No recursion, so the stack does not grow with actor→actor depth. |
| Non-worker thread → **idle** actor, `LeaderFlush` | **the calling thread** — socket thread, thread-pool continuation, `Main` (this is the hijack) | Serialized, but the caller pays for the whole queue. |
| Non-worker thread → **idle** actor, `Scheduled`, workers running | a worker, pulled off `JobSystem`'s ready queue | Serialized; `DoAsync` returns immediately. |
| Non-worker thread → **idle** actor, `Scheduled`, **no** workers running | the calling thread — `Scheduled` silently falls back to a hijack when `JobSystem.HasWorkers` is false | Serialized. |
| `DoAsyncAfter` / `DoAsyncEvery` fires, actor **idle**, workers running | a worker (the timer thread only schedules) | Serialized with ordinary jobs on the same actor. |
| `DoAsyncAfter` / `DoAsyncEvery` fires, actor **idle**, **no** workers running | the **timer thread**, plus a one-time `Warn` from `JobSystem.WarnTimerFallbackOnce` | Serialized. The fallback keeps single-process/no-dispatcher use working (defect P0-3). |
| Timer fires while the actor is **busy** | the thread that is currently the leader | Serialized. |
| `JobSystem.Post(Action)` | a worker. Nothing runs it if no workers exist. | The supported way to get off a non-worker thread. |
| Two or more producer threads → same actor | whichever thread happens to be the leader | Serialization: **yes**. Relative order of the two producers' jobs: **no guarantee** — use `Sequencer<T>`. |

A worker whose flush is capped by `JobOptions.MaxJobsPerFlush` hands the actor back to the ready
queue instead of draining it to empty, so a hot actor cannot own a worker forever. The remaining jobs
then run on whichever worker picks the actor up next — still one at a time.

### The `ExecuterQueue` hand-off in detail

When actor A's job calls `b.DoAsync(...)` and B is idle, A's thread does **not** start flushing B
inline. `Admit` sees `ThreadContext.CurrentExecuter != null` and pushes B onto the thread's
`ExecuterQueue` instead:

```csharp
private void RunFlushLoop()
{
    try
    {
        ThreadContext.CurrentExecuter = this;
        Flush();

        while (ThreadContext.ExecuterQueue.TryDequeue(out var next))
            next.Flush();
    }
    finally
    {
        ThreadContext.CurrentExecuter = null;
    }
}
```

So B's jobs run on the same thread, but only once A is fully drained, and the loop is flat — an
actor→actor→actor chain 500 deep costs 500 queue entries, not 500 stack frames
(`SerializationTests.NestedDispatchDoesNotRecurse`). Note that `ThreadContext.CurrentExecuter` keeps
reporting the *outermost* actor while nested actors are being drained; it is a "am I inside actor
code" flag, not a precise identity.

### Multi-producer ordering

Two producer threads posting to the same actor are serialized against each other, but their
*relative* order is whatever the CAS race decided. If a session's `EnterZone` and `Move` packets can
be handed to two different threads, `Move` may be admitted first. `Sequencer<T>` exists for exactly
this: funnel one source through it and the handler sees arrival order, one item at a time, on a
worker.

```csharp
// system is a JobSystem; the drain is posted to its worker pool for you.
var packets = new Sequencer<string>(system, line => Handle(session, line));

// IO thread only ever enqueues:
if (!packets.Enqueue(line))
    Log("session sequencer stopped, packet dropped");
```

## Where to go next

- [Guarantees & non-guarantees](guarantees.md) — the precise contract, including memory visibility.
- [Pitfalls](pitfalls.md) — the mistakes this model turns into hangs rather than errors.
