using ExampleMmorpgServer;

// ─────────────────────────────────────────────────────────────
// MMORPG 서버 예제 — JobDispatcherNET 플레이어 Actor 기반
//
// 핵심:
//   존이 1개여도 각 플레이어가 자신만의 AsyncExecutable(Actor)을 가지므로
//   서로 다른 플레이어의 패킷은 완전 병렬 처리된다.
//   같은 대상에 대한 작업은 해당 Actor에서 자동 직렬화 (lock 없음).
//
// 패킷 처리 흐름:
//   네트워크 IO 스레드 → GameZone → PlayerActor.DoAsync()
//     → 비어있는 워커 스레드가 처리 (플레이어 단위 병렬)
//
// 전투 흐름 (근접공격 A→B):
//   ActorA.DoAsync(스냅샷 캡처)
//     → ActorB.DoAsync(스냅샷으로 데미지 적용)
//   A와 B가 다른 스레드에서 병렬 처리될 수 있음
//
// 실행 모드:
//   인자 없이  → 시뮬레이션 모드 (자동 시나리오)
//   --network → TCP 서버 모드 (포트 9000)
// ─────────────────────────────────────────────────────────────

bool networkMode = args.Contains("--network");

var gameServer = new GameServer("대륙전쟁 필드", width: 500f, height: 500f, workerCount: 4);
await gameServer.StartAsync();

if (networkMode)
{
    await RunNetworkMode(gameServer);
}
else
{
    await RunSimulation(gameServer);
}

await gameServer.StopAsync();

// ─────────────────────────────────────────────
// TCP 서버 모드
// ─────────────────────────────────────────────
static async Task RunNetworkMode(GameServer gameServer)
{
    var networkServer = new NetworkServer(gameServer, port: 9000);
    await networkServer.StartAsync();

    Console.WriteLine("TCP 서버 모드. 'q' 입력 시 종료.");
    Console.WriteLine("텔넷 접속: telnet localhost 9000");
    Console.WriteLine("패킷 예시: EnterZone|field|50|50 / Move|60|70 / MeleeAttack|player_2 / AreaAttack|55|55|10\n");

    while (Console.ReadLine() != "q") { }

    await networkServer.StopAsync();
}

// ─────────────────────────────────────────────
// 시뮬레이션 모드
// ─────────────────────────────────────────────
static async Task RunSimulation(GameServer gameServer)
{
    Console.WriteLine("====== 시뮬레이션 시작 ======\n");

    // ── 1. 플레이어 생성 ──
    var players = new Player[]
    {
        new("warrior_1", "전사김철수", maxHp: 1200, attack: 80, defense: 30),
        new("mage_1",    "마법사이영희", maxHp: 600,  attack: 120, defense: 10),
        new("archer_1",  "궁수박민수", maxHp: 800,  attack: 90,  defense: 15),
        new("healer_1",  "힐러최수진", maxHp: 700,  attack: 40,  defense: 25),
        new("rogue_1",   "도적정태현", maxHp: 750,  attack: 100, defense: 12),
    };

    foreach (var p in players)
        p.SendPacket = _ => { };

    // ── 2. 전원 입장 (한 존에 모두) ──
    Console.WriteLine("── 시나리오 1: 단일 존에 플레이어 전원 입장 ──\n");

    gameServer.HandleEnterZone(players[0], 50f, 50f);    // 전사
    gameServer.HandleEnterZone(players[1], 52f, 50f);    // 마법사 (근처)
    gameServer.HandleEnterZone(players[2], 51f, 51f);    // 궁수 (근처)
    gameServer.HandleEnterZone(players[3], 100f, 100f);  // 힐러 (먼 곳)
    gameServer.HandleEnterZone(players[4], 51f, 50f);    // 도적 (근처)

    await Task.Delay(500);
    gameServer.PrintStatus();

    // ── 3. 이동 (각 플레이어 Actor에서 병렬 처리) ──
    Console.WriteLine("── 시나리오 2: 이동 (플레이어별 병렬 처리) ──\n");

    gameServer.HandleMove("warrior_1", 52f, 50f);
    gameServer.HandleMove("rogue_1", 52f, 51f);
    gameServer.HandleMove("healer_1", 53f, 52f);
    gameServer.HandleMove("archer_1", 52f, 52f);

    await Task.Delay(300);

    // ── 4. 근접 전투 ──
    Console.WriteLine("\n── 시나리오 3: 근접 전투 ──");
    Console.WriteLine("  전사→마법사, 도적→마법사 동시 공격 (각 Actor에서 병렬 처리)\n");

    // 전사와 도적이 동시에 마법사를 공격
    // warrior_1 Actor와 rogue_1 Actor는 병렬 실행
    // 데미지 적용은 mage_1 Actor에서 자동 직렬화
    gameServer.HandleMeleeAttack("warrior_1", "mage_1");
    gameServer.HandleMeleeAttack("rogue_1", "mage_1");

    // 마법사 반격
    gameServer.HandleMeleeAttack("mage_1", "warrior_1");

    await Task.Delay(500);
    gameServer.PrintStatus();

    // ── 5. 범위 공격 (AoE) ──
    Console.WriteLine("── 시나리오 4: 범위 공격 (AoE) ──");
    Console.WriteLine("  마법사가 (52,50) 중심 반경 5.0 범위 공격");
    Console.WriteLine("  → SpatialIndex에서 주변 조회 → 각 대상 Actor로 데미지 분배 (병렬!)\n");

    gameServer.HandleAreaAttack("mage_1", 52f, 51f, 5.0f);
    await Task.Delay(500);

    gameServer.PrintStatus();

    // ── 6. 집중 공격으로 사망 유발 ──
    Console.WriteLine("── 시나리오 5: 집중 공격 → 사망 → 5초 후 부활 ──\n");

    for (int i = 0; i < 8; i++)
    {
        gameServer.HandleMeleeAttack("warrior_1", "mage_1");
        gameServer.HandleMeleeAttack("rogue_1", "mage_1");
        await Task.Delay(30);
    }

    await Task.Delay(500);
    gameServer.PrintStatus();

    // ── 7. 부활 대기 ──
    Console.WriteLine("── 시나리오 6: 부활 대기 (5초, DoAsyncAfter) ──\n");
    Console.WriteLine("대기 중...");
    await Task.Delay(6000);

    gameServer.PrintStatus();

    // ── 8. 대규모 동시 전투 (병렬 처리 시연) ──
    Console.WriteLine("── 시나리오 7: 모든 플레이어 동시 전투 (완전 병렬) ──");
    Console.WriteLine("  5명이 서로를 동시에 공격 — 각 Actor가 독립적으로 병렬 처리\n");

    // 모든 플레이어를 가까이 이동
    gameServer.HandleMove("warrior_1", 50f, 50f);
    gameServer.HandleMove("mage_1", 51f, 50f);
    gameServer.HandleMove("archer_1", 50f, 51f);
    gameServer.HandleMove("healer_1", 51f, 51f);
    gameServer.HandleMove("rogue_1", 50.5f, 50.5f);
    await Task.Delay(200);

    // 동시 교차 공격
    gameServer.HandleMeleeAttack("warrior_1", "mage_1");    // 전사 → 마법사
    gameServer.HandleMeleeAttack("mage_1", "warrior_1");    // 마법사 → 전사
    gameServer.HandleMeleeAttack("archer_1", "rogue_1");    // 궁수 → 도적
    gameServer.HandleMeleeAttack("rogue_1", "healer_1");    // 도적 → 힐러
    gameServer.HandleMeleeAttack("healer_1", "archer_1");   // 힐러 → 궁수

    await Task.Delay(500);
    gameServer.PrintStatus();

    // ── 9. 접속 종료 ──
    Console.WriteLine("── 시나리오 8: 플레이어 접속 종료 ──\n");
    gameServer.HandlePlayerDisconnect("healer_1");
    await Task.Delay(200);
    gameServer.PrintStatus();

    Console.WriteLine("====== 시뮬레이션 종료 ======\n");
}
