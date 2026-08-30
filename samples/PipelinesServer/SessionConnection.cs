using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Threading.Channels;

namespace JobDispatcherNET.Samples.Pipelines;

/// <summary>
/// One frame lifted off the pipe, copied into a rented array so it can safely cross to a worker.
///
/// <para>The payload a <c>PipeReader</c> hands out lives in the pipe's own buffers and is recycled
/// the moment <c>AdvanceTo</c> runs, so anything queued for another thread must be copied first.
/// The array is rented and returned by the handler, so a steady 10k msg/s does not turn into 10k
/// gen0 allocations per second.</para>
/// </summary>
internal readonly struct InboundFrame(byte opcode, byte[] buffer, int length)
{
    public byte Opcode { get; } = opcode;

    /// <summary>Rented array. May be longer than <see cref="Length"/>.</summary>
    public byte[] Buffer { get; } = buffer;

    public int Length { get; } = length;

    public ReadOnlyMemory<byte> Payload => Buffer.AsMemory(0, Length);
}

/// <summary>
/// A single client connection driven by <see cref="System.IO.Pipelines"/>.
///
/// <para><b>Threading.</b> Two async loops per connection, both on the thread pool — no dedicated
/// OS thread per session:</para>
/// <list type="bullet">
/// <item><b>Receive loop</b> — <c>PipeReader.ReadAsync</c>, then <see cref="FrameCodec.TryReadFrame"/>
/// in a loop. It never deserializes and never touches the world; it copies each payload out and
/// pushes it into the session's <see cref="Sequencer{T}"/>.</item>
/// <item><b>Send loop</b> — the only thing allowed to touch the <see cref="PipeWriter"/>. It pulls
/// finished frames off a <b>bounded</b> channel, so a slow socket applies back-pressure all the way
/// up to the producers.</item>
/// </list>
///
/// <para><b>Ordering.</b> The sequencer is what makes a session's frames stay in order: the IO loop
/// only enqueues, and the first enqueue schedules one drain onto the job system's worker pool
/// (<c>JobSystem.Post</c>). Exactly one worker drains a given session at a time, in arrival order.
/// This is the same arrangement <c>AdvancedMmorpgServer</c> uses, minus the two OS threads.</para>
/// </summary>
public sealed class SessionConnection
{
    /// <summary>Frames the outbound channel holds before <see cref="TrySend"/> starts failing.</summary>
    public const int OutboundCapacity = 256;

    /// <summary>Dropped outbound frames tolerated before the client is disconnected.</summary>
    public const int SlowClientDropLimit = 64;

    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly PipeReader _reader;
    private readonly PipeWriter _writer;
    private readonly Channel<byte[]> _outbound;
    private readonly Sequencer<InboundFrame> _inbound;
    private readonly PipelinesGameServer _server;
    private readonly CancellationTokenSource _cts = new();

    private EntityActor? _entity;
    private int _entityId;
    private int _closed;
    private int _disconnectPosted;
    private long _framesReceived;
    private long _framesSent;
    private int _framesDropped;

    internal SessionConnection(long connectionId, Socket socket, PipelinesGameServer server)
    {
        ConnectionId = connectionId;
        _socket = socket;
        _server = server;

        _socket.NoDelay = true;
        _stream = new NetworkStream(socket, ownsSocket: false);

        // PipeReader/PipeWriter over the NetworkStream. Buffers are pooled by the pipe, so a
        // connection costs a couple of pooled segments instead of a 1 MB thread stack.
        _reader = PipeReader.Create(_stream, new StreamPipeReaderOptions(
            bufferSize: 4096, minimumReadSize: 512, leaveOpen: true));
        _writer = PipeWriter.Create(_stream, new StreamPipeWriterOptions(leaveOpen: true));

        // BoundedChannelFullMode.Wait + TryWrite means "fail fast when full" for our producers,
        // while the send loop's own await still gets natural back-pressure from the socket.
        _outbound = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(OutboundCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

        // The system-aware ctor posts drains straight onto the worker pool.
        _inbound = new Sequencer<InboundFrame>(
            server.System,
            handler: HandleFrame,
            onError: ex => JobLog.Error($"[session #{ConnectionId}] frame handling failed", ex));
    }

    /// <summary>Monotonic connection id.</summary>
    public long ConnectionId { get; }

    /// <summary>Entity id once logged in, 0 before.</summary>
    public int EntityId => Volatile.Read(ref _entityId);

    /// <summary>Frames read off the socket.</summary>
    public long FramesReceived => Interlocked.Read(ref _framesReceived);

    /// <summary>Frames written to the socket.</summary>
    public long FramesSent => Interlocked.Read(ref _framesSent);

    /// <summary>Outbound frames dropped because the client could not keep up.</summary>
    public int FramesDropped => Volatile.Read(ref _framesDropped);

    /// <summary>True once the session has been closed.</summary>
    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>Start the receive and send loops. Returns immediately.</summary>
    internal void Start()
    {
        _ = Task.Run(ReceiveLoopAsync);
        _ = Task.Run(SendLoopAsync);
    }

    // ── receive ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The IO half. Parses frames and does nothing else — no deserialization, no world access.
    /// </summary>
    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var result = await _reader.ReadAsync(_cts.Token).ConfigureAwait(false);
                var buffer = result.Buffer;

                // Drain every complete frame this read produced. TryReadFrame advances `buffer`
                // past each one, so after the loop buffer.Start is exactly what we consumed.
                while (FrameCodec.TryReadFrame(ref buffer, out var opcode, out var payload))
                {
                    Interlocked.Increment(ref _framesReceived);

                    // Internal opcodes must never be forgeable from the wire.
                    if (opcode >= 200)
                    {
                        JobLog.Warn($"[session #{ConnectionId}] reserved opcode {opcode} from the wire — dropping client");
                        _reader.AdvanceTo(buffer.Start, buffer.End);
                        return;
                    }

                    EnqueueForWorker(opcode, payload);
                }

                // consumed = what we parsed, examined = everything we looked at. Reporting
                // examined = buffer.End is what tells the pipe "a partial frame is all that's left,
                // don't wake me until more bytes arrive".
                _reader.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted)
                    break;      // peer closed
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            // Ordinary disconnect noise.
        }
        catch (Exception ex)
        {
            JobLog.Error($"[session #{ConnectionId}] receive loop failed", ex);
        }
        finally
        {
            await _reader.CompleteAsync().ConfigureAwait(false);
            HandleDisconnect();
        }
    }

    private void EnqueueForWorker(byte opcode, in ReadOnlySequence<byte> payload)
    {
        var length = (int)payload.Length;
        var rented = ArrayPool<byte>.Shared.Rent(Math.Max(1, length));
        payload.CopyTo(rented);

        if (!_inbound.Enqueue(new InboundFrame(opcode, rented, length)))
        {
            // Sequencer stopped: the session is closing and nothing more will be handled.
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    // ── worker side ─────────────────────────────────────────────────────────

    /// <summary>
    /// Runs on a job-system worker, one frame at a time, in arrival order.
    /// Deserialization happens here, off the IO path.
    /// </summary>
    private void HandleFrame(InboundFrame frame)
    {
        try
        {
            switch (frame.Opcode)
            {
                case Op.Login:
                    OnLogin(FrameCodec.Decode<LoginRequest>(frame.Payload));
                    break;

                case Op.Move:
                    _entity?.PostMove(FrameCodec.Decode<MoveRequest>(frame.Payload));
                    break;

                case Op.Chat:
                    _entity?.PostChat(FrameCodec.Decode<ChatRequest>(frame.Payload));
                    break;

                case Op.InternalDisconnect:
                    OnDisconnectReached();
                    break;

                default:
                    JobLog.Warn($"[session #{ConnectionId}] unknown opcode {frame.Opcode}");
                    break;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame.Buffer);
        }
    }

    private void OnLogin(LoginRequest request)
    {
        if (_entity is not null)
        {
            TrySend(FrameCodec.Encode(Op.LoginAck, new LoginResponse
            {
                EntityId = _entity.Id,
                Ok = false,
                ClientTicks = request.ClientTicks,
                Message = "already logged in",
            }));
            return;
        }

        var id = _server.World.NextEntityId();
        var name = string.IsNullOrWhiteSpace(request.Name) ? $"guest{id}" : request.Name;

        _entity = new EntityActor(id, name, this, _server.World, _server.System);
        Volatile.Write(ref _entityId, id);
        _server.World.PostAdd(_entity);

        TrySend(FrameCodec.Encode(Op.LoginAck, new LoginResponse
        {
            EntityId = id,
            Ok = true,
            ClientTicks = request.ClientTicks,
            Message = name,
        }));
    }

    /// <summary>
    /// The disconnect marker reached the front of the queue, so every packet this client sent has
    /// already been handled. Only now is it safe to take the entity out of the world.
    /// </summary>
    private void OnDisconnectReached()
    {
        var entity = _entity;
        _entity = null;
        if (entity is not null)
            _server.World.PostRemove(entity.Id);
    }

    // ── send ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Queue a pre-framed message. Thread-safe; callable from any worker or from the world tick.
    /// </summary>
    /// <returns>False when the frame was dropped (session closed, or the client is too slow).</returns>
    public bool TrySend(byte[] frame)
    {
        if (Volatile.Read(ref _closed) != 0)
            return false;

        if (_outbound.Writer.TryWrite(frame))
            return true;

        // The channel is full: the socket is not draining as fast as we produce. Tolerate a burst,
        // then drop the client rather than buffering the world into memory on its behalf.
        var dropped = Interlocked.Increment(ref _framesDropped);
        if (dropped == SlowClientDropLimit)
        {
            JobLog.Warn(
                $"[session #{ConnectionId}] outbound backlog full ({OutboundCapacity} frames), " +
                $"{dropped} frames dropped — disconnecting slow client");
            Close("slow client");
        }
        return false;
    }

    /// <summary>
    /// The only writer to the <see cref="PipeWriter"/>. Awaiting <c>FlushAsync</c> is what pushes
    /// back-pressure onto the bounded channel and, through it, onto <see cref="TrySend"/>.
    /// </summary>
    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (var frame in _outbound.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                _writer.Write(frame);
                var flush = await _writer.FlushAsync(_cts.Token).ConfigureAwait(false);
                Interlocked.Increment(ref _framesSent);

                if (flush.IsCompleted || flush.IsCanceled)
                    break;
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or ChannelClosedException)
        {
            // Ordinary disconnect noise.
        }
        catch (Exception ex)
        {
            JobLog.Error($"[session #{ConnectionId}] send loop failed", ex);
        }
        finally
        {
            await _writer.CompleteAsync().ConfigureAwait(false);
            HandleDisconnect();
        }
    }

    // ── lifecycle ───────────────────────────────────────────────────────────

    /// <summary>Called by <see cref="JobOptions.OnDropped"/> when the entity actor refuses work.</summary>
    internal void OnJobDropped(AsyncExecutable actor, DropReason reason)
    {
        if (reason == DropReason.ShuttingDown)
            return;     // the server is stopping; not this client's fault

        JobLog.Warn($"[session #{ConnectionId}] '{actor.Name}' refused a job ({reason}) — disconnecting");
        Close($"actor queue {reason}");
    }

    /// <summary>
    /// Push the disconnect marker through the same sequencer the packets went through, so the
    /// world only forgets this entity after its last packet was handled. Runs at most once.
    /// </summary>
    private void HandleDisconnect()
    {
        if (Interlocked.Exchange(ref _disconnectPosted, 1) == 0)
        {
            var length = 0;
            var rented = ArrayPool<byte>.Shared.Rent(1);
            if (!_inbound.Enqueue(new InboundFrame(Op.InternalDisconnect, rented, length)))
            {
                ArrayPool<byte>.Shared.Return(rented);

                // The sequencer was already stopped (server shutdown). Remove directly so the
                // world cannot keep a ghost entity.
                var id = EntityId;
                if (id != 0)
                    _server.World.PostRemove(id);
            }
        }

        Close("peer closed");
    }

    /// <summary>Close the socket and stop both loops. Idempotent.</summary>
    public void Close(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;

        // Refuse new inbound items; whatever was already accepted (the disconnect marker included)
        // is still drained in order.
        _inbound.Stop();
        _outbound.Writer.TryComplete();

        try { _cts.Cancel(); } catch { /* already disposed */ }
        try { _socket.Shutdown(SocketShutdown.Both); } catch { /* already gone */ }
        try { _stream.Dispose(); } catch { /* already gone */ }
        try { _socket.Dispose(); } catch { /* already gone */ }

        JobLog.Info(
            $"[session #{ConnectionId}] closed ({reason}) entity={EntityId} " +
            $"recv={FramesReceived} sent={FramesSent} dropped={FramesDropped}");

        _server.OnSessionClosed(this);
    }
}
