namespace LuminObjectPool;

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

/// <summary>
/// A ring cell that keeps its sequence number and item on a private cache line, so
/// adjacent producers and consumers do not bounce unrelated cells between cores.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RingSlot<T> where T : class
{
#pragma warning disable CS0169
    private long _pad0, _pad1, _pad2, _pad3, _pad4, _pad5, _pad6;
    internal long Sequence;
    internal T? Item;
    private long _pad8, _pad9, _pad10, _pad11, _pad12, _pad13, _pad14;
#pragma warning restore CS0169
}

/// <summary>A 64-bit counter padded to its own cache line.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PaddedCounter
{
#pragma warning disable CS0169
    private long _pad0, _pad1, _pad2, _pad3, _pad4, _pad5, _pad6;
    internal long Value;
    private long _pad8, _pad9, _pad10, _pad11, _pad12, _pad13, _pad14;
#pragma warning restore CS0169
}

internal static class RingMath
{
    /// <summary>
    /// Returns <c>capacity - 1</c> for a positive power of two and -1 otherwise, so
    /// callers can pick the mask fast path or fall back to a modulo.
    /// </summary>
    internal static int GetIndexMask(int capacity) =>
        capacity > 0 && (capacity & (capacity - 1)) == 0 ? capacity - 1 : -1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int GetIndex(long position, int indexMask, int capacity) =>
        indexMask >= 0
            ? (int)position & indexMask
            : (int)((ulong)position % (ulong)capacity);
}