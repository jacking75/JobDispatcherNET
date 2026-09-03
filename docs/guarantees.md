# Guarantees & non-guarantees

Read [Concepts](concepts.md) first — this page states the contract that the leader-flush model
produces.

## What is guaranteed

### Per-actor serialization

Two jobs belonging to the same `AsyncExecutable` never run at the same time, on any thread, under
any producer count. This holds for ordinary jobs, timer firings, `Ask` jobs and async-continuation
jobs alike, because all four go through the same admission counter.

Verified by `SerializationTests.SameActorNeverRunsOnTwoThreadsAtOnce` (8 producers × 20,000 jobs,
maximum observed concurrency must be exactly 1) and by the bounded-queue stress in
`RegressionTests.P0_1_BoundedRejectionNeverStrandsTheLeader`.

Different actors run fully in parallel — that is the whole point of the library.

### FIFO for a single producer

Jobs posted by **one** thread to **one** actor run in the order that thread posted them. The
producer's reservation and enqueue happen in its own program order, and the underlying queue is FIFO.
(`SerializationTests.SingleProducerOrderIsPreserved`.)

### Memory visibility

A job sees every write made by the previous job on the same actor, with no `volatile`, no `lock` and
no `Interlocked` of your own.

Two cases:

- **Same thread.** The leader ran the previous job and is running this one; program order is enough.
- **Leadership changed hands.** The outgoing leader's last act for a job is
  `Interlocked.Decrement(ref _remainingTaskCount)` — a full barrier that publishes everything it
  wrote. The incoming leader's first act is the `Volatile.Read` + `Interlocked.CompareExchange` on
  the same counter, which acquires it. `ConcurrentQueue<T>`'s enqueue/dequeue pair adds a second
  edge for the job object itself. So "job N's writes happen-before job N+1's reads" holds for every
  field of the actor, not just the ones passed through the job.

This does **not** extend to state shared between *different* actors. Two actors run in parallel by
design; anything both of them touch needs its own synchronization, or should be owned by a third
actor. See [Pitfalls](pitfalls.md).

### Re-entrancy

- `DoAsync` **on yourself, from inside your own job**: the counter is still non-zero (your job is
  counted until it returns), so the new job is simply queued and runs later in the same flush loop.
  It never recurses.
- `DoAsync` **on another idle actor, from inside a job**: handed to `ThreadContext.ExecuterQueue`
  and flushed after the current actor drains, on the same thread, iteratively. Deep actor→actor
  chains do not grow the stack.

### The rejected job is never partially run

If `DoAsync` returns `false`, the job did not run and never will. The `JobEntry` is recycled into the
pool by the library and is never handed to your `OnDropped` callback, so there is nothing to keep a
reference to.

## What is *not* guaranteed

### Ordering across producers

Two threads posting to the same actor race. Whoever wins the admission CAS is first. If order across
sources matters, funnel them through one `Sequencer<T>` (which does guarantee arrival order and
single-threaded handling — `SequencerTests.ItemsAreHandledInArrivalOrderByOneThreadAtATime`).

### Which thread runs your job

Under the default `ExecutionMode.LeaderFlush` it is whichever thread found the actor idle. See the
table in [Concepts](concepts.md). If you need it to be a worker, set
`Mode = ExecutionMode.Scheduled` **and** keep a dispatcher running — `Scheduled` falls back to an
inline flush when `JobSystem.HasWorkers` is false.

### Latency

Nothing here is real-time. A job waits for however long the actor's backlog takes, plus (in
`Scheduled` mode) a trip through the ready queue, plus up to `JobDispatcherOptions.IdleWaitMs` if
every worker happened to be parked (producers pulse the wait, so this is a bound, not a typical
cost).

### Timer precision

See [Timers](timers.md). `TimerPrecision.Coarse` is bounded by the OS timer resolution.

## Exceptions

A job that throws is caught inside the flush loop. Concretely:

1. `JobMetrics` counts it in **both** `TotalJobsExecuted` and `TotalJobsFailed`.
2. `protected virtual void OnJobError(Exception)` is called on the actor. Override it to handle
   failures per actor (drop the session that owns it, for instance). The default implementation
   forwards to the process-wide `AsyncExecutable.OnError` hook if one is set, otherwise logs
   through the system's `IJobLogger`.
3. The job entry is recycled regardless (its `Execute` recycles in a `finally`).
4. **The flush loop continues with the next job.** One bad job does not stop the actor, does not
   kill the worker, and does not drop the queue.
5. If `JobOptions.MaxConsecutiveFailures > 0` and that many jobs threw back to back, the actor moves
   to `IsFaulted`: it refuses further work with `DropReason.Faulted` until `ClearFault()` is called.
   A successful job resets the streak.

An exception thrown by `OnJobError` itself is caught and logged; it cannot take the worker down.

### Failures of the request/response and async APIs

`Ask`, `AskAsync` and `RunAsync` hand their exception to the caller through the returned task. That
used to be *all* they did: the job looked like a success to the flush loop, so `TotalJobsFailed`
never moved, `OnJobError` was never called, and — worst of it — the failure **reset** the
consecutive-failure streak, so an actor with a failing `Ask` between its failing `DoAsync`s could
never reach `IsFaulted`. Now:

| API | returned task | `TotalJobsFailed` | streak | `OnJobError` |
|---|---|---|---|---|
| `DoAsync` | — | ✓ | ✓ | ✓ |
| `RunAsync` | faulted | ✓ | ✓ | ✓ |
| `Ask` / `AskAsync` | faulted | ✓ | ✓ | only with `JobOptions.ReportAwaitedFailures` |

`RunAsync` reports to `OnJobError` because it returns no value and is commonly fire-and-forget: a
failure it does not report is recorded nowhere at all. `Ask`/`AskAsync` do not, because their caller
is by construction awaiting the exception and reporting it again would report it twice — set
`ReportAwaitedFailures` if you would rather have both.

One caveat on the streak for async jobs: the state-machine step that *starts* the job counts as a
success when it returns its task, so a `RunAsync` whose failure arrives later contributes a success
and then a failure. It is the price of counting the failure at all, and it only matters when
`MaxConsecutiveFailures` is set very low.

An exception escaping a `JobSystem.Post` action, or escaping a flush pulled off the ready queue, is
caught in `JobSystem.DrainReady`, logged, and forwarded to `AsyncExecutable.OnError`.

An exception that escapes the whole worker loop (from `IRunnable.Run`, say) kills the thread; the
dispatcher's supervisor logs which actor was running and restarts the slot. See
[Tuning](tuning.md#worker-supervision). An `OperationCanceledException` counts as a clean exit only
when the dispatcher is actually stopping; at any other time it is a crash like any other, and the
slot is restarted.

**Actor names cannot forge a log line.** `Name` goes straight into log messages, so a server that
names actors after player nicknames would otherwise let a newline in a nickname write a whole extra
log entry. Control characters in a name become `?` and the name is capped at 128 characters
(`BoundedQueueTests.AnActorNameCannotForgeALogLine`).

**A throwing `IJobLogger` cannot take a thread down.** The library logs from worker threads and from
the timer thread, neither of which has anything above it to catch an escaping exception — a log sink
that failed once used to stop every timer on the system permanently. Every library-internal log call
goes through a wrapper that swallows whatever the logger throws, the timer thread guards each
iteration, and a flush that fails anyway hands the actor's leadership back to the ready queue rather
than leaving it wedged.

## What `DoAsync` returning `false` means

`DoAsync` and `DoAsync<TState>` return `bool`. `false` means the job was refused and will never run.
`DoAsyncAfter`/`DoAsyncEvery` signal the same condition by returning a handle whose `IsPending` is
already `false`. The reasons, from `DropReason`:

| Reason | Cause |
|---|---|
| `QueueFull` | The actor is at its effective bound — `JobOptions.MaxQueueSize`, or `JobSystemOptions.DefaultMaxQueueSize` if the actor set none. (An interleaved `await` continuation is exempt — see [Async jobs](#async-jobs).) |
| `ShuttingDown` | `JobSystem.AcceptingWork` is false (set by `StopAsync`, `Dispose`, or by you). |
| `Disposed` | The actor's `DisposeAsync` has completed. |
| `Faulted` | The actor tripped `MaxConsecutiveFailures`. |
| `TimerQueueFull` | The actor already has `JobOptions.MaxPendingTimers` timers armed. Only from `DoAsyncAfter`/`DoAsyncEvery` — see [Timers](timers.md#bounding-armed-timers). |

Every refusal increments `TotalJobsDropped`. With `DropPolicy.Reject` (the default) your
`JobOptions.OnDropped(AsyncExecutable actor, DropReason reason)` callback also runs, on the producer's
thread; `DropPolicy.Silent` skips only the callback, not the counter.

`Ask`, `AskAsync` and `RunAsync` cannot return `false`, so they fault their returned task with a
`JobRejectedException` instead. Await them and you will see the refusal — do not fire and forget.
(Note that the exception's `Reason` is currently always `QueueFull` on that path, whatever the actual
cause was; use the message, or `OnDropped`, if you need to tell a shutdown apart from a full queue.)

## Async jobs

`RunAsync(Func<Task>)` and `AskAsync<T>(Func<Task<T>>)` queue an asynchronous job. What happens at
each `await` depends on `JobOptions.AsyncReentrancy`.

### `AsyncReentrancy.Interleaved` (default)

While a job runs, the actor installs its own `SynchronizationContext`. An `await` inside the job
therefore captures that context, and the continuation is posted back as **a new job on this actor's
queue**. So:

- The continuation runs under the actor's serialization guarantee — it is an ordinary job on the
  queue, so no other job on that actor runs concurrently with it
  (`AsyncJobTests.InterleavedContinuationsComeBackOntoTheActor`).
- Other queued jobs **do** run during the await, and they may run *between* the await and the
  continuation. Your invariants must survive that — this is the same trade-off Orleans makes.
- **Do not use `ConfigureAwait(false)`.** It explicitly opts out of the captured context, so the
  continuation resumes on the thread pool, outside the actor's queue, touching actor state with no
  serialization at all. This is the single most damaging mistake in an interleaved async job.

```csharp
public sealed class AccountActor : AsyncExecutable
{
    private int _balance;

    public Task<int> ReloadAsync(IDbConnection db) => AskAsync(async () =>
    {
        var fresh = await db.ReadBalanceAsync();   // no ConfigureAwait(false)
        _balance = fresh;                          // back on the actor, still serialized
        return _balance;
    });
}
```

**The continuation is never refused.** It is the second half of a job the actor already admitted, so
turning it away would strand the async state machine and leave the task from `RunAsync`/`AskAsync`
permanently incomplete. It therefore bypasses `MaxQueueSize` *and* the disposed / shutting-down /
faulted checks, and runs whatever the actor's state. Two consequences worth knowing:

- `RemainingTaskCount` can sit above `MaxQueueSize`, by the number of jobs currently awaiting.
  Admission of genuinely new work still respects the bound.
- Shutdown waits for these jobs. `JobSystem.PendingAsyncJobs` counts every async job parked on an
  `await`, and both `DrainAsync` and `AsyncExecutable.DisposeAsync` block until it reaches zero — so
  the workers are still there when the continuation lands. An `await` that never completes therefore
  costs you the whole drain timeout; see [Shutdown](shutdown.md).

#### What the drain can and cannot see

The counter is fed by `RunAsync`/`AskAsync`, which hand it a task to watch, and by the `async void`
notifications the compiler sends through the actor's `SynchronizationContext`. It cannot see an async
lambda nobody awaits:

```csharp
actor.RunAsync(() => SaveAsync());       // counted
actor.DoAsync(() => HandleAsync());      // counted (async void)
actor.DoAsync(() => _ = SaveAsync());    // NOT counted — nothing reports it
```

The third line's continuation still comes back onto the actor, past the bound and past the disposed
check, but no drain waits for it: shutdown can stop the workers and dispose the actor with that
continuation still on its way. Wrap it in `RunAsync`.

A drain requested from inside one of the actor's own async jobs does not count that job — otherwise
`await DisposeAsync()` inside `RunAsync` would wait for itself forever. See
[Shutdown](shutdown.md#disposing-an-actor-from-inside-its-own-async-job).

### `AsyncReentrancy.Exclusive`

The actor holds one extra reservation for the whole lifetime of the async job. No other producer can
become leader and the flushing thread parks instead of taking the next job, so **nothing else on the
actor runs until the async job finishes** (`AsyncJobTests.ExclusiveReentrancyBlocksOtherJobsUntilTheAwaitCompletes`).

Because the actor is suspended, the continuation does not need to come back to it: it runs on the
thread pool (`TaskScheduler.Default`). When it completes, leadership is handed back — to a worker via
the ready queue if one is running, else flushed inline.

Simplest to reason about, and the right choice when the awaited operation is short. One slow await
stalls every message to that actor, including timer firings. A throwing async job releases the
suspension correctly (`AsyncJobTests.ExclusiveActorRecoversWhenTheAsyncJobThrows`).

### Blocking is not an option

`Ask(...).Result`, `.Wait()`, or any other synchronous wait for another actor **from inside a job**
deadlocks: the thread that would run the work you are waiting on is the thread you just parked.
`AskSync` calls `JobDiagnostics.GuardBlockingWait`, which throws an `InvalidOperationException`
naming the actor when `JobSystemOptions.DetectBlockingWaitOnWorker` is on (the default in DEBUG
builds). Call the same guard at the top of your own blocking helpers.

When the job itself throws, `AskSync` rethrows **that** exception, not an `AggregateException`
wrapping it, so `catch (InvalidDataException)` around an `AskSync` works the way it reads
(`AskTests.AskSyncThrowsTheJobsOwnExceptionRatherThanAnAggregate`). It still throws `TimeoutException`
when the actor does not answer in time. `Ask`/`AskAsync` are unaffected — awaiting a task has always
unwrapped.
