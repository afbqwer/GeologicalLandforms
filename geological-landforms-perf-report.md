# Geological Landforms — 世界地图重建性能报告

> 目的：为分叉一个更快的版本提供依据。所有耗时都来自实机测量，标注 `(推测)` 的部分是结构性推算，未单独插桩。
>
> **当前状态**：并行填充、水格短路、两轮分配清理（GL 侧 + LunarFramework 侧）均已实现，并通过 1.7 / net10 的 Release 编译（0 警告 0 错误）。最近一次插桩测量（§3.2）是在第二轮分配清理**之前**做的，因此那一轮改动的收益仍按预期区间标注。

## 1. 结论

1. 读档后第一次打开世界地图，`WorldLayer_Landforms` 单线程时 **约 3.5 秒**（插桩下 4.19 s），占整次重建的 40~46%，是单层最大开销。
2. 这 3.5 秒里 **约 91% 是 GL 自己的纯 CPU 代码**，不是 I/O、不是资源加载、不是 Unity 对象创建——所以它可以被并行，而且不需要改任何算法。
3. **并行已落地并实测**：在 `WorldLayer_Landforms.Regenerate` 开头对全部 tile 做一次 `Parallel.For` 预热缓存，单步从插桩 4,191.9 ms 降到 1,274.1 ms（约 3.3×，整次 span 4,131.9 ms）。但 16 线程只换来约 3.3×，瓶颈已经变成**分配/GC**：GL 填充期间约 1.2 GB 短命对象（估算），触发 96 次 gen0，会把所有 worker 一起挂起。
4. 所以第二轮优化全部围绕"消除每格固定分配"：水 biome 的 landform 解析整体短路、GL 热路径上的装箱/枚举器/临时集合，以及 **LunarFramework 的 `ExtensionPoint.Apply` 和 XML 动态值求值**（详见 §5 ④⑤）。

## 2. 测量环境

| | |
|---|---|
| 世界 | 295,732 个 surface tile，838 个活动 mod，约 30 个 biome |
| CPU | Ryzen 9 7945HX，16 核 32 线程 |
| 测量工具 | 自建 Harmony profiler（按 step 计时 + 按 self time 聚合 + 间隙归因） |
| 对照 | 无插桩下同层 3,509.9 ms（另一优化 mod 独立记录），插桩下 4,191.9 ms |
| 最近测量 | 2026-11-20 09:20:33；span 4,131.9 ms、760 steps、gen0=96 / gen1=21 / gen2=0、进程分配 1,732.4 MB |

插桩膨胀系数约 1.2×，下文凡是"插桩 ms"都按此换算。

## 3. 数据

### 3.1 并行前（基线）

```
WorldLayer_Landforms        4,191.9 ms   self 3,813.8 ms (91.0%)   分配 1,100 MB
  └ WorldGrid.GetTileCenter   377.1 ms   1,082,140 次  (0.35 µs/次)
```

折算到单 tile：**约 12 µs 计算、3.9 KB 分配**。1.1 GB 全是短命对象，整次重建触发 128 次 gen0。

`1,082,140 / 295,732 = 3.66`——平均每个 tile 只取了 3.66 次邻居中心，因为水 tile 在 `DetermineTopology` 开头就 `return` 了；陆地 tile 是 7 次。

注意：`GetTileCenter` 的实现只是对 6 个顶点求平均，0.35 µs/次大概率被插桩放大，真实单次成本远低于此。这也是没有对它做缓存的原因。

### 3.2 并行填充后（第二轮分配清理前）

2026-11-20 09:20:33 的测量，包含并行填充、水格短路和第一轮 LINQ 清理：

```
WorldLayer_Landforms        1,274.1 ms   self 959.3 ms (75.3% of step)   主线程分配 75.5 MB
  └ WorldGrid.GetTileCenter   314.1 ms   67,136 次（主线程份额）
```

整次 span：gen0=96、gen1=21、gen2=0，进程总分配 1,732.4 MB，off-thread 调用 1,015,004 次。同 span 内 `Beautification@Surface` 1,700.3 ms / 576.9 MB 来自 ReGrowth，与本 mod 无关。

关键读数：

- profiler 的 `alloc` 列只统计 owner thread（主线程）；`Parallel.For` 会把调用线程也算作一个 worker，所以 75.5 MB 只是**十六分之一的份额**：`75.5 × 16 ≈ 1.2 GB`，与并行前单线程测得的 1.1 GB 吻合。
- 主线程 `GetTileCenter` 67,136 次 ÷ 3.66 次/格 ≈ 18.3k 格 ≈ `295,732 / 16`，进一步确认主线程只做了 1/16 的填充。
- 插桩 4,191.9 ms → 1,274.1 ms 约 3.3×（无插桩基线 3,509.9 ms 按同比估算约 1.07 s），明显低于 16 线程的预期；差距主要来自分配/GC——worker 同时制造垃圾，96 次 gen0 停顿会把所有线程一起挂起。`gen2=0` 说明这些都是短命对象，不是缓存泄漏。
- 按 `1.2 GB / 295,732 格 ≈ 4 KB/格` 反推，每格约 100 个小对象，正好对应 §4 与 §5 里列出的"每格 × 固定倍数"的分配点：

| 分配点 | 每格次数 | 来源 |
|---|---|---|
| `worldTileConditions.Get(ctxTile)` 的 `CtxTile` 装箱 | 每个带条件的 variant 1 次 | §5 ④ |
| `AggregateSupplierList` 的 `Select` 迭代器 | 每次 `allOf/anyOf` 求值 1 次 | §5 ⑤ |
| `ExtensionPoint.Apply` 的枚举器 + 闭包 | 每格约 6 次（hook + 邻居转换） | §5 ⑤ |
| `IReadOnlyList<T>` foreach 的枚举器 | 每格约 10 次（层 + landform） | §5 ④ |
| `BorderingBiomes` 的 List / 增长数组 / ToArray | 边界格 3~4 次 | §5 ④ |

## 4. 成本结构

`WorldTileInfo.Get(tileId)` 每个 tile 做三件事：

| 阶段 | 每 tile 规模 | 复杂度 |
|---|---|---|
| `DetermineTopology` | 6 个邻居：`GetHeadingFromTo`、`CoastTypeFromTile`、`BiomeTransition.IsTransition`、4 个 `List<Rot6>` 增删 | O(6) |
| `DetermineLandforms` | 遍历 `LandformManager.LandformLayers` × 层内所有 landform，逐个 `GetCommonnessForTile` | **O(L)** |
| `DetermineBiomeVariants` | `tileData == null` 时遍历 `DefDatabase<BiomeVariantDef>.AllDefsListForReading` | **O(V)** |

关键点：`LandformData._tileData` 里只有玩家手动改过的那几十格，**其余 29.5 万格的 `tileData` 都是 null**，因此几乎每个 tile 都走"遍历全部 landform + 全部 variant"的分支。这个 O(L)+O(V) 是硬性的——候选集取决于本格的 biome / hilliness / 温度 / 海岸，不同格答案不同，**不能像纹理路径那样缓存成有限几条**。

补充（这次实测代码后才确认）：`DetermineTopology` 对水格确实提前返回，但 `DetermineLandforms` **没有**对应的提前返回，水格依然会跑完整层循环，最后被 `biomeProps.AllowsLandform` 全部否掉。典型世界水格占 40~60%，这部分在并行前是纯浪费——现已由 §5 ② 的短路消除。

`_cache` 是 `WorldTileInfoPrimer[]`，按 tileId 索引，每格独立写入。`_tsc_*` 那 9 个临时 `List` 全部标了 `[ThreadStatic]`——作者当初就按可重入写的，这是并行方案成立的前提。

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

并行本身不改变分配量（当时 1.1 GB 的 churn 和 128 次 gen0 仍在），所以这一项在并行后相对更值钱：

| 位置 | 原实现 | 现实现 |
|---|---|---|
| `DetermineLandforms` 层选择 | `commonness.Sum()`（`List<float>` 装箱枚举） | 手写累加，顺序不变 |
| `DetermineLandforms` 尾部 | `landforms.SelectMany(lf => lf.ApplyLayers).ToList()` | 用 `initialCount` 快照的双层 `for`，语义一致且省 2 个分配 |
| `NodeUIWorldTileReq.BiomeCondition.GetScore` | 两次 `FirstOrDefault(e => e.BiomeDef == tile.Biome.defName)`，lambda 捕获 `tile` → 每次一个闭包对象 | 两段 `for`，先精确匹配再取 null 兜底项 |
| `ExtensionUtils.LargestRiverLink/LargestRoadLink` | `OrderByDescending(...).FirstOrDefault()`（排序 + 分配） | 单次线性取最大值（严格 `>`，平局取靠前者，和稳定排序一致） |

`BiomeCondition` 和 river/road 条件都会被最多 43 个 landform 逐格调用，属于 `DetermineLandforms` 内层循环的一部分。

### ④ 第二轮分配清理：GL 热路径（已实现）

对应 §3.2 里"每格约 100 个对象"的主要来源，全部是语义等价的改写：

| 位置 | 原实现 | 现实现 |
|---|---|---|
| `WorldTileInfo.DetermineBiomeVariants` | `CtxTile` 是 readonly struct，传进 `XmlDynamicValue<…, ICtxTile>.Get` 时**每个 variant 装箱一次** | 惰性 `ICtxTile ctxTile`，整格最多装箱 1 次；没有条件时完全不装箱 |
| `WorldTileInfo.DetermineLandforms` | `foreach` 遍历 `IReadOnlyList<Layer>` 和 `IReadOnlyList<Landform>`，每层分配一个 `SZGenericArrayEnumerator` | 下标 `for` 循环 |
| `WorldLayer_Landforms.Regenerate` | `foreach` 遍历 `tile.BiomeVariants` / `tile.Landforms`（同样是接口枚举器） | 下标 `for` 循环 |
| `DetermineTopology` 的 `BorderingBiomes` | `new List` + 增长数组 + `ToArray()`，边界格 3~4 次分配 | `[ThreadStatic]` 复用 List，只保留最终精确大小的数组 |

`LandformManager.LandformLayers` / `Layer.Landforms` 的公开类型保持 `IReadOnlyList<T>` 不变，避免破坏其他 mod 的二进制兼容；只在热循环里改成下标访问。

### ⑤ 第二轮分配清理：LunarFramework（已实现，本地重编）

按 §3.2 的结构推算，这是本轮最大的单项。`ExtensionPoint` 和 `XmlDynamicValue` 的源码就在仓库内的 `LunarFramework/Sources`，可以本地修改并重编组件 DLL。

| 位置 | 原实现 | 现实现 |
|---|---|---|
| `ExtensionPoint.Apply` | `Extensions.Aggregate(initialValue, (current, extension) => …)`：每次调用装箱 `List.Enumerator` + 捕获 `context` 的闭包，**2 个分配/次**，即使没有订阅者 | `Count == 0` 直接返回；否则 `for` 循环折叠，零分配 |
| `XmlDynamicValueSupport.AggregateSupplierList` | `suppliers.Select(f => f(ctx)).Aggregate(op)`：每次求值一个 `Select` 迭代器（`allOf` / `anyOf` 走这里） | 先取 `[0]` 再 `for` 循环左折叠 |
| `XmlDynamicValueSupport.SupplierList` | `suppliers.Select(...).ToList()`：迭代器 + 扩容 | 预分配容量的 `for` 循环 |
| `XmlDynamicValueSupport.DyadicModifierVerbose.ApplyModifiers` | `operandModifiers.Aggregate(...)`：枚举器装箱 + 闭包 | `for` 循环 |

语义等价性：遍历顺序、左折叠顺序（无种子时以首元素为种子）、空列表在构建期抛异常的行为都保持不变。

受影响的调用点（都按"每格"计）：

- `WorldTileInfoHook.Apply(world, info)`：每格 1 次，多数存档没有订阅者，走空快路径后归零；
- `BiomeTransition.IsTransition` 里的 `UnidirectionalBiomeTransitions.Apply(world)`：每个异 biome 邻居 1 次（陆地格约 5 次）；
- `LandformEnabled.Apply` / `BiomeVariantEnabled.Apply`：每个入选 landform / variant 1 次。

构建与替换（fork 的目录结构需要注意）：

1. `LunarFramework/Sources/Directory.Build.props` 的 `RimWorldPath=..\..\..\..` 是按"LunarFramework 是独立 mod 目录"计算的；它现在嵌在 GL 目录里，需要用参数覆盖：
   `dotnet build LunarFramework\Sources\LunarFramework\LunarFramework.csproj -c Release -p:RimWorldPath="<RimWorld 根目录>"`
2. 其后置的 `RegenerateLunarCheckFiles` 目标同样路径错误；构建后手动执行 `Sources/regen-check-files.ps1 -ModRoot <mod 根>` 重算 `.lfc`（`MD5(dll) XOR MD5(About/Version.txt)`），否则 LunarLoader 会认为组件校验失败。
3. 产物 `LunarFramework.dll`（1.2.2）与 `.lfc` 已更新到 `1.7/Lunar/Components/`；**1.6 未重编**。
4. `LunarFramework/` 在父仓库中是一个未跟踪的嵌套 git 检出（不在 `.gitmodules` 里），这些改动需要在该目录内单独提交，不会随 GL 的提交带走。

## 6. 其它优化项（已解决 / 待验证）

### `ExtensionPoint.Apply` —— 已解决（见 §5 ⑤）

早期版本的本报告认为这一项"本仓库改不了"，因为 `ExtensionPoint` 在预编译的 `LunarFramework.dll` 里。这个判断是错误的：`LunarFramework/Sources` 就在仓库内（独立的嵌套 git 检出，未跟踪），且 `1.7/Lunar/Components/LunarFramework.dll` 的版本 1.2.2 与源码一致，可以本地重编。

已按当时的设想落地：`Extensions.Count == 0` 快路径 + `for` 循环替换 `Aggregate`，同时顺带修掉了 `XmlDynamicValueSupport` 里三处每次求值的 LINQ 分配。原先预估"收益 2~3%"明显偏保守——按 §3.2 的分配结构，`ExtensionPoint` 约每格 6 次调用 × 2 个对象，是 1 GB churn 里可观的组成部分。

如果上游也要修，建议把这两处补丁提给 LunarFramework 仓库；本地这份改动已能单独工作。

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

| 指标 | 并行前 | 已实测（§3.2，第二轮清理前） | 第二轮清理后（目标） |
|---|---|---|---|
| `WorldLayer_Landforms` 单步 | 3,509.9 ms | 1,274.1 ms | 并行扩展性改善后应进一步下降，待实测 |
| 该层分配 | 1,100 MB | 约 1.2 GB（主线程 75.5 MB × 16 线程估算） | ≤ 100 MB（只剩缓存对象和少量 List），待实测 |
| 整次重建 | 5,265 ms | 4,131.9 ms | 待实测 |
| gen0 次数 | 128 | 96 | 预期 ~10，待实测 |

正确性验收：同一 seed 生成的世界，导出的 `WorldTileInfo`（Topology / Landforms / BiomeVariants / Coast）逐格与改前一致。这些改动都是语义等价的：水格短路的条件严格等价于"所有 landform 都被 `AllowsLandform` 拒绝"；LINQ 替换保持了累加顺序、遍历顺序和"平局取首个"的排序语义；第二轮的下标循环与接口 foreach 顺序一致；`CtxTile` 装箱只是把"每次调用装箱"变成"每格装箱一次"，不改变求值次数与顺序。这部分是纯函数，结果应当 byte-identical。

编译状态：`GeologicalLandforms` 和 `LunarFramework`（1.7 / net10.0 / Release）均编译通过，0 警告 0 错误，产物输出到 `1.7/Lunar/Components/`。1.6 分支（`#if !RW_1_6_OR_GREATER`）及 1.6 的 LunarFramework 组件未在本机编译验证——本地只有 1.7 的托管程序集。
