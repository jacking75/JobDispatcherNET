using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace AdvancedMmorpgClient;

/// <summary>
/// 단순 비동기 TCP 클라이언트. 봇 하나당 하나의 인스턴스가 사용된다.
/// 수신은 ReadLine 루프로 WorldState 갱신.
/// 송신은 Channel 큐 + SendLoop로 분리되어 AI 스레드가 블로킹되지 않음.
/// </summary>
public sealed class NetworkClient
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly TcpClient _tcp = new();
    private NetworkStream? _stream;
    private readonly Channel<string> _outgoing;
    private CancellationTokenSource? _cts;
    private readonly WorldState _world;
    private TaskCompletionSource<int>? _welcomeTcs;

    public int MyPlayerId { get; private set; }
    public bool Connected => _tcp.Connected && Volatile.Read(ref _disposed) == 0;
    private int _disposed;

    public NetworkClient(WorldState world)
    {
        _world = world;
        _outgoing = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true, SingleWriter = false
        });
    }

    public async Task<int> ConnectAndLoginAsync(string host, int port, string botName, CancellationToken ct)
    {
        await _tcp.ConnectAsync(host, port, ct);
        _stream = _tcp.GetStream();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _welcomeTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = ReceiveLoopAsync(_cts.Token);
        _ = SendLoopAsync(_cts.Token);

        Send($"LOGIN|{botName}");
        var pid = await _welcomeTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        MyPlayerId = pid;
        _world.RegisterMyBot(pid);
        return pid;
    }

    public void Send(string packet)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _outgoing.Writer.TryWrite(packet);
    }

    public void SendMove(float x, float y)
        => Send($"MOVE|{x.ToString("F1", Inv)}|{y.ToString("F1", Inv)}");

    public void SendAttack(int targetId)
        => Send($"ATTACK|{targetId}");

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];
        var sb = new StringBuilder();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await _stream!.ReadAsync(buffer, ct);
                if (n == 0) break;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, n));
                while (true)
                {
                    var s = sb.ToString();
                    int idx = s.IndexOf('\n');
                    if (idx < 0) break;
                    var line = s[..idx].Trim('\r', ' ', '\t');
                    sb.Remove(0, idx + 1);
                    if (line.Length == 0) continue;

                    _world.HandlePacket(line);

                    if (line.StartsWith("WELCOME|"))
                    {
                        var parts = line.Split('|');
                        if (parts.Length >= 2 && int.TryParse(parts[1], out var pid))
                            _welcomeTcs?.TrySetResult(pid);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* 연결 종료 */ }
        finally
        {
            _welcomeTcs?.TrySetException(new IOException("연결이 종료됨"));
            Close();
        }
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var msg in _outgoing.Reader.ReadAllAsync(ct))
            {
                if (!_tcp.Connected) break;
                var data = Encoding.UTF8.GetBytes(msg + "\n");
                await _stream!.WriteAsync(data, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _outgoing.Writer.TryComplete(); } catch { }
        try { _cts?.Cancel(); } catch { }
        try { _tcp.Close(); } catch { }
    }
}
