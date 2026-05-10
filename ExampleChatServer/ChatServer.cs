using JobDispatcherNET;

namespace ExampleChatServer;

// ─────────────────────────────────────────────────────────────────────
// ChatServer — JobDispatcherNET 활용을 보여주기 위한 actor 기반 채팅 서버
//
// 코딩 컨벤션:
//   public Handle*(args)  → DoAsync(() => Process*(args))   (큐에 푸시만)
//   private Process*(args) → 실제 본문 (디버거 / 스택트레이스 / grep 친화적)
//
// 동기식 (async/await 키워드 미사용):
//   - Start / Stop / GetSnapshot 모두 차단(blocking) API.
//   - 라이브러리 내부의 Task 는 호출 끝에서 .AsTask().Wait() 로만 1회 차단한다.
//
// 학습 포인트:
//   1) actor = AsyncExecutable. ChatServer / Room / User 모두 자기 큐를 가진다.
//      → 클래스 안에 lock / Mutex / ReaderWriterLockSlim 이 단 하나도 없다.
//   2) 외부 read 는 ManualResetEventSlim 신호 대기.
//   3) IRunnable + AsyncExecutable 협업 — 워커가 InboundCommands 를 dequeue.
//   4) DoAsyncAfter 로 주기 작업 자기복제.
// ─────────────────────────────────────────────────────────────────────

public sealed record ServerSnapshot(
    IReadOnlyList<UserSnapshot> Users,
    IReadOnlyList<RoomSnapshot> Rooms);

public sealed class ChatServer : AsyncExecutable
{
    private readonly Dictionary<string, User> _users = [];
    private readonly Dictionary<string, Room> _rooms = [];
    private readonly int _workerCount;
    private readonly TimeSpan _statsPeriod;
    private readonly TimeSpan _idleScanPeriod;
    private readonly long _idleThresholdMs;

    private JobDispatcher<ChatWorker>? _dispatcher;
    private volatile bool _stopped;

    public ChatServer(
        int workerCount = 4,
        TimeSpan? statsPeriod = null,
        TimeSpan? idleScanPeriod = null,
        long idleThresholdMs = 30_000)
    {
        _workerCount = workerCount;
        _statsPeriod = statsPeriod ?? TimeSpan.FromSeconds(5);
        _idleScanPeriod = idleScanPeriod ?? TimeSpan.FromSeconds(10);
        _idleThresholdMs = idleThresholdMs;
    }

    public void Start()
    {
        Console.WriteLine($"[Server] 시작 (워커 {_workerCount}개, 유휴 임계 {_idleThresholdMs}ms)");

        DoAsync(CreateDefaultRooms);

        _dispatcher = new JobDispatcher<ChatWorker>(_workerCount);
        // RunWorkerThreadsAsync 는 스레드를 시작하고 Task 를 즉시 반환 — await 불필요
        _ = _dispatcher.RunWorkerThreadsAsync();

        // 자기복제 heartbeat 두 종류 시작
        DoAsync(StatsHeartbeat);
        DoAsync(IdleScanHeartbeat);
    }

    public void Stop()
    {
        Console.WriteLine("[Server] 종료 시작");
        _stopped = true;

        DoAsync(ForceCleanupAllRooms);

        // 자기 큐 drain 대기 (라이브러리의 ValueTask 를 Task 로 바꿔 한 번만 차단)
        DisposeAsync().AsTask().Wait();

        // 워커 스레드 정지 + Join (동기)
        _dispatcher?.Dispose();

        TimerRegistry.DisposeAll();
        Console.WriteLine("[Server] 종료 완료");
    }

    private void CreateDefaultRooms()
    {
        _rooms["general"] = MakeRoom("general", "일반 채팅");
        _rooms["game"] = MakeRoom("game", "게임 채팅");
        _rooms["dev"] = MakeRoom("dev", "개발자 채팅");
    }

    private Room MakeRoom(string id, string name)
    {
        var room = new Room(id, name);
        room.StartHeartbeat(TimeSpan.FromSeconds(15));
        return room;
    }

    private void ForceCleanupAllRooms()
    {
        foreach (var room in _rooms.Values)
            room.ForceRemoveAllUsers();
    }

    // ── 외부 진입점 (큐에 푸시만) ──────────────────────────────────────

    public void HandleUserConnect(IChatClient client)
        => DoAsync(() => ProcessUserConnect(client));

    public void HandleUserDisconnect(string userId)
        => DoAsync(() => ProcessUserDisconnect(userId));

    public void HandleRoomJoin(string userId, string roomId)
        => DoAsync(() => ProcessRoomJoin(userId, roomId));

    public void HandleRoomLeave(string userId, string roomId)
        => DoAsync(() => ProcessRoomLeave(userId, roomId));

    public void HandleRoomChat(string userId, string roomId, string content)
        => DoAsync(() => ProcessRoomChat(userId, roomId, content));

    public void HandlePrivateChat(string senderId, string recipientId, string content)
        => DoAsync(() => ProcessPrivateChat(senderId, recipientId, content));

    public void HandleInstantMessage(string senderId, string recipientId, string content)
        => DoAsync(() => ProcessInstantMessage(senderId, recipientId, content));

    // ── 실제 본문 (private, 자기 큐에서 직렬 실행) ─────────────────────

    private void ProcessUserConnect(IChatClient client)
    {
        if (_users.ContainsKey(client.UserId))
        {
            Console.WriteLine($"[Server] 이미 접속 중: {client.UserId}");
            return;
        }

        var user = new User(client);
        _users[user.UserId] = user;
        Console.WriteLine($"[Server] 접속: {user.Username} ({user.UserId})");

        BroadcastSystemToAll(MessageType.UserConnect, $"{user.Username}님이 접속하셨습니다.");
    }

    private void ProcessUserDisconnect(string userId)
    {
        if (!_users.Remove(userId, out var user))
            return;

        Console.WriteLine($"[Server] 종료: {user.Username} ({user.UserId})");

        // 참여 중이던 방에서 퇴장 — 방 actor 큐가 처리한다
        foreach (var room in _rooms.Values)
            room.RemoveUser(userId);

        BroadcastSystemToAll(MessageType.UserDisconnect, $"{user.Username}님이 접속을 종료하셨습니다.");
    }

    private void ProcessRoomJoin(string userId, string roomId)
    {
        if (_users.TryGetValue(userId, out var user) &&
            _rooms.TryGetValue(roomId, out var room))
        {
            room.AddUser(user);
        }
        else
        {
            Console.WriteLine($"[Server] 입장 실패: user={userId}, room={roomId}");
        }
    }

    private void ProcessRoomLeave(string userId, string roomId)
    {
        if (_rooms.TryGetValue(roomId, out var room))
            room.RemoveUser(userId);
    }

    private void ProcessRoomChat(string userId, string roomId, string content)
    {
        if (_rooms.TryGetValue(roomId, out var room))
            room.BroadcastChat(userId, content);
    }

    private void ProcessPrivateChat(string senderId, string recipientId, string content)
    {
        if (!_users.TryGetValue(senderId, out var sender) ||
            !_users.TryGetValue(recipientId, out var recipient))
            return;

        sender.TouchActivity();

        var msg = new ChatMessage(
            Guid.NewGuid(), MessageType.PrivateChat,
            sender.Username, recipient.Username, null, content, DateTimeOffset.UtcNow);

        sender.DeliverMessage(msg);
        recipient.DeliverMessage(msg);
    }

    private void ProcessInstantMessage(string senderId, string recipientId, string content)
    {
        if (!_users.TryGetValue(senderId, out var sender) ||
            !_users.TryGetValue(recipientId, out var recipient))
            return;

        sender.TouchActivity();

        var msg = new ChatMessage(
            Guid.NewGuid(), MessageType.InstantMessage,
            sender.Username, recipient.Username, null, content, DateTimeOffset.UtcNow);

        sender.DeliverMessage(msg);
        recipient.DeliverMessage(msg);
    }

    // ── 동기 read API ──────────────────────────────────────────────────

    /// <summary>
    /// 차단(blocking) 스냅샷. 호출자는 actor Flush 컨텍스트 밖(메인 스레드)에서 호출해야 한다.
    /// 두 단계로 동작:
    ///   1) server actor 큐에서 user/room 참조만 복사 (signaling)
    ///   2) 호출자 스레드에서 각 actor 의 GetSnapshot() 호출 — actor flush 안이 아니므로 데드락 없음
    /// </summary>
    public ServerSnapshot GetSnapshot()
    {
        User[] userArr;
        Room[] roomArr;
        using (var ev = new ManualResetEventSlim(false))
        {
            // 람다가 ev / 결과 변수를 closure 로 잡아야 해서 여기는 이름 추출이 어렵다.
            // 짧은 본문이므로 인라인 람다 유지.
            User[]? u = null; Room[]? r = null;
            DoAsync(() =>
            {
                u = _users.Values.ToArray();
                r = _rooms.Values.ToArray();
                ev.Set();
            });
            ev.Wait();
            userArr = u!;
            roomArr = r!;
        }

        // 2단계 — 호출자 스레드에서 fan-out (각 actor 가 자기 큐로 직렬화)
        var users = userArr.Select(x => x.GetSnapshot()).ToArray();
        var rooms = roomArr.Select(x => x.GetSnapshot()).ToArray();
        return new ServerSnapshot(users, rooms);
    }

    // ── heartbeat (자기복제) ───────────────────────────────────────────

    private void StatsHeartbeat()
    {
        if (_stopped) return;

        Console.WriteLine($"\n==== [Stats] 사용자 {_users.Count}명 / 방 {_rooms.Count}개 / 워커 처리 {ChatWorker.TotalProcessed}건 ====\n");

        DoAsyncAfter(_statsPeriod, StatsHeartbeat);
    }

    private void IdleScanHeartbeat()
    {
        if (_stopped) return;

        // 각 user actor 에게 자체 검사를 부탁 (lastActivity 는 user 본인만이 정확히 안다)
        foreach (var user in _users.Values)
            user.CheckIdleAndDisconnect(this, _idleThresholdMs);

        DoAsyncAfter(_idleScanPeriod, IdleScanHeartbeat);
    }

    // ── 내부 helper ────────────────────────────────────────────────────

    private void BroadcastSystemToAll(MessageType type, string content)
    {
        var msg = new ChatMessage(
            Guid.NewGuid(), type, "시스템", null, null, content, DateTimeOffset.UtcNow);

        foreach (var user in _users.Values)
            user.DeliverMessage(msg);
    }
}
