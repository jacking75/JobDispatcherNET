using JobDispatcherNET;

namespace ExampleChatServer;

public sealed record UserSnapshot(string UserId, string Username, IReadOnlyList<string> JoinedRoomIds, long IdleMs);

/// <summary>
/// 사용자 actor — 자기 상태(_joinedRoomIds, _client, _lastActivityTickMs)는
/// 전적으로 자기 큐에서만 읽고 쓴다. 락이 단 하나도 없다.
///
/// 코딩 컨벤션:
///   public 동사(args)        → DoAsync(() => Process동사(args))   (큐에 푸시만)
///   private Process동사(args) → 실제 본문
///
/// 단, 1-statement 짜리 trivial 본문(예: 단순 활동 시각 갱신)은 인라인 람다 유지.
///
/// 동기식 (async/await 키워드 사용 안 함):
///   - 외부 read 는 ManualResetEventSlim 으로 신호받아 차단(blocking) 대기.
///   - 차단 호출자는 actor 의 Flush 컨텍스트가 아닌 곳(예: 메인 스레드)에서 호출해야 한다.
/// </summary>
public sealed class User : AsyncExecutable
{
    private readonly IChatClient _client;
    private readonly HashSet<string> _joinedRoomIds = [];
    private long _lastActivityTickMs;

    public string UserId => _client.UserId;
    public string Username => _client.Username;

    public User(IChatClient client)
    {
        _client = client;
        _lastActivityTickMs = Environment.TickCount64;
    }

    // ── 외부 진입점 (큐에 푸시만) ──────────────────────────────────────

    public void DeliverMessage(ChatMessage message)
        => DoAsync(() => ProcessDeliverMessage(message));

    public void NoteRoomJoined(string roomId)
        => DoAsync(() => ProcessNoteRoomJoined(roomId));

    public void NoteRoomLeft(string roomId)
        => DoAsync(() => _joinedRoomIds.Remove(roomId));      // 1-statement: 인라인 유지

    public void TouchActivity()
        => DoAsync(() => _lastActivityTickMs = Environment.TickCount64);   // 1-statement

    public void CheckIdleAndDisconnect(ChatServer server, long thresholdMs)
        => DoAsync(() => ProcessCheckIdleAndDisconnect(server, thresholdMs));

    // ── 실제 본문 (private) ────────────────────────────────────────────

    private void ProcessDeliverMessage(ChatMessage message)
    {
        _lastActivityTickMs = Environment.TickCount64;
        // 네트워크 송신 시뮬레이션의 sleep 은 이 actor 의 Flush 안에서 도므로
        // 다른 actor(Room/Server)는 막지 않는다.
        _client.SendMessage(message);
    }

    private void ProcessNoteRoomJoined(string roomId)
    {
        _joinedRoomIds.Add(roomId);
        _lastActivityTickMs = Environment.TickCount64;
    }

    /// <summary>
    /// 유휴 시간이 임계치를 넘었으면 server 에 disconnect 를 요청한다.
    /// User 큐에서 판정하므로 _lastActivityTickMs 가 동시에 갱신될 일이 없다.
    /// 이 메서드 자체가 actor → actor 메시지 패싱의 표본.
    /// </summary>
    private void ProcessCheckIdleAndDisconnect(ChatServer server, long thresholdMs)
    {
        long idle = Environment.TickCount64 - _lastActivityTickMs;
        if (idle > thresholdMs)
        {
            Console.WriteLine($"[Idle] {Username} ({UserId}) 유휴 {idle}ms — 자동 종료 요청");
            server.HandleUserDisconnect(UserId);
        }
    }

    // ── 동기 read API ──────────────────────────────────────────────────

    /// <summary>
    /// 차단(blocking) 스냅샷 — ManualResetEventSlim 으로 신호받아 대기.
    /// 호출자는 다른 actor 의 Flush 안에서 부르면 안 된다 (데드락 위험).
    /// </summary>
    public UserSnapshot GetSnapshot()
    {
        using var ev = new ManualResetEventSlim(false);
        UserSnapshot? result = null;
        DoAsync(() =>
        {
            long idle = Environment.TickCount64 - _lastActivityTickMs;
            result = new UserSnapshot(UserId, Username, _joinedRoomIds.ToList(), idle);
            ev.Set();
        });
        ev.Wait();
        return result!;
    }
}
