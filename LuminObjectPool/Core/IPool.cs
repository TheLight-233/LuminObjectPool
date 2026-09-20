namespace LuminObjectPool;

/// <summary>
/// A bounded cache of reusable instances. Each implementation trades safety for
/// throughput differently; see the concrete pool types for the exact contract.
/// </summary>
/// <typeparam name="T">The pooled reference type.</typeparam>
public interface IPool<T> : IDisposable where T : class
{
    /// <summary>
    /// Gets a cached instance, or creates one through the configured factory when
    /// the cache is empty.
    /// </summary>
    T Rent();

    /// <summary>
    /// Resets <paramref name="item"/> and caches it. The instance is destroyed when
    /// the pool rejects it or the cache is full.
    /// </summary>
    void Return(T item);

    /// <summary>
    /// Gets a best-effort count of cached instances. The value may already be stale
    /// when it is observed, so it must not be used to make lifetime decisions.
    /// </summary>
    int Count { get; }

    /// <summary>Gets the maximum number of instances the pool retains.</summary>
    int Capacity { get; }

    /// <summary>
    /// Destroys every cached instance. For concurrent pools this is a quiescent
    /// operation and must not run while other threads are using the pool.
    /// </summary>
    void Clear();

    /// <summary>
    /// Creates and caches up to <paramref name="count"/> instances so the first
    /// rentals do not pay for construction.
    /// </summary>
    void Prewarm(int count);
}