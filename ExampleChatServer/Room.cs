using JobDispatcherNET;

namespace ExampleChatServer;

public sealed record RoomSnapshot(string RoomId, string Name, IReadOnlyList<string> UserIds);

/// <summary>
/// 채팅방 actor — _users 컬렉션에 락이 0개. 모든 진입은 자기 큐 통과.
///
/// 코딩 컨벤션:
///   public 동사(args)        → DoAsync(() => Process동사(args))   (큐에 푸시만)
///   private Process동사(args) → 실제 본문
///
/// 학습 포인트:
///   - DoAsyncAfter 로 자기 자신에게 다음 heartbeat 를 예약 (타이머 자기복제 패턴).
///   - 외부 read 는 GetSnapshot 으로 ManualResetEventSlim 신호 대기 (동기식).
///   - 송신(BroadcastSystem)은 user.DeliverMessage 로 위임 — Room 큐가 네트워크 IO 에 막히지 않는다.
/// </summary>
public sealed class Room : AsyncExecutable
{
    private readonly Dictionary<string, User> _users = [];
    private readonly string _roomId;
    private readonly string _name;
    private volatile bool _stopped;

    public Room(string roomId, string name)
    {
        _roomId = roomId;
        _name = name;
        Console.WriteLine($"[Room] 생성: {_name} ({_roomId})");
    }

    public string RoomId => _roomId;
    public string Name => _name;

    // ── 외부 진입점 (큐에 푸시만) ──────────────────────────────────────

    public void AddUser(User user) => DoAsync(() => ProcessAddUser(user));

    public void RemoveUser(string userId) => DoAsync(() => ProcessRemoveUser(userId));

    public void BroadcastChat(string senderId, string content)
        => DoAsync(() => ProcessBroadcastChat(senderId, content));

    public void StartHeartbeat(TimeSpan period) => DoAsync(() => Heartbeat(period));

    public void ForceRemoveAllUsers() => DoAsync(ProcessForceRemoveAllUsers);

    // ── 실제 본문 (private) ────────────────────────────────────────────

    private void ProcessAddUser(User user)
    {
        if (!_users.TryAdd(user.UserId, user))
            return;

        Console.WriteLine($"[Room {_roomId}] 입장: {user.Username}");
        BroadcastSystem(MessageType.RoomJoin, $"{user.Username}님이 입장하셨습니다.");
        user.NoteRoomJoined(_roomId);
    }

    private void ProcessRemoveUser(string userId)
    {
        if (!_users.Remove(userId, out var user))
            return;

        Console.WriteLine($"[Room {_roomId}] 퇴장: {user.Username}");
        BroadcastSystem(MessageType.RoomLeave, $"{user.Username}님이 퇴장하셨습니다.");
        user.NoteRoomLeft(_roomId);
    }

    private void ProcessBroadcastChat(string senderId, string content)
    {
        if (!_users.TryGetValue(senderId, out var sender))
            return;

        sender.TouchActivity();
        Console.WriteLine($"[Room {_roomId}] {sender.Username}: {content}");

        var message = new ChatMessage(
            Guid.NewGuid(),
            MessageType.RoomChat,
            sender.Username,
            null,
            _roomId,
            content,
            DateTimeOffset.UtcNow);

        foreach (var user in _users.Values)
            user.DeliverMessage(message);
    }

    private void ProcessForceRemoveAllUsers()
    {
        foreach (var user in _users.Values)
            user.NoteRoomLeft(_roomId);

        _users.Clear();
        Console.WriteLine($"[Room {_roomId}] 모든 사용자 강제 제거됨");
    }

    /// <summary>
    /// heartbeat 자기복제 패턴 — DoAsyncAfter 로 자기 자신을 다시 큐에 넣는다.
    /// 락 없이 주기 작업이 가능한 이유: 모든 본문이 자기 actor 큐 안에서 직렬 실행되므로
    /// _users 를 안전하게 읽을 수 있다.
    /// </summary>
    private void Heartbeat(TimeSpan period)
    {
        if (_stopped) return;

        if (_users.Count > 0)
        {
            BroadcastSystem(MessageType.RoomChat,
                $"[알림] 현재 {_name} 방에 {_users.Count}명이 있습니다.");
        }

        DoAsyncAfter(period, () => Heartbeat(period));
    }

    private void BroadcastSystem(MessageType type, string content)
    {
        var message = new ChatMessage(
            Guid.NewGuid(), type, "시스템", null, _roomId, content, DateTimeOffset.UtcNow);

        foreach (var user in _users.Values)
            user.DeliverMessage(message);
    }

    // ── 동기 read API ──────────────────────────────────────────────────

    /// <summary>차단(blocking) 스냅샷.</summary>
    public RoomSnapshot GetSnapshot()
    {
        using var ev = new ManualResetEventSlim(false);
        RoomSnapshot? result = null;
        DoAsync(() =>
        {
            result = new RoomSnapshot(_roomId, _name, _users.Keys.ToList());
            ev.Set();
        });
        ev.Wait();
        return result!;
    }

    public override ValueTask DisposeAsync()
    {
        _stopped = true;
        return base.DisposeAsync();
    }
}
