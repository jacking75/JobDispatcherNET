# JobDispatcherNET

[![NuGet](https://img.shields.io/nuget/v/JobDispatcherNET.svg)](https://www.nuget.org/packages/JobDispatcherNET/)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![net8.0 | net10.0](https://img.shields.io/badge/target-net8.0%20%7C%20net10.0-512BD4.svg)](#)

**Lock-free actor-style job dispatcher for .NET game servers.**
Each object owns a job queue. Work on one object is serialized without locks; different objects run
fully in parallel, on dedicated OS threads.

한국어 문서: **[README.ko.md](README.ko.md)** · 전체 가이드: **[Book/](Book/README.md)**

```
packet: player A "move"    → actorA.DoAsync(Move)   ─┐
packet: player B "move"    → actorB.DoAsync(Move)   ─┼─ fully parallel
packet: player C "hit A"   → actorC.DoAsync(Snapshot) → actorA.DoAsync(TakeDamage)
```

---

## 30-second example

```csharp
using JobDispatcherNET;

public sealed class PlayerActor : AsyncExecutable
{
    private int _hp = 100;   // no lock, ever

    public void TakeDamage(int amount) =>
        DoAsync(static t => t.Self.Apply(t.Amount), (Self: this, Amount: amount));

    private void Apply(int amount)
    {
        _hp -= amount;                       // only one thread is ever in here
        if (_hp <= 0) DoAsyncAfter(TimeSpan.FromSeconds(5), Respawn);
    }

    private void Respawn() => _hp = 100;
}

// Start a pool of dedicated OS worker threads.
using var dispatcher = new JobDispatcher(workerCount: 8);
_ = dispatcher.RunWorkerThreadsAsync();

var player = new PlayerActor();
player.TakeDamage(10);                       // safe to call from any thread

// Graceful shutdown: drain everything in flight, then stop.
await JobSystem.Default.StopAsync(TimeSpan.FromSeconds(10));
```

Install:

```bash
dotnet add package JobDispatcherNET
```

---

## Why this instead of the obvious alternatives

`ActionBlock<T>` with `MaxDegreeOfParallelism = 1` gives you the same *serialization* guarantee, and
for many programs it is the right answer. This library exists because a game server tends to want
four other things at the same time:

|                              | JobDispatcherNET | `ActionBlock<T>` | raw `Channel<T>` | Akka.NET | Orleans |
|---|---|---|---|---|---|
| Runtime dependencies         | none             | none (in-box)    | none (in-box)    | several  | several |
| Threads                      | dedicated OS threads | thread pool  | thread pool      | dedicated pool | thread pool |
| Latency for actor→actor call | inline, no hop   | scheduler hop    | scheduler hop    | mailbox hop | mailbox hop |
| Allocation on the hot path   | none (`DoAsync<TState>` + pooled jobs) | closure + task | closure | message object | message object |
| Timers on the actor          | built in, cancellable | no          | no               | yes      | yes |
| Back-pressure                | per-actor cap + drop callback | bounded capacity | bounded | mailbox | n/a |
| Distribution / clustering    | **no**           | no               | no               | yes      | yes |
| Lines of code to read        | ~2,000           | —                | —                | large    | large |

**Don't use this if:** you need actors across more than one process (use Orleans), you have a
straight data-flow pipeline (use TPL Dataflow), or nearly every one of your jobs is an `await` on a
database — a thread-pool design fits that better, though `AsyncReentrancy` handles it if you need to
mix the two.

---

## What you get

- **Serialization without locks** — one actor's jobs never overlap, so its fields need no
  synchronization. [How it works](docs/concepts.md).
- **Dedicated OS threads** — real threads, not thread-pool threads, so long loops and per-thread
  state are safe. Idle workers block on a signal; there is no polling.
- **Allocation-free hot path** — `DoAsync<TState>(static lambda, state)` plus a capped job pool.
- **Cancellable and repeating timers** — `DoAsyncAfter` / `DoAsyncEvery` return an `ITimerHandle`.
  One timer thread per system, so a worker crash cannot take your timers with it.
- **Back-pressure** — `MaxQueueSize` with a drop callback that tells you *why* a job was refused.
- **Two execution modes** — inline (`LeaderFlush`) for actor-to-actor calls, `Scheduled` for actors
  reached from socket or thread-pool threads so they never run game logic on your IO thread.
- **async/await support** — `RunAsync` / `AskAsync` with a choice of interleaved or exclusive
  re-entrancy; continuations come back onto the actor.
- **Request/response** — `Ask` returns a `Task<T>`; `AskSync` blocks safely and *throws* if you call
  it from somewhere that would deadlock.
- **Observability** — counters, gauges and histograms published through
  `System.Diagnostics.Metrics`, so OpenTelemetry and `dotnet-counters` see them with no wiring.
- **Ordering across producers** — `Sequencer<T>` keeps one session's packets in arrival order.
- **One-call shutdown** — `StopAsync` drains in-flight work (including work it cascades into), then
  stops timers and workers.

---

## Core types

| Type | Role |
|---|---|
| `AsyncExecutable` | Base class for an actor. `DoAsync`, `DoAsync<TState>`, `DoAsyncAfter`, `DoAsyncEvery`, `Ask`, `RunAsync` |
| `JobSystem` | Owns workers, the timer thread, metrics and the shutdown gate. `JobSystem.Default` is implicit |
| `JobDispatcher` | Worker pool with no user loop — workers block until there is work |
| `JobDispatcher<T>` | Worker pool that runs your `IRunnable` loop on each thread |
| `JobOptions` | Per-actor: queue cap, drop policy, execution mode, fairness, failure limit |
| `Sequencer<T>` | Arrival-order, single-drainer handling for one source (a session's packets) |
| `ITimerHandle` | Cancels a scheduled or repeating timer |
| `JobMetrics` | Counters, plus the `JobDispatcherNET` meter |
| `JobDiagnostics` | Turns "blocked a worker waiting on an actor" from a hang into an exception |

---

## Production shape

```csharp
// One system per pool of actors. Most servers need exactly one.
var system = new JobSystem(new JobSystemOptions
{
    Name = "game",
    Logger = new MicrosoftLoggerAdapter(logger),   // JobDispatcherNET.Extensions.Logging
    MaxJobDuration = TimeSpan.FromMilliseconds(50),
});

using var dispatcher = new JobDispatcher(8, new JobDispatcherOptions { System = system });
_ = dispatcher.RunWorkerThreadsAsync();

public sealed class PlayerActor : AsyncExecutable
{
    public PlayerActor(Player p, JobSystem system) : base(new JobOptions
    {
        Name    = $"Player#{p.Id}",
        System  = system,
        MaxQueueSize = 256,                       // OOM guard
        OnDropped = static (actor, reason) => Log.Warn($"{actor.Name} refused a job: {reason}"),
        MaxConsecutiveFailures = 10,              // quarantine a broken actor
    }) { }

    // Hot path: static lambda + explicit state means no closure allocation.
    public void Move(float x, float y) =>
        DoAsync(static t => t.Self.ProcessMove(t.X, t.Y), (Self: this, X: x, Y: y));
}

// Packets from an IO thread, kept in order, handled on a worker:
// maxPending is this session's back-pressure: unbounded, one fast client is an OOM.
var packets = new Sequencer<string>(system, line => PacketHandler.Handle(session, line),
    onError: null, maxPending: 256);
// ...on the socket thread:
if (!packets.Enqueue(line))
    session.Disconnect("inbound queue full");

// Shutdown.
await system.StopAsync(TimeSpan.FromSeconds(10));
```

With ASP.NET Core or the Generic Host, `JobDispatcherNET.Extensions.Hosting` does the wiring:

```csharp
services.AddJobDispatcher(o => o.WorkerCount = 8);
```

---

## Documentation

| Page | What is in it |
|---|---|
| [Concepts](docs/concepts.md) | **Start here.** Which thread runs your job, and why |
| [Guarantees](docs/guarantees.md) | Ordering, visibility, re-entrancy, exceptions — and the non-guarantees |
| [Timers](docs/timers.md) | Precision, cancellation, OS resolution caveats |
| [Shutdown](docs/shutdown.md) | The drain sequence |
| [Tuning](docs/tuning.md) | Worker count, queue sizes, reading the metrics |
| [Pitfalls](docs/pitfalls.md) | The mistakes this model turns into hangs |
| [ADRs](docs/adr/README.md) | Why the design is the way it is |
| [Benchmarks](docs/benchmarks.md) | How to reproduce the numbers |
| [Book (Korean, 13 chapters)](Book/README.md) | Full walkthrough from first principles |

---

## Samples

| Project | What it shows |
|---|---|
| `samples/ExampleConsoleApp` | The basics: `DoAsync`, `DoAsyncAfter`, worker threads |
| `samples/ExampleChatServer` | Multi-room chat — one actor per room |
| `samples/ExampleMmorpgServer` | Single-zone MMORPG — player actors, spatial index |
| `samples/ExampleSectorServer` | Sector-partitioned world with hand-off at boundaries |
| `samples/AdvancedMmorpgServer` | **The reference server.** Queue caps, `Sequencer`, metrics, supervisor, push AOI, one-call shutdown |
| `samples/AdvancedMmorpgClient` | MonoGame bot/viewer client that drives the server |
| `samples/PipelinesServer` | **Binary protocol server** — `System.IO.Pipelines`, length-prefixed MessagePack frames, no thread per session |
| `samples/LoadClient` | Headless load generator for the above; reports latency percentiles and exits non-zero on failure |
| `samples/Observability` | Generic Host + OpenTelemetry metrics |

```bash
dotnet run --project samples/AdvancedMmorpgServer      # listens on 25100
# console: status | metrics | q

# Binary-protocol server plus a 200-client load run:
dotnet run -c Release --project samples/PipelinesServer -- --port 25120 --workers 8
dotnet run -c Release --project samples/LoadClient    -- --port 25120 --clients 200 --duration 20
```

Or start from the template:

```bash
dotnet new install JobDispatcherNET.Templates
dotnet new jobdispatcher-server -n MyGameServer
```

---

## Building and testing

```bash
dotnet build All.sln
dotnet test JobDispatcherNET.Tests/JobDispatcherNET.Tests.csproj --filter "Category!=Stress"
dotnet run  -c Release --project JobDispatcherNET.Benchmarks -- --filter *
```

The library targets **net8.0** and **net10.0** and has no runtime dependencies.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Concurrency changes need a regression test that fails
without the fix — [`RegressionTests.cs`](JobDispatcherNET.Tests/RegressionTests.cs) is the model.

## Acknowledgements

The execution model — an actor that owns its job queue, with producers electing a flush
leader — follows the design of the C++ [JobDispatcher](https://github.com/ujentus/JobDispatcher)
by [ujentus](https://github.com/ujentus). The .NET code in this repository is an independent
implementation written from that design; no source was translated or copied from it.

## License

[MIT](LICENSE).
