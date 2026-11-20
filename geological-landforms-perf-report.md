# Geological Landforms — 世界地图重建性能报告

> 目的：为分叉一个更快的版本提供依据。所有耗时都来自实机测量，标注 `(推测)` 的部分是结构性推算，未单独插桩。
>
> **当前状态**：① 并行填充已实现；水格短路和主要分配清理已实现。以下"已实现"项均通过 1.7 / net10 的 Release 编译（0 警告 0 错误），但**尚未重跑插桩测量**，因此新耗时仍按预期区间标注。

## 1. 结论

1. 读档后第一次打开世界地图，`WorldLayer_Landforms` **约 3.5 秒**（插桩下 4.19 s），占整次重建的 40~46%，是单层最大开销。
2. 这 3.5 秒里 **约 91% 是 GL 自己的纯 CPU 代码**，不是 I/O、不是资源加载、不是 Unity 对象创建——所以它可以被并行，而且不需要改任何算法。
3. **并行已落地**：在 `WorldLayer_Landforms.Regenerate` 开头对全部 tile 做一次 `Parallel.For` 预热缓存，同样代码、同样结果，预期 **3.5 s → 0.4~0.7 s**。
4. 并行之后新的瓶颈是**分配/GC**和**水格上的无效计算**，而不是单 tile 的算术。已针对性做了两项：水 biome 的 landform 解析整体短路、热路径上的 LINQ 分配清理（详见 §5）。

## 2. 测量环境

| | |
|---|---|
| 世界 | 295,732 个 surface tile，838 个活动 mod，约 30 个 biome |
| CPU | Ryzen 9 7945HX，16 核 32 线程 |
| 测量工具 | 自建 Harmony profiler（按 step 计时 + 按 self time 聚合 + 间隙归因） |
| 对照 | 无插桩下同层 3,509.9 ms（另一优化 mod 独立记录），插桩下 4,191.9 ms |

插桩膨胀系数约 1.2×，下文凡是"插桩 ms"都按此换算。

## 3. 数据（并行前）

```
WorldLayer_Landforms        4,191.9 ms   self 3,813.8 ms (91.0%)   分配 1,100 MB
  └ WorldGrid.GetTileCenter   377.1 ms   1,082,140 次  (0.35 µs/次)
```

折算到单 tile：**约 12 µs 计算、3.9 KB 分配**。1.1 GB 全是短命对象，整次重建触发 128 次 gen0。

`1,082,140 / 295,732 = 3.66`——平均每个 tile 只取了 3.66 次邻居中心，因为水 tile 在 `DetermineTopology` 开头就 `return` 了；陆地 tile 是 7 次。

注意：`GetTileCenter` 的实现只是对 6 个顶点求平均，0.35 µs/次大概率被插桩放大，真实单次成本远低于此。这也是没有对它做缓存的原因。

## 4. 成本结构

`WorldTileInfo.Get(tileId)` 每个 tile 做三件事：

| 阶段 | 每 tile 规模 | 复杂度 |
|---|---|---|
| `DetermineTopology` | 6 个邻居：`GetHeadingFromTo`、`CoastTypeFromTile`、`BiomeTransition.IsTransition`、4 个 `List<Rot6>` 增删 | O(6) |
| `DetermineLandforms` | 遍历 `LandformManager.LandformLayers` × 层内所有 landform，逐个 `GetCommonnessForTile` | **O(L)** |
| `DetermineBiomeVariants` | `tileData == null` 时遍历 `DefDatabase<BiomeVariantDef>.AllDefsListForReading` | **O(V)** |

关键点：`LandformData._tileData` 里只有玩家手动改过的那几十格，**其余 29.5 万格的 `tileData` 都是 null**，因此几乎每个 tile 都走"遍历全部 landform + 全部 variant"的分支。这个 O(L)+O(V) 是硬性的——候选集取决于本格的 biome / hilliness / 温度 / 海岸，不同格答案不同，**不能像纹理路径那样缓存成有限几条**。

补充（这次实测代码后才确认）：`DetermineTopology` 对水格确实提前返回，但 `DetermineLandforms` **没有**对应的提前返回，水格依然会跑完整层循环，最后被 `biomeProps.AllowsLandform` 全部否掉。典型世界水格占 40~60%，这部分在并行前是纯浪费。

`_cache` 是 `WorldTileInfoPrimer[]`，按 tileId 索引，每格独立写入。`_tsc_*` 那 8 个临时 `List` 全部标了 `[ThreadStatic]`——作者当初就按可重入写的，这是并行方案成立的前提。

## 5. 已实现的改动

### ① 并行填充缓存（已实现）

`WorldLayer_Landforms.Regenerate` 在逐格绘制前调用 `WorldTileInfo.WarmCache()`：

```csharp
var threads = Math.Min(Environment.ProcessorCount, 16);
var chunk = (count + threads - 1) / threads;

Interlocked.Increment(ref _parallelFillDepth);
try
{
    Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, worker =>
    {
        var from = worker * chunk;
        var to = Math.Min(count, from + chunk);
        for (var i = from; i < to; i++) Get(i);
    });
}
finally { Interlocked.Decrement(ref _parallelFillDepth); }
```

配套处理：

- 只在 `_cache != null` 且长度覆盖 `grid.TilesCount` 时执行；用 `(World 实例, _validCacheVersion)` 标记防止重复填充，缓存失效后自动重新武装；
- 填充中途版本变化则丢弃结果、不记标记；
- 并发期间 `Get` 的 catch **不再** `InvalidateCache()`（改为只返回兜底对象），避免把其他线程刚写好的条目整表作废；
- `world.LandformData()` 在主线程先预热，worker 只读。

### ② 水 biome 整体短路（已实现）

`DetermineLandforms` 开头新增：当无手动 `tileData`、无 `overrideLandforms`、且该 biome 同时不允许 landform、不允许 biome transition、也没有白名单时，结果必然为空，直接返回。

```csharp
if (tileData == null && biomeProps.overrideLandforms == null
    && !biomeProps.AllowLandforms && !biomeProps.AllowBiomeTransitions
    && biomeProps.allowedLandforms == null)
{
    info.Landforms = null;
    return;
}
```

这正好覆盖 Ocean / Lake 以及同类的 mod 水体 biome（如 Biomes! Islands、Terra Project、Cave Biome）。同时跳过了 `info.WorldObject`——它是 `world.worldObjects.MapParentAt(tileId)`，在 `WorldObjectsHolder` 里是 **O(世界对象数)** 的线性扫描，这个世界的对象数量可能上百。陆地格和有过手动编辑的格子行为完全不变。

预计这是并行之后单项收益最大的一处 (推测)：水格占 40~60%，每格省下约 L 次条件求值。

### ③ 分配 / LINQ 清理（已实现）

并行不改变分配量，1.1 GB 的 churn 和 128 次 gen0 仍在，所以这一项在并行后相对更值钱：

| 位置 | 原实现 | 现实现 |
|---|---|---|
| `DetermineLandforms` 层选择 | `commonness.Sum()`（`List<float>` 装箱枚举） | 手写累加，顺序不变 |
| `DetermineLandforms` 尾部 | `landforms.SelectMany(lf => lf.ApplyLayers).ToList()` | 用 `initialCount` 快照的双层 `for`，语义一致且省 2 个分配 |
| `NodeUIWorldTileReq.BiomeCondition.GetScore` | 两次 `FirstOrDefault(e => e.BiomeDef == tile.Biome.defName)`，lambda 捕获 `tile` → 每次一个闭包对象 | 两段 `for`，先精确匹配再取 null 兜底项 |
| `ExtensionUtils.LargestRiverLink/LargestRoadLink` | `OrderByDescending(...).FirstOrDefault()`（排序 + 分配） | 单次线性取最大值（严格 `>`，平局取靠前者，和稳定排序一致） |

`BiomeCondition` 和 river/road 条件都会被最多 43 个 landform 逐格调用，属于 `DetermineLandforms` 内层循环的一部分。

## 6. 仍待处理

### `ExtensionPoint.Apply` 空列表快路径 —— 被外部 DLL 卡住

`LunarFramework.Utility.ExtensionPoint.Apply` 的当前实现（1.6 和 1.7 的组件 DLL 已确认相同）：

```csharp
return Extensions.Aggregate(initialValue, (current, extension) => extension.Func(context, current));
```

`Aggregate` 走 `IEnumerable<T>` 重载把 `List<Extension>.Enumerator` 装箱，lambda 又捕获 `context` 生成 display class——**即使一个订阅者都没有，每次调用仍分配 2 个对象**。热路径调用点：

- `WorldTileInfo.Get` 里每 tile 一次 `WorldTileInfoHook.Apply(world, primer)` → 约 59 万对象；
- `DetermineLandforms` 里每个入选 landform 一次 `LandformEnabled.Apply(lf)`；
- `BiomeTransition.IsTransition` 里每个异 biome 邻居一次 `UnidirectionalBiomeTransitions.Apply(world)`。

**本仓库改不了**：`ExtensionPoint` 在 `1.x/Lunar/Components/LunarFramework.dll` 里，源码不在本仓库；`Extensions` 是 `private readonly List<Extension>`，没有公开的 Count，无法在调用侧安全短路。要修只能拉 LunarFramework 源码加 `if (Extensions.Count == 0) return initialValue;`（并把 `Aggregate` 换成 `for` 循环），重新编译并替换组件 DLL。收益约 2~3% (推测)，优先级低于上文的已完成项。

### `DetermineBiomeVariants` 按 biome 建索引 —— 结论：按原方案不可行

```csharp
foreach (var variant in DefDatabase<BiomeVariantDef>.AllDefsListForReading)
    if (variant.worldTileConditions?.Get(ctxTile) ?? true) { ... }
```

原计划是"从 `worldTileConditions` 的表达式树里识别 biome 限定条件"。实际确认 `XmlDynamicValue<T, TC>` 只保存一个私有的 `Modifier<T, TC> _root` 委托，XML 解析后没有可读的表达式结构，也没有 biome 引用可供分类。要实现等价索引只能自己重新解析 XML，风险大于收益，搁置。

另外 `biomeProps.AllowBiomeTransitions == false` 的 biome 本来就已经跳过这段循环。

### 待验证项（需要先实测，不建议盲改）

1. **`WorldTileInfo.WorldObject`**：`world.worldObjects.MapParentAt(TileId)` 是 O(世界对象数) 的线性扫描，每 tile 一次。水格短路上线后只剩陆地格，但如果这个世界的 MapParent 很多（大型存档常见），仍可能占几百毫秒。可以考虑按 tileId 建字典缓存，并在 `WorldObjectsHolder.Add/Remove`（已有 Harmony 补丁）和读档时失效。
2. **`GetTileCenter` 缓存**：1,082,140 次调用，插桩 377 ms。但实现只是 6 顶点平均，实测成本大概率被插桩放大；缓存 29.5 万 `Vector3`（约 3.5 MB）收益不确定，建议先复测再决定。
3. **`Landform.IdHash`**：`GenText.StableStringHash(Id)` 每次访问重算，可以缓存；但只有入选的 landform 才会调用，量级小。

## 7. 不建议改的

- **`WorldTileInfo` 持久化到存档/旁文件**。理论上二次读档可以降到 0.2 s，但失效条件太多（改格、换 mod、GL 版本、API 钩子），错了会静默画错图，不值得。
- **让 `Get` 返回空/跳过整层**。`Get` 有 19 处调用，其中地图生成、寻路通行性、袭击落点都依赖它，跳过会直接改变游戏内容。注意水格短路只跳过"必然为空"的 landform 解析，不改变 `Get` 的返回对象和其他字段。
- **把 `Get` 改成懒求值（只算绘制需要的字段）**。绘制层需要 `Biome` / `BiomeVariants` / `Landforms`，而 `Landforms` 本身依赖 topology，绕不开。

## 8. 验收

同一个存档、同一次"读档 → 打开世界地图"：

| 指标 | 并行前 | 目标 | 当前状态 |
|---|---|---|---|
| `WorldLayer_Landforms` 单步 | 3,509.9 ms | 400~700 ms | 已实现并行，待实测 |
| 该层分配 | 1,100 MB | 约不变（并行不改分配量） | 已做分配清理，预期小幅下降，待实测 |
| 整次重建 | 5,265 ms | 2,200 ms 左右 | 待实测 |
| gen0 次数 | 128 | 持平 | 预期小幅下降，待实测 |

正确性验收：同一 seed 生成的世界，导出的 `WorldTileInfo`（Topology / Landforms / BiomeVariants / Coast）逐格与改前一致。这些改动都是语义等价的：水格短路的条件严格等价于"所有 landform 都被 `AllowsLandform` 拒绝"，LINQ 替换保持了累加顺序、遍历顺序和"平局取首个"的排序语义。这部分是纯函数，结果应当 byte-identical。

编译状态：`GeologicalLandforms`（1.7 / net10.0 / Release）编译通过，0 警告 0 错误，产物输出到 `1.7/Lunar/Components/`。1.6 分支（`#if !RW_1_6_OR_GREATER`）未在本机编译验证——本地只有 1.7 的托管程序集。
