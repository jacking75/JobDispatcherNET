# JobDispatcherNET templates

```bash
dotnet new install JobDispatcherNET.Templates
dotnet new jobdispatcher-server -n MyGameServer
cd MyGameServer && dotnet run
```

Options: `--Workers <n>` (default 4), `--Port <n>` (default 25150), `--Framework net10.0|net8.0`.

The generated project is a working TCP server wired the way a production server should be: a
dedicated worker pool, bounded actor queues with a drop callback, per-session ordering through
`Sequencer<T>`, a cancellable tick, metrics, and a one-call graceful shutdown.

## Testing the template locally

```bash
dotnet pack templates/JobDispatcherNET.Templates.csproj -o ./nupkg
dotnet new install ./nupkg/JobDispatcherNET.Templates.0.10.0.nupkg
dotnet new jobdispatcher-server -n Scratch -o /tmp/scratch
dotnet new uninstall JobDispatcherNET.Templates
```
