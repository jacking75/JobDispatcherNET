# JobDispatcherNET.Benchmarks

[BenchmarkDotNet](https://benchmarkdotnet.org/) harness for the scenarios in **ROADMAP §3.3**.
It exists so that §4 ("P2 — 성능 개선") can be decided by measurement instead of intuition:
**측정 없이 바꾸지 말 것.**

## Running

Always Release. Debug builds are meaningless here and BenchmarkDotNet will refuse to trust them.

```bash
# everything (long: tens of minutes)
dotnet run -c Release --project JobDispatcherNET.Benchmarks -- --filter *

# one family
dotnet run -c Release --project JobDispatcherNET.Benchmarks -- --filter *PingPong*
dotnet run -c Release --project JobDispatcherNET.Benchmarks -- --filter *Alternatives*

# list what exists without running anything
dotnet run -c Release --project JobDispatcherNET.Benchmarks -- --list flat

# smoke test that the harness executes (1 op per benchmark, numbers are garbage)
dotnet run -c Release --project JobDispatcherNET.Benchmarks -- --filter *PingPong* --job Dry

# markdown ready to paste into docs
dotnet run -c Release --project JobDispatcherNET.Benchmarks -- --filter * --exporters github
```

Without arguments the switcher prints an interactive menu of the available benchmark classes.

## What is measured

| Class | ROADMAP §3.3 row | What it answers |
|---|---|---|
| `SingleActorThroughput` | single actor, single producer | ops/s and alloc/op for `DoAsync(Action)` (capturing closure) vs `DoAsync<TState>` (static lambda), in both `LeaderFlush` and `Scheduled` mode |
| `ManyActorsThroughput` | 1,000 actors × 8 producers | end-to-end cost per message at 1 / 100 / 1000 actors and 4 / 8 workers |
| `PingPongLatency` | actor→actor ping-pong | round-trip cost inline, on a worker, and one-at-a-time from an external thread |
| `TimerScheduling` | 10,000 timers at once | schedule + fire lag, and schedule + cancel cost on its own |
| `RejectionCost` | bounded rejection path | what a refused `DoAsync` costs, next to an accepted one as baseline |
| `PoolEffect` | pool on/off | Gen0 collections with `Job.MaxPoolSize` at its default vs 0 — the evidence ROADMAP §4.1 asks for |
| `AlternativesComparison` | comparison targets | the identical workload on JobDispatcherNET, raw `Channel<T>` + a ThreadPool drain loop, and TPL Dataflow `ActionBlock(MaxDegreeOfParallelism = 1)` |

Akka.NET and Proto.Actor are named in ROADMAP §3.3 and are **not** here yet — they pull in a large
dependency graph and need their own tuning pass (dispatcher and mailbox configuration) before a
comparison against them would be fair rather than a strawman. See the TODO in
`AlternativesComparison.cs`.

## Where results go

Committed numbers belong in **`docs/benchmarks.md`**, not in this folder and not in source comments.
`BenchmarkDotNet.Artifacts/` (the raw exporter output) is build output — do not commit it.

For each row record: OS and version, CPU model, .NET SDK/runtime version, and the git commit the
numbers came from. A table without that header is not reproducible and should not be quoted.

## Numbers are per-machine — regenerate them

**Never copy a result from one machine, OS, or container into another's table.** These benchmarks
are dominated by things that differ across environments:

- **Timer resolution.** `TimerScheduling` measures firing lag, and on Windows the default
  `TimerPrecision.Coarse` floor is the system timer resolution (~15.6 ms unless another process has
  raised it). Linux behaves differently, and a VM differs from bare metal. This is exactly the
  measurement ROADMAP §4.4 asks for, so it must be taken on the target OS.
- **Thread scheduling and core count.** `ManyActorsThroughput` and `AlternativesComparison` run 8
  producer threads plus a worker pool; results move with physical core count, SMT, and CPU
  affinity/power settings.
- **GC configuration.** `PoolEffect` reads Gen0 counts, which shift with server vs workstation GC.

So: publish Windows **and** Linux tables (ROADMAP §3.3 asks for both), regenerate after any change
to the dispatch path, and re-run the whole family rather than a single row when comparing.
