namespace LuminObjectPool;

using System.Runtime.CompilerServices;

/// <summary>
/// Fixed-capacity unordered storage used as the shared overflow area of
/// <see cref="AtomicObjectPool{T}"/>. Every slot independently transitions between
/// empty and occupied, so callers only ever exchange a single reference.
/// </summary>
/// <remarks>
/// A probing cursor makes successive operations start on different slots. The
/// cursor lives in the caller's thread-local state, so no shared counter is
/// touched and contention stays limited to the probed slots themselves.
/// </remarks>
internal sealed class AtomicSlotPool<T> where T : class
{
    private readonly T?[] _slots;
    private readonly int _indexMask;

    internal AtomicSlotPool(int capacity)
    {
        _slots = new T?[capacity];
        _indexMask = RingMath.GetIndexMask(capacity);
    }

    internal int Capacity => _slots.Length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryTake(ref int cursor, out T? item)
    {
        T?[] slots = _slots;
        int length = slots.Length;
        int index = length == 0 ? 0 : RingMath.GetIndex(cursor, _indexMask, length);

        for (int remaining = length; remaining > 0; remaining--)
        {
            // Skip the interlocked write when the slot is observably empty.
            if (Volatile.Read(ref slots[index]) is not null)
            {
                item = Interlocked.Exchange(ref slots[index], null);
                if (item is not null)
                {
                    cursor = index + 1;
                    return true;
                }
            }

            if (++index == length)
                index = 0;
        }

        cursor = unchecked(cursor + 1);
        item = null;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryStore(ref int cursor, T item)
    {
        T?[] slots = _slots;
        int length = slots.Length;
        int index = length == 0 ? 0 : RingMath.GetIndex(cursor, _indexMask, length);

        for (int remaining = length; remaining > 0; remaining--)
        {
            // Skip the locked CAS when the slot is observably occupied. A racing
            // taker is harmless because the whole area is still scanned.
            if (Volatile.Read(ref slots[index]) is null &&
                Interlocked.CompareExchange(ref slots[index], item, null) is null)
            {
                cursor = index + 1;
                return true;
            }

            if (++index == length)
                index = 0;
        }

        cursor = unchecked(cursor + 1);
        return false;
    }

    /// <summary>Removes every stored item, keeping the slots reusable.</summary>
    internal void Drain(Action<T> destroy)
    {
        T?[] slots = _slots;
        for (int i = 0; i < slots.Length; i++)
        {
            T? item = Interlocked.Exchange(ref slots[i], null);
            if (item is not null)
                destroy(item);
        }
    }

    internal int GetApproximateCount()
    {
        T?[] slots = _slots;
        int count = 0;
        for (int i = 0; i < slots.Length; i++)
        {
            if (Volatile.Read(ref slots[i]) is not null)
                count++;
        }

        return count;
    }

    internal bool[] GetOccupancyBitmap()
    {
        T?[] slots = _slots;
        bool[] result = new bool[slots.Length];
        for (int i = 0; i < slots.Length; i++)
            result[i] = Volatile.Read(ref slots[i]) is not null;

        return result;
    }
}