# ADR 0001 — Producers flush actors ("leader flush")

- **Status**: Accepted. Follows the design of the C++ [JobDispatcher](https://github.com/ujentus/JobDispatcher);
  recorded retroactively.
- **Affects**: `AsyncExecutable.Admit`, `AsyncExecutable.Flush`, `ThreadContext.ExecuterQueue`

## Context

The library's job is to let many objects — players, NPCs, rooms, sectors — each process their own
messages serially, while different objects proceed in parallel, in a game server with tens of
thousands of live objects.

The obvious designs do not fit:

- **A thread per actor** is impossible at that count.
- **A lock per actor** serialises correctly but blocks the caller, and lock ordering between actors
  (A damages B, B notifies A) produces deadlocks that are hard to rule out statically.
- **A queue per actor plus a scheduler that assigns queues to threads** works, but every message pays
  a trip through a central scheduler even when the target is idle and the caller could just run it.

## Decision

An actor has a queue and an integer counter, and no thread of its own. Posting a job:

1. Reserves a slot in the counter.
2. Enqueues the job.
3. If the reservation moved the counter from `0` to `1`, the caller has found the actor idle and
   becomes its **leader**: it runs the flush loop until the counter falls back to zero.

Exactly one thread can observe the `0 → 1` transition, so exactly one leader exists at any moment.
Serialization is a property of the counter, not of a lock, and no thread ever waits for another
thread to release anything.

Two supporting rules make it usable:

- **No recursion between actors.** If the leader is already flushing an actor
  (`ThreadContext.CurrentExecuter != null`) and it makes another actor ready, that actor goes on a
  per-thread `ExecuterQueue` and is flushed after the current one drains. An actor→actor chain 500
  deep costs 500 queue entries, not 500 stack frames.
- **An escape hatch for non-worker producers.** `ExecutionMode.Scheduled` (added in v2.1) lets a
  producer hand the actor to the worker pool's ready queue instead of flushing it, for actors reached
  from socket or thread-pool threads. See [ADR 0002](0002-dedicated-threads.md).

## Consequences

**Good**

- Sending to an idle actor from inside a worker is nearly free: no scheduler, no wake-up, no context
  switch. This is the common case in a game server, where actors mostly message each other.
- No locks means no lock ordering and no priority inversion between actors.
- The mechanism is small enough to read in one sitting, which matters for a library whose whole value
  proposition is "you can understand your concurrency".

**Bad — and these drive most of the rest of the documentation**

- **The caller's thread runs actor code.** A socket thread that posts to an idle actor drains that
  actor's whole queue before returning. This is the single most common surprise for new users; it is
  why `ExecutionMode.Scheduled`, `JobSystem.Post` and `Sequencer<T>` exist, and it has its own
  section in [Pitfalls](../pitfalls.md).
- **Blocking inside a job is a guaranteed deadlock**, not merely a bad idea: the thread that would run
  the work you are waiting for is the one you parked. Hence `JobDiagnostics.GuardBlockingWait`.
- **One hot actor can own a thread indefinitely**, since the leader drains to empty. Hence
  `JobOptions.MaxJobsPerFlush`, which hands the actor back to the ready queue after N jobs.
- **The counter must be exactly right.** Any window where the counter and the queue disagree strands
  the leader or, worse, admits a second one. See [ADR 0004](0004-admission-cas.md).

## Alternatives considered

- **Central scheduler for every message** (the Akka.NET / Proto.Actor mailbox model). Uniform, no
  hijack, easy to reason about — but it puts a queue hop and a wake-up on the actor→actor path that
  dominates this workload. `ExecutionMode.Scheduled` makes this available per actor for the call
  sites that need it, rather than imposing it everywhere.
- **`ActionBlock<T>` with `MaxDegreeOfParallelism = 1` per actor.** Equivalent guarantees, and it is
  the closest thing in the BCL. It schedules on the thread pool, so it has no dedicated-thread story,
  no inline fast path, and no per-actor timer integration.
- **A lock per actor.** Rejected for deadlock risk and for blocking the producer.
