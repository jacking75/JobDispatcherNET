using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using MessagePack;

namespace JobDispatcherNET.Samples.Pipelines;

/// <summary>
/// Wire opcodes. One byte, so the header stays 3 bytes.
/// Client-&gt;server opcodes are &lt; 100, server-&gt;client are &gt;= 100.
/// </summary>
public static class Op
{
    // client -> server
    /// <summary>Client asks to enter the world.</summary>
    public const byte Login = 1;

    /// <summary>Client reports a new position.</summary>
    public const byte Move = 2;

    /// <summary>Client says something.</summary>
    public const byte Chat = 3;

    // server -> client
    /// <summary>Answer to <see cref="Login"/>.</summary>
    public const byte LoginAck = 101;

    /// <summary>Answer to <see cref="Move"/>; echoes the client stopwatch stamp.</summary>
    public const byte MoveAck = 102;

    /// <summary>Answer to <see cref="Chat"/>; echoes the client stopwatch stamp.</summary>
    public const byte ChatAck = 103;

    /// <summary>Unsolicited world state, pushed by the world tick.</summary>
    public const byte Snapshot = 110;

    /// <summary>
    /// Not a wire opcode. The session pushes this through its own <c>Sequencer&lt;InboundFrame&gt;</c>
    /// when the socket dies, so "this client left" is handled after every packet that arrived before it.
    /// </summary>
    public const byte InternalDisconnect = 255;
}

/// <summary>Client -&gt; server: enter the world.</summary>
[MessagePackObject]
public sealed class LoginRequest
{
    /// <summary>Display name.</summary>
    [Key(0)] public string Name { get; set; } = string.Empty;

    /// <summary>Client <c>Stopwatch.GetTimestamp()</c> at send time, echoed back for latency math.</summary>
    [Key(1)] public long ClientTicks { get; set; }
}

/// <summary>Server -&gt; client: the answer to <see cref="LoginRequest"/>.</summary>
[MessagePackObject]
public sealed class LoginResponse
{
    /// <summary>Entity id assigned to this connection.</summary>
    [Key(0)] public int EntityId { get; set; }

    /// <summary>False when the login was refused.</summary>
    [Key(1)] public bool Ok { get; set; }

    /// <summary>Echo of <see cref="LoginRequest.ClientTicks"/>.</summary>
    [Key(2)] public long ClientTicks { get; set; }

    /// <summary>Human-readable detail, mostly for failures.</summary>
    [Key(3)] public string Message { get; set; } = string.Empty;
}

/// <summary>Client -&gt; server: new position.</summary>
[MessagePackObject]
public sealed class MoveRequest
{
    /// <summary>New X.</summary>
    [Key(0)] public float X { get; set; }

    /// <summary>New Y.</summary>
    [Key(1)] public float Y { get; set; }

    /// <summary>Client stopwatch stamp, echoed back.</summary>
    [Key(2)] public long ClientTicks { get; set; }
}

/// <summary>Server -&gt; client: the move the entity actor actually applied.</summary>
[MessagePackObject]
public sealed class MoveResponse
{
    /// <summary>Entity that moved.</summary>
    [Key(0)] public int EntityId { get; set; }

    /// <summary>Applied X.</summary>
    [Key(1)] public float X { get; set; }

    /// <summary>Applied Y.</summary>
    [Key(2)] public float Y { get; set; }

    /// <summary>Echo of <see cref="MoveRequest.ClientTicks"/>.</summary>
    [Key(3)] public long ClientTicks { get; set; }
}

/// <summary>Client -&gt; server: chat line.</summary>
[MessagePackObject]
public sealed class ChatRequest
{
    /// <summary>What was said.</summary>
    [Key(0)] public string Text { get; set; } = string.Empty;

    /// <summary>Client stopwatch stamp, echoed back.</summary>
    [Key(1)] public long ClientTicks { get; set; }
}

/// <summary>Server -&gt; client: chat acknowledgement.</summary>
[MessagePackObject]
public sealed class ChatResponse
{
    /// <summary>Who said it.</summary>
    [Key(0)] public int EntityId { get; set; }

    /// <summary>What was said.</summary>
    [Key(1)] public string Text { get; set; } = string.Empty;

    /// <summary>Echo of <see cref="ChatRequest.ClientTicks"/>.</summary>
    [Key(2)] public long ClientTicks { get; set; }
}

/// <summary>One entity inside a <see cref="SnapshotMessage"/>.</summary>
[MessagePackObject]
public sealed class SnapshotEntity
{
    /// <summary>Entity id.</summary>
    [Key(0)] public int Id { get; set; }

    /// <summary>Last reported X.</summary>
    [Key(1)] public float X { get; set; }

    /// <summary>Last reported Y.</summary>
    [Key(2)] public float Y { get; set; }
}

/// <summary>Server -&gt; client: periodic world state, produced by the world actor's tick timer.</summary>
[MessagePackObject]
public sealed class SnapshotMessage
{
    /// <summary>Monotonic tick number.</summary>
    [Key(0)] public long Tick { get; set; }

    /// <summary>Total entities in the world (may be more than <see cref="Entities"/> carries).</summary>
    [Key(1)] public int TotalEntities { get; set; }

    /// <summary>A bounded slice of the world.</summary>
    [Key(2)] public SnapshotEntity[] Entities { get; set; } = [];
}

/// <summary>
/// Length-prefixed binary framing over a byte stream.
///
/// <code>
///  ┌──────────────────┬────────────┬──────────────────────────────┐
///  │ payloadLength    │ opcode     │ payload                      │
///  │ 2 bytes, LE u16  │ 1 byte     │ payloadLength bytes, MsgPack │
///  └──────────────────┴────────────┴──────────────────────────────┘
///    0                2            3                    3+payloadLength
/// </code>
///
/// <para>The length counts the payload only, not the header, so a frame occupies
/// <see cref="HeaderSize"/> + <c>payloadLength</c> bytes. Because the length is a
/// <see cref="ushort"/>, a payload can never exceed 64 KiB — the read path therefore needs no
/// separate "max frame size" guard against a hostile peer claiming a 2 GB body.</para>
/// </summary>
public static class FrameCodec
{
    /// <summary>Bytes before the payload: 2-byte length + 1-byte opcode.</summary>
    public const int HeaderSize = 3;

    /// <summary>Largest payload the 16-bit length field can describe.</summary>
    public const int MaxPayloadSize = ushort.MaxValue;

    /// <summary>
    /// MessagePack settings used on both ends. <see cref="MessagePackSecurity.UntrustedData"/>
    /// caps recursion depth and pre-allocation, which matters for anything fed by a socket.
    /// </summary>
    public static readonly MessagePackSerializerOptions SerializerOptions =
        MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);

    /// <summary>
    /// Pull one whole frame off the front of <paramref name="buffer"/>.
    ///
    /// <para><b>This is the heart of the sample.</b> A <see cref="PipeReader"/> hands you a
    /// <see cref="ReadOnlySequence{T}"/> that may be one span or a chain of them, and TCP will
    /// happily split a frame anywhere — including in the middle of the two length bytes. So:</para>
    /// <list type="bullet">
    /// <item>Never index into <c>buffer.FirstSpan</c>. Segment 1 can hold one length byte and
    /// segment 2 the other.</item>
    /// <item><see cref="SequenceReader{T}"/> does the straddling reads for us:
    /// <c>TryReadLittleEndian</c> returns false rather than reading garbage when fewer than two
    /// bytes are available, and stitches the two halves when they live in different segments.</item>
    /// <item>On a partial frame we return false <b>without</b> moving
    /// <paramref name="buffer"/>. The caller then reports <c>consumed = buffer.Start</c> and
    /// <c>examined = buffer.End</c> to <see cref="PipeReader.AdvanceTo(SequencePosition,SequencePosition)"/>,
    /// so the pipe keeps the partial frame and blocks until more bytes arrive.</item>
    /// <item>On success <paramref name="buffer"/> is advanced past the frame, so the caller can
    /// simply loop <c>while (TryReadFrame(ref buffer, ...))</c> and drain every complete frame that
    /// one read produced — a busy client often delivers several per <c>ReadAsync</c>.</item>
    /// </list>
    ///
    /// <para><paramref name="payload"/> points <i>into the pipe's own memory</i>. It is only valid
    /// until <c>AdvanceTo</c> is called, so a caller that hands the payload to another thread must
    /// copy it out first.</para>
    /// </summary>
    /// <param name="buffer">Unparsed bytes. Advanced past the frame when this returns true.</param>
    /// <param name="opcode">The frame's opcode.</param>
    /// <param name="payload">The frame body, still inside the pipe's buffers.</param>
    /// <returns>True when a whole frame was available.</returns>
    public static bool TryReadFrame(ref ReadOnlySequence<byte> buffer, out byte opcode, out ReadOnlySequence<byte> payload)
    {
        opcode = 0;
        payload = default;

        // Cheap early out: not even a header yet.
        if (buffer.Length < HeaderSize)
            return false;

        var reader = new SequenceReader<byte>(buffer);

        // Reads two bytes even when they sit in different segments.
        if (!reader.TryReadLittleEndian(out short rawLength))
            return false;
        var payloadLength = (ushort)rawLength;

        if (!reader.TryRead(out opcode))
        {
            opcode = 0;
            return false;
        }

        // Header is complete but the body is still in flight. Leave everything where it is.
        if (reader.Remaining < payloadLength)
        {
            opcode = 0;
            return false;
        }

        // reader.Position is the first payload byte. GetPosition walks the segment chain for us,
        // so this is correct whether the payload is contiguous or spread over five segments.
        var bodyStart = reader.Position;
        var bodyEnd = buffer.GetPosition(payloadLength, bodyStart);

        payload = buffer.Slice(bodyStart, bodyEnd);
        buffer = buffer.Slice(bodyEnd);
        return true;
    }

    /// <summary>
    /// Serialize <paramref name="value"/> and wrap it in a frame.
    ///
    /// <para>Returns a standalone array so the frame can cross threads: the session's send loop is
    /// the only thing allowed to touch the <see cref="PipeWriter"/>, and worker threads hand it
    /// finished frames through a bounded channel. A latency-critical server would serialize
    /// straight into the writer's span instead; the cost here is one extra copy per message.</para>
    /// </summary>
    public static byte[] Encode<T>(byte opcode, T value)
    {
        var payload = MessagePackSerializer.Serialize(value, SerializerOptions);
        if (payload.Length > MaxPayloadSize)
            throw new InvalidOperationException($"Payload of {payload.Length} bytes exceeds the {MaxPayloadSize}-byte frame limit.");

        var frame = new byte[HeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, (ushort)payload.Length);
        frame[2] = opcode;
        payload.CopyTo(frame.AsSpan(HeaderSize));
        return frame;
    }

    /// <summary>Deserialize a payload that is still sitting in pipe memory.</summary>
    public static T Decode<T>(in ReadOnlySequence<byte> payload) =>
        MessagePackSerializer.Deserialize<T>(payload, SerializerOptions);

    /// <summary>Deserialize a payload that was copied into a rented array.</summary>
    public static T Decode<T>(ReadOnlyMemory<byte> payload) =>
        MessagePackSerializer.Deserialize<T>(payload, SerializerOptions);
}
