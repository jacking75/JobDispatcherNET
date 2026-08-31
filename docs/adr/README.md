# Architecture decision records

Short records of the decisions that shape JobDispatcherNET: what the problem was, what was decided,
what it costs, and what was rejected. Each record is immutable once accepted — a later decision that
changes it gets a new record that supersedes the old one.

## Core library

| # | Decision | Status |
|---|---|---|
| [0001](0001-leader-flush.md) | Producers flush actors; actors have no thread of their own | Accepted (follows the C++ [JobDispatcher](https://github.com/ujentus/JobDispatcher), recorded retroactively) |
| [0002](0002-dedicated-threads.md) | Workers are dedicated OS threads, not thread-pool threads | Accepted (recorded retroactively) |
| [0003](0003-timer-service.md) | One timer thread per `JobSystem`, replacing per-thread timer queues | Accepted in v2.1 — fixes P0-2 and P0-3 |
| [0004](0004-admission-cas.md) | Queue admission is a CAS on the counter, not a bounded-channel write | Accepted in v2.1 — fixes P0-1 |

## Sample-specific design notes

These two are **not** core-library ADRs. They are the design notes for the AoE/AOI system in the
`AdvancedMmorpgServer` sample, moved here from `AdvancedMmorpgServer/Docs/` so they are findable;
they describe that sample's game logic, not the dispatcher. Both are written in Korean.

| Document | What it covers |
|---|---|
| [aoe-aoi-design.md](aoe-aoi-design.md) | Concurrency design for area-of-effect attacks (how to avoid a job explosion when one attack hits many targets, and how to return the result to the attacker) and for area-of-interest queries (why the sector grid needs no lock). |
| [aoi-push-implementation-plan.md](aoi-push-implementation-plan.md) | The implementation plan that turned that design into the sample's push-model AOI: 3×3 sector visibility, per-player state deltas, and the periodic resync safety net. |
