# Benchmarks

> **Read the scope line on each table.** [The job pool](#the-job-pool-c1) below carries real
> measurements, because a change was made on their strength — but from a purpose-built harness on one
> machine, not from BenchmarkDotNet, and they are ratios worth trusting rather than absolutes worth
> quoting. Everything under [Scenarios](#scenarios) is still unmeasured, and no figure for this
> library should be quoted, estimated or inferred from anywhere else until it is filled in from an
> actual run — with the hardware, OS and runtime version recorded alongside.

The `JobDispatcherNET.Benchmarks` project (BenchmarkDotNet) is in the repository and runnable. What
is missing is a full measurement run: only a smoke run has been done with the `Dry` job — enough to
prove the harness executes and allocates nothing, nowhere near enough to publish.

**Machine for every number on this page:** Windows 11 Pro 10.0.26200, 20 logical cores, .NET SDK
10.0.400, Release, `ServerGarbageCollection=true`, `TieredPGO=true`, `PublishMeter=false`,
`EnableDetailedMetrics=false`, `Logger=NullJobLogger`. It is a developer workstation, not a quiet
bench: repeated runs of an unchanged build varied by ±30% on the worker-scaling rows, which is why
only ratios of medians are reported and why the small changes below are described as unmeasurable
rather than as wins.

<details>
<summary>Smoke run (not a result — <code>--job Dry</code>, single iteration, one machine)</summary>

```
BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200) / .NET 10.0.9, X64 RyuJIT AVX2

| Method                                      | Mean      | Ratio | Allocated |
| 'A<->B round trip, LeaderFlush (inline)'    |  3.875 us |  1.00 |         - |
| 'A<->B round trip, Scheduled (on worker)'   |  4.365 us |  1.13 |         - |
| 'single round trip kicked + awaited per op' | 49.055 us | 12.66 |         - |
```

A `Dry` job runs each benchmark once with no warmup, so the means carry no statistical weight. The
one thing it does establish is that the actor-to-actor path allocates zero bytes per operation.
</details>

## Regenerating the numbers

```bash
dotnet run -c Release --project JobDispatcherNET.Benchmarks -- --filter *
```

Rules for a result that is worth publishing:

- **Release configuration only**, and never under a debugger or with a profiler attached.
- Record CPU model, core count, OS and build, and `dotnet --version`.
- Run on **both Windows and Linux**. The timer and idle-wait paths are resolution-sensitive and the
  two platforms differ by more than an order of magnitude (see [Timers](timers.md#precision)).
- Report allocation per operation alongside throughput (`[MemoryDiagnoser]`); for this library the
  allocation story is half the point.
- State whether `EnableDetailedMetrics` and `PublishMeter` were on. They are hot-path costs and
  should be **off** for throughput runs and measured separately.

## The job pool (C1)

One measurement *has* been made properly, because a change was made on the strength of it: replacing
the `Job` pool's `ConcurrentBag` + shared counter with a per-thread stack that exchanges batches with
a shared pool. The old design put three read-modify-writes on one process-wide cache line into every
job, and the effect was not a constant overhead — it was a **ceiling**.

Same harness, same machine, back to back; each cell is the median of 7 runs after a warm-up.

| scenario | before | after | |
|---|---|---|---|
| 8 producers → 1,000 `Scheduled` actors, **1** worker | 3.67 | 11.90 | 3.2× |
| 8 producers → 1,000 `Scheduled` actors, **4** workers | 2.81 | 15.55 | 5.5× |
| 8 producers → 1,000 `Scheduled` actors, **8** workers | 2.03 | 18.37 | 9.0× |
| 8 producers → 1,000 `LeaderFlush` actors, 0 workers | 12.43 | 29.29 | 2.4× |
| actor→actor ring, ×64 | 6.2–6.3 | 9.2–10.4 | ~1.6× |
| single actor, single producer, inline | 7.47 | 11.91 | 1.6× |

Millions of jobs per second, higher is better. Read the first three rows downward rather than across:
**the old code got slower as workers were added** (3.67 → 2.03) and the new code gets faster
(11.90 → 18.37). That is the shape of shared-cache-line contention, and it is why the change was
worth making even though the job body here is a single field increment and therefore flatters any
per-job saving. `ManyActorsThroughput` carries `Workers = 1, 4, 8` so the curve stays on the record.

Alongside it: a short spin before a worker parks (`SpinBeforeParkIterations`), striping the
ready-queue depth counter, one fewer wake-up per scheduled timer, and 128-byte striped-counter cells.
These are inside the run-to-run noise of this machine individually; they are in because each removes
a shared write or a wake-up that has no reason to exist, not because a number was attached to any one
of them.

### What was measured and *not* done

Replacing the actor's `ConcurrentQueue<JobEntry>` with an intrusive Vyukov MPSC queue was proposed on
the grounds that an actor queue is multi-producer/single-consumer by construction, so dequeue needs no
CAS. Measured in isolation, in the shape an actor uses it, against `ConcurrentQueue`:

| producers | `ConcurrentQueue` ns/item | intrusive MPSC ns/item |
|---|---|---|
| 1 | 36.9 / 21.8 / 22.9 | 32.7 / 38.1 / 34.6 |
| 2 | 46.4 / 53.4 / 72.0 | 58.2 / 45.3 / 61.2 |
| 4 | 90.2 / 122.3 / 38.9 | 63.8 / 52.8 / 57.2 |

Three runs each. The sign of the difference flips between runs, and the one-producer column — the
case that matters, since a thousand actors divide eight producers between them — is *worse* in two
runs of three. The change would also require `JobEntry.Execute()` to stop recycling itself, because
the node a Vyukov queue hands back becomes its next sentinel and cannot go into the pool until the
dequeue after that. Changing a public contract and rewriting the queue that ADR 0004's admission
invariant rests on, for a difference this measurement cannot even sign, is not a trade worth making.
Revisit if a profile on real work shows the actor queue near the top.

## Fixtures added by the follow-up review

Four of the items in [`review-followup-2026-09-03.md`](review-followup-2026-09-03.md) are performance
questions that a number should decide rather than an argument. These fixtures exist to produce those
numbers; none of them has been run properly yet, so nothing here is published as a result.

| Fixture | Item | Question |
|---|---|---|
| `ActorRingThroughput` | S2 | Does actor→actor fan-out scale with the pool? 64 independent rings across 1/4/8 workers, with `FanOutToWorkers` both ways. The ring row below is flat across worker counts, which is the symptom the fix targets — this fixture is what confirms or refutes it. |
| `SequencerThroughput` | S7 | Cost per item through one sequencer, unbounded vs bounded. The bounded cell is the control and should not move. |
| `TimerArmAndCancel` | S22 | The timer service's single lock and its unpooled entries. Read the curve across 1/4/8 arming threads for the lock, the allocation column for the entries. **No change has been made here** — pooling `TimerEntry` needs generation numbers on the handle to keep a stale `Cancel()` from cancelling somebody else's timer, and that is not worth doing on a hunch. |
| `JobStateShape` | S23 | `DoAsync(static a => …, this)` against `DoAsync(static t => …, (Self: this, X: x))`. A reference-typed `TState` compiles to shared generic code, so the pool's `[ThreadStatic]` costs a generic dictionary lookup per access; a value-typed one is specialised. Both idioms are documented side by side, and the gap is the cost of picking the first. **No change made** — moving the thread-local storage out of the generic type is a real change to the pool and wants this measurement first. |

The C1 numbers below were taken with the reference-typed idiom, so they already include the S23 cost.

## Scenarios

| Scenario | Metric | Result |
|---|---|---|
| Single actor, single producer, `DoAsync(Action)` | ops/s, bytes/op | *not measured* |
| Single actor, single producer, `DoAsync<TState>` with a `static` lambda | ops/s, bytes/op | *not measured* |
| 1,000 actors, 8 producer threads | throughput (jobs/s), queue-wait p50 / p99 | *not measured* |
| Actor → actor ping-pong | round-trip latency | *not measured* |
| 10,000 timers scheduled at once | firing lag distribution (p50 / p99 / max) | *not measured* |
| Bounded queue, rejection path (`MaxQueueSize` saturated) | cost per rejected call | *not measured* |
| Job pool on vs off (`Job.MaxPoolSize`) | gen-0 collections, bytes/op | *not measured* |
| `LeaderFlush` vs `Scheduled` from a non-worker thread | latency, throughput | *not measured* |
| `TimerPrecision.Coarse` vs `High` vs `RaiseSystemTimerResolution` | delivered lag, CPU cost | *not measured* |

## Comparison targets

The same workload should be implemented against each of these, in the same harness, before any
comparison is published:

- raw `Channel<T>` + thread pool
- TPL Dataflow `ActionBlock<T>` with `MaxDegreeOfParallelism = 1` per actor (the closest built-in
  equivalent, and the first thing a reader will ask about)
- Akka.NET
- Proto.Actor

A comparison where the alternative was written to lose is worse than no comparison. Implement each
one the way its own documentation recommends.
