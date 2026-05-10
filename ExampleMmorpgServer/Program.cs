using ExampleMmorpgServer;
using JobDispatcherNET;

// ─────────────────────────────────────────────────────────────
// MMORPG 서버 예제 — JobDispatcherNET 플레이어 Actor 기반
//
// 핵심:
//   존이 1개여도 각 플레이어가 자신만의 AsyncExecutable(Actor)을 가지므로
//   서로 다른 플레이어의 패킷은 완전 병렬 처리된다.
//   같은 대상에 대한 작업은 해당 Actor에서 자동 직렬화 (lock 없음).
//
// 동기식 (async/await 키워드 미사용):
//   실제 게임 서버는 IO 대기를 별도 OS 스레드로 처리하므로
//   async 상태머신 비용을 지불할 필요가 없다.
//
// 패킷 처리 흐름 (IO 스레드와 패킷 처리 스레드 완전 분리):
//   네트워크 IO 스레드 → GameWorker.InboundCommands 에 push 만
//   워커 스레드 → InboundCommands dequeue → GameServer.HandleX
//     → GameZone actor Flush → PlayerActor.DoAsync()
//     → 비어있는 워커 스레드가 처리 (플레이어 단위 병렬)
//
// 전투 흐름 (근접공격 A→B):
//   ActorA.DoAsync(스냅샷 캡처) → ActorB.DoAsync(스냅샷으로 데미지 적용)
//   A와 B가 다른 스레드에서 병렬 처리될 수 있음
//
// 실행 모드:
//   인자 없이  → 시뮬레이션 모드 (자동 시나리오)
//   --network → TCP 서버 모드 (포트 9000)
// ─────────────────────────────────────────────────────────────

// Actor 큐 안에서 발생한 미처리 예외를 한 곳에서 잡는다 (라이브러리 기능)
AsyncExecutable.OnError = ex => Console.Error.WriteLine($"[ActorError] {ex}");

bool networkMode = args.Contains("--network");

var gameServer = new GameServer("대륙전쟁 필드", width: 500f, height: 500f, workerCount: 4);
gameServer.Start();

if (networkMode)
    RunNetworkMode(gameServer);
else
    RunSimulation(gameServer);

gameServer.Stop();

// ─────────────────────────────────────────────
// TCP 서버 모드
// ─────────────────────────────────────────────
static void RunNetworkMode(GameServer gameServer)
{
    var networkServer = new NetworkServer(gameServer, port: 9000);
    networkServer.Start();

    Console.WriteLine("TCP 서버 모드. 'q' 입력 시 종료.");
    Console.WriteLine("텔넷 접속: telnet localhost 9000");
    Console.WriteLine("패킷 예시: EnterZone|field|50|50 / Move|60|70 / MeleeAttack|player_2 / AreaAttack|55|55|10\n");

    while (Console.ReadLine() != "q") { }

    networkServer.Stop();
}

// ─────────────────────────────────────────────
// 시뮬레이션 모드
// 메인 스레드도 IO 스레드처럼 InboundCommands 에 push 만 하고
// 실제 처리는 워커 스레드에서 일어난다 (네트워크 모드와 동일한 흐름).
// ─────────────────────────────────────────────
static void RunSimulation(GameServer gameServer)
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

    Push(() => gameServer.HandleEnterZone(players[0], 50f, 50f));    // 전사
    Push(() => gameServer.HandleEnterZone(players[1], 52f, 50f));    // 마법사 (근처)
    Push(() => gameServer.HandleEnterZone(players[2], 51f, 51f));    // 궁수 (근처)
    Push(() => gameServer.HandleEnterZone(players[3], 100f, 100f));  // 힐러 (먼 곳)
    Push(() => gameServer.HandleEnterZone(players[4], 51f, 50f));    // 도적 (근처)

    Thread.Sleep(500);
    gameServer.PrintStatus();

    // ── 3. 이동 (각 플레이어 Actor에서 병렬 처리) ──
    Console.WriteLine("── 시나리오 2: 이동 (플레이어별 병렬 처리) ──\n");

    Push(() => gameServer.HandleMove("warrior_1", 52f, 50f));
    Push(() => gameServer.HandleMove("rogue_1", 52f, 51f));
    Push(() => gameServer.HandleMove("healer_1", 53f, 52f));
    Push(() => gameServer.HandleMove("archer_1", 52f, 52f));

    Thread.Sleep(300);

    // ── 4. 근접 전투 ──
    Console.WriteLine("\n── 시나리오 3: 근접 전투 ──");
    Console.WriteLine("  전사→마법사, 도적→마법사 동시 공격 (각 Actor에서 병렬 처리)\n");

    // 전사와 도적이 동시에 마법사를 공격
    // warrior_1 Actor와 rogue_1 Actor는 병렬 실행
    // 데미지 적용은 mage_1 Actor에서 자동 직렬화
    Push(() => gameServer.HandleMeleeAttack("warrior_1", "mage_1"));
    Push(() => gameServer.HandleMeleeAttack("rogue_1", "mage_1"));

    // 마법사 반격
    Push(() => gameServer.HandleMeleeAttack("mage_1", "warrior_1"));

    Thread.Sleep(500);
    gameServer.PrintStatus();

    // ── 5. 범위 공격 (AoE) ──
    Console.WriteLine("── 시나리오 4: 범위 공격 (AoE) ──");
    Console.WriteLine("  마법사가 (52,50) 중심 반경 5.0 범위 공격");
    Console.WriteLine("  → SpatialIndex에서 주변 조회 → 각 대상 Actor로 데미지 분배 (병렬!)\n");

    Push(() => gameServer.HandleAreaAttack("mage_1", 52f, 51f, 5.0f));
    Thread.Sleep(500);

    gameServer.PrintStatus();

    // ── 6. 집중 공격으로 사망 유발 ──
    Console.WriteLine("── 시나리오 5: 집중 공격 → 사망 → 5초 후 부활 ──\n");

    for (int i = 0; i < 8; i++)
    {
        Push(() => gameServer.HandleMeleeAttack("warrior_1", "mage_1"));
        Push(() => gameServer.HandleMeleeAttack("rogue_1", "mage_1"));
        Thread.Sleep(30);
    }

    Thread.Sleep(500);
    gameServer.PrintStatus();

    // ── 7. 부활 대기 ──
    Console.WriteLine("── 시나리오 6: 부활 대기 (5초, DoAsyncAfter) ──\n");
    Console.WriteLine("대기 중...");
    Thread.Sleep(6000);

    gameServer.PrintStatus();

    // ── 8. 대규모 동시 전투 (병렬 처리 시연) ──
    Console.WriteLine("── 시나리오 7: 모든 플레이어 동시 전투 (완전 병렬) ──");
    Console.WriteLine("  5명이 서로를 동시에 공격 — 각 Actor가 독립적으로 병렬 처리\n");

    // 모든 플레이어를 가까이 이동
    Push(() => gameServer.HandleMove("warrior_1", 50f, 50f));
    Push(() => gameServer.HandleMove("mage_1", 51f, 50f));
    Push(() => gameServer.HandleMove("archer_1", 50f, 51f));
    Push(() => gameServer.HandleMove("healer_1", 51f, 51f));
    Push(() => gameServer.HandleMove("rogue_1", 50.5f, 50.5f));
    Thread.Sleep(200);

    // 동시 교차 공격
    Push(() => gameServer.HandleMeleeAttack("warrior_1", "mage_1"));    // 전사 → 마법사
    Push(() => gameServer.HandleMeleeAttack("mage_1", "warrior_1"));    // 마법사 → 전사
    Push(() => gameServer.HandleMeleeAttack("archer_1", "rogue_1"));    // 궁수 → 도적
    Push(() => gameServer.HandleMeleeAttack("rogue_1", "healer_1"));    // 도적 → 힐러
    Push(() => gameServer.HandleMeleeAttack("healer_1", "archer_1"));   // 힐러 → 궁수

    Thread.Sleep(500);
    gameServer.PrintStatus();

    // ── 9. 접속 종료 ──
    Console.WriteLine("── 시나리오 8: 플레이어 접속 종료 ──\n");
    Push(() => gameServer.HandlePlayerDisconnect("healer_1"));
    Thread.Sleep(200);
    gameServer.PrintStatus();

    Console.WriteLine("====== 시뮬레이션 종료 ======\n");
}

// 메인 스레드(IO 스레드 역할)는 InboundCommands 에 push 만 한다.
// 실제 처리는 GameWorker 가 dequeue 해서 워커 스레드에서 invoke.
static void Push(Action cmd) => GameWorker.InboundCommands.Enqueue(cmd);
