using System.Buffers;

namespace JobDispatcherNET.Samples.Pipelines.LoadClient;

/// <summary>
/// A segment in a hand-built <see cref="ReadOnlySequence{T}"/>. Real pipes hand out multi-segment
/// sequences only under load, so the self-test builds pathological ones on purpose.
/// </summary>
internal sealed class Segment : ReadOnlySequenceSegment<byte>
{
    private Segment(ReadOnlyMemory<byte> memory, long runningIndex)
    {
        Memory = memory;
        RunningIndex = runningIndex;
    }

    /// <summary>Build a sequence whose segments are exactly <paramref name="chunkSize"/> bytes.</summary>
    public static ReadOnlySequence<byte> Chunked(ReadOnlyMemory<byte> data, int chunkSize)
    {
        if (data.Length == 0)
            return ReadOnlySequence<byte>.Empty;

        Segment? first = null;
        Segment? last = null;
        long running = 0;

        for (var offset = 0; offset < data.Length; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, data.Length - offset);
            var segment = new Segment(data.Slice(offset, length), running);
            running += length;

            if (first is null)
            {
                first = segment;
            }
            else
            {
                last!.Next = segment;
            }
            last = segment;
        }

        return new ReadOnlySequence<byte>(first!, 0, last!, last!.Memory.Length);
    }
}

/// <summary>
/// Exercises <see cref="FrameCodec.TryReadFrame"/> against sequences that are split in every place
/// TCP could split them — inside the two length bytes, between length and opcode, and all through
/// the payload. Run it with <c>--selftest</c>; it needs no server, so CI can use it as a fast gate
/// before spending time on the real load run.
/// </summary>
public static class FramingSelfTest
{
    /// <summary>Run every case. Returns 0 on success.</summary>
    public static int Run()
    {
        var failures = 0;

        var messages = new (byte Opcode, MoveRequest Payload)[]
        {
            (Op.Move, new MoveRequest { X = 1.5f, Y = -2.5f, ClientTicks = 12345 }),
            (Op.Move, new MoveRequest { X = float.MaxValue, Y = float.MinValue, ClientTicks = long.MaxValue }),
            (Op.Move, new MoveRequest { X = 0, Y = 0, ClientTicks = 0 }),
        };

        // Three frames back to back, so the parser also has to keep going after the first one.
        var stream = new List<byte>();
        foreach (var (opcode, payload) in messages)
            stream.AddRange(FrameCodec.Encode(opcode, payload));
        var wire = stream.ToArray();

        // 1 byte per segment is the worst case: every field straddles a boundary.
        foreach (var chunkSize in new[] { 1, 2, 3, 4, 7, 13, wire.Length })
        {
            var sequence = Segment.Chunked(wire, chunkSize);
            var buffer = sequence;
            var decoded = 0;

            while (FrameCodec.TryReadFrame(ref buffer, out var opcode, out var payload))
            {
                var expected = messages[decoded];
                var actual = FrameCodec.Decode<MoveRequest>(payload);

                if (opcode != expected.Opcode || actual.X != expected.Payload.X ||
                    actual.Y != expected.Payload.Y || actual.ClientTicks != expected.Payload.ClientTicks)
                {
                    Console.Error.WriteLine($"  FAIL chunk={chunkSize}: frame {decoded} decoded wrong");
                    failures++;
                }
                decoded++;
            }

            if (decoded != messages.Length)
            {
                Console.Error.WriteLine($"  FAIL chunk={chunkSize}: decoded {decoded} of {messages.Length} frames");
                failures++;
            }
            else if (!buffer.IsEmpty)
            {
                Console.Error.WriteLine($"  FAIL chunk={chunkSize}: {buffer.Length} bytes left over");
                failures++;
            }
            else
            {
                Console.WriteLine($"  ok   chunk={chunkSize,-5} → {decoded} frames, buffer fully consumed");
            }
        }

        // Every prefix of the stream must be refused without consuming anything, so the pipe can
        // keep the partial frame until the rest arrives.
        for (var prefix = 0; prefix < wire.Length; prefix++)
        {
            var partial = new ReadOnlySequence<byte>(wire.AsMemory(0, prefix));
            var buffer = partial;
            var frames = 0;
            while (FrameCodec.TryReadFrame(ref buffer, out _, out _))
                frames++;

            // Whatever it could not parse must still be sitting in the buffer.
            var consumed = prefix - buffer.Length;
            if (frames == 0 && consumed != 0)
            {
                Console.Error.WriteLine($"  FAIL prefix={prefix}: consumed {consumed} bytes from an incomplete frame");
                failures++;
            }
        }
        Console.WriteLine($"  ok   {wire.Length} truncated prefixes all left their partial frame in the buffer");

        // A header-only frame (payloadLength == 0) is legal and must not stall the parser.
        var empty = new byte[] { 0x00, 0x00, Op.Move };
        var emptyBuffer = Segment.Chunked(empty, 1);
        if (!FrameCodec.TryReadFrame(ref emptyBuffer, out var emptyOpcode, out var emptyPayload)
            || emptyOpcode != Op.Move || !emptyPayload.IsEmpty || !emptyBuffer.IsEmpty)
        {
            Console.Error.WriteLine("  FAIL: header-only frame not parsed");
            failures++;
        }
        else
        {
            Console.WriteLine($"  ok   header-only frame ({empty.Length} bytes, {emptyPayload.Length}-byte payload)");
        }

        Console.WriteLine(failures == 0 ? "framing self-test PASS" : $"framing self-test FAIL ({failures})");
        return failures == 0 ? 0 : 1;
    }
}
