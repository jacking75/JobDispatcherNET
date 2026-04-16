using ExampleSectorServer;

// ─────────────────────────────────────────────────────────────
// 섹터 기반 MMORPG 서버 예제
//
// 존(300x300)을 3x3=9개 섹터로 분할, 각 섹터가 AsyncExecutable.
// 같은 섹터 내 작업은 lock 없이 안전.
// 섹터 경계 상호작용은 스냅샷 + 대상 섹터 DoAsync 패턴.
//
// ★ 이 예제에서 주의 깊게 볼 부분:
//   - 섹터 경계 근접 공격 (시나리오 3)
//   - 섹터 경계 범위 공격 (시나리오 4)
//   - 다른 섹터 귓속말 (시나리오 5)
//   - 섹터 이동 (시나리오 6)
// ─────────────────────────────────────────────────────────────

var server = new GameServer(
    zoneWidth: 300f, zoneHeight: 300f,
    sectorCols: 3, sectorRows: 3,
    workerCount: 3
);
await server.StartAsync();
await RunSimulation(server);
await server.StopAsync();

static async Task RunSimulation(GameServer server)
{
    Console.WriteLine("====== 시뮬레이션 시작 ======\n");

    // ── 플레이어 생성 ──
    var players = new Player[]
    {
        // 섹터(0,0) — 좌상단 [0~100, 0~100]
        new("warrior", "전사", maxHp: 1200, attack: 80, defense: 30),

        // 섹터(1,0) — 중앙상단 [100~200, 0~100], 경계 근처
        new("mage",    "마법사", maxHp: 600, attack: 120, defense: 10),

        // 섹터(0,0) — 전사와 같은 섹터
        new("archer",  "궁수", maxHp: 800, attack: 90, defense: 15),

        // 섹터(2,2) — 우하단 [200~300, 200~300], 먼 곳
        new("healer",  "힐러", maxHp: 700, attack: 40, defense: 25),

        // 섹터(1,1) — 중앙 [100~200, 100~200]
        new("rogue",   "도적", maxHp: 750, attack: 100, defense: 12),
    };

    // ────────────────────────────────────
    // 시나리오 1: 플레이어 입장
    // ────────────────────────────────────
    Console.WriteLine("── 시나리오 1: 다양한 섹터에 플레이어 입장 ──\n");

    server.EnterZone(players[0], 95f, 50f);    // 전사 — 섹터(0,0) 오른쪽 끝 근처!
    server.EnterZone(players[1], 105f, 50f);   // 마법사 — 섹터(1,0) 왼쪽 끝 근처! (전사와 경계 사이 거리 10)
    server.EnterZone(players[2], 30f, 30f);    // 궁수 — 섹터(0,0) 내부
    server.EnterZone(players[3], 250f, 250f);  // 힐러 — 섹터(2,2) 먼 곳
    server.EnterZone(players[4], 150f, 150f);  // 도적 — 섹터(1,1) 중앙

    await Task.Delay(500);
    server.PrintStatus();

    // ────────────────────────────────────
    // 시나리오 2: 같은 섹터 내 전투 (안전)
    // ────────────────────────────────────
    Console.WriteLine("── 시나리오 2: 같은 섹터 내 근접 공격 (간단하고 안전) ──\n");
    Console.WriteLine("  전사와 궁수는 같은 섹터(0,0)에 있으므로 lock 없이 안전.\n");

    server.MeleeAttack("warrior", "archer");
    await Task.Delay(300);

    // ────────────────────────────────────
    // 시나리오 3: ★ 섹터 경계 근접 공격
    // ────────────────────────────────────
    Console.WriteLine("\n── 시나리오 3: ★ 섹터 경계 근접 공격 ──");
    Console.WriteLine("  전사(95,50)는 섹터(0,0), 마법사(105,50)는 섹터(1,0).");
    Console.WriteLine("  거리=10, 근접범위=5 → 사거리 밖이지만 구조를 보여주기 위해 이동 후 공격.\n");

    // 전사를 경계 가까이 이동
    server.Move("warrior", 98f, 50f);
    // 마법사도 경계 가까이 이동
    server.Move("mage", 102f, 50f);
    await Task.Delay(200);

    // 경계 넘어 공격! (전사 섹터(0,0) → 마법사 섹터(1,0))
    Console.WriteLine("  ★ 경계 넘어 공격: 전사 섹터(0,0) → 마법사 섹터(1,0)");
    server.MeleeAttack("warrior", "mage");
    await Task.Delay(500);

    server.PrintStatus();

    // ────────────────────────────────────
    // 시나리오 4: ★ 섹터 경계 범위 공격
    // ────────────────────────────────────
    Console.WriteLine("── 시나리오 4: ★ 섹터 경계 범위 공격 (AoE) ──");
    Console.WriteLine("  마법사(102,50)가 중심(100,50) 반경15로 AoE → 섹터(0,0)과 (1,0) 양쪽 영향\n");

    server.AreaAttack("mage", 100f, 50f, 15f);
    await Task.Delay(500);

    server.PrintStatus();

    // ────────────────────────────────────
    // 시나리오 5: ★ 원거리 귓속말
    // ────────────────────────────────────
    Console.WriteLine("── 시나리오 5: ★ 원거리 귓속말 ──");
    Console.WriteLine("  전사 섹터(0,0) → 힐러 섹터(2,2): 완전히 다른 섹터!");
    Console.WriteLine("  반드시 대상 섹터의 DoAsync를 통해야 안전.\n");

    server.Whisper("warrior", "healer", "힐러님 힐 부탁드려요!");
    server.Whisper("healer", "warrior", "네 곧 갈게요!");
    await Task.Delay(300);

    // 같은 섹터 내 귓속말도 테스트
    Console.WriteLine("\n  (같은 섹터 내 귓속말도 동일한 패턴으로 처리)");
    server.Whisper("warrior", "archer", "궁수님 같이 가요");
    await Task.Delay(300);

    // ────────────────────────────────────
    // 시나리오 6: ★ 섹터 이동 (경계 통과)
    // ────────────────────────────────────
    Console.WriteLine("\n── 시나리오 6: ★ 섹터 이동 (경계 통과) ──");
    Console.WriteLine("  도적(150,150) 섹터(1,1) → (50,50) 섹터(0,0)으로 이동");
    Console.WriteLine("  IsTransferring 플래그로 이동 중 공격 보호.\n");

    server.Move("rogue", 50f, 50f);
    await Task.Delay(500);

    server.PrintStatus();

    // ────────────────────────────────────
    // 시나리오 7: 서로 다른 섹터에서 동시 전투
    // ────────────────────────────────────
    Console.WriteLine("── 시나리오 7: 서로 다른 섹터에서 동시 전투 (병렬) ──");
    Console.WriteLine("  섹터(0,0)과 섹터(1,0)에서 동시에 전투 발생 → 병렬 처리!\n");

    // 섹터(0,0): 도적 → 궁수
    server.MeleeAttack("rogue", "archer");
    // 섹터(1,0): 마법사 → (혼자이므로 자기 섹터 내 AoE)
    server.AreaAttack("mage", 102f, 50f, 5f);

    await Task.Delay(500);
    server.PrintStatus();

    // ────────────────────────────────────
    // 시나리오 8: 접속 종료
    // ────────────────────────────────────
    Console.WriteLine("── 시나리오 8: 플레이어 접속 종료 ──\n");
    server.LeaveZone("healer");
    await Task.Delay(200);
    server.PrintStatus();

    Console.WriteLine("====== 시뮬레이션 종료 ======\n");
}
