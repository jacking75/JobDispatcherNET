# PipelinesServer + LoadClient

A binary-protocol game server built on `System.IO.Pipelines` and JobDispatcherNET, plus a headless
console load generator.

## What it demonstrates

| Concern | How this sample handles it |
| --- | --- |
| Socket IO | `System.IO.Pipelines` — a `PipeReader`/`PipeWriter` pair per connection over `NetworkStream`, driven by two `async` loops on the thread pool. **No thread per session.** |
| Framing | Length-prefixed binary, parsed by `FrameCodec.TryReadFrame(ref ReadOnlySequence<byte>, …)`, which is correct when a frame is split across segments — even between the two length bytes. |
| Serialization | MessagePack (`[MessagePackObject]` request/response types), with `MessagePackSecurity.UntrustedData`. |
| Per-session ordering | One `Sequencer<InboundFrame>` per session. The IO loop only enqueues; the sequencer hands one drain at a time to the worker pool, so a client's frames are handled in arrival order, on a worker, never on the IO path. |
| Actor model | An `EntityActor` per logged-in client (`ExecutionMode.Scheduled`, `MaxQueueSize = 512`) and a single `WorldActor` that owns the registry. |
| Timers | `WorldActor.DoAsyncEvery(100ms, …)` builds and broadcasts a snapshot. |
| Back-pressure | The outbound side is a **bounded** channel (256 frames). A client that cannot keep up gets frames dropped, then disconnected with a log line. |
| Shutdown | Stop accepting → close sessions → cancel the tick and empty the registry → `system.StopAsync(10s)`. |

## Wire format

```
 ┌──────────────────┬────────────┬──────────────────────────────┐
 │ payloadLength    │ opcode     │ payload                      │
 │ 2 bytes, LE u16  │ 1 byte     │ payloadLength bytes, MsgPack │
 └──────────────────┴────────────┴──────────────────────────────┘
   0                2            3                    3+payloadLength
```

`payloadLength` counts the payload only, so a frame is `3 + payloadLength` bytes. Because the length
field is 16 bits, a payload can never exceed 64 KiB — the reader needs no separate "max frame size"
guard against a peer claiming a 2 GB body.

| Opcode | Direction | Message |
| ---: | --- | --- |
| 1 | C→S | `LoginRequest { Name, ClientTicks }` |
| 2 | C→S | `MoveRequest { X, Y, ClientTicks }` |
| 3 | C→S | `ChatRequest { Text, ClientTicks }` |
| 101 | S→C | `LoginResponse { EntityId, Ok, ClientTicks, Message }` |
| 102 | S→C | `MoveResponse { EntityId, X, Y, ClientTicks }` |
| 103 | S→C | `ChatResponse { EntityId, Text, ClientTicks }` |
| 110 | S→C | `SnapshotMessage { Tick, TotalEntities, Entities[] }` |
| 255 | — | internal disconnect marker; rejected if it arrives from the wire |

`ClientTicks` is the client's `Stopwatch.GetTimestamp()` at send time. The server copies it into the
ack untouched, which is how the load client measures round-trip latency without any clock sync.

### Why `TryReadFrame` is the interesting part

TCP will split a frame anywhere, and a `PipeReader` hands you a `ReadOnlySequence<byte>` that may be
a chain of segments. So the parser:

* never indexes into `buffer.FirstSpan` — segment 1 can hold one length byte and segment 2 the other;
* uses `SequenceReader<byte>.TryReadLittleEndian`, which stitches a straddling read and returns
  `false` instead of reading garbage;
* on a partial frame returns `false` **without moving the buffer**, so the caller reports
  `AdvanceTo(buffer.Start, buffer.End)` and the pipe holds the partial frame until more bytes arrive;
* on success advances the buffer, so the caller loops `while (TryReadFrame(ref buffer, …))` and
  drains every frame one `ReadAsync` produced.

`LoadClient --selftest` proves all of that: it re-parses a three-frame stream out of sequences whose
segments are 1, 2, 3, 4, 7, 13 and N bytes long, checks that every truncated prefix leaves its
partial frame untouched, and checks the header-only frame. No server needed, so CI can run it as a
fast gate.

## Data flow

```
accept loop (async socket)
      │
      ▼
SessionConnection ── receive loop ── PipeReader.ReadAsync
                          │            └─ TryReadFrame ×N  (IO thread; parse only)
                          ▼
                  Sequencer<InboundFrame>.Enqueue
                          │  (first enqueue posts one drain to JobSystem)
                          ▼
                   ═══ worker thread ═══
                   HandleFrame → MessagePack decode
                          │
                          ├─ EntityActor.PostMove/PostChat   (Scheduled, bounded)
                          │        └─ WorldActor.PostPosition
                          │        └─ session.TrySend(ack)
                          └─ WorldActor.PostAdd/PostRemove
                                   ▲
        WorldActor.DoAsyncEvery(100ms) ─ builds one snapshot frame, sends it to every session
                          │
                          ▼
                 bounded Channel<byte[]>  ← drops here mean a slow client
                          │
                          ▼
                  send loop ── PipeWriter.Write + FlushAsync   (only writer to the pipe)
```

## Running it

```bash
# terminal 1 — server
dotnet run --project samples/PipelinesServer -- --port 25120 --workers 8

# terminal 2 — load
dotnet run --project samples/LoadClient -- --clients 200 --rate 10 --duration 30
```

Server options: `--port` (default 25120), `--workers` (default: processor count), `--tick-ms`
(default 100), `--shutdown-after <seconds>` (graceful stop on a timer — use this in CI, where stdin
is not a console). Console commands: `status`, `metrics`, `q`.

Load-client options: `--host`, `--port` (25120), `--clients` (200), `--rate` (10 msg/s per client),
`--duration` (30s), `--ramp-ms` (2), `--max-p99-ms` (1000), `--max-latency-ms` (5000), `--selftest`.
Exit codes: `0` ok, `1` fatal/self-test failed, `2` a connection failed, `3` the server answered
nothing, `4` latency out of budget — so it works as a smoke test.

### CI smoke test

```bash
dotnet run --project samples/LoadClient -- --selftest || exit 1
dotnet run --project samples/PipelinesServer -- --shutdown-after 25 &
sleep 2
dotnet run --project samples/LoadClient -- --clients 20 --duration 5
```

### Observed output

Smoke test — 20 clients, 5 s, 4 workers (Windows 11, Debug build):

```
  connections ok      : 20/20
  messages sent       : 1033
  messages received   : 2211          # 1033 acks + 1178 snapshot frames
  send throughput     : 206 msg/s
  latency p50         : 0.23 ms
  latency p95         : 0.71 ms
PASS
```

Default scale — 200 clients, 10 msg/s each, 20 s, 8 workers:

```
  connections ok      : 200/200
  messages sent       : 42952
  messages received   : 87364
  send throughput     : 2147 msg/s
  recv throughput     : 4366 msg/s
  latency p50         : 0.37 ms
  latency p95         : 1.02 ms
  latency p99         : 1.75 ms
  latency max         : 186.72 ms
PASS
```

All 200 sessions closed with `dropped=0`, and the server drained cleanly:

```
  accepted total     : 200       entities in world : 0
  world ticks        : 450       snapshots sent : 44412
  jobs executed      : 82203     jobs dropped : 0      jobs failed : 0
  in flight          : 0         ready queue depth : 0     live workers : 8
[server] shutdown complete (drained=True, accepted=200, workers=8)
```

The first message on each connection is slow (~100-200 ms), which is why `max` is far from `p99`:
MessagePack emits its formatters on first use. Steady-state p50 is well under a millisecond. Warm
the serializer before measuring if that matters to you.

## What to look at in the metrics

`metrics` prints `system.Metrics.Snapshot()`. When you push the load up, these are the numbers that
tell you what is happening:

| Counter | What a bad value means |
| --- | --- |
| `jobs dropped` | Non-zero means an actor queue hit `MaxQueueSize` — a client outran the workers, and the session was disconnected. Either raise `EntityActor.MaxQueue` or add workers. |
| `ready queue depth` | Persistently above ~0 means work is arriving faster than the workers drain it. Add workers, or find the job that is running long. |
| `in flight` | Should sit near zero between ticks. A rising floor is the same story as ready depth. |
| `timers fired` | Should be `duration / tick-ms`. Falling behind means the timer thread cannot get a worker. |
| `jobs failed` / `actors faulted` | Any non-zero value is a bug in a handler; `EntityActor.OnJobError` kills the session that caused it. |
| `worker restarts` | The supervisor caught a worker crash. Look for the logged exception. |

The server also runs with `MaxJobDuration = 50ms`, so any single job that overruns is logged as a
warning by the library itself — the cheapest way to find the handler that is stalling a worker.

Session-level numbers come from `status`: `outbound dropped` above zero is the slow-client path
firing, and `snapshots sent` versus `world ticks × sessions` shows how much broadcast is being
dropped.

## How this differs from `AdvancedMmorpgServer`

Both use the same library the same way — IO threads only enqueue into a per-session `Sequencer`,
workers run all game logic — but the transport is the opposite end of the design space:

| | `AdvancedMmorpgServer` | `PipelinesServer` (this sample) |
| --- | --- | --- |
| Protocol | Text, `\n`-delimited, `string.Split` | Binary, 3-byte length-prefixed header + MessagePack |
| Socket IO | Synchronous, **two dedicated OS threads per session** (`RecvLoop`, `SendLoop`) plus one accept thread | `async` Pipelines, thread-pool tasks, no dedicated thread anywhere |
| Cost per connection | ~2 thread stacks (~2 MB) + a `BlockingCollection` | a few pooled pipe segments + a bounded channel |
| Buffer management | `StringBuilder` accumulate + `IndexOf('\n')`, allocating a string per packet | `ReadOnlySequence` parsing, payloads copied into `ArrayPool` rentals and returned after handling |
| Accept | Blocking `AcceptTcpClient` on its own thread | `AcceptAsync` on a task |
| Outbound back-pressure | `BlockingCollection` with `BoundedCapacity`, drop then disconnect | bounded `Channel<byte[]>`, drop then disconnect |
| Actor `Mode` | default `LeaderFlush` | `ExecutionMode.Scheduled` on entities, so a completion thread never runs game logic |
| Scaling limit | Thread count — a few thousand sessions is a few thousand threads | Socket handles and memory |

Everything below the transport is deliberately identical, which is the point: the `Sequencer` →
worker → actor arrangement does not change when the IO model does.

## Files

| File | Contents |
| --- | --- |
| `Protocol.cs` | Opcodes, MessagePack DTOs, `FrameCodec` (`TryReadFrame` / `Encode` / `Decode`). Linked into `LoadClient`, so both ends share one definition of the wire. |
| `SessionConnection.cs` | Pipelines receive/send loops, the per-session `Sequencer`, the bounded outbound channel, disconnect ordering. |
| `Actors.cs` | `EntityActor` (per client) and `WorldActor` (registry + snapshot tick). |
| `PipelinesGameServer.cs` | `JobSystem`, `JobDispatcher`, accept loop, shutdown sequence. |
| `Program.cs` | CLI and the `status` / `metrics` / `q` console loop. |
| `../LoadClient/BotClient.cs` | One simulated player: connect, login, send at a rate, measure round trips. |
| `../LoadClient/FramingSelfTest.cs` | `--selftest`: the frame codec against deliberately pathological segment splits. |
