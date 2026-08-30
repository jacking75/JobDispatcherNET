using AdvancedMmorpgServer;
using JobDispatcherNET;

// ─────────────────────────────────────────────────────────────
// AdvancedMmorpgServer entry point.
//
// Library features used here:
//   - JobLog for unified logging instead of scattered Console.WriteLine
//   - system.Metrics.Snapshot() for queue depth / throughput / drops / restarts
//   - World.GetSnapshot() for a consistent read, computed on the world's own queue
// ─────────────────────────────────────────────────────────────

// 라이브러리 로거 — Info 부터 출력 (기본은 Warn 부터)
JobLog.Current = new ConsoleJobLogger { MinLevel = JobLogLevel.Info };

var configPath = args.Length > 0 ? args[0] : "config.json";
var config = ServerConfig.Load(configPath);

AsyncExecutable.OnError = ex => JobLog.Error("[Actor 오류]", ex);

var server = new GameServer(config);
server.Start();

using var exitEvent = new ManualResetEventSlim(false);

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    JobLog.Info("[서버] Ctrl+C 감지 — 종료 시작");
    exitEvent.Set();
};

Console.WriteLine("'q' 입력 시 종료 / 'status' 로 상태 / 'metrics' 로 라이브러리 메트릭 / Ctrl+C\n");

var inputThread = new Thread(() =>
{
    while (!exitEvent.IsSet)
    {
        string? line;
        try { line = Console.ReadLine(); }
        catch { break; }
        if (line is null) break;

        var trimmed = line.Trim();
        if (trimmed.Equals("q", StringComparison.OrdinalIgnoreCase))
        {
            exitEvent.Set();
            break;
        }
        else if (trimmed.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            PrintStatus(server);
        }
        else if (trimmed.Equals("metrics", StringComparison.OrdinalIgnoreCase))
        {
            PrintMetrics(server);
        }
    }
})
{
    IsBackground = true,
    Name = "ConsoleInput",
};
inputThread.Start();

exitEvent.Wait();

server.Dispose();

static void PrintStatus(GameServer s)
{
    var snap = s.World.GetSnapshot();
    Console.WriteLine($"[상태] 세션 {snap.SessionCount} / 플레이어 {snap.LivePlayerCount}/{snap.TotalPlayerCount} / NPC {snap.LiveNpcCount}/{snap.TotalNpcCount} / WorldQueue {snap.WorldQueueDepth}");
}

static void PrintMetrics(GameServer s)
{
    var m = s.System.Metrics.Snapshot();
    Console.WriteLine(
        $"[metrics] executed={m.TotalJobsExecuted} dropped={m.TotalJobsDropped} failed={m.TotalJobsFailed} " +
        $"inFlight={m.InFlightJobs} pendingTimers={m.PendingTimerJobs} ready={m.ReadyQueueDepth} " +
        $"jobPool={m.ActiveJobPoolSize} workers={m.LiveWorkers} restarts={m.WorkerRestarts}");
}
