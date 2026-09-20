namespace LuminObjectPool;

using System.Runtime.CompilerServices;

/// <summary>
/// Resolves the delegate set every pool variant shares. Instances are immutable
/// after construction so each delegate call site stays monomorphic, which lets the
/// JIT devirtualize and inline the user callbacks.
/// </summary>
internal sealed class PoolCore<T> where T : class
{
    private static readonly Func<T> s_defaultFactory = static () => Activator.CreateInstance<T>();

    private static readonly Action<T> s_defaultDestroy = static item =>
    {
        if (item is IDisposable disposable)
            disposable.Dispose();
    };

    internal readonly Func<T> Factory;
    internal readonly Action<T>? OnReturn;
    internal readonly Func<T, bool>? ReturnPredicate;
    internal readonly Action<T> OnDestroy;

    internal PoolCore(PoolOptions<T> options)
    {
        Factory = options.Factory ?? s_defaultFactory;
        OnReturn = options.OnReturn;
        ReturnPredicate = options.ReturnPredicate;
        OnDestroy = options.OnDestroy ?? s_defaultDestroy;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal T Create() => Factory();

    /// <summary>
    /// Runs the reset callback and the rejection predicate. Returns false when the
    /// instance must be destroyed instead of cached.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryReturn(T item)
    {
        OnReturn?.Invoke(item);
        return ReturnPredicate is null || ReturnPredicate(item);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Destroy(T item) => OnDestroy(item);

    /// <summary>Validates a capacity supplied by the caller.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int ValidateCapacity(int capacity)
    {
        if (capacity < 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        return capacity;
    }
}