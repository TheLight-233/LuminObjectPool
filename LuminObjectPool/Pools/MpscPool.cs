namespace LuminObjectPool;

using System.Runtime.CompilerServices;

/// <summary>
/// A multi-producer single-consumer pool backed by a bounded ring buffer. Many
/// threads may return, but only one thread may rent.
/// </summary>
/// <remarks>
/// Producers claim a ring position with a compare-and-swap, while the consumer owns
/// its position outright and therefore runs without any atomic operation on the
/// rent path. This suits one owner thread that drains instances produced by a set
/// of worker threads.
/// </remarks>
/// <typeparam name="T">The pooled reference type.</typeparam>
public sealed class MpscPool<T> : IPool<T> where T : class
{
    private readonly PoolCore<T> _core;
    private readonly RingSlot<T>[] _slots;
    private readonly int _capacity;
    private readonly int _indexMask;
    private PaddedCounter _enqueuePosition;
    private PaddedCounter _dequeuePosition;

    /// <inheritdoc/>
    public int Capacity => _capacity;

    /// <summary>Creates a pool with the default capacity and no callbacks.</summary>
    public MpscPool()
        : this(PoolOptions<T>.Default)
    {
    }

    /// <summary>Creates a pool with the given capacity and no callbacks.</summary>
    public MpscPool(int capacity)
        : this(new PoolOptions<T> { Capacity = capacity })
    {
    }

    /// <summary>Creates a pool with a custom factory.</summary>
    public MpscPool(Func<T>? factory)
        : this(new PoolOptions<T> { Factory = factory })
    {
    }

    /// <summary>Creates a pool with a custom factory and capacity.</summary>
    public MpscPool(Func<T>? factory, int capacity)
        : this(new PoolOptions<T> { Factory = factory, Capacity = capacity })
    {
    }

    /// <summary>Creates a pool with a custom factory, reset callback, and capacity.</summary>
    public MpscPool(Func<T>? factory, Action<T>? onReturn, int capacity = PoolOptions<T>.DefaultCapacity)
        : this(new PoolOptions<T> { Factory = factory, OnReturn = onReturn, Capacity = capacity })
    {
    }

    /// <summary>Creates a pool from a full configuration.</summary>
    public MpscPool(PoolOptions<T> options)
    {
        _capacity = PoolCore<T>.ValidateCapacity(options.Capacity);
        _indexMask = RingMath.GetIndexMask(_capacity);
        _slots = _capacity > 0 ? new RingSlot<T>[_capacity] : Array.Empty<RingSlot<T>>();
        _core = new PoolCore<T>(options);

        for (int i = 0; i < _capacity; i++)
            _slots[i].Sequence = i;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T Rent()
    {
        if (TryDequeue(out T? item))
            return item!;

        return _core.Create();
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Return(T item)
    {
        if (item is null)
            throw new ArgumentNullException(nameof(item));

        if (!_core.TryReturn(item))
        {
            _core.Destroy(item);
            return;
        }

        if (TryEnqueue(item))
            return;

        _core.Destroy(item);
    }

    /// <inheritdoc/>
    public void Prewarm(int count)
    {
        for (int i = 0; i < count; i++)
            Return(_core.Create());
    }

    /// <inheritdoc/>
    /// <remarks>Must run on the consumer thread, or while all threads are quiescent.</remarks>
    public void Clear()
    {
        while (TryDequeue(out T? item))
        {
            if (item is not null)
                _core.Destroy(item);
        }
    }

    /// <inheritdoc/>
    /// <remarks>Equivalent to <see cref="Clear"/>; the pool keeps no disposed state.</remarks>
    public void Dispose() => Clear();

    /// <inheritdoc/>
    public int Count
    {
        get
        {
            long count = Volatile.Read(ref _enqueuePosition.Value) -
                         Volatile.Read(ref _dequeuePosition.Value);
            if (count <= 0)
                return 0;

            return count >= _capacity ? _capacity : (int)count;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryDequeue(out T? item)
    {
        int capacity = _capacity;
        if (capacity == 0)
        {
            item = null;
            return false;
        }

        long position = _dequeuePosition.Value;
        ref RingSlot<T> slot = ref _slots[RingMath.GetIndex(position, _indexMask, capacity)];

        // The consumer never revisits a position, so the cell is either waiting to be
        // produced or already published. Any other value means the ring is empty.
        if (Volatile.Read(ref slot.Sequence) != position + 1)
        {
            item = null;
            return false;
        }

        item = slot.Item;
        slot.Item = null;
        Volatile.Write(ref slot.Sequence, position + capacity);
        _dequeuePosition.Value = position + 1;
        return item is not null;
    }

    private bool TryEnqueue(T item)
    {
        int capacity = _capacity;
        if (capacity == 0)
            return false;

        long position = Volatile.Read(ref _enqueuePosition.Value);

        while (true)
        {
            ref RingSlot<T> slot = ref _slots[RingMath.GetIndex(position, _indexMask, capacity)];
            long difference = Volatile.Read(ref slot.Sequence) - position;

            if (difference == 0)
            {
                if (Interlocked.CompareExchange(
                        ref _enqueuePosition.Value, position + 1, position) == position)
                {
                    slot.Item = item;
                    Volatile.Write(ref slot.Sequence, position + 1);
                    return true;
                }
            }
            else if (difference < 0)
            {
                // The cell still holds the previous lap, so the ring is full.
                return false;
            }

            position = Volatile.Read(ref _enqueuePosition.Value);
        }
    }
}