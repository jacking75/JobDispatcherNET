using AdvancedMmorpgServer;
using JobDispatcherNET;

// ─────────────────────────────────────────────────────────────
// AdvancedMmorpgServer 메인 — 동기 (async/await 미사용)
//
// 라이브러리 v2 활용:
//   - JobLog 로 통합 로깅 (Console.WriteLine 직접 호출 지양)
//   - JobMetrics.Snapshot() 으로 큐 깊이 / 처리량 / 드롭 / 재기동 통계 노출
//   - World.GetSnapshot() 으로 일관된 read 스냅샷
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
            PrintMetrics();
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

static void PrintMetrics()
{
    var m = JobMetrics.Snapshot();
    Console.WriteLine(
        $"[메트릭] 실행={m.TotalJobsExecuted} 드롭={m.TotalJobsDropped} 실패={m.TotalJobsFailed} " +
        $"대기timer={m.PendingTimerJobs} timerDispatch={m.PendingTimerDispatch} " +
        $"JobPool={m.ActiveJobPoolSize} 워커재기동={m.WorkerRestarts}");
}
