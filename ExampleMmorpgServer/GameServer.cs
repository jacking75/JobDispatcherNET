using JobDispatcherNET;

namespace ExampleMmorpgServer;

/// <summary>
/// 게임 서버. 단일 존 + 플레이어 Actor 기반 병렬 처리.
///
/// 스레딩 요약:
///   - GameWorker 스레드 N개가 워커 풀을 구성
///   - 각 플레이어의 PlayerActor(AsyncExecutable)가 독립적인 작업 큐를 소유
///   - 패킷 수신 → 해당 플레이어 Actor의 DoAsync → 비어있는 워커가 처리
///   - 존이 1개여도 플레이어 수만큼 Actor가 있으므로 완전 병렬
///   - lock 사용: SpatialIndex의 ConcurrentDictionary 내부 버킷 lock만 (최소)
/// </summary>
public class GameServer
{
    private readonly GameZone _zone;
    private readonly int _workerCount;
    private JobDispatcher<GameWorker>? _dispatcher;

    public GameServer(string zoneName, float width, float height, int workerCount = 4)
    {
        _zone = new GameZone(zoneName, width, height);
        _workerCount = workerCount;
    }

    public async Task StartAsync()
    {
        Console.WriteLine("========================================");
        Console.WriteLine($"  MMORPG 서버 시작");
        Console.WriteLine($"  존: {_zone.Name} | 워커 스레드: {_workerCount}개");
        Console.WriteLine($"  구조: 플레이어 Actor 기반 병렬 처리");
        Console.WriteLine("========================================\n");

        _dispatcher = new JobDispatcher<GameWorker>(_workerCount);
        _ = Task.Run(async () => await _dispatcher.RunWorkerThreadsAsync());

        Console.WriteLine("서버 준비 완료.\n");
    }

    public async Task StopAsync()
    {
        Console.WriteLine("\n서버를 종료합니다...");

        await _zone.DisposeAllActorsAsync();

        if (_dispatcher is not null)
            await _dispatcher.DisposeAsync();

        Console.WriteLine("서버가 종료되었습니다.");
    }

    // ── 패킷 핸들러 — 네트워크 스레드에서 호출됨 ──

    public void HandleEnterZone(Player player, float spawnX, float spawnY)
    {
        _zone.EnterZone(player, spawnX, spawnY);
    }

    public void HandleMove(string playerId, float newX, float newY)
    {
        _zone.HandleMove(playerId, newX, newY);
    }

    public void HandleMeleeAttack(string attackerId, string targetId)
    {
        _zone.HandleMeleeAttack(attackerId, targetId);
    }

    public void HandleAreaAttack(string attackerId, float centerX, float centerY, float radius)
    {
        _zone.HandleAreaAttack(attackerId, centerX, centerY, radius);
    }

    public void HandlePlayerDisconnect(string playerId)
    {
        _zone.LeaveZone(playerId);
    }

    public void PrintStatus()
    {
        Console.WriteLine("\n========== 서버 상태 ==========");
        _zone.PrintStatus();
        Console.WriteLine("================================\n");
    }
}
