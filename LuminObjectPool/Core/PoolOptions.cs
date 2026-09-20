namespace LuminObjectPool;

/// <summary>
/// Describes how a pool creates, resets, and destroys instances. Every member is
/// optional, so <c>new AtomicObjectPool&lt;Foo&gt;()</c> already works with the
/// defaults.
/// </summary>
/// <typeparam name="T">The pooled reference type.</typeparam>
/// <remarks>
/// The pool copies these values when it is constructed, so mutating the options
/// afterwards does not reconfigure an existing pool.
/// </remarks>
public sealed class PoolOptions<T> where T : class
{
    /// <summary>The capacity applied when none is specified.</summary>
    public const int DefaultCapacity = 16;

    /// <summary>
    /// Creates new instances. When null, <see cref="Activator.CreateInstance{T}"/>
    /// is used, which requires a public parameterless constructor. Supply a factory
    /// delegate to avoid the reflection lookup on every cache miss.
    /// </summary>
    public Func<T>? Factory { get; set; }

    /// <summary>
    /// Resets an instance before it is cached. Invoked on every return the pool
    /// keeps. Do not destroy the instance here; the pool owns lifetime decisions.
    /// </summary>
    public Action<T>? OnReturn { get; set; }

    /// <summary>
    /// Decides whether a reset instance may be cached. Returning false destroys the
    /// instance. Applied after <see cref="OnReturn"/>.
    /// </summary>
    public Func<T, bool>? ReturnPredicate { get; set; }

    /// <summary>
    /// Destroys an instance the pool no longer keeps. When null, the instance is
    /// disposed if it implements <see cref="IDisposable"/>, and dropped otherwise.
    /// </summary>
    public Action<T>? OnDestroy { get; set; }

    /// <summary>The maximum number of instances the pool retains. Zero caches nothing.</summary>
    public int Capacity { get; set; } = DefaultCapacity;

    /// <summary>Gets a configuration with the default capacity and no callbacks.</summary>
    public static PoolOptions<T> Default => new();
}