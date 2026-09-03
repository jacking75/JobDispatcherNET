using System.Runtime.InteropServices;

namespace JobDispatcherNET;

/// <summary>
/// A monotonic counter spread across cache lines.
///
/// A single <c>Interlocked.Increment</c> shared by eight workers serialises them on one cache
/// line; striping by thread id keeps each worker on its own line and turns the increment back
/// into an uncontended operation. Reads sum the stripes, so they are O(stripes) and only meant
/// for snapshots, not for the hot path.
/// </summary>
internal sealed class StripedCounter
{
    /// <summary>
    /// 128 bytes, with the value in the second half.
    ///
    /// A 64-byte cell is one cache line, but the array header pushes element 0 off a line boundary,
    /// so neighbouring cells straddle the same line and the striping does nothing for them. 128
    /// bytes with the value at offset 64 lands each value in its own line whatever the header does,
    /// and also separates the adjacent-line prefetch pairs that x86 fetches together. The cost is
    /// 64 bytes of padding per stripe.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct Cell
    {
        [FieldOffset(64)] public long Value;
    }

    private readonly Cell[] _cells;
    private readonly int _mask;

    public StripedCounter()
    {
        var stripes = 1;
        var target = Math.Min(Math.Max(Environment.ProcessorCount, 1), 64);
        while (stripes < target) stripes <<= 1;
        _mask = stripes - 1;
        _cells = new Cell[stripes];
    }

    public void Increment() =>
        Interlocked.Increment(ref _cells[Environment.CurrentManagedThreadId & _mask].Value);

    public void Add(long value) =>
        Interlocked.Add(ref _cells[Environment.CurrentManagedThreadId & _mask].Value, value);

    public long Value
    {
        get
        {
            long total = 0;
            for (var i = 0; i < _cells.Length; i++)
                total += Interlocked.Read(ref _cells[i].Value);
            return total;
        }
    }

    public void Reset()
    {
        for (var i = 0; i < _cells.Length; i++)
            Interlocked.Exchange(ref _cells[i].Value, 0);
    }
}
