using AdvancedMmorpgServer;

var configPath = args.Length > 0 ? args[0] : "config.json";
var config = ServerConfig.Load(configPath);

JobDispatcherNET.AsyncExecutable.OnError = ex =>
    Console.Error.WriteLine($"[Actor 오류] {ex}");

var server = new GameServer(config);
await server.StartAsync();

using var exitCts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("\n[서버] Ctrl+C 감지 — 종료 시작");
    exitCts.Cancel();
};

Console.WriteLine("'q' 입력 시 종료. Ctrl+C 도 가능.\n");

var inputTask = Task.Run(() =>
{
    while (!exitCts.IsCancellationRequested)
    {
        var line = Console.ReadLine();
        if (line is null) break;
        if (line.Trim().Equals("q", StringComparison.OrdinalIgnoreCase))
        {
            exitCts.Cancel();
            break;
        }
        if (line.Trim().Equals("status", StringComparison.OrdinalIgnoreCase))
            PrintStatus(server);
    }
});

try
{
    await Task.Delay(Timeout.Infinite, exitCts.Token);
}
catch (OperationCanceledException) { }

await server.DisposeAsync();

static void PrintStatus(GameServer s)
{
    int alivePlayers = s.World.Players.Count(p => !p.Despawned);
    int aliveNpcs = s.World.Npcs.Count(n => !n.Despawned && n.Npc.IsAlive);
    int sessions = s.World.Sessions.Count;
    Console.WriteLine($"[상태] 세션 {sessions} / 플레이어 {alivePlayers} / 살아있는 NPC {aliveNpcs}/{s.World.Npcs.Count}");
}
