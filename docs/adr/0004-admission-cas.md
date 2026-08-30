# ADR 0004 — Queue admission is a CAS on the counter

- **Status**: Accepted in v2.1. Supersedes the "increment, then try to write to a bounded channel"
  admission of v2.0.
- **Fixes**: P0-1 (a rejected write strands the leader and can admit a second one)
- **Affects**: `AsyncExecutable.Admit`, `AsyncExecutable.Flush`; replaces `Channel<JobEntry>` with
  `ConcurrentQueue<JobEntry>`

## Context

`JobOptions.MaxQueueSize` bounds an actor's queue so a producer faster than the actor cannot grow it
until the process dies. In v2.0 the bound was enforced by the queue: the actor held a **bounded**
`Channel<JobEntry>`, and posting a job did

```csharp
Interlocked.Increment(ref _remainingTaskCount);        // claim a slot
if (!_jobQueue.Writer.TryWrite(task))                  // ...then try to actually write it
    Interlocked.Decrement(ref _remainingTaskCount);    // ...and give it back on failure
```

Between the increment and the decrement the counter claims a job the queue does not have — a ghost
entry. And the flush loop only ever exited by observing that the decrement *after running a job*
reached zero; a failed `TryRead` spun with no counter check at all.

That is defect **P0-1**. With `MaxQueueSize = 1`:

```
L (leader, worker A)   : running job1, job2 in the channel        count = 2
Q (producer, thread C) : Increment                                count = 3
Q                      : TryWrite → fails, the channel is full
   --- Q is preempted here ---
L                      : job1 done, Decrement → 2   (≠ 0, keep going)
L                      : TryRead job2, run it, Decrement → 1   (≠ 0, keep going)
L                      : TryRead fails → spin
Q                      : Decrement                                count = 0
L                      : count is 0 but the loop never looks → spins forever, burning a core
M (new producer)       : Increment 0 → 1 → becomes leader → flushes the SAME actor concurrently
```

The window is tens of nanoseconds wide, so it needs a preemption at exactly the wrong moment. At a
few thousand rejections a second, sustained for days, it happens — and the outcome is a burned core
plus a permanent, silent loss of the per-actor serialization guarantee, which is the library's entire
contract.

## Decision

**Make the counter the sole authority on admission, and make the queue unbounded.**

```csharp
int current;
while (true)
{
    current = Volatile.Read(ref _remainingTaskCount);
    if (_maxQueueSize != 0 && current >= _maxQueueSize)
    {
        task.Discard();                        // recycled into the pool by the library
        return Refuse(DropReason.QueueFull);
    }
    if (Interlocked.CompareExchange(ref _remainingTaskCount, current + 1, current) == current)
        break;
}

_queue.Enqueue(task);       // unbounded ConcurrentQueue → cannot fail

if (current != 0)
    return true;            // somebody else already owns the flush
// current == 0 → we are the leader
```

Plus a second exit in the flush loop, which is what makes the invariant safe rather than merely
tidy:

```csharp
else    // TryDequeue failed
{
    if (Volatile.Read(ref _remainingTaskCount) == 0)
        return;             // nothing reserved → nothing is coming
    spinner.SpinOnce();     // a producer is between its CAS and its Enqueue
}
```

The invariant is now one-directional and never violated: **the counter is greater than or equal to
what the queue holds.** A slot is reserved before the enqueue and released only after the job has
run. So the leader may see an empty queue while the counter is positive — a producer mid-admission —
and spins for it. It can never see a zero counter while a job is still pending, so exiting on zero is
safe: any producer arriving after that point reads zero itself, wins the CAS, and becomes the leader.

`ConcurrentQueue` replaces `Channel<T>` because the only thing the channel provided over it was the
bounding that just moved into the CAS (and an async reader nobody used).

## Consequences

**Good**

- The stranded-leader spin and the concurrent second leader are both structurally impossible; there
  is no state in which the counter and the queue can disagree in the dangerous direction.
- The rejection path is cheaper: a read and a compare, with no channel write attempted and no
  compensating decrement.
- The bound is now exactly "queued + in-flight jobs", a number users can reason about and monitor
  (`RemainingTaskCount`, `MaxObservedQueueDepth`).
- The unbounded queue removes the bounded channel's internal lock from the hot path.
- Rejected jobs are recycled into the pool by the library instead of being handed to the user's
  `OnDropped` callback, so a callback that stashes the argument can no longer resurrect a reused
  entry. The callback signature became `(AsyncExecutable actor, DropReason reason)`.

**Bad**

- The admission CAS can spin under heavy multi-producer contention on a single actor. This is the
  same contention the old `Interlocked.Increment` had; it is now a loop rather than a single
  instruction, so a very hot actor pays slightly more per contended post. If that shows up in a
  profile, the actor is already the bottleneck for other reasons.
- The queue no longer enforces anything, so any future code path that enqueues without going through
  `Admit` would break the invariant silently. All enqueues go through `Admit`, and the comment in the
  source says why.
- "Unbounded queue" is now true in the literal sense: forget to set `MaxQueueSize` and there is no
  second line of defence at all. [Tuning](../tuning.md#maxqueuesize) says how to size it.

## Regression test

`JobDispatcherNET.Tests/RegressionTests.cs::P0_1_BoundedRejectionNeverStrandsTheLeader` — eight
producers × 40,000 attempts against `MaxQueueSize = 1`, ignoring rejections, with a `Thread.Yield()`
every 64 iterations to widen the window between the CAS and the enqueue. It asserts that the observed
concurrent-execution count never exceeded 1, that every accepted job ran, that `RemainingTaskCount`
returns to 0, and that the run both accepted *and* rejected work (so the test cannot pass vacuously).

## Alternatives considered

- **Keep the bounded channel and add the safety-net exit only** (roadmap fix 2 without fix 1). The
  infinite spin goes away, but the ghost entry remains: the counter can still read non-zero for a job
  the queue will never receive, so a leader can exit *early* while a producer is mid-decrement and
  another can enter concurrently. Rejected — it treats the symptom.
- **A lock around admission.** Correct and obvious, and it puts a lock on the hottest path in the
  library, which is the one thing the design is built to avoid.
- **Semaphore-based admission** (`SemaphoreSlim.Wait(0)` for the bound). Equivalent semantics at a
  higher cost, and it would still need a separate counter for the leader-election `0 → 1` edge.
