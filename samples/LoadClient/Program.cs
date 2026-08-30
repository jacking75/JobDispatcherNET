using System.Diagnostics;
using System.Globalization;

namespace JobDispatcherNET.Samples.Pipelines.LoadClient;

/// <summary>
/// Headless load generator for <c>PipelinesServer</c>. No graphics, no library dependency —
/// it is meant to be runnable as a CI smoke test, so it reports a non-zero exit code when the
/// server failed to accept connections, answered nothing, or answered pathologically slowly.
/// </summary>
public static class Program
{
    /// <summary>Exit code: everything within limits.</summary>
    public const int ExitOk = 0;

    /// <summary>Exit code: bad arguments or an unexpected crash.</summary>
    public const int ExitFatal = 1;

    /// <summary>Exit code: one or more bots failed to connect.</summary>
    public const int ExitConnectFailed = 2;

    /// <summary>Exit code: the server never answered.</summary>
    public const int ExitNoTraffic = 3;

    /// <summary>Exit code: latency above the configured limit.</summary>
    public const int ExitLatency = 4;

    /// <summary>Run the load test.</summary>
    public static async Task<int> Main(string[] args)
    {
        if (Array.Exists(args, a => a is "-h" or "--help"))
        {
            PrintUsage();
            return ExitOk;
        }

        if (Array.Exists(args, a => a == "--selftest"))
        {
            Console.WriteLine("framing self-test (no server needed)");
            return FramingSelfTest.Run() == 0 ? ExitOk : ExitFatal;
        }

        var host = GetString(args, "--host", "127.0.0.1");
        var port = GetInt(args, "--port", 25120);
        var clients = GetInt(args, "--clients", 200);
        var rate = GetDouble(args, "--rate", 10);
        var duration = GetInt(args, "--duration", 30);
        var rampMs = GetInt(args, "--ramp-ms", 2);
        var maxP99 = GetDouble(args, "--max-p99-ms", 1000);
        var maxLatency = GetDouble(args, "--max-latency-ms", 5000);

        if (clients < 1 || rate <= 0 || duration < 1)
        {
            Console.Error.WriteLine("--clients and --duration must be >= 1, --rate must be > 0");
            return ExitFatal;
        }

        Console.WriteLine($"LoadClient -> {host}:{port}");
        Console.WriteLine($"  clients={clients} rate={rate}/s/client duration={duration}s ramp={rampMs}ms");

        var bots = new BotClient[clients];
        var tasks = new Task[clients];
        using var sendCts = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            sendCts.Cancel();
        };

        var started = Stopwatch.GetTimestamp();

        // Ramp in: hammering a listener with N simultaneous SYNs mostly measures the accept
        // backlog, not the server.
        for (var i = 0; i < clients; i++)
        {
            bots[i] = new BotClient(i, host, port, rate);
            tasks[i] = bots[i].RunAsync(sendCts.Token);
            if (rampMs > 0)
                await Task.Delay(rampMs).ConfigureAwait(false);
        }

        var rampElapsed = Stopwatch.GetElapsedTime(started);
        Console.WriteLine($"  all {clients} bots launched in {rampElapsed.TotalSeconds:F1}s, running for {duration}s...");

        var runStarted = Stopwatch.GetTimestamp();
        try { await Task.Delay(TimeSpan.FromSeconds(duration), sendCts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { Console.WriteLine("  interrupted"); }

        var runElapsed = Stopwatch.GetElapsedTime(runStarted);
        await sendCts.CancelAsync().ConfigureAwait(false);

        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (Exception ex) { Console.Error.WriteLine($"  bot task failed: {ex.Message}"); }

        return Report(bots, runElapsed, maxP99, maxLatency);
    }

    private static int Report(BotClient[] bots, TimeSpan elapsed, double maxP99, double maxLatency)
    {
        var connected = 0;
        long sent = 0, received = 0;
        var latencies = new List<double>(bots.Length * 64);
        var errors = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var bot in bots)
        {
            var s = bot.Stats;
            if (s.Connected) connected++;
            sent += Interlocked.Read(ref s.Sent);
            received += Interlocked.Read(ref s.Received);
            latencies.AddRange(s.Latencies);
            if (s.Error is { } error)
                errors[error] = errors.GetValueOrDefault(error) + 1;
        }

        latencies.Sort();
        var seconds = Math.Max(0.001, elapsed.TotalSeconds);

        Console.WriteLine();
        Console.WriteLine("══ load report ════════════════════════════");
        Console.WriteLine($"  duration            : {seconds:F1}s");
        Console.WriteLine($"  connections ok      : {connected}/{bots.Length}");
        Console.WriteLine($"  messages sent       : {sent}");
        Console.WriteLine($"  messages received   : {received}");
        Console.WriteLine($"  send throughput     : {sent / seconds:F0} msg/s");
        Console.WriteLine($"  recv throughput     : {received / seconds:F0} msg/s");
        Console.WriteLine($"  latency samples     : {latencies.Count}");
        if (latencies.Count > 0)
        {
            Console.WriteLine($"  latency p50         : {Percentile(latencies, 0.50):F2} ms");
            Console.WriteLine($"  latency p95         : {Percentile(latencies, 0.95):F2} ms");
            Console.WriteLine($"  latency p99         : {Percentile(latencies, 0.99):F2} ms");
            Console.WriteLine($"  latency max         : {latencies[^1]:F2} ms");
        }
        if (errors.Count > 0)
        {
            Console.WriteLine("  errors:");
            foreach (var (message, count) in errors.OrderByDescending(e => e.Value))
                Console.WriteLine($"    {count,5}x {message}");
        }
        Console.WriteLine("═══════════════════════════════════════════");

        if (connected < bots.Length)
        {
            Console.Error.WriteLine($"FAIL: only {connected}/{bots.Length} connections were established");
            return ExitConnectFailed;
        }
        if (received == 0 || latencies.Count == 0)
        {
            Console.Error.WriteLine("FAIL: the server answered nothing");
            return ExitNoTraffic;
        }

        var p99 = Percentile(latencies, 0.99);
        if (p99 > maxP99 || latencies[^1] > maxLatency)
        {
            Console.Error.WriteLine(
                $"FAIL: latency out of budget (p99 {p99:F1}ms > {maxP99:F0}ms, or max {latencies[^1]:F1}ms > {maxLatency:F0}ms)");
            return ExitLatency;
        }

        Console.WriteLine("PASS");
        return ExitOk;
    }

    /// <summary>Nearest-rank percentile over an already-sorted list.</summary>
    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        var rank = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Count - 1)];
    }

    private static string GetString(string[] args, string name, string fallback)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
    }

    private static int GetInt(string[] args, string name, int fallback) =>
        int.TryParse(GetString(args, name, string.Empty), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : fallback;

    private static double GetDouble(string[] args, string name, double fallback) =>
        double.TryParse(GetString(args, name, string.Empty), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : fallback;

    private static void PrintUsage()
    {
        Console.WriteLine("""
            LoadClient — headless load generator for PipelinesServer

              --host <name>          server host (default 127.0.0.1)
              --port <n>             server port (default 25120)
              --clients <n>          concurrent connections (default 200)
              --rate <n>             messages per second per client (default 10)
              --duration <n>         seconds to run (default 30)
              --ramp-ms <n>          delay between connects (default 2)
              --max-p99-ms <n>       fail above this p99 (default 1000)
              --max-latency-ms <n>   fail above this max (default 5000)
              --selftest             verify the frame codec against split buffers and exit

            exit codes: 0 ok, 1 fatal, 2 connect failed, 3 no traffic, 4 latency
            """);
    }
}
