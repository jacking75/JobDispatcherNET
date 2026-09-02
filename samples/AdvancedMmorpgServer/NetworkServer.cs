using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using JobDispatcherNET;

namespace AdvancedMmorpgServer;

/// <summary>
/// 동기 IO 기반 TCP 서버. async/await 미사용.
///
/// 스레드 구성:
///   - Accept 스레드 1개 — 동기 AcceptTcpClient() 로 연결 수락
///   - 세션마다 RecvIO / SendIO 전용 OS 스레드 1개씩
///   - 송신 큐는 BlockingCollection&lt;string&gt; (BoundedCapacity) — slow client drop
///
/// How the library is used here:
///   an IO thread never calls PacketHandler or the world directly. It only pushes lines into the
///   session's <see cref="Sequencer{T}"/>, which hands the drain to the job system's worker pool.
///   Actor code therefore always runs on a worker, never on a socket thread.
/// </summary>
public sealed class NetworkServer
{
    private readonly GameServer _server;
    private readonly int _port;
    private TcpListener? _listener;
    private Thread? _acceptThread;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<long, ClientSession> _sessionsByConn = [];
    private long _nextConnId;
    private int _stopped;

    public NetworkServer(GameServer server, int port)
    {
        _server = server;
        _port = port;
    }

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();

        _acceptThread = new Thread(AcceptLoop)
        {
            IsBackground = true,
            Name = "NetAccept",
        };
        _acceptThread.Start();

        JobLog.Info($"[네트워크] 포트 {_port} 리스닝 시작");
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;

        try { _cts.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }

        foreach (var s in _sessionsByConn.Values)
            s.Close();
        _sessionsByConn.Clear();

        if (_acceptThread is { IsAlive: true })
            _acceptThread.Join(TimeSpan.FromSeconds(2));

        try { _cts.Dispose(); } catch { }
    }

    private void AcceptLoop()
    {
        JobLog.Info($"[네트워크] AcceptLoop 시작 (스레드:{Environment.CurrentManagedThreadId})");
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                TcpClient tcp;
                try
                {
                    tcp = _listener!.AcceptTcpClient();
                }
                catch (SocketException) when (_cts.Token.IsCancellationRequested) { break; }
                catch (ObjectDisposedException) { break; }

                if (_cts.Token.IsCancellationRequested)
                {
                    try { tcp.Dispose(); } catch { }
                    break;
                }

                long connId = Interlocked.Increment(ref _nextConnId);
                var session = new ClientSession(connId, tcp, _server, OnSessionClosed);
                _sessionsByConn[connId] = session;
                session.Start();
            }
        }
        catch (Exception ex)
        {
            JobLog.Error("[네트워크] AcceptLoop 오류", ex);
        }
        JobLog.Info("[네트워크] AcceptLoop 종료");
    }

    /// <summary>
    /// Only removes the session from the map. RemovePlayer happens when the sequencer reaches the
    /// disconnect marker on a worker, after every packet that arrived before it — and Stop() now
    /// guarantees that marker is still drained, so a disconnect can no longer leave a ghost player
    /// behind.
    /// </summary>
    private void OnSessionClosed(ClientSession s)
    {
        _sessionsByConn.TryRemove(s.ConnectionId, out _);
    }
}

/// <summary>
/// 단일 클라이언트 세션. RecvIO / SendIO 가 별도의 OS 스레드에서 동기 IO 로 동작.
///
/// Packet handling:
///   RecvIO only calls <see cref="Sequencer{T}.Enqueue"/>. The sequencer schedules its drain onto
///   the job system (<see cref="JobSystem.Post(Action)"/>), a worker picks it up, and
///   PacketHandler.Handle runs there. One session is drained by one worker at a time, so a
///   client's packets keep their arrival order.
/// </summary>
public sealed class ClientSession
{
    public long ConnectionId { get; }
    public int PlayerId { get; private set; }

    private readonly TcpClient _tcp;
    private readonly GameServer _server;
    private readonly Action<ClientSession> _onClosed;
    private readonly NetworkStream _stream;

    private const int OutgoingCapacity = 1000;
    private const int SlowClientDropLimit = 200;

    private readonly BlockingCollection<string> _outgoing =
        new(new ConcurrentQueue<string>(), OutgoingCapacity);

    private readonly Sequencer<string> _packetSequencer;

    private Thread? _recvThread;
    private Thread? _sendThread;

    private int _closed;
    private int _closeNotified;
    private int _droppedCount;
    private long _sentCount;

    // disconnect sentinel — 일반 패킷에 등장할 수 없는 문자열.
    private const string DisconnectMarker = "\0__DISCONNECT__\0";

    public ClientSession(long connId, TcpClient tcp, GameServer server, Action<ClientSession> onClosed)
    {
        ConnectionId = connId;
        _tcp = tcp;
        _server = server;
        _onClosed = onClosed;
        _stream = tcp.GetStream();

        // The system-aware constructor schedules drains straight onto the worker pool — the
        // hand-rolled inbound command queue this sample used to need is gone.
        _packetSequencer = new Sequencer<string>(
            server.System,
            handler: HandleOnePacket,
            onError: ex => JobLog.Error($"[session #{connId}] packet handling failed", ex));
    }

    public void OnLoggedIn(int playerId)
    {
        PlayerId = playerId;
    }

    public void Start()
    {
        _recvThread = new Thread(RecvLoop)
        {
            IsBackground = true,
            Name = $"NetRecv-{ConnectionId}",
        };
        _sendThread = new Thread(SendLoop)
        {
            IsBackground = true,
            Name = $"NetSend-{ConnectionId}",
        };
        _recvThread.Start();
        _sendThread.Start();
    }

    // ── 수신 ─────────────────────────────────────────────────────────

    /// <summary>
    /// IO 스레드 본문 — 패킷을 파싱해 Sequencer 에 push 만 한다.
    /// 실제 처리(PacketHandler.Handle) 는 워커 스레드에서 일어남.
    /// </summary>
    private void RecvLoop()
    {
        var buffer = new byte[4096];
        var sb = new StringBuilder();
        try
        {
            while (true)
            {
                int n;
                try { n = _stream.Read(buffer, 0, buffer.Length); }
                catch (IOException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { break; }

                if (n == 0) break;

                sb.Append(Encoding.UTF8.GetString(buffer, 0, n));

                while (true)
                {
                    var s = sb.ToString();
                    int idx = s.IndexOf('\n');
                    if (idx < 0) break;

                    var line = s[..idx].Trim('\r', ' ', '\t');
                    sb.Remove(0, idx + 1);
                    if (line.Length > 0)
                        _packetSequencer.Enqueue(line);
                }
            }
        }
        catch (Exception ex)
        {
            JobLog.Error($"[세션 #{ConnectionId}] Recv 오류", ex);
        }
        finally
        {
            HandleDisconnect();
        }
    }

    /// <summary>
    /// 워커 스레드에서 호출되는 단일-드레이너 핸들러.
    /// disconnect sentinel 도 동일 큐를 통해 들어와 EnterZone/Move 등이 모두 처리된 뒤 실행됨.
    /// </summary>
    private void HandleOnePacket(string line)
    {
        if (ReferenceEquals(line, DisconnectMarker))
        {
            if (PlayerId != 0)
                _server.World.RemovePlayer(PlayerId);
            return;
        }
        PacketHandler.Handle(_server, this, line);
    }

    // ── 송신 ─────────────────────────────────────────────────────────

    private void SendLoop()
    {
        try
        {
            foreach (var msg in _outgoing.GetConsumingEnumerable())
            {
                if (!_tcp.Connected) break;
                var bytes = Encoding.UTF8.GetBytes(msg + "\n");
                try { _stream.Write(bytes, 0, bytes.Length); }
                catch (IOException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { break; }
            }
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            JobLog.Error($"[세션 #{ConnectionId}] Send 오류", ex);
        }
        finally
        {
            HandleDisconnect();
        }
    }

    public void SendPacket(string msg)
    {
        if (Volatile.Read(ref _closed) != 0) return;
        if (_outgoing.IsAddingCompleted) return;

        try
        {
            if (_outgoing.TryAdd(msg))
            {
                Interlocked.Increment(ref _sentCount);
                return;
            }
        }
        catch (InvalidOperationException)
        {
            return;
        }

        int dropped = Interlocked.Increment(ref _droppedCount);
        if (dropped == SlowClientDropLimit)
        {
            JobLog.Warn($"[세션 #{ConnectionId}] 송신 큐 포화 — {dropped}개 드롭, 연결 종료");
            Close();
        }
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;

        // Refuse new packets; whatever was already accepted (including the disconnect marker)
        // is still drained in order.
        _packetSequencer.Stop();

        // 송신 큐 종료 → SendLoop 자연 종료
        try { _outgoing.CompleteAdding(); } catch { }

        // 소켓 닫기 → RecvLoop Read 가 깨어남
        try { _stream.Dispose(); } catch { }
        try { _tcp.Close(); } catch { }

        var current = Thread.CurrentThread;
        if (_recvThread is { IsAlive: true } && _recvThread != current)
            _recvThread.Join(TimeSpan.FromSeconds(2));
        if (_sendThread is { IsAlive: true } && _sendThread != current)
            _sendThread.Join(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// _onClosed 는 1회만. disconnect sentinel 을 sequencer 에 넣어
    /// 잔여 패킷 처리 후에 RemovePlayer 가 워커에서 실행되게 한다.
    /// </summary>
    private void HandleDisconnect()
    {
        if (Interlocked.Exchange(ref _closeNotified, 1) != 0)
        {
            Close();
            return;
        }

        // The disconnect goes through the same queue so it runs after every pending packet.
        if (!_packetSequencer.Enqueue(DisconnectMarker) && PlayerId != 0)
        {
            // The sequencer was already stopped (server shutdown): remove the player directly.
            _server.World.RemovePlayer(PlayerId);
        }

        Close();
        JobLog.Info($"[Session #{ConnectionId}] closed, sent={Interlocked.Read(ref _sentCount)}, dropped={Volatile.Read(ref _droppedCount)}");
        _onClosed(this);
    }
}
