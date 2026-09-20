namespace LuminObjectPool;

/// <summary>
/// Configures a pool once and builds any of the available variants from it, so the
/// same creation, reset, and destroy callbacks can be reused across strategies.
/// </summary>
/// <typeparam name="T">The pooled reference type.</typeparam>
/// <example>
/// <code>
/// var pool = ObjectPoolBuilder&lt;Foo&gt;.Create()
///     .WithFactory(static () =&gt; new Foo())
///     .WithReturn(static foo =&gt; foo.Reset())
///     .WithCapacity(32)
///     .BuildAtomic();
/// </code>
/// </example>
public sealed class ObjectPoolBuilder<T> where T : class
{
    private Func<T>? _factory;
    private Action<T>? _onReturn;
    private Func<T, bool>? _returnPredicate;
    private Action<T>? _onDestroy;
    private int _capacity = PoolOptions<T>.DefaultCapacity;
    private int _initialCount;

    private ObjectPoolBuilder()
    {
    }

    /// <summary>Creates an empty builder.</summary>
    public static ObjectPoolBuilder<T> Create() => new();

    /// <summary>Sets the instance factory.</summary>
    public ObjectPoolBuilder<T> WithFactory(Func<T> factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    /// <summary>Sets the reset callback invoked before an instance is cached.</summary>
    public ObjectPoolBuilder<T> WithReturn(Action<T> onReturn)
    {
        _onReturn = onReturn ?? throw new ArgumentNullException(nameof(onReturn));
        return this;
    }

    /// <summary>
    /// Sets a predicate that decides whether a reset instance may be cached.
    /// Returning false destroys the instance.
    /// </summary>
    public ObjectPoolBuilder<T> WithReturnPredicate(Func<T, bool> predicate)
    {
        _returnPredicate = predicate ?? throw new ArgumentNullException(nameof(predicate));
        return this;
    }

    /// <summary>Sets the callback used to destroy instances the pool no longer keeps.</summary>
    public ObjectPoolBuilder<T> WithDestroy(Action<T> onDestroy)
    {
        _onDestroy = onDestroy ?? throw new ArgumentNullException(nameof(onDestroy));
        return this;
    }

    /// <summary>Sets the maximum number of retained instances.</summary>
    public ObjectPoolBuilder<T> WithCapacity(int capacity)
    {
        _capacity = PoolCore<T>.ValidateCapacity(capacity);
        return this;
    }

    /// <summary>Creates and caches this many instances while building.</summary>
    public ObjectPoolBuilder<T> WithInitialCount(int count)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));

        _initialCount = count;
        return this;
    }

    /// <summary>Builds the single-threaded pool, which is the fastest variant.</summary>
    public ThreadUnsafePool<T> BuildThreadUnsafe() => Complete(new ThreadUnsafePool<T>(Options()));

    /// <summary>Builds the pool combining a thread-local slot with shared atomic slots.</summary>
    public AtomicObjectPool<T> BuildAtomic() => Complete(new AtomicObjectPool<T>(Options()));

    /// <summary>Builds the bounded, resizable multi-producer multi-consumer ring.</summary>
    public RingBufferPool<T> BuildRingBuffer() => Complete(new RingBufferPool<T>(Options()));

    /// <summary>Builds the multi-producer single-consumer ring.</summary>
    public MpscPool<T> BuildMpsc() => Complete(new MpscPool<T>(Options()));

    /// <summary>Builds the single-producer multi-consumer ring.</summary>
    public SpmcPool<T> BuildSpmc() => Complete(new SpmcPool<T>(Options()));

    /// <summary>Builds the single-producer single-consumer ring.</summary>
    public SpscPool<T> BuildSpsc() => Complete(new SpscPool<T>(Options()));

    private PoolOptions<T> Options() => new()
    {
        Factory = _factory,
        OnReturn = _onReturn,
        ReturnPredicate = _returnPredicate,
        OnDestroy = _onDestroy,
        Capacity = _capacity,
    };

    private TPool Complete<TPool>(TPool pool) where TPool : class, IPool<T>
    {
        if (_initialCount > 0)
            pool.Prewarm(_initialCount);

        return pool;
    }
}