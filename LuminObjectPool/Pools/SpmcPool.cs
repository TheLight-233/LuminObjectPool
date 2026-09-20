namespace LuminObjectPool;

using System.Runtime.CompilerServices;

/// <summary>
/// A single-producer multi-consumer pool backed by a bounded ring buffer. Only one
/// thread may return, but any number of threads may rent.
/// </summary>
/// <remarks>
/// The producer owns its ring position and publishes instances through the cell
/// sequence alone, so the return path runs without a compare-and-swap. Consumers
/// claim positions with a compare-and-swap. This suits one owner thread that
/// refills the pool while worker threads draw from it.
/// </remarks>
/// <typeparam name="T">The pooled reference type.</typeparam>
public sealed class SpmcPool<T> : IPool<T> where T : class
{
    private readonly PoolCore<T> _core;
    private readonly RingSlot<T>[] _slots;
    private readonly int _capacity;
    private readonly int _indexMask;
    private PaddedCounter _producePosition;
    private PaddedCounter _consumePosition;

    /// <inheritdoc/>
    public int Capacity => _capacity;

    /// <summary>Creates a pool with the default capacity and no callbacks.</summary>
    public SpmcPool()
        : this(PoolOptions<T>.Default)
    {
    }

    /// <summary>Creates a pool with the given capacity and no callbacks.</summary>
    public SpmcPool(int capacity)
        : this(new PoolOptions<T> { Capacity = capacity })
    {
    }

    /// <summary>Creates a pool with a custom factory.</summary>
    public SpmcPool(Func<T>? factory)
        : this(new PoolOptions<T> { Factory = factory })
    {
    }

    /// <summary>Creates a pool with a custom factory and capacity.</summary>
    public SpmcPool(Func<T>? factory, int capacity)
        : this(new PoolOptions<T> { Factory = factory, Capacity = capacity })
    {
    }

    /// <summary>Creates a pool with a custom factory, reset callback, and capacity.</summary>
    public SpmcPool(Func<T>? factory, Action<T>? onReturn, int capacity = PoolOptions<T>.DefaultCapacity)
        : this(new PoolOptions<T> { Factory = factory, OnReturn = onReturn, Capacity = capacity })
    {
    }

    /// <summary>Creates a pool from a full configuration.</summary>
    public SpmcPool(PoolOptions<T> options)
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
    /// <remarks>Must run on the producer thread, or while all threads are quiescent.</remarks>
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
            long count = Volatile.Read(ref _producePosition.Value) -
                         Volatile.Read(ref _consumePosition.Value);
            if (count <= 0)
                return 0;

            return count >= _capacity ? _capacity : (int)count;
        }
    }

    private bool TryEnqueue(T item)
    {
        int capacity = _capacity;
        if (capacity == 0)
            return false;

        long position = _producePosition.Value;
        ref RingSlot<T> slot = ref _slots[RingMath.GetIndex(position, _indexMask, capacity)];

        // The producer never revisits a position, so the cell still holds the previous
        // lap exactly when it has not been consumed yet, which means the ring is full.
        if (Volatile.Read(ref slot.Sequence) != position)
            return false;

        slot.Item = item;
        Volatile.Write(ref slot.Sequence, position + 1);
        _producePosition.Value = position + 1;
        return true;
    }

    private bool TryDequeue(out T? item)
    {
        int capacity = _capacity;
        if (capacity == 0)
        {
            item = null;
            return false;
        }

        while (true)
        {
            long position = Volatile.Read(ref _consumePosition.Value);
            ref RingSlot<T> slot = ref _slots[RingMath.GetIndex(position, _indexMask, capacity)];
            long difference = Volatile.Read(ref slot.Sequence) - (position + 1);

            if (difference == 0)
            {
                if (Interlocked.CompareExchange(
                        ref _consumePosition.Value, position + 1, position) == position)
                {
                    item = slot.Item;
                    slot.Item = null;
                    Volatile.Write(ref slot.Sequence, position + capacity);
                    return item is not null;
                }
            }
            else if (difference < 0)
            {
                // The producer has not reached this position, so nothing is cached.
                item = null;
                return false;
            }

            // Another consumer claimed this position first; read the next one.
        }
    }
}