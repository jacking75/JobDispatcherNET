using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ExampleMmorpgServer;

/// <summary>
/// 동기 IO 기반 TCP 네트워크 서버.
///
/// 설계 원칙:
///   - async/await 미사용. 실제 게임 서버는 IO 대기를 별도 스레드로 처리하므로
///     async 상태머신 비용을 지불할 필요가 없다.
///   - IO 스레드와 패킷 처리 스레드 완전 분리:
///       Accept 스레드 → 클라이언트별 RecvIO + SendIO 스레드
///                      → 세션별 패킷 큐에 적재 + drain 명령을 InboundCommands 에 1회 등록
///       워커 스레드(JobDispatcher) → InboundCommands dequeue → 세션 drain → GameServer.HandleX
///     IO 스레드는 절대로 GameZone/PlayerActor 의 Flush 를 실행하지 않는다.
///   - 송신도 IO 대기가 발생할 수 있으므로 워커가 아닌 전용 SendIO 스레드에서 처리한다.
///   - 같은 클라이언트의 패킷 순서 보장: 세션별 ConcurrentQueue + CAS 기반 drain 락.
///     한 시점에 한 워커만 같은 세션을 drain 하므로 _zone.DoAsync 의 multi-producer race 가 없다.
/// </summary>
public class NetworkServer
{
    private readonly GameServer _gameServer;
    private readonly int _port;
    private TcpListener? _listener;
    private Thread? _acceptThread;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, ClientSession> _sessions = [];
    private int _disposed;

    public NetworkServer(GameServer gameServer, int port = 9000)
    {
        _gameServer = gameServer;
        _port = port;
    }

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        Console.WriteLine($"네트워크 서버 시작 (포트:{_port})\n");

        _acceptThread = new Thread(AcceptLoop)
        {
            IsBackground = true,
            Name = "NetAccept",
        };
        _acceptThread.Start();
    }

    /// <summary>
    /// 동기 Accept 루프 — 전용 OS 스레드에서 실행.
    /// AcceptTcpClient() 가 블로킹되지만 이 스레드는 accept 만 하므로 다른 작업을 막지 않는다.
    /// </summary>
    private void AcceptLoop()
    {
        Console.WriteLine($"[NetAccept] 시작 (스레드:{Environment.CurrentManagedThreadId})");
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                TcpClient tcpClient;
                try
                {
                    tcpClient = _listener!.AcceptTcpClient();
                }
                catch (SocketException) when (_cts.Token.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                // 종료 중이라면 새 세션 만들지 말고 클라이언트만 닫는다 (리소스 누수 방지)
                if (_cts.Token.IsCancellationRequested)
                {
                    try { tcpClient.Dispose(); } catch { }
                    break;
                }

                var session = new ClientSession(tcpClient, _gameServer, OnSessionDisconnected);
                _sessions[session.Player.PlayerId] = session;
                session.Start();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[NetAccept] 오류: {ex.Message}");
        }
        Console.WriteLine("[NetAccept] 종료");
    }

    /// <summary>
    /// IO 스레드 finally 에서 호출됨. dict 에서 세션 reference 만 제거 (Dispose 는 세션이 자체 처리).
    /// </summary>
    private void OnSessionDisconnected(string playerId)
    {
        _sessions.TryRemove(playerId, out _);
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _cts.Cancel();
        _listener?.Stop();

        // dict snapshot — IO 스레드의 _onDisconnected 가 동시에 dict 를 수정해도 안전하다
        foreach (var session in _sessions.Values)
            session.Dispose();

        _sessions.Clear();

        if (_acceptThread is { IsAlive: true })
            _acceptThread.Join(TimeSpan.FromSeconds(2));

        _cts.Dispose();
        Console.WriteLine("네트워크 서버 종료");
    }
}

/// <summary>
/// 클라이언트 세션 — TCP 연결 하나에 대응하며 전용 RecvIO/SendIO 스레드를 가진다.
///
/// 스레드 구성:
///   - RecvIO 스레드: 동기 Read → 패킷 파싱 → 세션 자체의 <see cref="_packetQueue"/> 에 푸시,
///     그리고 <see cref="GameWorker.InboundCommands"/> 에 drain 명령을 (1회) 푸시한다.
///   - SendIO 스레드: <see cref="_sendQueue"/> 에서 dequeue → 동기 Write.
///   - 워커 스레드: drain 명령을 dequeue 하여 세션 패킷들을 순서대로 처리.
///     같은 세션은 동시에 한 워커만 drain (CAS 락) → 클라이언트별 패킷 순서 보장.
///
/// 왜 세션별 drain 이 필요한가?
///   1) 만약 RecvIO 가 패킷마다 InboundCommands 에 따로 푸시하면, 4개 워커가 동시에
///      각 패킷을 dequeue 하여 _zone.DoAsync 를 동시에 호출한다.
///   2) AsyncExecutable.DoTask 의 Increment+TryWrite 는 원자적이지 않으므로
///      follower 의 TryWrite 가 leader 의 TryWrite 보다 먼저 채널에 들어갈 수 있다.
///   3) 결과적으로 같은 클라이언트의 EnterZone → Move 순서가 _zone 큐에서
///      Move → EnterZone 으로 뒤집혀, Move 가 actor 미등록 상태에서 무시될 수 있다.
///   세션별 drain 으로 한 클라이언트 패킷은 단일 워커가 순차 처리하므로 이 race 가 사라진다.
///
/// IO 스레드는 절대 GameServer.HandleX 를 직접 호출하지 않는다.
/// 직접 호출하면 actor 큐가 비어있을 때 IO 스레드에서 Flush 가 실행되어
/// 패킷 처리가 IO 스레드에서 일어나는 문제가 생긴다.
/// </summary>
public class ClientSession : IDisposable
{
    private const int MaxPendingBytes = 64 * 1024; // 패킷 누적 한도 (방어적 상한)

    private static int _sessionCounter;
    private readonly TcpClient _tcpClient;
    private readonly GameServer _gameServer;
    private readonly Action<string> _onDisconnected;
    private readonly NetworkStream _stream;
    private readonly BlockingCollection<byte[]> _sendQueue = new(new ConcurrentQueue<byte[]>());

    // 세션별 입력 큐 — 단일 RecvIO 스레드가 push, 단일 워커(시점마다 다름)가 drain
    private readonly ConcurrentQueue<string> _packetQueue = new();
    // CAS 기반 drain 락 — 0=idle, 1=drain 예정/진행. 한 시점에 한 워커만 세션을 drain.
    private int _drainScheduled;

    private Thread? _recvThread;
    private Thread? _sendThread;

    private int _socketClosed;
    private int _disposed;
    private int _disconnectNotified;

    public Player Player { get; }

    public ClientSession(TcpClient tcpClient, GameServer gameServer, Action<string> onDisconnected)
    {
        _tcpClient = tcpClient;
        _gameServer = gameServer;
        _onDisconnected = onDisconnected;
        _stream = tcpClient.GetStream();

        int id = Interlocked.Increment(ref _sessionCounter);
        Player = new Player($"player_{id}", $"모험가{id}");

        // SendPacket 콜백은 SendIO 스레드용 큐에 푸시만 한다 — 워커 스레드를 블록하지 않음
        Player.SendPacket = EnqueueSend;

        Console.WriteLine($"[네트워크] 클라이언트 접속: {Player.Name} ({_tcpClient.Client.RemoteEndPoint})");
    }

    public void Start()
    {
        _recvThread = new Thread(RecvLoop)
        {
            IsBackground = true,
            Name = $"NetRecv-{Player.PlayerId}",
        };
        _sendThread = new Thread(SendLoop)
        {
            IsBackground = true,
            Name = $"NetSend-{Player.PlayerId}",
        };
        _recvThread.Start();
        _sendThread.Start();
    }

    // ── 수신 ─────────────────────────────────────────────────────────

    /// <summary>
    /// 전용 RecvIO 스레드 본문. 동기 Read 로 블로킹 후 패킷 파싱 → 워커 큐로 푸시.
    /// </summary>
    private void RecvLoop()
    {
        Console.WriteLine($"[네트워크] {Player.Name} RecvIO 시작 (스레드:{Environment.CurrentManagedThreadId})");

        var buffer = new byte[1024];
        var pendingText = new StringBuilder();
        try
        {
            while (true)
            {
                int bytesRead;
                try
                {
                    bytesRead = _stream.Read(buffer, 0, buffer.Length);
                }
                catch (IOException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { break; }

                if (bytesRead == 0)
                    break;

                // 누적 한도 초과 시 강제 종료 (악성/비정상 클라이언트 방어)
                if (pendingText.Length + bytesRead > MaxPendingBytes)
                {
                    Console.WriteLine($"[네트워크] {Player.Name} 패킷 누적 한도 초과 — 연결 종료");
                    break;
                }

                pendingText.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));

                // 줄 단위로 분리하여 완성된 패킷만 처리, 미완성된 마지막 토막은 다음 read 까지 보관
                bool pushedAny = false;
                while (true)
                {
                    string text = pendingText.ToString();
                    int newlineIdx = text.IndexOf('\n');
                    if (newlineIdx < 0)
                        break;

                    string line = text[..newlineIdx].Trim();
                    pendingText.Remove(0, newlineIdx + 1);

                    if (line.Length == 0)
                        continue;

                    // 세션별 큐에 적재 (이 스레드만 push 하므로 순서 보장).
                    _packetQueue.Enqueue(line);
                    pushedAny = true;
                }

                if (pushedAny)
                {
                    // drain 락을 CAS 로 확보한 워커만 InboundCommands 에 drain 명령을 1회 등록.
                    // 이 패턴이 "한 세션은 한 워커가 순차 처리" 를 보장한다.
                    TryScheduleDrain();
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[네트워크] {Player.Name} RecvIO 오류: {ex.Message}");
        }
        finally
        {
            // 자체 정리 — TcpClient 닫고 send 큐 닫고 send 스레드 join.
            // 자기 스레드는 join 하지 않도록 Dispose 가 처리.
            Dispose();
            Console.WriteLine($"[네트워크] {Player.Name} 연결 종료");
        }
    }

    /// <summary>
    /// 패킷 큐 drain 권한을 CAS 로 한 스레드에만 부여하고, 그 스레드가 워커 큐에 drain 명령을 1회 등록한다.
    /// 이미 drain 이 예약/진행 중이면 아무 일도 하지 않는다 (큐의 새 패킷은 진행 중인 drain 에 흡수된다).
    /// </summary>
    private void TryScheduleDrain()
    {
        if (Interlocked.CompareExchange(ref _drainScheduled, 1, 0) != 0)
            return;

        GameWorker.InboundCommands.Enqueue(DrainPackets);
    }

    /// <summary>
    /// 워커 스레드에서 실행. 세션의 패킷을 모두 순서대로 PacketHandler 에 넘긴다.
    /// drain 종료 후 큐에 남은 패킷이 있으면 (RecvIO 가 새로 push 했다면) 다시 스케줄.
    /// </summary>
    private void DrainPackets()
    {
        try
        {
            while (_packetQueue.TryDequeue(out var line))
            {
                if (ReferenceEquals(line, DisconnectMarker))
                {
                    _gameServer.HandlePlayerDisconnect(Player.PlayerId);
                }
                else
                {
                    PacketHandler.Handle(_gameServer, Player, line);
                }
            }
        }
        finally
        {
            // 락 해제. Volatile.Write 로 다른 스레드(RecvIO)가 즉시 _drainScheduled=0 을 보게 한다.
            Volatile.Write(ref _drainScheduled, 0);

            // 락 해제와 RecvIO 의 큐 push 사이 race 처리 — 큐에 남은 게 있으면 다시 스케줄.
            if (!_packetQueue.IsEmpty)
                TryScheduleDrain();
        }
    }

    // ── 송신 ─────────────────────────────────────────────────────────

    /// <summary>
    /// 워커 스레드에서 호출되는 진입점 — 큐에 푸시만 하고 즉시 반환.
    /// 실제 Write 블로킹은 SendIO 스레드에서 일어난다.
    ///
    /// 세션 disconnect 와의 race 가 흔하다 (워커가 PlayerActor 큐에 남아있던 패킷을
    /// 늦게 처리하면서 SendPacket 호출). 모든 예외를 흡수해 actor 크래시를 막는다.
    /// </summary>
    private void EnqueueSend(string message)
    {
        try
        {
            _sendQueue.Add(Encoding.UTF8.GetBytes(message + "\n"));
        }
        catch (InvalidOperationException)
        {
            // CompleteAdding 호출됨 또는 큐 disposed (ObjectDisposedException 도 InvalidOperationException 의 하위형)
        }
    }

    /// <summary>
    /// 전용 SendIO 스레드 본문. 큐에서 byte[] 를 꺼내 동기 Write 로 송출.
    /// CompleteAdding 이 호출되면 GetConsumingEnumerable 이 자연스럽게 종료된다.
    /// </summary>
    private void SendLoop()
    {
        Console.WriteLine($"[네트워크] {Player.Name} SendIO 시작 (스레드:{Environment.CurrentManagedThreadId})");
        try
        {
            foreach (var data in _sendQueue.GetConsumingEnumerable())
            {
                try
                {
                    _stream.Write(data, 0, data.Length);
                }
                catch (IOException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { break; }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[네트워크] {Player.Name} SendIO 오류: {ex.Message}");
        }

        // SendIO 가 종료되면 RecvIO 도 종료시켜야 일관된 상태 — 소켓을 닫는다
        CloseSocket();
    }

    // ── 정리 ─────────────────────────────────────────────────────────

    /// <summary>
    /// 소켓/스트림 닫기 (idempotent). Read/Write 가 ObjectDisposedException 또는 IOException 으로 깨어나게 한다.
    /// </summary>
    private void CloseSocket()
    {
        if (Interlocked.Exchange(ref _socketClosed, 1) != 0) return;

        try { _stream.Dispose(); } catch { }
        try { _tcpClient.Dispose(); } catch { }
    }

    // disconnect sentinel — _packetQueue 에 넣으면 DrainPackets 가 패킷 대신 disconnect 처리.
    // 일반 패킷에는 절대 등장할 수 없는 문자열.
    private const string DisconnectMarker = "\0__DISCONNECT__\0";

    /// <summary>
    /// 게임 서버에 1회만 disconnect 통지. 외부 Dispose 와 RecvLoop finally 양쪽에서 호출돼도 안전.
    ///
    /// 중요: disconnect 도 세션 패킷 큐의 마지막에 sentinel 로 넣는다.
    /// 일반 패킷과 같은 단일-드레이너 경로를 타기 때문에 EnterZone/Move 등이 모두 처리된 뒤에야
    /// HandlePlayerDisconnect 가 실행된다.
    /// </summary>
    private void NotifyDisconnectOnce()
    {
        if (Interlocked.Exchange(ref _disconnectNotified, 1) != 0) return;

        _onDisconnected(Player.PlayerId);

        _packetQueue.Enqueue(DisconnectMarker);
        TryScheduleDrain();
    }

    /// <summary>
    /// idempotent. RecvLoop finally(자기 스레드)에서도, NetworkServer.Stop(외부 스레드)에서도 호출 가능.
    /// 자기 스레드 join 은 건너뛰어 데드락을 방지한다.
    ///
    /// 주의: <see cref="_sendQueue"/> 는 의도적으로 Dispose 하지 않는다.
    /// 워커가 PlayerActor 큐에 남아있던 패킷을 늦게 처리하면서 EnqueueSend 를 호출할 수 있는데,
    /// 그 시점에 큐가 disposed 라면 ObjectDisposedException 이 actor Flush 안에서 터진다.
    /// CompleteAdding 만 해도 SendLoop 는 정상 종료되며, 큐 자체는 GC 에 맡긴다.
    /// </summary>
    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);

        // 1) 게임 서버에 disconnect 통지 — 워커 큐로 푸시 (1회 보장)
        NotifyDisconnectOnce();

        // 2) 송신 큐 종료 → SendLoop 가 자연스럽게 종료
        if (!_sendQueue.IsAddingCompleted)
        {
            try { _sendQueue.CompleteAdding(); } catch { }
        }

        // 3) 소켓 닫기 → RecvLoop 의 Read 가 깨어남
        CloseSocket();

        // 4) 스레드 join (자기 스레드는 건너뜀)
        var current = Thread.CurrentThread;
        if (_recvThread is { IsAlive: true } && _recvThread != current)
            _recvThread.Join(TimeSpan.FromSeconds(2));
        if (_sendThread is { IsAlive: true } && _sendThread != current)
            _sendThread.Join(TimeSpan.FromSeconds(2));
    }
}
