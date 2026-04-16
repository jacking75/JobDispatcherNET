using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ExampleMmorpgServer;

/// <summary>
/// 간략한 비동기 TCP 네트워크 서버.
/// 네트워크 자체는 이 예제의 핵심이 아니므로 최소한으로 구현한다.
/// 패킷을 수신하면 PacketHandler를 통해 GameServer(AsyncExecutable)에 전달한다.
/// </summary>
public class NetworkServer
{
    private readonly GameServer _gameServer;
    private readonly int _port;
    private TcpListener? _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, ClientSession> _sessions = [];

    public NetworkServer(GameServer gameServer, int port = 9000)
    {
        _gameServer = gameServer;
        _port = port;
    }

    public async Task StartAsync()
    {
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        Console.WriteLine($"네트워크 서버 시작 (포트:{_port})\n");

        // 클라이언트 접속 수락 루프
        _ = Task.Run(async () =>
        {
            try
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    var tcpClient = await _listener.AcceptTcpClientAsync(_cts.Token);
                    var session = new ClientSession(tcpClient, _gameServer, OnSessionDisconnected);
                    _sessions[session.Player.PlayerId] = session;
                    _ = session.StartAsync(_cts.Token);
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    private void OnSessionDisconnected(string playerId)
    {
        _sessions.TryRemove(playerId, out _);
    }

    public async Task StopAsync()
    {
        await _cts.CancelAsync();
        _listener?.Stop();

        foreach (var session in _sessions.Values)
            session.Dispose();

        _sessions.Clear();
        Console.WriteLine("네트워크 서버 종료");
    }
}

/// <summary>
/// 클라이언트 세션. TCP 연결 하나에 대응하며 Player를 소유한다.
/// 수신된 패킷은 즉시 PacketHandler로 전달되어 GameServer의 DoAsync를 통해 처리된다.
/// → 네트워크 IO 스레드에서 로직 스레드로 자연스럽게 전환됨.
/// </summary>
public class ClientSession : IDisposable
{
    private static int _sessionCounter;
    private readonly TcpClient _tcpClient;
    private readonly GameServer _gameServer;
    private readonly Action<string> _onDisconnected;
    private readonly NetworkStream _stream;

    public Player Player { get; }

    public ClientSession(TcpClient tcpClient, GameServer gameServer, Action<string> onDisconnected)
    {
        _tcpClient = tcpClient;
        _gameServer = gameServer;
        _onDisconnected = onDisconnected;
        _stream = tcpClient.GetStream();

        int id = Interlocked.Increment(ref _sessionCounter);
        Player = new Player($"player_{id}", $"모험가{id}");

        // 네트워크 전송 콜백 설정
        Player.SendPacket = SendPacket;

        Console.WriteLine($"[네트워크] 클라이언트 접속: {Player.Name} ({_tcpClient.Client.RemoteEndPoint})");
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[1024];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int bytesRead = await _stream.ReadAsync(buffer, cancellationToken);
                if (bytesRead == 0)
                    break;

                string raw = Encoding.UTF8.GetString(buffer, 0, bytesRead).Trim();

                // 여러 패킷이 한번에 올 수 있으므로 줄 단위로 분리
                foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    // 네트워크 IO 스레드에서 호출 → PacketHandler가 GameServer.DoAsync()로 전달
                    // → 워커 스레드에서 로직 실행 (lock 없음!)
                    PacketHandler.Handle(_gameServer, Player, line.Trim());
                }
            }
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[네트워크] {Player.Name} 연결 오류: {ex.Message}");
        }
        finally
        {
            _gameServer.HandlePlayerDisconnect(Player.PlayerId);
            _onDisconnected(Player.PlayerId);
            Console.WriteLine($"[네트워크] {Player.Name} 연결 종료");
        }
    }

    public void SendPacket(string message)
    {
        try
        {
            if (_tcpClient.Connected)
            {
                var data = Encoding.UTF8.GetBytes(message + "\n");
                _stream.WriteAsync(data);
            }
        }
        catch { /* 연결 끊김 무시 */ }
    }

    public void Dispose()
    {
        _tcpClient.Dispose();
    }
}
