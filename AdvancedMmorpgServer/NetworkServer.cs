using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace AdvancedMmorpgServer;

/// <summary>
/// 비동기 TCP 서버. 본 샘플의 핵심이 아니므로 최소 구현.
/// 수신은 ReadLine 루프, 송신은 세션별 Channel + 별도 SendLoop로 분리되어
/// 게임 로직(워커 스레드)이 네트워크 IO에 블로킹되지 않는다.
/// </summary>
public sealed class NetworkServer
{
    private readonly GameServer _server;
    private readonly int _port;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private readonly ConcurrentDictionary<long, ClientSession> _sessionsByConn = [];
    private long _nextConnId;

    public NetworkServer(GameServer server, int port)
    {
        _server = server;
        _port = port;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        Console.WriteLine($"[네트워크] 포트 {_port} 리스닝 시작");
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        foreach (var s in _sessionsByConn.Values) s.Close();
        _sessionsByConn.Clear();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var tcp = await _listener!.AcceptTcpClientAsync(ct);
                long connId = Interlocked.Increment(ref _nextConnId);
                var session = new ClientSession(connId, tcp, _server, OnSessionClosed);
                _sessionsByConn[connId] = session;
                _ = session.RunAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Console.Error.WriteLine($"[네트워크] Accept 오류: {ex.Message}"); }
    }

    private void OnSessionClosed(ClientSession s)
    {
        _sessionsByConn.TryRemove(s.ConnectionId, out _);
        if (s.PlayerId != 0)
            _server.World.RemovePlayer(s.PlayerId);
    }
}

/// <summary>
/// 단일 클라이언트 세션. Read/Write는 분리된 비동기 루프에서 동작한다.
/// 송신 채널이 분리되어 있으므로 World/Actor는 SendPacket()을 즉시 반환할 수 있다.
/// </summary>
public sealed class ClientSession
{
    public long ConnectionId { get; }
    public int PlayerId { get; private set; }

    private readonly TcpClient _tcp;
    private readonly GameServer _server;
    private readonly Action<ClientSession> _onClosed;
    private readonly NetworkStream _stream;
    private readonly Channel<string> _outgoing;
    private CancellationTokenSource? _selfCts;
    private int _closed;
    private int _droppedCount;

    // 송신 큐 용량 — 16봇×~50패킷/s 기준 ~1.25초 버퍼. 이 범위를 넘기면 슬로우 클라이언트로 간주.
    private const int OutgoingCapacity = 1000;
    // 누적 드롭이 이 임계값에 도달하면 세션을 강제 종료 — 누수 방지.
    private const int SlowClientDropLimit = 200;

    public ClientSession(long connId, TcpClient tcp, GameServer server, Action<ClientSession> onClosed)
    {
        ConnectionId = connId;
        _tcp = tcp;
        _server = server;
        _onClosed = onClosed;
        _stream = tcp.GetStream();
        _outgoing = Channel.CreateBounded<string>(new BoundedChannelOptions(OutgoingCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite,
        });
    }

    public void OnLoggedIn(int playerId)
    {
        PlayerId = playerId;
    }

    public Task RunAsync(CancellationToken parentCt)
    {
        _selfCts = CancellationTokenSource.CreateLinkedTokenSource(parentCt);
        var ct = _selfCts.Token;
        var send = SendLoopAsync(ct);
        var recv = ReceiveLoopAsync(ct);
        return Task.WhenAll(send, recv).ContinueWith(_ => HandleDisconnect());
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];
        var sb = new StringBuilder();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await _stream.ReadAsync(buffer, ct);
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
                        PacketHandler.Handle(_server, this, line);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { /* 정상 종료 */ }
        catch (Exception ex) { Console.Error.WriteLine($"[세션 #{ConnectionId}] Recv 오류: {ex.Message}"); }
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var msg in _outgoing.Reader.ReadAllAsync(ct))
            {
                if (!_tcp.Connected) break;
                var bytes = Encoding.UTF8.GetBytes(msg + "\n");
                await _stream.WriteAsync(bytes, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (Exception ex) { Console.Error.WriteLine($"[세션 #{ConnectionId}] Send 오류: {ex.Message}"); }
    }

    public void SendPacket(string msg)
    {
        if (Volatile.Read(ref _closed) != 0) return;
        if (_outgoing.Writer.TryWrite(msg)) return;

        int dropped = Interlocked.Increment(ref _droppedCount);
        if (dropped == SlowClientDropLimit)
        {
            Console.Error.WriteLine($"[세션 #{ConnectionId}] 송신 큐 포화 — {dropped}개 드롭, 연결 종료");
            Close();
        }
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        try { _outgoing.Writer.TryComplete(); } catch { }
        try { _selfCts?.Cancel(); } catch { }
        try { _tcp.Close(); } catch { }
    }

    private void HandleDisconnect()
    {
        Close();
        _onClosed(this);
    }
}
