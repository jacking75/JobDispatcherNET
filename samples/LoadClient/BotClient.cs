using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net.Sockets;

namespace JobDispatcherNET.Samples.Pipelines.LoadClient;

/// <summary>What one bot recorded during the run.</summary>
public sealed class BotStats
{
    /// <summary>True once the TCP connect succeeded.</summary>
    public bool Connected { get; set; }

    /// <summary>Set when the bot died with something other than an ordinary disconnect.</summary>
    public string? Error { get; set; }

    /// <summary>Frames written.</summary>
    public long Sent;

    /// <summary>Frames read (acks and snapshots).</summary>
    public long Received;

    /// <summary>Round-trip milliseconds, one per ack that carried a stamp.</summary>
    public List<double> Latencies { get; } = new(1024);
}

/// <summary>
/// One simulated player: connects, logs in, then sends Move/Chat at a fixed rate while a
/// <see cref="PipeReader"/> loop reads acks and snapshots.
///
/// <para>It reuses <see cref="FrameCodec.TryReadFrame"/>, which is the point — the same framing
/// code drives both ends, so a bug in the split-frame handling shows up here immediately.</para>
/// </summary>
public sealed class BotClient(int index, string host, int port, double ratePerSecond)
{
    private const int DrainGraceMs = 750;

    /// <summary>Counters for this bot.</summary>
    public BotStats Stats { get; } = new();

    /// <summary>Connect, run for as long as <paramref name="sendToken"/> stays uncancelled, then drain.</summary>
    public async Task RunAsync(CancellationToken sendToken)
    {
        using var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(host, port, sendToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Stats.Error = $"connect: {ex.GetType().Name}: {ex.Message}";
            return;
        }

        Stats.Connected = true;
        tcp.NoDelay = true;

        var stream = tcp.GetStream();
        var reader = PipeReader.Create(stream, new StreamPipeReaderOptions(bufferSize: 4096, leaveOpen: true));
        var writer = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));

        // The receive side must outlive the send side by the grace period, so it gets its own CTS.
        using var receiveCts = new CancellationTokenSource();
        var receiveTask = ReceiveLoopAsync(reader, receiveCts.Token);

        try
        {
            await SendAsync(writer, Op.Login, new LoginRequest
            {
                Name = $"bot{index}",
                ClientTicks = Stopwatch.GetTimestamp(),
            }, sendToken).ConfigureAwait(false);

            await SendLoopAsync(writer, sendToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* run finished */ }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            Stats.Error ??= $"send: {ex.GetType().Name}";
        }
        catch (Exception ex)
        {
            Stats.Error ??= $"send: {ex.GetType().Name}: {ex.Message}";
        }

        // Give the last acks a chance to arrive before we tear the socket down, otherwise the
        // tail of the run always looks like packet loss.
        try { await Task.Delay(DrainGraceMs, CancellationToken.None).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* ignore */ }

        await receiveCts.CancelAsync().ConfigureAwait(false);
        try { await receiveTask.ConfigureAwait(false); } catch { /* already recorded */ }

        try { await writer.CompleteAsync().ConfigureAwait(false); } catch { /* socket gone */ }
        try { await reader.CompleteAsync().ConfigureAwait(false); } catch { /* socket gone */ }
    }

    private async Task SendLoopAsync(PipeWriter writer, CancellationToken ct)
    {
        var periodMs = Math.Max(1.0, 1000.0 / ratePerSecond);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(periodMs));

        // Stagger the bots inside one period so 200 of them do not all fire on the same millisecond.
        var jitter = Random.Shared.Next((int)periodMs + 1);
        try { await Task.Delay(jitter, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        var n = 0;
        var x = index % 64 * 4f;
        var y = index / 64 % 64 * 4f;

        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            n++;

            // Nine moves to one chat — roughly what a real client's mix looks like.
            if (n % 10 == 0)
            {
                await SendAsync(writer, Op.Chat, new ChatRequest
                {
                    Text = $"bot{index} msg {n}",
                    ClientTicks = Stopwatch.GetTimestamp(),
                }, ct).ConfigureAwait(false);
            }
            else
            {
                x += MathF.Sin(n * 0.1f);
                y += MathF.Cos(n * 0.1f);
                await SendAsync(writer, Op.Move, new MoveRequest
                {
                    X = x,
                    Y = y,
                    ClientTicks = Stopwatch.GetTimestamp(),
                }, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task SendAsync<T>(PipeWriter writer, byte opcode, T payload, CancellationToken ct)
    {
        writer.Write(FrameCodec.Encode(opcode, payload));
        await writer.FlushAsync(ct).ConfigureAwait(false);
        Interlocked.Increment(ref Stats.Sent);
    }

    /// <summary>
    /// Same read pattern as the server: ReadAsync, drain every complete frame, AdvanceTo with
    /// consumed/examined so a partial frame stays in the pipe.
    /// </summary>
    private async Task ReceiveLoopAsync(PipeReader reader, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await reader.ReadAsync(ct).ConfigureAwait(false);
                var buffer = result.Buffer;

                while (FrameCodec.TryReadFrame(ref buffer, out var opcode, out var payload))
                {
                    Interlocked.Increment(ref Stats.Received);
                    Observe(opcode, payload);
                }

                reader.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted)
                    break;
            }
        }
        catch (OperationCanceledException) { /* run finished */ }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            Stats.Error ??= $"recv: {ex.GetType().Name}";
        }
        catch (Exception ex)
        {
            Stats.Error ??= $"recv: {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// Latency is measured by echo: the client stamps <c>Stopwatch.GetTimestamp()</c> into the
    /// request, the server copies it into the ack, and the round trip is the difference. Both
    /// readings come from the same process, so no clock sync is involved.
    /// </summary>
    private void Observe(byte opcode, in ReadOnlySequence<byte> payload)
    {
        long stamp;
        switch (opcode)
        {
            case Op.LoginAck:
                stamp = FrameCodec.Decode<LoginResponse>(payload).ClientTicks;
                break;
            case Op.MoveAck:
                stamp = FrameCodec.Decode<MoveResponse>(payload).ClientTicks;
                break;
            case Op.ChatAck:
                stamp = FrameCodec.Decode<ChatResponse>(payload).ClientTicks;
                break;
            default:
                return;     // snapshots carry no stamp
        }

        if (stamp > 0)
            Stats.Latencies.Add(Stopwatch.GetElapsedTime(stamp).TotalMilliseconds);
    }
}
