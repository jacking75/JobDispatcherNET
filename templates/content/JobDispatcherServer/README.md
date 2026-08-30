# JobDispatcherServer

Generated from the `jobdispatcher-server` template.

```bash
dotnet run
# then, from another terminal:
telnet localhost LISTEN_PORT
```

Console commands: `status`, `metrics`, `q`.

## What the template already does for you

| Concern | Where |
|---|---|
| Dedicated worker pool, no polling | `Program.cs` — `JobDispatcher` |
| Lock-free shared state | `WorldActor` — a plain `Dictionary` behind an actor queue |
| Per-client ordering, off the IO thread | `SessionActor` — `Sequencer<string>` |
| Back-pressure | `JobOptions.MaxQueueSize` + `OnDropped` on both actors |
| No actor code on IO threads | `ExecutionMode.Scheduled` |
| Cancellable simulation tick | `WorldActor.Start` — `DoAsyncEvery` |
| Safe reads from the console thread | `WorldActor.GetSessionCount` — `AskSync` |
| Graceful shutdown | `Program.Main` — `system.StopAsync` |

## Next steps

- Replace `SessionActor.HandleLine` with your real protocol.
- Give each gameplay entity its own actor and call between them with `DoAsync`, never with a lock.
- Watch `dropped`, `failed` and `inFlight` in `metrics` under load — see the
  [tuning guide](https://github.com/jacking75/JobDispatcherNET/blob/main/docs/tuning.md).
