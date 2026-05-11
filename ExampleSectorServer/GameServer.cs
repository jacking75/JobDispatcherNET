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

    // ── 공개 API (write) ─────────────────────────────────────────────
    //
    // 모든 write 는 SectorWorker.InboundCommands 에 푸시만 한다.
    // → caller 스레드(시뮬레이터/네트워크 IO)는 즉시 반환
    // → 워커 스레드가 dequeue 하여 _zone.HandleX 를 호출
    // → 그 호출 안에서 _zone.DoAsync 가 일어나므로 actor Flush 가 워커 스레드에서 실행됨
    //
    // 만약 EnterZone 등을 _zone.HandleX 로 직접 호출하면 시뮬레이터 스레드가 leader 가 되어
    // actor 의 작업을 시뮬레이터 스레드에서 실행하게 된다 — producer/consumer 분리가 깨진다.

    public void EnterZone(Player player, float x, float y)
        => SectorWorker.InboundCommands.Enqueue(() => _zone.EnterZone(player, x, y));

    public void LeaveZone(string playerId)
        => SectorWorker.InboundCommands.Enqueue(() => _zone.LeaveZone(playerId));

    public void Move(string playerId, float x, float y)
        => SectorWorker.InboundCommands.Enqueue(() => _zone.HandleMove(playerId, x, y));

    public void MeleeAttack(string attackerId, string targetId)
        => SectorWorker.InboundCommands.Enqueue(() => _zone.HandleMeleeAttack(attackerId, targetId));

    public void AreaAttack(string attackerId, float cx, float cy, float radius)
        => SectorWorker.InboundCommands.Enqueue(() => _zone.HandleAreaAttack(attackerId, cx, cy, radius));

    public void Whisper(string senderId, string targetId, string message)
        => SectorWorker.InboundCommands.Enqueue(() => _zone.HandleWhisper(senderId, targetId, message));

    // ── 공개 API (read) ──────────────────────────────────────────────
    //
    // 차단 스냅샷 — caller 스레드가 직접 호출.
    // GameZone/ZoneSector 의 큐에 (블로킹) 작업을 등록하고 결과를 받아온다.
    // write 와 달리 producer/consumer 분리 데모와는 무관 (외부 조회 도구 성격).

    public void PrintStatus()
    {
        Console.WriteLine($"\n========== 서버 상태 (조회 스레드:{Environment.CurrentManagedThreadId}) ==========");
        Console.WriteLine($"  InboundCommands 누적 처리 = {SectorWorker.TotalProcessed}건");
        _zone.PrintStatus();
        Console.WriteLine("================================\n");
    }
}
