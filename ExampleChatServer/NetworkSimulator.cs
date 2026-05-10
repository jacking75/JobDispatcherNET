namespace ExampleChatServer;

/// <summary>
/// 가짜 네트워크 IO 시뮬레이터.
///
/// N개의 백그라운드 스레드가 각자 하나의 TCP 연결을 흉내 낸다.
/// 각 스레드는 자기 클라이언트의 패킷을 파싱했다고 가정하고
/// <see cref="ChatWorker.InboundCommands"/> 에 명령을 동시에 푸시한다.
///
/// 실제 채팅 서버와의 매핑:
///   N개의 TCP 연결 ── 각 연결당 IO 스레드 ── 패킷 파싱 → server.HandleX 호출
///   ↑ 이 부분이 NetworkSimulator 가 시뮬레이트하는 영역
///
/// 학습 포인트:
///   1) <c>ConcurrentQueue&lt;Action&gt;</c> 는 다중 producer 안전 — N개 스레드가
///      동시에 Enqueue 해도 lock 이 필요 없다.
///   2) 같은 actor(ChatServer)에 여러 IO 스레드가 동시에 명령을 푸시해도
///      actor 의 큐가 한 번에 하나만 통과시키므로 그 안의 _users / _rooms 는 안전.
///   3) IO 와 actor 처리가 완전히 분리됨 (decoupled):
///      IO 스레드는 push 만 하고 즉시 다음 패킷으로,
///      실제 처리는 worker pool 이 자기 페이스로 진행.
/// </summary>
public sealed class NetworkSimulator : IDisposable
{
    private static readonly string[] s_rooms = ["general", "game", "dev"];

    private static readonly string[] s_phrases =
    [
        "안녕!", "뭐해?", "오늘 날씨 좋다", "ㅋㅋㅋ", "그래?",
        "잠깐만", "ㅎㅎ", "오케이", "응응", "잘 가",
        "GG", "한판 더", "밥 먹었어?", "졸리다", "출근했어",
    ];

    private readonly Thread[] _ioThreads;
    private readonly CancellationTokenSource _cts = new();
    private readonly ChatServer _server;
    private readonly int _connectionCount;
    private long _totalSent;
    private int _disposed;

    public long TotalSent => Interlocked.Read(ref _totalSent);

    public NetworkSimulator(ChatServer server, int connectionCount)
    {
        _server = server;
        _connectionCount = connectionCount;
        _ioThreads = new Thread[connectionCount];

        for (int i = 0; i < connectionCount; i++)
        {
            int connId = i;
            _ioThreads[i] = new Thread(() => IoLoop(connId))
            {
                IsBackground = true,
                Name = $"NetIO-{i}",
            };
        }
    }

    public void Start()
    {
        Console.WriteLine($"[NetSim] {_connectionCount}개 IO 스레드 기동");
        foreach (var t in _ioThreads)
            t.Start();
    }

    private void IoLoop(int connId)
    {
        var rng = new Random(connId * 9173 + 1);
        var userId = $"net{connId}";
        var userName = $"클라{connId}";
        var room = s_rooms[rng.Next(s_rooms.Length)];

        Console.WriteLine($"[NetIO-{connId}] 시작 (스레드 ID: {Environment.CurrentManagedThreadId})");

        // 1) 접속
        var client = new ChatNetworkClient(userId, userName);
        Push(() => _server.HandleUserConnect(client));
        Sleep(rng, 50, 200);

        // 2) 방 입장
        Push(() => _server.HandleRoomJoin(userId, room));
        Sleep(rng, 100, 300);

        // 3) 주기적 채팅 (취소될 때까지)
        int sentCount = 0;
        while (!_cts.IsCancellationRequested)
        {
            Sleep(rng, 80, 400);
            if (_cts.IsCancellationRequested) break;

            // 가끔 1:1 채팅 (10% 확률)
            if (rng.Next(100) < 10)
            {
                int partnerId = rng.Next(_connectionCount);
                if (partnerId != connId)
                {
                    int n = sentCount;
                    Push(() => _server.HandlePrivateChat(userId, $"net{partnerId}", $"안녕 #{n}"));
                    sentCount++;
                    continue;
                }
            }

            // 방 채팅
            var phrase = s_phrases[rng.Next(s_phrases.Length)];
            int idx = sentCount;
            Push(() => _server.HandleRoomChat(userId, room, $"{phrase} ({idx})"));
            sentCount++;
        }

        // 4) 종료
        Push(() => _server.HandleUserDisconnect(userId));
        Console.WriteLine($"[NetIO-{connId}] 종료 — {sentCount}개 메시지 발사");
    }

    private void Push(Action cmd)
    {
        ChatWorker.InboundCommands.Enqueue(cmd);
        Interlocked.Increment(ref _totalSent);
    }

    private void Sleep(Random rng, int minMs, int maxMs)
    {
        try { Thread.Sleep(rng.Next(minMs, maxMs)); }
        catch (ThreadInterruptedException) { }
    }

    public void Dispose()
    {
        // idempotent — Program.cs 에서 명시적으로 한 번 호출 후 using 이 또 호출해도 안전
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _cts.Cancel();
        foreach (var t in _ioThreads)
        {
            if (t.IsAlive)
                t.Join(TimeSpan.FromSeconds(2));
        }
        _cts.Dispose();
        Console.WriteLine($"[NetSim] 정지 — 총 {TotalSent}개 명령 push");
    }
}
