using System.Net;
using System.Net.Sockets;
using JobDispatcherNET;

namespace JobDispatcherServer;

internal static class Program
{
    private const int ListenPort = LISTEN_PORT;
    private const int WorkerCount = WORKER_COUNT;

    private static int Main()
    {
        JobLog.Current = new ConsoleJobLogger { MinLevel = JobLogLevel.Info };

        // One system owns the workers, the timer thread, the metrics and the shutdown gate.
        using var system = new JobSystem(new JobSystemOptions
        {
            Name = "server",

            // Warn about any job that hogs a worker. Tune to your tick budget.
            MaxJobDuration = TimeSpan.FromMilliseconds(50),
        });

        // The non-generic dispatcher has no polling loop: idle workers block on a signal.
        using var dispatcher = new JobDispatcher(WorkerCount, new JobDispatcherOptions
        {
            System = system,
            RestartFailedWorkers = true,
        });
        _ = dispatcher.RunWorkerThreadsAsync();

        var world = new WorldActor(system);
        world.Start();

        using var listener = new SessionListener(system, world, ListenPort);
        listener.Start();

        JobLog.Info($"listening on {ListenPort} with {WorkerCount} workers — type 'metrics' or 'q'");

        using var exit = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; exit.Set(); };

        RunConsoleLoop(system, world, exit);

        // Graceful shutdown, in one call: stop taking connections, let the actors finish what
        // they and their cascades started, then stop timers and workers.
        listener.Stop();
        world.Stop();

        if (!system.StopAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult())
            JobLog.Warn("shutdown drained incompletely");

        JobLog.Info("stopped");
        return 0;
    }

    private static void RunConsoleLoop(JobSystem system, WorldActor world, ManualResetEventSlim exit)
    {
        if (Console.IsInputRedirected)
        {
            exit.Wait();
            return;
        }

        while (!exit.IsSet)
        {
            var line = Console.ReadLine()?.Trim();
            if (line is null || line.Equals("q", StringComparison.OrdinalIgnoreCase))
                return;

            if (line.Equals("metrics", StringComparison.OrdinalIgnoreCase))
            {
                var m = system.Metrics.Snapshot();
                Console.WriteLine(
                    $"executed={m.TotalJobsExecuted} dropped={m.TotalJobsDropped} failed={m.TotalJobsFailed} " +
                    $"inFlight={m.InFlightJobs} ready={m.ReadyQueueDepth} workers={m.LiveWorkers}");
            }
            else if (line.Equals("status", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"sessions={world.GetSessionCount()}");
            }
        }
    }
}

/// <summary>
/// Accepts connections. The accept thread never touches actor state directly — it hands each new
/// session to the world actor, so game logic only ever runs on a worker.
/// </summary>
internal sealed class SessionListener(JobSystem system, WorldActor world, int port) : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private Thread? _thread;

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _thread = new Thread(AcceptLoop) { IsBackground = true, Name = "Accept" };
        _thread.Start();
    }

    public void Stop()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch (SocketException) { }
        _thread?.Join(TimeSpan.FromSeconds(2));
    }

    private void AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = _listener!.AcceptTcpClient(); }
            catch (SocketException) { break; }
            catch (ObjectDisposedException) { break; }

            var session = new SessionActor(system, world, client);
            world.AddSession(session);
            session.Start();
        }
    }

    public void Dispose()
    {
        Stop();
        _cts.Dispose();
    }
}
