namespace LuminObjectPool;

using System.Runtime.CompilerServices;

/// <summary>
/// The fastest pool variant. It performs no thread-local lookup, no atomic
/// operation, and no memory barrier, so it is only safe while a single thread both
/// rents and returns.
/// </summary>
/// <remarks>
/// Cached instances form a last-in-first-out stack, which keeps the hottest
/// instance on the same cache line. Use <see cref="AtomicObjectPool{T}"/> or one of
/// the ring pools when more than one thread touches the pool.
/// </remarks>
/// <typeparam name="T">The pooled reference type.</typeparam>
public sealed class ThreadUnsafePool<T> : IPool<T> where T : class
{
    private const int MinimumArrayLength = 4;

    private readonly PoolCore<T> _core;
    private T?[] _items;
    private int _count;

    /// <inheritdoc/>
    public int Capacity { get; }

    /// <summary>Creates a pool with the default capacity and no callbacks.</summary>
    public ThreadUnsafePool()
        : this(PoolOptions<T>.Default)
    {
    }

    /// <summary>Creates a pool with the given capacity and no callbacks.</summary>
    public ThreadUnsafePool(int capacity)
        : this(new PoolOptions<T> { Capacity = capacity })
    {
    }

    /// <summary>Creates a pool with a custom factory.</summary>
    public ThreadUnsafePool(Func<T>? factory)
        : this(new PoolOptions<T> { Factory = factory })
    {
    }

    /// <summary>Creates a pool with a custom factory and capacity.</summary>
    public ThreadUnsafePool(Func<T>? factory, int capacity)
        : this(new PoolOptions<T> { Factory = factory, Capacity = capacity })
    {
    }

    /// <summary>Creates a pool with a custom factory, reset callback, and capacity.</summary>
    public ThreadUnsafePool(Func<T>? factory, Action<T>? onReturn, int capacity = PoolOptions<T>.DefaultCapacity)
        : this(new PoolOptions<T> { Factory = factory, OnReturn = onReturn, Capacity = capacity })
    {
    }

    /// <summary>Creates a pool from a full configuration.</summary>
    public ThreadUnsafePool(PoolOptions<T> options)
    {
        Capacity = PoolCore<T>.ValidateCapacity(options.Capacity);
        _core = new PoolCore<T>(options);
        _items = Capacity == 0 ? Array.Empty<T?>() : new T?[Math.Min(Capacity, MinimumArrayLength)];
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T Rent()
    {
        int count = _count;
        if (count != 0)
        {
            count--;
            T?[] items = _items;
            T item = items[count]!;

            // Clearing the cell keeps rented instances out of reach of Clear and
            // Dispose, and lets the collector reclaim them while they are in use.
            items[count] = null;
            _count = count;
            return item;
        }

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

        int count = _count;
        if (count == Capacity)
        {
            _core.Destroy(item);
            return;
        }

        if (count == _items.Length)
            Grow();

        _items[count] = item;
        _count = count + 1;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Prewarm(int count)
    {
        for (int i = 0; i < count; i++)
            Return(_core.Create());
    }

    /// <inheritdoc/>
    public void Clear()
    {
        T?[] items = _items;
        int count = _count;
        _count = 0;

        for (int i = 0; i < count; i++)
        {
            T? item = items[i];
            items[i] = null;
            if (item is not null)
                _core.Destroy(item);
        }
    }

    /// <inheritdoc/>
    /// <remarks>Equivalent to <see cref="Clear"/>; the pool keeps no disposed state.</remarks>
    public void Dispose() => Clear();

    /// <summary>Gets the exact number of cached instances.</summary>
    public int Count => _count;

    private void Grow()
    {
        int target = Math.Min(Capacity, Math.Max(MinimumArrayLength, _items.Length * 2));
        Array.Resize(ref _items, target);
    }
}