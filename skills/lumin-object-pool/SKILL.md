---
name: lumin-object-pool
description: 用 LuminObjectPool 写对象池代码，覆盖池选型、委托式配置、线程模型契约与常见陷阱。当任务涉及 LuminObjectPool、Rent/Return、AtomicObjectPool、RingBufferPool、MpscPool、SpmcPool、SpscPool、ThreadUnsafePool、PoolOptions 或 ObjectPoolBuilder 时使用。不用于实现通用池化逻辑。
---

# LuminObjectPool 使用手册

命名空间 `LuminObjectPool`。程序集目标框架 `netstandard2.1;net8.0;net9.0`，Unity（IL2CPP）可用。

面向上手的完整介绍见仓库根的 `README.md`；本文件只讲写代码时需要遵守的规则和容易写错的地方。

## 一、先选池，再写代码

默认选 `AtomicObjectPool<T>`。只有在调用方能明确保证线程角色时才选专用变体。

| 场景 | 选择 |
| --- | --- |
| 单线程独占的复用热点 | `ThreadUnsafePool<T>` |
| 通用、跨线程、低争用（默认） | `AtomicObjectPool<T>` |
| 需要 FIFO、容量可运行时调整 | `RingBufferPool<T>` |
| 多线程产出、固定一个线程回收 | `MpscPool<T>` |
| 固定一个线程补充、多线程取用 | `SpmcPool<T>` |
| 恰好一进一出、要求延迟最低 | `SpscPool<T>` |

所有变体实现同一个接口：

```csharp
public interface IPool<T> : IDisposable where T : class
{
    T Rent();
    void Return(T item);
    int Count { get; }       // best-effort，见第五节
    int Capacity { get; }
    void Clear();
    void Prewarm(int count);
}
```

## 二、配置方式

`T` 不需要实现任何接口，也不需要 `IDisposable`。四种写法等价，按场景挑一种。

```csharp
// 1. 全默认：工厂走 Activator.CreateInstance<T>()，不重置，容量 16
var pool = new AtomicObjectPool<Foo>();

// 2. 委托式（日常首选）
var pool = new AtomicObjectPool<Foo>(static () => new Foo(), static f => f.Reset());

// 3. 需要容量 / 拒绝回收 / 自定义销毁
var pool = new RingBufferPool<Foo>(new PoolOptions<Foo>
{
    Factory = static () => new Foo(4096),
    OnReturn = static f => f.Reset(),
    ReturnPredicate = static f => f.Length <= 64 * 1024,
    OnDestroy = static f => f.Release(),
    Capacity = 64,
});

// 4. 同一份配置构建多种变体（需要替换池实现时用）
var builder = ObjectPoolBuilder<Foo>.Create()
    .WithFactory(static () => new Foo())
    .WithReturn(static f => f.Reset())
    .WithCapacity(32)
    .WithInitialCount(8);

var atomic = builder.BuildAtomic();
var ring = builder.BuildRingBuffer();
```

委托构造的重载对所有变体一致：`()`、`(capacity)`、`(factory)`、`(factory, capacity)`、
`(factory, onReturn, capacity = 16)`、`(PoolOptions<T>)`。

`PoolOptions<T>` 的每个成员都可选，`Capacity` 默认 16（`PoolOptions<T>.DefaultCapacity`）。
构造函数里的 `onReturn` 参数类型是 `Action<T>`，只负责重置。"这个实例不值得缓存"是另一件事，
用 `PoolOptions.ReturnPredicate` 或 `ObjectPoolBuilder.WithReturnPredicate(Func<T, bool>)` 表达——
`Action<T>` 与 `Func<T, bool>` 被刻意拆成两个名字，避免 `x => x.Reset()` 这类 lambda 产生重载歧义。

## 三、回收链路的责任划分

归还一个实例时，池按固定顺序执行：

```text
OnReturn(item) → ReturnPredicate(item) → 缓存 或 销毁
```

- `OnReturn`：只重置状态。**不要在这里销毁对象**，也不要再调用池。
- `ReturnPredicate`：返回 `false` 表示"这个实例不值得缓存"，由池负责销毁。
- `OnDestroy`：为 `null` 时，若 `T` 实现 `IDisposable` 就调用 `Dispose()`，否则直接丢弃且不报错。

只有池会销毁实例，触发点共三个：`ReturnPredicate` 返回 false、缓存已满、显式
`Clear()` / `Dispose()`。被拒绝或超出容量的实例一定恰好销毁一次。

成功 `Return` 之后不要再持有或使用该实例；`Rent` 返回的实例在归还前只属于调用方。

## 四、线程模型是契约，不是建议

违反下列约束**不会抛异常**，而是丢实例或数据错乱。改代码前先确认调用方的线程角色。

- `ThreadUnsafePool<T>`：同一个池只能由一个线程 `Rent` 和 `Return`。
- `AtomicObjectPool<T>`：任意线程，无额外约束。
- `RingBufferPool<T>`：任意线程。
- `MpscPool<T>`：`Return` 可多线程，`Rent` **只能来自同一个线程**。
- `SpmcPool<T>`：`Rent` 可多线程，`Return` **只能来自同一个线程**。
- `SpscPool<T>`：`Rent` 一个线程、`Return` 另一个线程，各一个。

另外这些方法属于静默操作，调用时不得有其他线程正在 `Rent` / `Return`：

- 所有并发变体的 `Clear()` / `Dispose()`
- `RingBufferPool<T>.Resize(int)`

`MpscPool<T>.Clear()` 额外要求跑在消费者线程上。热路径本身不加锁，所以这些约束没有运行时检查。

## 五、`AtomicObjectPool<T>` 的容量语义

这是最容易误判的一处：**`Capacity` 只约束共享原子槽，每个线程额外持有一个线程局部槽**。

- 单线程下实际保留 `Capacity + 1` 个实例。
- `Capacity = 0` 时其他变体不缓存任何实例，但 `AtomicObjectPool<T>` 仍会保留一个线程局部实例。
- `Count` 与 `GetDiagnosticOccupancyBitmap()` 只反映共享区域，**不含**线程局部实例。

需要严格不缓存任何实例时，改用 `RingBufferPool<T>`、`MpscPool<T>`、`SpmcPool<T>`、`SpscPool<T>`
或 `ThreadUnsafePool<T>`。

长生命周期线程（线程池线程、自建常驻线程）停止使用某类型的池前，必须显式释放：

```csharp
AtomicObjectPool<Foo>.ClearThreadLocalCache();
```

它用所属池的 `OnDestroy` 销毁当前线程缓存的实例。`Clear()` / `Dispose()` 只清理共享区域，
不会回收其他线程的线程局部实例。

同一线程上的多个 `AtomicObjectPool<T>` 通过 owner 隔离，不会互相串对象；切换 owner 时被顶替的
实例会回到原池，原池满了才销毁。这一过程会调用 `OnDestroy`，所以要保证 `OnDestroy` 可重入。

## 六、其它行为细节

- **`Prewarm(n)`**：用工厂建实例后直接归还，因此 `OnReturn` 会在从未被租借过的实例上被调用。
  重置回调必须对"全新实例"也成立（幂等）。
- **`Count`**：best-effort，读取时可能已经过期，禁止据此决定生命周期（不要用它判断"该不该销毁"）。
- **`Factory` 为 `null`**：走 `Activator.CreateInstance<T>()`，要求 `T` 有公开无参构造函数，
  否则抛 `MissingMethodException`。热路径请显式传工厂委托。
- **`Return(null)`** 抛 `ArgumentNullException`，其余入参不做校验。
- **`Dispose()` 等价于 `Clear()`**：池不记录 disposed 状态，Dispose 之后再用不会抛异常，
  只会继续正常工作。要真正释放，必须自己保证不再使用。

## 七、性能写法

委托式配置让 JIT 无法把 `OnReturn` 完全内联，这是换取"无需 Policy 类型"的代价：归还路径每次
多一次间接调用。回调体越重，这次开销占比越小；不配 `OnReturn` 时没有任何调用，反而比 v1 的
`static abstract` 方案更快。

写热路径时注意两点：

1. **把池存成具体类型，不要存成 `IPool<T>`**。接口派发会阻止 `Rent` / `Return` 内联，让本应
   只有几条指令的线程局部快路径被派发开销淹没。只有在非热点、或需要在多种变体间切换时才用
   `IPool<T>`。
2. **不需要重置就别配 `OnReturn`**。省下的是每次归还的一次间接调用。

## 八、构建

```text
dotnet build
dotnet pack -c Release
```

改动库代码后，必须保证 `netstandard2.1` 目标仍然编译通过——Unity 与旧运行时的可用性完全依赖
它，破坏了不会在 `net8.0` 上暴露。`T` 上不要加 `where T : IDisposable` 之类的额外约束，也不要
在库里引用目标框架新增的 BCL API 而不加 `#if`。

## 九、从 v1 迁移

| v1 | v2 |
| --- | --- |
| `LuminPack.Utility.ObjectPool<T>` | `LuminObjectPool.AtomicObjectPool<T>` |
| `T : IPooledObjectPolicy<T>, IDisposable` | 无约束（`T : class`） |
| `static T Create()` / `static bool Return(T)` | `Factory` / `OnReturn` / `ReturnPredicate` |
| `new ObjectPool<T>(maxSize: 32)` | `new AtomicObjectPool<T>(capacity: 32)` |
| `ApproximateCentralCount` | `Count` |
| `ObjectPool<T>.ClearThreadLocalCache()` | `AtomicObjectPool<T>.ClearThreadLocalCache()` |

`GetDiagnosticOccupancyBitmap()` 名字未变。`ObjectPool<T>` 与 `IPooledObjectPolicy<T>` 在 v2 中已删除，
引用它们的旧代码必须改写而不是加别名。