# Benchmarks

> **No numbers have been measured on this machine yet.** Every result cell below is empty on purpose.
> Do not quote, estimate or infer figures for this library until this page has been filled in from an
> actual run — and record the hardware, OS and runtime version alongside them when you do.

The `JobDispatcherNET.Benchmarks` project (BenchmarkDotNet) is in the repository and runnable. What
is missing is a real measurement run: only a smoke run has been done so far, on one Windows laptop,
with the `Dry` job — enough to prove the harness executes and allocates nothing, nowhere near enough
to publish.

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
