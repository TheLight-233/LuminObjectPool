# LuminObjectPool

#### 基于C#的极致性能物品缓存池

#### 使用方法：

1.  导入cs代码文件或引用dll
2.  .Net8以上，存入物品池的Class或Struct需要继承IPooledObjectPolicy\<T>并实现静态方法
3.  .Net Standard2.1版本，需要单例定义一个类继承 IPooledObjectPolicy\<T>，以此作为池的策略注入到ObjectPool。这与微软的Microsoft.Extension.ObjectPool插件相同

    ***

    #### 代码示例：

```cs
//物品池声明
#if NET8_0_OR_GREATER
    private static readonly ObjectPool<LuminTaskSource> Pool = new();
#else
    private static readonly ObjectPool<LuminTaskSource> Pool = new(new PoolPolicy());
#endif
```

```cs
#if NET8_0_OR_GREATER
    static Item IPooledObjectPolicy<Item>.Create() => new(); //创建策略

    static bool IPooledObjectPolicy<Item>.Return(Item obj) => true; //Return策略
#else
    private sealed class PoolPolicy : IPooledObjectPolicy<Item>
    {
        public Item Create() => new();

        public bool Return(Item obj)
        {
            obj.Dispose();
            return true;
        }
    }
#endif
```
