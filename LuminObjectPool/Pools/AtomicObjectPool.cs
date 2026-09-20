namespace LuminObjectPool;

using System.Runtime.CompilerServices;

/// <summary>
/// A low-contention pool that combines a one-instance thread-local slot with a
/// fixed-capacity shared area built from independent atomic slots.
/// </summary>
/// <remarks>
/// The thread-local slot absorbs the common rent/return cycle with no atomic
/// operation at all, and drops to the shared area only when a thread holds more
/// than one instance. Every thread of a given <typeparamref name="T"/> shares the
/// same thread-local cell, so the owning pool is tracked explicitly and instances
/// never migrate between two pools of the same type. The shared capacity is
/// immutable and excludes the per-thread instances.
/// </remarks>
/// <typeparam name="T">The pooled reference type.</typeparam>
public sealed class AtomicObjectPool<T> : IPool<T> where T : class
{
    // Thread-local state is shared by every AtomicObjectPool<T> on a thread; the
    // owner reference keeps two pools of the same T from exchanging instances.
    [ThreadStatic]
    private static AtomicObjectPool<T>? t_owner;

    [ThreadStatic]
    private static T? t_item;

    [ThreadStatic]
    private static int t_rentCursor;

    [ThreadStatic]
    private static int t_storeCursor;

    private readonly PoolCore<T> _core;
    private readonly AtomicSlotPool<T> _central;

    /// <inheritdoc/>
    public int Capacity { get; }

    /// <summary>Creates a pool with the default capacity and no callbacks.</summary>
    public AtomicObjectPool()
        : this(PoolOptions<T>.Default)
    {
    }

    /// <summary>Creates a pool with the given capacity and no callbacks.</summary>
    public AtomicObjectPool(int capacity)
        : this(new PoolOptions<T> { Capacity = capacity })
    {
    }

    /// <summary>Creates a pool with a custom factory.</summary>
    public AtomicObjectPool(Func<T>? factory)
        : this(new PoolOptions<T> { Factory = factory })
    {
    }

    /// <summary>Creates a pool with a custom factory and capacity.</summary>
    public AtomicObjectPool(Func<T>? factory, int capacity)
        : this(new PoolOptions<T> { Factory = factory, Capacity = capacity })
    {
    }

    /// <summary>Creates a pool with a custom factory, reset callback, and capacity.</summary>
    public AtomicObjectPool(Func<T>? factory, Action<T>? onReturn, int capacity = PoolOptions<T>.DefaultCapacity)
        : this(new PoolOptions<T> { Factory = factory, OnReturn = onReturn, Capacity = capacity })
    {
    }

    /// <summary>Creates a pool from a full configuration.</summary>
    public AtomicObjectPool(PoolOptions<T> options)
    {
        Capacity = PoolCore<T>.ValidateCapacity(options.Capacity);
        _core = new PoolCore<T>(options);
        _central = new AtomicSlotPool<T>(Capacity);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T Rent()
    {
        if (ReferenceEquals(t_owner, this))
        {
            T? item = t_item;
            if (item is not null)
            {
                t_item = null;
                return item;
            }

            return RentSlow();
        }

        return RentAfterOwnerSwitchSlow();
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Return(T item)
    {
        if (item is null)
            throw new ArgumentNullException(nameof(item));

        // Kept inline instead of behind a helper so the thread-local fast path stays
        // a straight line; only the callbacks themselves remain indirect calls.
        Action<T>? onReturn = _core.OnReturn;
        if (onReturn is not null)
            onReturn(item);

        Func<T, bool>? returnPredicate = _core.ReturnPredicate;
        if (returnPredicate is not null && !returnPredicate(item))
        {
            _core.Destroy(item);
            return;
        }

        if (ReferenceEquals(t_owner, this))
        {
            if (t_item is null)
                t_item = item;
            else
                ReturnSlow(item);
            return;
        }

        ReturnAfterOwnerSwitchSlow(item);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Prewarm(int count)
    {
        for (int i = 0; i < count; i++)
            Return(_core.Create());
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Clears the shared area only. Instances cached by other threads stay alive
    /// until those threads call <see cref="ClearThreadLocalCache"/>.
    /// </remarks>
    public void Clear() => _central.Drain(_core.Destroy);

    /// <inheritdoc/>
    /// <remarks>Equivalent to <see cref="Clear"/>; the pool keeps no disposed state.</remarks>
    public void Dispose() => Clear();

    /// <summary>
    /// Destroys the instance this thread cached for <typeparamref name="T"/>, if any,
    /// using the owning pool's destroy callback. Call it before a long-lived thread
    /// stops using pools of this type.
    /// </summary>
    public static void ClearThreadLocalCache()
    {
        T? item = t_item;
        AtomicObjectPool<T>? owner = t_owner;

        t_item = null;
        t_owner = null;
        t_rentCursor = 0;
        t_storeCursor = 0;

        // The owning pool holds the destroy callback, so the instance is released
        // exactly the way every other eviction is. The thread-local instance only
        // ever exists next to its owner, so the pair is never half populated.
        if (item is not null && owner is not null)
            owner._core.Destroy(item);
    }

    /// <summary>Gets a non-linearizable, best-effort count of occupied shared slots.</summary>
    public int Count => _central.GetApproximateCount();

    /// <summary>
    /// Returns a non-linearizable, best-effort occupancy bitmap of the shared slots.
    /// Per-thread instances are excluded and no instance references are exposed.
    /// </summary>
    public bool[] GetDiagnosticOccupancyBitmap() => _central.GetOccupancyBitmap();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private T RentSlow()
    {
        if (_central.TryTake(ref t_rentCursor, out T? item))
            return item!;

        return _core.Create();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ReturnSlow(T item)
    {
        if (!_central.TryStore(ref t_storeCursor, item))
            _core.Destroy(item);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private T RentAfterOwnerSwitchSlow()
    {
        SwitchThreadLocalOwner();
        return RentSlow();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ReturnAfterOwnerSwitchSlow(T item)
    {
        SwitchThreadLocalOwner();

        // Destroying the displaced instance can run arbitrary user code, including a
        // reentrant pool operation, so the thread-local state is re-checked first.
        if (ReferenceEquals(t_owner, this) && t_item is null)
            t_item = item;
        else
            ReturnSlow(item);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void SwitchThreadLocalOwner()
    {
        AtomicObjectPool<T>? previousOwner = t_owner;
        T? displacedItem = t_item;

        t_item = null;
        t_owner = this;

        int threadHash = unchecked(Environment.CurrentManagedThreadId * (int)0x9E3779B9);
        t_rentCursor = threadHash;
        t_storeCursor = threadHash;

        // The displaced instance was already reset and accepted by the other pool.
        if (displacedItem is not null && previousOwner is not null)
            previousOwner.StoreCentralOrDispose(displacedItem);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void StoreCentralOrDispose(T item)
    {
        if (!_central.TryStore(ref t_storeCursor, item))
            _core.Destroy(item);
    }
}