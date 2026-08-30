using System.Globalization;

namespace JobDispatcherNET.Samples.Pipelines;

/// <summary>Console entry point for the Pipelines sample server.</summary>
public static class Program
{
    /// <summary>Run the server until <c>q</c>, Ctrl+C or <c>--shutdown-after</c>.</summary>
    public static async Task<int> Main(string[] args)
    {
        if (Array.Exists(args, a => a is "-h" or "--help"))
        {
            PrintUsage();
            return 0;
        }

        // The library logs at Warn by default so a hot path cannot flood stdout. This sample wants
        // its lifecycle lines, so turn it down to Info.
        JobLog.Current = new ConsoleJobLogger { MinLevel = JobLogLevel.Info };

        var options = new ServerOptions
        {
            Port = GetInt(args, "--port", 25120),
            WorkerThreads = GetInt(args, "--workers", Math.Max(2, Environment.ProcessorCount)),
            TickPeriod = TimeSpan.FromMilliseconds(GetInt(args, "--tick-ms", 100)),
        };
        var shutdownAfter = GetInt(args, "--shutdown-after", 0);

        var server = new PipelinesGameServer(options);
        try
        {
            server.Start();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"failed to start: {ex.Message}");
            return 1;
        }

        using var quit = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;                // let the graceful path run
            quit.Cancel();
        };

        if (shutdownAfter > 0)
        {
            Console.WriteLine($"auto-shutdown in {shutdownAfter}s (--shutdown-after)");
            quit.CancelAfter(TimeSpan.FromSeconds(shutdownAfter));
        }

        Console.WriteLine("commands: status | metrics | q");
        var console = Task.Run(() => ConsoleLoop(server, quit));

        try { await Task.Delay(Timeout.InfiniteTimeSpan, quit.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected */ }

        PrintStatus(server);
        PrintMetrics(server);

        await server.StopAsync().ConfigureAwait(false);
        await Task.WhenAny(console, Task.Delay(200)).ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// Reads console commands. Runs on its own task so a blocking <c>ReadLine</c> never holds up
    /// shutdown; when stdin is redirected (CI, a background process) <c>ReadLine</c> returns null
    /// and we simply stop reading instead of spinning.
    /// </summary>
    private static void ConsoleLoop(PipelinesGameServer server, CancellationTokenSource quit)
    {
        while (!quit.IsCancellationRequested)
        {
            string? line;
            try { line = Console.ReadLine(); }
            catch (IOException) { return; }

            if (line is null)
            {
                Console.WriteLine("stdin closed — console commands disabled (Ctrl+C or --shutdown-after still work)");
                return;
            }

            switch (line.Trim().ToLowerInvariant())
            {
                case "":
                    break;
                case "status":
                    PrintStatus(server);
                    break;
                case "metrics":
                    PrintMetrics(server);
                    break;
                case "q":
                case "quit":
                case "exit":
                    quit.Cancel();
                    return;
                default:
                    Console.WriteLine("commands: status | metrics | q");
                    break;
            }
        }
    }

    private static void PrintStatus(PipelinesGameServer server)
    {
        var sessions = server.Sessions;
        long recv = 0, sent = 0;
        var dropped = 0;
        foreach (var s in sessions)
        {
            recv += s.FramesReceived;
            sent += s.FramesSent;
            dropped += s.FramesDropped;
        }

        Console.WriteLine("── status ─────────────────────────────────");
        Console.WriteLine($"  sessions live      : {server.SessionCount}");
        Console.WriteLine($"  accepted total     : {server.AcceptedTotal}");
        Console.WriteLine($"  entities in world  : {server.World.EntityCount}");
        Console.WriteLine($"  world ticks        : {server.World.TickCount}");
        Console.WriteLine($"  snapshots sent     : {server.World.SnapshotsSent}");
        Console.WriteLine($"  frames recv/sent   : {recv} / {sent}");
        Console.WriteLine($"  outbound dropped   : {dropped}");
    }

    private static void PrintMetrics(PipelinesGameServer server)
    {
        var m = server.System.Metrics.Snapshot();
        Console.WriteLine("── metrics ────────────────────────────────");
        Console.WriteLine($"  jobs executed      : {m.TotalJobsExecuted}");
        Console.WriteLine($"  jobs dropped       : {m.TotalJobsDropped}");
        Console.WriteLine($"  jobs failed        : {m.TotalJobsFailed}");
        Console.WriteLine($"  in flight          : {m.InFlightJobs}");
        Console.WriteLine($"  ready queue depth  : {m.ReadyQueueDepth}");
        Console.WriteLine($"  live workers       : {m.LiveWorkers}");
        Console.WriteLine($"  timers fired/pend  : {m.TimersFired} / {m.PendingTimerJobs}");
        Console.WriteLine($"  worker restarts    : {m.WorkerRestarts}");
        Console.WriteLine($"  actors faulted     : {m.ActorsFaulted}");
    }

    private static int GetInt(string[] args, string name, int fallback)
    {
        var index = Array.IndexOf(args, name);
        if (index >= 0 && index + 1 < args.Length &&
            int.TryParse(args[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }
        return fallback;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            PipelinesServer — binary-protocol game server on System.IO.Pipelines + JobDispatcherNET

              --port <n>            listen port (default 25120)
              --workers <n>         job-system worker threads (default: processor count)
              --tick-ms <n>         world snapshot period in ms (default 100)
              --shutdown-after <n>  stop gracefully after n seconds (handy in CI)

            console commands: status | metrics | q
            """);
    }
}
