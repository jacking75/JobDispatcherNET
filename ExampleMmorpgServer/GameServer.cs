using JobDispatcherNET;

namespace ExampleMmorpgServer;

/// <summary>
/// 게임 서버. GameZone(actor) + 플레이어별 PlayerActor(actor) 기반 병렬 처리.
///
/// 동기식 (async/await 키워드 미사용):
///   - Start / Stop 모두 차단(blocking) API.
///   - 라이브러리 내부의 ValueTask 는 호출 끝에서 .AsTask().Wait() 로만 1회 차단한다.
///
/// 스레딩 요약:
///   - JobDispatcher가 N개의 전용 OS 스레드를 만들어 ThreadLocal 안정성을 보장
///   - GameZone이 AsyncExecutable이라 패킷 라우팅이 lock 없이 직렬화됨
///   - 각 PlayerActor도 자기 큐를 소유 → 서로 다른 플레이어 작업은 완전 병렬
///   - 같은 플레이어 대상 작업은 해당 PlayerActor 큐에서 자동 직렬화
///   - 외부 lock은 SpatialIndex의 ConcurrentDictionary 내부 버킷 lock뿐 (최소)
/// </summary>
public class GameServer
{
    private readonly GameZone _zone;
    private readonly int _workerCount;
    private JobDispatcher<GameWorker>? _dispatcher;

    public GameZone Zone => _zone;

    public GameServer(string zoneName, float width, float height, int workerCount = 4)
    {
        _zone = new GameZone(zoneName, width, height);
        _workerCount = workerCount;
    }

    public void Start()
    {
        Console.WriteLine("========================================");
        Console.WriteLine($"  MMORPG 서버 시작");
        Console.WriteLine($"  존: {_zone.Name} | 워커 스레드: {_workerCount}개");
        Console.WriteLine($"  구조: GameZone(actor) + 플레이어별 PlayerActor(actor)");
        Console.WriteLine("========================================\n");

        _dispatcher = new JobDispatcher<GameWorker>(_workerCount);
        // RunWorkerThreadsAsync는 이미 OS 스레드를 직접 만들고 Task를 즉시 반환한다.
        _ = _dispatcher.RunWorkerThreadsAsync();

        // 존 자체의 heartbeat 시작 — DoAsyncAfter 자기복제 패턴
        _zone.StartHeartbeat(TimeSpan.FromSeconds(5));

        Console.WriteLine("서버 준비 완료.\n");
    }

    public void Stop()
    {
        Console.WriteLine("\n서버를 종료합니다...");

        // GameZone actor 종료 — 모든 PlayerActor를 Despawn하고 큐들을 drain
        // ValueTask 를 Task 로 바꿔 한 번만 차단
        _zone.DisposeAsync().AsTask().Wait();

        // 워커 스레드 정지 + Join (동기)
        _dispatcher?.Dispose();

        // 비-워커 스레드(IO/메인/시뮬레이션 스레드)에서 만들어졌을 수 있는 TimerQueue 정리
        TimerRegistry.DisposeAll();

        Console.WriteLine("서버가 종료되었습니다.");
    }

    // ── 패킷 핸들러 — 모두 GameZone actor 큐로 위임 ──

    public void HandleEnterZone(Player player, float spawnX, float spawnY)
        => _zone.EnterZone(player, spawnX, spawnY);

    public void HandleMove(string playerId, float newX, float newY)
        => _zone.HandleMove(playerId, newX, newY);

    public void HandleMeleeAttack(string attackerId, string targetId)
        => _zone.HandleMeleeAttack(attackerId, targetId);

    public void HandleAreaAttack(string attackerId, float centerX, float centerY, float radius)
        => _zone.HandleAreaAttack(attackerId, centerX, centerY, radius);

    public void HandlePlayerDisconnect(string playerId)
        => _zone.LeaveZone(playerId);

    /// <summary>
    /// GetSnapshot 패턴으로 안전하게 상태 조회.
    /// 외부에서 _actors / Player 필드를 직접 읽는 race-prone read를 방지한다.
    /// </summary>
    public void PrintStatus()
    {
        var snap = _zone.GetSnapshot();
        Console.WriteLine("\n========== 서버 상태 ==========");
        Console.WriteLine($"  [{snap.Name}] 플레이어: {snap.Players.Count}명");
        foreach (var p in snap.Players)
        {
            Console.WriteLine($"    - {p.Name} ({p.X:F1},{p.Y:F1}) HP:{p.Hp}/{p.MaxHp} " +
                              $"{(p.IsAlive ? "생존" : "사망")}");
        }
        Console.WriteLine("================================\n");
    }
}
