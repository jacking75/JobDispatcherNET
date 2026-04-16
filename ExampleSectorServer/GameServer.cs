using JobDispatcherNET;

namespace ExampleSectorServer;

/// <summary>
/// 섹터 기반 게임 서버.
/// 존을 NxN 섹터로 분할하고 워커 스레드를 할당한다.
///
/// 아키텍처 비교:
///   ExampleMmorpgServer — 플레이어 Actor 기반 (플레이어 수만큼 병렬)
///   ExampleSectorServer — 섹터 기반 (섹터 수만큼 병렬)
///
/// 섹터 기반의 장점: 같은 섹터 내 플레이어 간 상호작용이 빈번할 때 효율적
/// 섹터 기반의 단점: 특정 섹터에 플레이어가 몰리면 병목, 경계 처리 복잡
/// </summary>
public class GameServer
{
    private readonly GameZone _zone;
    private readonly int _workerCount;
    private JobDispatcher<SectorWorker>? _dispatcher;

    public GameServer(float zoneWidth, float zoneHeight, int sectorCols, int sectorRows,
        int workerCount)
    {
        _zone = new GameZone(zoneWidth, zoneHeight, sectorCols, sectorRows);
        _workerCount = workerCount;
    }

    public async Task StartAsync()
    {
        Console.WriteLine("================================================");
        Console.WriteLine("  섹터 기반 MMORPG 서버");
        Console.WriteLine($"  존: {300}x{300}, 섹터: 3x3, 워커: {_workerCount}개");
        Console.WriteLine("  각 섹터 = AsyncExecutable (lock-free 직렬화)");
        Console.WriteLine("================================================\n");

        _dispatcher = new JobDispatcher<SectorWorker>(_workerCount);
        _ = Task.Run(async () => await _dispatcher.RunWorkerThreadsAsync());

        Console.WriteLine("서버 준비 완료.\n");
    }

    public async Task StopAsync()
    {
        Console.WriteLine("\n서버 종료 중...");
        await _zone.DisposeAllAsync();
        if (_dispatcher is not null)
            await _dispatcher.DisposeAsync();
        Console.WriteLine("서버 종료 완료.");
    }

    // ── 공개 API ──

    public void EnterZone(Player player, float x, float y) => _zone.EnterZone(player, x, y);
    public void LeaveZone(string playerId) => _zone.LeaveZone(playerId);
    public void Move(string playerId, float x, float y) => _zone.HandleMove(playerId, x, y);
    public void MeleeAttack(string attackerId, string targetId) => _zone.HandleMeleeAttack(attackerId, targetId);
    public void AreaAttack(string attackerId, float cx, float cy, float radius) => _zone.HandleAreaAttack(attackerId, cx, cy, radius);
    public void Whisper(string senderId, string targetId, string message) => _zone.HandleWhisper(senderId, targetId, message);

    public void PrintStatus()
    {
        Console.WriteLine("\n========== 서버 상태 ==========");
        _zone.PrintStatus();
        Console.WriteLine("================================\n");
    }
}
