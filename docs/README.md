# JobDispatcherNET documentation

A lock-free actor-style job dispatcher for .NET: every object owns a queue, jobs on one object run
one at a time in order, and different objects run fully in parallel.

## Start here

| Page | What it covers |
|---|---|
| **[Concepts — the threading model](concepts.md)** | **Read this first.** Leader flush, the admission CAS, and a table of every call site with the thread that actually runs the job and what is guaranteed. |
| [Guarantees & non-guarantees](guarantees.md) | The precise contract: serialization, FIFO, memory visibility, re-entrancy, exceptions, what `DoAsync` returning `false` means, and the async/await rules for each `AsyncReentrancy` mode. |
| [Timers](timers.md) | The per-system timer thread, `ITimerHandle`, `DoAsyncEvery`, precision and OS resolution, drift, and what happens to pending timers at shutdown. |
| [Shutdown](shutdown.md) | The one-call sequence `await system.StopAsync(drainTimeout)`, its phases, `DrainAsync`, per-actor `DisposeAsync`, `TryStop`, and a worked example from the sample server. |
| [Tuning guide](tuning.md) | Worker count, `MaxQueueSize`, `MaxJobsPerFlush`, pooling, `LeaderFlush` vs `Scheduled`, metric costs, and a table for finding the bottleneck from the counters. |
| [Pitfalls & FAQ](pitfalls.md) | The mistakes this model turns into hangs: hijack, blocking inside a job, `ConfigureAwait(false)`, shared collections, non-`static` lambdas, ignored return values, dying timer chains, wrong shutdown order. |
| [Benchmarks](benchmarks.md) | Scenario table and the command to regenerate it. No numbers measured yet. |

## Design decisions

| Page | What it covers |
|---|---|
| [ADR index](adr/README.md) | All architecture decision records, plus the sample's AoE/AOI design notes. |
| [0001 — Leader flush](adr/0001-leader-flush.md) | Why producers flush actors instead of a scheduler assigning them to threads. |
| [0002 — Dedicated threads](adr/0002-dedicated-threads.md) | Why workers are real OS threads rather than thread-pool threads. |
| [0003 — Timer service](adr/0003-timer-service.md) | Why v2.1 replaced per-thread timer queues with one timer thread per `JobSystem` (fixes P0-2, P0-3). |
| [0004 — Admission CAS](adr/0004-admission-cas.md) | Why v2.1 made the counter, not the queue, the authority on admission (fixes P0-1). |

## Sample walkthroughs

Standalone HTML code walkthroughs of two of the example servers, written in Korean. Open them in a
browser.

| Guide | Sample |
|---|---|
| [Chat server guide](guide-chat-server.html) | `ExampleChatServer` — one actor per chat room. |
| [MMORPG server guide](guide-mmorpg-server.html) | `ExampleMmorpgServer` — one actor per player, single-zone parallel processing. |

Both were written against the v2.0 API and still show the older idioms (`IRunnable` worker loops with
`Thread.Sleep(1)`, the static `AcceptingWork` gate, `TimerRegistry`). The structure they explain is
still accurate; for the current API follow the pages above.

## Minimal example

```csharp
using JobDispatcherNET;

public sealed class PlayerActor : AsyncExecutable
{
    private int _hp = 100;

    public PlayerActor(JobSystem system) : base(new JobOptions
    {
        System = system,
        Mode = ExecutionMode.Scheduled,   // reached from network threads
        MaxQueueSize = 256,
    }) { }

    // static lambda + explicit state: no closure allocated
    public bool TakeDamage(int amount) =>
        DoAsync(static t => t.Self.Apply(t.Amount), (Self: this, Amount: amount));

    private void Apply(int amount) => _hp -= amount;   // no lock needed
}

using var system = new JobSystem(new JobSystemOptions { Name = "game" });
using var dispatcher = new JobDispatcher(Environment.ProcessorCount,
    new JobDispatcherOptions { System = system });
_ = dispatcher.RunWorkerThreadsAsync();

var player = new PlayerActor(system);
if (!player.TakeDamage(10))
    Console.Error.WriteLine("queue full — back-pressure");

await system.StopAsync(TimeSpan.FromSeconds(5));
```
