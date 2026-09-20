namespace LuminObjectPool;

using System.Runtime.CompilerServices;

/// <summary>
/// The fastest cross-thread pool. Exactly one thread may return and exactly one
/// other thread may rent, and neither side performs a compare-and-swap.
/// </summary>
/// <remarks>
/// Each side owns one cursor and reads the other with acquire/release semantics,
/// so the two threads hand instances over through nothing more than two counters
/// and a slot array. Producer and consumer cursors are kept on separate cache
/// lines to avoid false sharing.
/// </remarks>
/// <typeparam name="T">The pooled reference type.</typeparam>
public sealed class SpscPool<T> : IPool<T> where T : class
{
    private readonly PoolCore<T> _core;
    private readonly T?[] _slots;
    private readonly int _capacity;
    private readonly int _indexMask;
    private PaddedCounter _producePosition;
    private PaddedCounter _consumePosition;

    /// <inheritdoc/>
    public int Capacity => _capacity;

    /// <summary>Creates a pool with the default capacity and no callbacks.</summary>
    public SpscPool()
        : this(PoolOptions<T>.Default)
    {
    }

    /// <summary>Creates a pool with the given capacity and no callbacks.</summary>
    public SpscPool(int capacity)
        : this(new PoolOptions<T> { Capacity = capacity })
    {
    }

    /// <summary>Creates a pool with a custom factory.</summary>
    public SpscPool(Func<T>? factory)
        : this(new PoolOptions<T> { Factory = factory })
    {
    }

    /// <summary>Creates a pool with a custom factory and capacity.</summary>
    public SpscPool(Func<T>? factory, int capacity)
        : this(new PoolOptions<T> { Factory = factory, Capacity = capacity })
    {
    }

    /// <summary>Creates a pool with a custom factory, reset callback, and capacity.</summary>
    public SpscPool(Func<T>? factory, Action<T>? onReturn, int capacity = PoolOptions<T>.DefaultCapacity)
        : this(new PoolOptions<T> { Factory = factory, OnReturn = onReturn, Capacity = capacity })
    {
    }

    /// <summary>Creates a pool from a full configuration.</summary>
    public SpscPool(PoolOptions<T> options)
    {
        _capacity = PoolCore<T>.ValidateCapacity(options.Capacity);
        _indexMask = RingMath.GetIndexMask(_capacity);
        _slots = _capacity > 0 ? new T?[_capacity] : Array.Empty<T?>();
        _core = new PoolCore<T>(options);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T Rent()
    {
        int capacity = _capacity;
        if (capacity == 0)
            return _core.Create();

        long position = _consumePosition.Value;

        // Acquire the producer's cursor before reading the cell it published.
        if (position == Volatile.Read(ref _producePosition.Value))
            return _core.Create();

        int index = RingMath.GetIndex(position, _indexMask, capacity);
        T? item = _slots[index];
        _slots[index] = null;

        // Release the cell only after the reference has been read out of it.
        Volatile.Write(ref _consumePosition.Value, position + 1);
        return item!;
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

        int capacity = _capacity;
        long position = _producePosition.Value;

        // A lagging consumer cursor is what makes the ring full; the volatile read
        // also guarantees the cell reference it dropped is observed in order.
        if (capacity == 0 || position - Volatile.Read(ref _consumePosition.Value) >= capacity)
        {
            _core.Destroy(item);
            return;
        }

        _slots[RingMath.GetIndex(position, _indexMask, capacity)] = item;
        Volatile.Write(ref _producePosition.Value, position + 1);
    }

    /// <inheritdoc/>
    public void Prewarm(int count)
    {
        for (int i = 0; i < count; i++)
            Return(_core.Create());
    }

    /// <inheritdoc/>
    /// <remarks>Must run on the consumer thread, or while both threads are quiescent.</remarks>
    public void Clear()
    {
        int capacity = _capacity;
        long start = Volatile.Read(ref _consumePosition.Value);
        long end = Volatile.Read(ref _producePosition.Value);

        for (long position = start; position < end; position++)
        {
            int index = RingMath.GetIndex(position, _indexMask, capacity);
            T? item = _slots[index];
            _slots[index] = null;
            if (item is not null)
                _core.Destroy(item);
        }

        Volatile.Write(ref _consumePosition.Value, end);
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
}