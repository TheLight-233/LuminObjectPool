# LuminObjectPool

面向 .NET 8+、.NET Standard 2.1 与 Unity 的高性能通用对象池库，提供多种可选的并发策略。

v2 不再需要编写 Policy 类型，直接用委托配置即可；所有池共享同一套创建 / 重置 / 销毁
回调，按需选择并发策略。

## 安装

```text
dotnet add package LuminObjectPool
```

## 快速开始

```csharp
using LuminObjectPool;

// 最简：默认工厂 + 不重置，容量 16
var pool = new AtomicObjectPool<Foo>();

// 委托式：工厂 + 重置回调（无 Policy 类型，无接口实现）
var pool = new AtomicObjectPool<Foo>(static () => new Foo(), static foo => foo.Reset());

// 附带容量
var pool = new AtomicObjectPool<Foo>(static () => new Foo(), static foo => foo.Reset(), capacity: 32);

Foo item = pool.Rent();
pool.Return(item);
```

`T` 不再要求实现 `IDisposable`，也不要求实现任何接口。

## 缓存池变体

| 类型 | 并发模型 | 数据结构 | 适用场景 |
| --- | --- | --- | --- |
| `ThreadUnsafePool<T>` | 无（单线程） | 数组 LIFO 栈 | 最高性能。单线程独占使用 |
| `AtomicObjectPool<T>` | MPMC 无锁 | TLS 单槽 + 固定容量原子槽 | 通用默认选择，低争用 |
| `RingBufferPool<T>` | MPMC 有界 | sequence number 环形队列 | 需要 FIFO、容量可 `Resize` |
| `MpscPool<T>` | 多生产者 / 单消费者 | 有界环，生产侧 CAS | 多线程产出，一个线程回收 |
| `SpmcPool<T>` | 单生产者 / 多消费者 | 有界环，消费侧 CAS | 一个线程补充，多线程取用 |
| `SpscPool<T>` | 单生产者 / 单消费者 | 双游标环 | 跨线程最快，无 CAS |

所有变体都实现 `IPool<T>`：

```csharp
public interface IPool<T> : IDisposable where T : class
{
    T Rent();
    void Return(T item);
    int Count { get; }
    int Capacity { get; }
    void Clear();
    void Prewarm(int count);
}
```

## 配置

`PoolOptions<T>` 的每个成员都是可选的，`Capacity` 默认为 16。

```csharp
var pool = new RingBufferPool<Buffer>(new PoolOptions<Buffer>
{
    Factory = static () => new Buffer(4096),
    OnReturn = static buffer => buffer.Reset(),
    ReturnPredicate = static buffer => buffer.Length <= 64 * 1024, // false => 销毁，不缓存
    OnDestroy = static buffer => buffer.Release(),
    Capacity = 64,
});
```

- `Factory`：为 null 时使用 `Activator.CreateInstance<T>()`（需要公开无参构造函数）。
  热路径建议显式传入工厂委托。
- `OnReturn`：缓存前调用，用于重置状态。**不要**在这里销毁对象。
- `ReturnPredicate`：在 `OnReturn` 之后调用，返回 `false` 表示对象不适合缓存，由池负责销毁。
- `OnDestroy`：为 null 时，若 `T` 实现 `IDisposable` 则调用 `Dispose()`，否则直接丢弃。

### 链式构建

同一份配置可以构建任意变体：

```csharp
var builder = ObjectPoolBuilder<Foo>.Create()
    .WithFactory(static () => new Foo())
    .WithReturn(static foo => foo.Reset())
    .WithReturnPredicate(static foo => foo.CanReuse)
    .WithDestroy(static foo => foo.Release())
    .WithCapacity(32)
    .WithInitialCount(8);

ThreadUnsafePool<Foo> single = builder.BuildThreadUnsafe();
AtomicObjectPool<Foo> atomic = builder.BuildAtomic();
RingBufferPool<Foo> ring = builder.BuildRingBuffer();
MpscPool<Foo> mpsc = builder.BuildMpsc();
SpmcPool<Foo> spmc = builder.BuildSpmc();
SpscPool<Foo> spsc = builder.BuildSpsc();
```

### 泛型约束与 Unity

委托式配置在 `netstandard2.1` 上同样可用，不需要 `static abstract` 接口成员，
因此 Unity（IL2CPP）与旧版运行时无需额外写法：

```csharp
var pool = new ThreadUnsafePool<Buffer>(static () => new Buffer(), static buffer => buffer.Reset(), 32);
```

## 行为与语义

### 容量

`Capacity` 是池保留实例数的上界。达到上界后归还的实例会被销毁（走 `OnDestroy`）。

`AtomicObjectPool<T>` 的容量只约束共享原子槽：每个线程额外持有一个线程局部槽，因此
单线程下实际保留数为 `Capacity + 1`。该线程局部槽不出现在 `Count` 与占用位图中。

容量为 0 时其他变体不缓存任何实例；`AtomicObjectPool<T>` 仍会保留一个线程局部实例。

### 销毁时机

只有池会销毁实例，共三种情况：`ReturnPredicate` 返回 `false`、缓存已满、
显式调用 `Clear()` / `Dispose()`。被拒绝或超出容量的实例一定恰好销毁一次。

### 重置回调与 `Prewarm`

`Prewarm(n)` 通过工厂创建实例后直接归还，因此 `OnReturn` 会在从未被租借过的实例上调用。
重置回调应当是幂等的。

### 线程局部缓存

`AtomicObjectPool<T>` 是唯一带线程局部状态的变体。同一线程上的多个
`AtomicObjectPool<T>` 通过 owner 隔离，不会交换实例；切换 owner 时，被顶替的实例会回到
原池，原池已满时才销毁。

长生命周期线程停止使用某类型的池前，调用：

```csharp
AtomicObjectPool<Foo>.ClearThreadLocalCache();
```

它会用所属池的 `OnDestroy` 回调销毁当前线程缓存的实例。`Clear()` 与 `Dispose()` 只清理
共享区域，不触及其他线程的线程局部实例。

### 清空是静默操作

对并发变体调用 `Clear()` / `Dispose()` / `RingBufferPool<T>.Resize()` 时，不得有其它线程
正在 `Rent` / `Return`。热路径本身不加锁。

### 空引用

`Return(null)` 抛出 `ArgumentNullException`。其余入参不做校验。

## 诊断

- `Count`：best-effort 的缓存实例数，读取时可能已过期，不应据此决定生命周期。
- `AtomicObjectPool<T>.GetDiagnosticOccupancyBitmap()`：共享槽占用位图，非线性一致，
  不暴露实例引用。

## 选型建议

- 单线程热点：`ThreadUnsafePool<T>`。
- 通用、跨线程、低争用：`AtomicObjectPool<T>`。
- 需要 FIFO、容量可调、跨线程公平：`RingBufferPool<T>`。
- 明确的单 / 多角色分工：`MpscPool<T>` / `SpmcPool<T>` / `SpscPool<T>`。
- 不确定时：先选 `AtomicObjectPool<T>`。

## 构建

```text
dotnet build
dotnet pack -c Release
```

## 从 v1 迁移

| v1 | v2 |
| --- | --- |
| `LuminPack.Utility.ObjectPool<T>` | `LuminObjectPool.AtomicObjectPool<T>` |
| `T : IPooledObjectPolicy<T>, IDisposable` | 无约束（`T : class`） |
| `static T Create()` / `bool Return(T)` | `Factory` / `OnReturn` / `ReturnPredicate` |
| `new ObjectPool<T>(maxSize: 32)` | `new AtomicObjectPool<T>(capacity: 32)` |
| `ApproximateCentralCount` | `Count` |
| `GetDiagnosticOccupancyBitmap()` | 同名保留 |
| `ObjectPool<T>.ClearThreadLocalCache()` | `AtomicObjectPool<T>.ClearThreadLocalCache()` |