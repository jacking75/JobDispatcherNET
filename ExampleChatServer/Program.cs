using ExampleChatServer;
using JobDispatcherNET;

// ─────────────────────────────────────────────────────────────────────
// JobDispatcherNET 활용 채팅 서버 데모 (동기식 — async/await 키워드 미사용)
//
// 이 예제가 라이브러리에 대해 보여주는 것:
//   1) AsyncExecutable 을 상속한 actor 3종(ChatServer / Room / User)이
//      lock 없이 자기 상태를 안전하게 갱신한다.
//   2) JobDispatcher<ChatWorker> 의 워커 스레드 위에서 실제 명령이 처리된다.
//      외부 입력은 ChatWorker.InboundCommands 에 enqueue → 워커가 dequeue & invoke
//      → server.HandleX → DoAsync → Flush 가 워커 스레드에서 일어난다.
//   3) DoAsyncAfter 를 사용한 자기복제 heartbeat:
//      - ChatServer: 5초마다 통계, 10초마다 유휴 스캔
//      - Room: 15초마다 인원 알림
//      - User: idle 검사 (server → user.CheckIdleAndDisconnect)
//   4) 동기 read API (GetSnapshot) — ManualResetEventSlim 신호 대기.
//
// 실행 모드:
//   인자 없이                  : 인터랙티브 REPL (콘솔에서 명령 입력)
//   --simulate                 : 자동 시나리오 (단일 producer, 회귀용)
//   --simulate-idle            : 유휴 disconnect 데모 (짧은 임계치로 자동 종료까지)
//   --network [conn] [seconds] : 멀티스레드 네트워크 시뮬레이션 (기본 8개 IO 스레드, 8초)
//                                각 IO 스레드가 자기 클라이언트의 패킷을 흉내내어
//                                ChatWorker.InboundCommands 에 동시에 푸시.
// ─────────────────────────────────────────────────────────────────────

AsyncExecutable.OnError = ex => Console.Error.WriteLine($"[ActorError] {ex}");

var mode = args.Length == 0 ? "interactive" :
           args[0] == "--simulate" ? "simulate" :
           args[0] == "--simulate-idle" ? "simulate-idle" :
           args[0] == "--network" ? "network" :
           "interactive";

if (mode == "simulate-idle")
    RunSimulationIdle();
else if (mode == "simulate")
    RunSimulation();
else if (mode == "network")
    RunNetworkSimulation(args);
else
    RunInteractive();

return;

// ── 모드별 진입점 ────────────────────────────────────────────────────

static void RunInteractive()
{
    var server = new ChatServer(workerCount: 4, idleThresholdMs: 60_000);
    server.Start();

    Console.WriteLine();
    Console.WriteLine("명령:");
    Console.WriteLine("  connect <id> <name>           사용자 접속");
    Console.WriteLine("  disconnect <id>               사용자 종료");
    Console.WriteLine("  join <id> <roomId>            방 입장 (general/game/dev)");
    Console.WriteLine("  leave <id> <roomId>           방 퇴장");
    Console.WriteLine("  say <id> <roomId> <message>   방 채팅");
    Console.WriteLine("  pm <senderId> <recvId> <msg>  1:1 채팅");
    Console.WriteLine("  im <senderId> <recvId> <msg>  쪽지");
    Console.WriteLine("  status                        서버 스냅샷");
    Console.WriteLine("  demo                          기본 시나리오 자동 실행");
    Console.WriteLine("  quit                          종료");
    Console.WriteLine();

    while (true)
    {
        Console.Write("> ");
        var line = Console.ReadLine();
        if (line is null) break;
        line = line.Trim();
        if (line.Length == 0) continue;

        if (line == "quit" || line == "exit") break;

        if (line == "status")
        {
            var snap = server.GetSnapshot();
            PrintSnapshot(snap);
            continue;
        }

        if (line == "demo")
        {
            EnqueueScriptedScenario(server);
            Console.WriteLine("(시나리오 명령을 워커 큐에 푸시했습니다)");
            continue;
        }

        if (!TryParseCommand(line, out var action, out var error))
        {
            Console.WriteLine($"잘못된 명령: {error}");
            continue;
        }

        // 워커 스레드가 dequeue 하여 실행 → server actor Flush 가 워커 스레드에서 일어남
        ChatWorker.InboundCommands.Enqueue(() => action(server));
    }

    server.Stop();
}

static void RunSimulation()
{
    var server = new ChatServer(workerCount: 4, idleThresholdMs: 60_000);
    server.Start();

    EnqueueScriptedScenario(server);

    // 워커들이 처리할 시간을 준다
    Thread.Sleep(2_500);

    var snap = server.GetSnapshot();
    PrintSnapshot(snap);

    Thread.Sleep(500);
    server.Stop();
}

/// <summary>
/// 멀티스레드 네트워크 부하 시뮬레이션.
/// N개 IO 스레드가 동시에 ChatWorker.InboundCommands 에 명령을 푸시한다.
/// 실제 채팅 서버에서 N개 TCP 연결이 각자 자기 IO 스레드를 갖는 상황을 흉내냄.
/// </summary>
static void RunNetworkSimulation(string[] args)
{
    int connectionCount = args.Length >= 2 && int.TryParse(args[1], out var c) ? c : 8;
    int durationSec     = args.Length >= 3 && int.TryParse(args[2], out var d) ? d : 8;

    var server = new ChatServer(workerCount: 4, idleThresholdMs: 60_000);
    server.Start();

    using var net = new NetworkSimulator(server, connectionCount);
    net.Start();

    Console.WriteLine();
    Console.WriteLine($"=== 네트워크 시뮬레이션 시작: {connectionCount}개 IO 스레드 × {durationSec}초 ===");
    Console.WriteLine($"=== 멀티 producer → ConcurrentQueue → {4}개 워커 → actor 큐 ===");
    Console.WriteLine();

    Thread.Sleep(TimeSpan.FromSeconds(durationSec));

    Console.WriteLine();
    Console.WriteLine("=== IO 스레드 정지, 잔여 명령 drain 대기 ===");
    net.Dispose();
    Thread.Sleep(500);   // 워커가 InboundCommands 잔여를 처리할 시간

    var snap = server.GetSnapshot();
    PrintSnapshot(snap);

    Console.WriteLine($"[Stats] IO push: {net.TotalSent}건, Worker 처리: {ChatWorker.TotalProcessed}건");

    server.Stop();
}

/// <summary>
/// 짧은 유휴 임계치 + 빠른 스캔으로 idle disconnect 흐름을 30초 안에 보여준다.
/// </summary>
static void RunSimulationIdle()
{
    var server = new ChatServer(
        workerCount: 4,
        idleScanPeriod: TimeSpan.FromSeconds(2),
        idleThresholdMs: 3_000);
    server.Start();

    var alice = new ChatNetworkClient("alice", "앨리스");
    var bob   = new ChatNetworkClient("bob",   "밥");

    ChatWorker.InboundCommands.Enqueue(() => server.HandleUserConnect(alice));
    ChatWorker.InboundCommands.Enqueue(() => server.HandleUserConnect(bob));
    ChatWorker.InboundCommands.Enqueue(() => server.HandleRoomJoin("alice", "general"));
    ChatWorker.InboundCommands.Enqueue(() => server.HandleRoomJoin("bob",   "general"));
    ChatWorker.InboundCommands.Enqueue(() => server.HandleRoomChat("alice", "general", "안녕!"));

    Console.WriteLine("(3초 유휴 임계 — 자동 disconnect 발생을 기다립니다)");
    Thread.Sleep(7_000);

    var snap = server.GetSnapshot();
    PrintSnapshot(snap);

    server.Stop();
}

// ── 시나리오 / 파서 / 출력 helper ────────────────────────────────────

static void EnqueueScriptedScenario(ChatServer server)
{
    var clients = new[]
    {
        new ChatNetworkClient("user1", "철수"),
        new ChatNetworkClient("user2", "영희"),
        new ChatNetworkClient("user3", "민수"),
        new ChatNetworkClient("user4", "지영"),
    };

    foreach (var c in clients)
        ChatWorker.InboundCommands.Enqueue(() => server.HandleUserConnect(c));

    ChatWorker.InboundCommands.Enqueue(() => server.HandleRoomJoin("user1", "general"));
    ChatWorker.InboundCommands.Enqueue(() => server.HandleRoomJoin("user2", "general"));
    ChatWorker.InboundCommands.Enqueue(() => server.HandleRoomJoin("user3", "game"));
    ChatWorker.InboundCommands.Enqueue(() => server.HandleRoomJoin("user4", "game"));
    ChatWorker.InboundCommands.Enqueue(() => server.HandleRoomJoin("user1", "dev"));
    ChatWorker.InboundCommands.Enqueue(() => server.HandleRoomJoin("user3", "dev"));

    ChatWorker.InboundCommands.Enqueue(() => server.HandleRoomChat("user1", "general", "안녕하세요, 모두들!"));
    ChatWorker.InboundCommands.Enqueue(() => server.HandleRoomChat("user2", "general", "반갑습니다, 철수님!"));
    ChatWorker.InboundCommands.Enqueue(() => server.HandleRoomChat("user3", "game", "게임 하실 분?"));
    ChatWorker.InboundCommands.Enqueue(() => server.HandleRoomChat("user4", "game", "저요!"));

    ChatWorker.InboundCommands.Enqueue(() => server.HandlePrivateChat("user1", "user2", "잠깐 얘기 좀."));
    ChatWorker.InboundCommands.Enqueue(() => server.HandleInstantMessage("user3", "user4", "서버 IP: 192.168.1.100"));

    ChatWorker.InboundCommands.Enqueue(() => server.HandleRoomLeave("user1", "general"));
    ChatWorker.InboundCommands.Enqueue(() => server.HandleUserDisconnect("user4"));
}

static bool TryParseCommand(string line, out Action<ChatServer> action, out string error)
{
    action = _ => { };
    error = "";

    var parts = line.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0) { error = "빈 명령"; return false; }

    var cmd = parts[0];
    var rest = parts.Length > 1 ? parts[1] : "";

    switch (cmd)
    {
        case "connect":
        {
            var p = rest.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (p.Length != 2) { error = "connect <id> <name>"; return false; }
            var id = p[0]; var name = p[1];
            action = s => s.HandleUserConnect(new ChatNetworkClient(id, name));
            return true;
        }
        case "disconnect":
        {
            if (rest.Length == 0) { error = "disconnect <id>"; return false; }
            var id = rest;
            action = s => s.HandleUserDisconnect(id);
            return true;
        }
        case "join":
        case "leave":
        {
            var p = rest.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (p.Length != 2) { error = $"{cmd} <id> <roomId>"; return false; }
            var id = p[0]; var room = p[1];
            action = cmd == "join"
                ? s => s.HandleRoomJoin(id, room)
                : s => s.HandleRoomLeave(id, room);
            return true;
        }
        case "say":
        {
            var p = rest.Split(' ', 3, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (p.Length != 3) { error = "say <id> <roomId> <message>"; return false; }
            var id = p[0]; var room = p[1]; var msg = p[2];
            action = s => s.HandleRoomChat(id, room, msg);
            return true;
        }
        case "pm":
        case "im":
        {
            var p = rest.Split(' ', 3, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (p.Length != 3) { error = $"{cmd} <senderId> <recvId> <message>"; return false; }
            var s1 = p[0]; var s2 = p[1]; var msg = p[2];
            action = cmd == "pm"
                ? s => s.HandlePrivateChat(s1, s2, msg)
                : s => s.HandleInstantMessage(s1, s2, msg);
            return true;
        }
        default:
            error = $"알 수 없는 명령: {cmd}";
            return false;
    }
}

static void PrintSnapshot(ServerSnapshot snap)
{
    Console.WriteLine();
    Console.WriteLine($"==== Snapshot — Users {snap.Users.Count}, Rooms {snap.Rooms.Count} ====");
    foreach (var u in snap.Users)
        Console.WriteLine($"  user {u.UserId} ({u.Username}) idle={u.IdleMs}ms rooms=[{string.Join(",", u.JoinedRoomIds)}]");
    foreach (var r in snap.Rooms)
        Console.WriteLine($"  room {r.RoomId} ({r.Name}) members=[{string.Join(",", r.UserIds)}]");
    Console.WriteLine("====================================================");
    Console.WriteLine();
}
