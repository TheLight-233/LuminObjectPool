namespace LuminObjectPool;

using System.Runtime.CompilerServices;

/// <summary>
/// A bounded multi-producer multi-consumer pool backed by a ring buffer with
/// per-cell sequence numbers. Producers and consumers claim a position before
/// touching a cell, so an instance is published only after its reference is
/// visible and a cell cannot be recycled before the consumer releases it.
/// </summary>
/// <remarks>
/// Unlike <see cref="AtomicObjectPool{T}"/>, the ring is first-in-first-out and
/// hands out the least recently returned instance, which bounds how long a cached
/// instance is kept in cache-cold state. The capacity can be changed with
/// <see cref="Resize"/>.
/// </remarks>
/// <typeparam name="T">The pooled reference type.</typeparam>
public sealed class RingBufferPool<T> : IPool<T> where T : class
{
    private readonly PoolCore<T> _core;
    private Ring _ring;

    /// <summary>Creates a pool with the default capacity and no callbacks.</summary>
    public RingBufferPool()
        : this(PoolOptions<T>.Default)
    {
    }

    /// <summary>Creates a pool with the given capacity and no callbacks.</summary>
    public RingBufferPool(int capacity)
        : this(new PoolOptions<T> { Capacity = capacity })
    {
    }

    /// <summary>Creates a pool with a custom factory.</summary>
    public RingBufferPool(Func<T>? factory)
        : this(new PoolOptions<T> { Factory = factory })
    {
    }

    /// <summary>Creates a pool with a custom factory and capacity.</summary>
    public RingBufferPool(Func<T>? factory, int capacity)
        : this(new PoolOptions<T> { Factory = factory, Capacity = capacity })
    {
    }

    /// <summary>Creates a pool with a custom factory, reset callback, and capacity.</summary>
    public RingBufferPool(Func<T>? factory, Action<T>? onReturn, int capacity = PoolOptions<T>.DefaultCapacity)
        : this(new PoolOptions<T> { Factory = factory, OnReturn = onReturn, Capacity = capacity })
    {
    }

    /// <summary>Creates a pool from a full configuration.</summary>
    public RingBufferPool(PoolOptions<T> options)
    {
        _core = new PoolCore<T>(options);
        _ring = new Ring(PoolCore<T>.ValidateCapacity(options.Capacity));
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T Rent()
    {
        if (Volatile.Read(ref _ring).TryDequeue(out T? item))
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

        if (Volatile.Read(ref _ring).TryEnqueue(item))
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
    public void Clear() => Volatile.Read(ref _ring).Drain(_core.Destroy);

    /// <inheritdoc/>
    /// <remarks>Equivalent to <see cref="Clear"/>; the pool keeps no disposed state.</remarks>
    public void Dispose() => Clear();

    /// <inheritdoc/>
    public int Capacity => Volatile.Read(ref _ring).Capacity;

    /// <inheritdoc/>
    public int Count => Volatile.Read(ref _ring).Count;

    /// <summary>
    /// Replaces the ring with one of <paramref name="capacity"/> cells, keeping as
    /// many cached instances as fit and destroying the rest.
    /// </summary>
    /// <remarks>
    /// Resizing is a quiescent operation. Do not call it while another thread is
    /// renting or returning; the hot paths themselves never take a lock.
    /// </remarks>
    public void Resize(int capacity)
    {
        PoolCore<T>.ValidateCapacity(capacity);

        Ring previous = Volatile.Read(ref _ring);
        if (previous.Capacity == capacity)
            return;

        Ring replacement = new(capacity);
        while (previous.TryDequeue(out T? item))
        {
            if (item is null)
                continue;

            if (!replacement.TryEnqueue(item))
                _core.Destroy(item);
        }

        Volatile.Write(ref _ring, replacement);
    }

    /// <summary>
    /// The bounded ring itself. Producers and consumers each claim a position with a
    /// compare-and-swap and then wait for the cell sequence to match that position.
    /// </summary>
    private sealed class Ring
    {
        private readonly RingSlot<T>[] _slots;
        private readonly int _capacity;
        private readonly int _indexMask;
        private PaddedCounter _enqueuePosition;
        private PaddedCounter _dequeuePosition;

        internal Ring(int capacity)
        {
            _capacity = capacity;
            _indexMask = RingMath.GetIndexMask(capacity);
            _slots = capacity > 0 ? new RingSlot<T>[capacity] : Array.Empty<RingSlot<T>>();

            for (int i = 0; i < capacity; i++)
                _slots[i].Sequence = i;
        }

        internal int Capacity => _capacity;

        internal int Count
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
        internal bool TryEnqueue(T item)
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
                    return false;
                }

                position = Volatile.Read(ref _enqueuePosition.Value);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryDequeue(out T? item)
        {
            int capacity = _capacity;
            if (capacity == 0)
            {
                item = null;
                return false;
            }

            long position = Volatile.Read(ref _dequeuePosition.Value);

            while (true)
            {
                ref RingSlot<T> slot = ref _slots[RingMath.GetIndex(position, _indexMask, capacity)];
                long difference = Volatile.Read(ref slot.Sequence) - (position + 1);

                if (difference == 0)
                {
                    if (Interlocked.CompareExchange(
                            ref _dequeuePosition.Value, position + 1, position) == position)
                    {
                        item = slot.Item;
                        slot.Item = null;
                        Volatile.Write(ref slot.Sequence, position + capacity);
                        return item is not null;
                    }
                }
                else if (difference < 0)
                {
                    item = null;
                    return false;
                }

                position = Volatile.Read(ref _dequeuePosition.Value);
            }
        }

        internal void Drain(Action<T> destroy)
        {
            while (TryDequeue(out T? item))
            {
                if (item is not null)
                    destroy(item);
            }
        }
    }
}