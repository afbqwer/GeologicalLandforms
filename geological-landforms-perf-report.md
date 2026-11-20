# Geological Landforms — 世界地图重建性能报告

> 目的：为分叉一个更快的版本提供依据。所有耗时都来自实机测量，标注 `(推测)` 的部分是结构性推算，未单独插桩。

## 1. 结论

1. 读档后第一次打开世界地图，`WorldLayer_Landforms` **约 3.5 秒**（插桩下 4.19 s），占整次重建的 40~46%，是单层最大开销。
2. 这 3.5 秒里 **约 91% 是 GL 自己的纯 CPU 代码**，不是 I/O、不是资源加载、不是 Unity 对象创建——所以它可以被并行，而且不需要改任何算法。
3. 只要在充满缓存前做一次 `Parallel.For`，同样代码、同样结果，**3.5 s → 0.4~0.7 s**。这是唯一值得先做的事；其余三项是小修小补。

## 2. 测量环境

| | |
|---|---|
| 世界 | 295,732 个 surface tile，838 个活动 mod，约 30 个 biome |
| CPU | Ryzen 9 7945HX，16 核 32 线程 |
| 测量工具 | 自建 Harmony profiler（按 step 计时 + 按 self time 聚合 + 间隙归因） |
| 对照 | 无插桩下同层 3,509.9 ms（另一优化 mod 独立记录），插桩下 4,191.9 ms |

插桩膨胀系数约 1.2×，下文凡是"插桩 ms"都按此换算。

## 3. 数据

```
WorldLayer_Landforms        4,191.9 ms   self 3,813.8 ms (91.0%)   分配 1,100 MB
  └ WorldGrid.GetTileCenter   377.1 ms   1,082,140 次  (0.35 µs/次)
```

折算到单 tile：**约 12 µs 计算、3.9 KB 分配**。1.1 GB 全是短命对象，整次重建触发 128 次 gen0。

`1,082,140 / 295,732 = 3.66`——平均每个 tile 只取了 3.66 次邻居中心，因为水 tile 在 `DetermineTopology` 开头就 `return` 了；陆地 tile 是 7 次。

## 4. 成本结构

`WorldTileInfo.Get(tileId)` 每个 tile 做三件事：

| 阶段 | 每 tile 规模 | 复杂度 |
|---|---|---|
| `DetermineTopology` | 6 个邻居：`GetHeadingFromTo`、`CoastTypeFromTile`、`BiomeTransition.IsTransition`、4 个 `List<Rot6>` 增删 | O(6) |
| `DetermineLandforms` | 遍历 `LandformManager.LandformLayers` × 层内所有 landform，逐个 `GetCommonnessForTile` | **O(L)** |
| `DetermineBiomeVariants` | `tileData == null` 时遍历 `DefDatabase<BiomeVariantDef>.AllDefsListForReading` | **O(V)** |

关键点：`LandformData._tileData` 里只有玩家手动改过的那几十格，**其余 29.5 万格的 `tileData` 都是 null**，因此几乎每个 tile 都走"遍历全部 landform + 全部 variant"的分支。这个 O(L)+O(V) 是硬性的——候选集取决于本格的 biome / hilliness / 温度 / 海岸，不同格答案不同，**不能像纹理路径那样缓存成有限几条**。

所以：要么并行，要么改选格算法（风险大、易与上游分叉），没有第三条便宜路。

`_cache` 是 `WorldTileInfoPrimer[]`，按 tileId 索引，每格独立写入。`_tsc_*` 那 8 个临时 `List` 全部标了 `[ThreadStatic]`——作者当初就按可重入写的，这是并行方案成立的前提。

## 5. 改动点（按收益/风险排序）

### ① 并行填充缓存 —— 3.5 s → 0.4~0.7 s（收益 ~85%）

现状：

```csharp
// WorldGenStep_Landforms.cs
public override void GenerateFromScribe(string seed, PlanetLayer layer)
    => WorldTileInfo.CreateNewCache();   // 只建空数组，等绘制层逐 tile 现算
```

新世界走 `GenerateFresh`，会在那里老老实实算一遍；**读档走这条，等于每次读档后重算 29.5 万格**。

改法（在 `WorldLayer_Landforms.Regenerate` 的循环之前）：

```csharp
int count = grid.TilesCount;
int threads = Math.Min(Environment.ProcessorCount, 16);
int chunk = (count + threads - 1) / threads;
Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, w => {
    int to = Math.Min(count, (w + 1) * chunk);
    for (int i = w * chunk; i < to; i++) WorldTileInfo.Get(i);
});
```

注意事项（都是必须的）：

- 只在 `_cache != null && _cache.Length >= count` 时做，否则 `Get` 不写缓存，白算；
- 用一个 `(World 实例, _validCacheVersion)` 标记防止重复填充；`InvalidateCache` / `CreateNewCache` 会让版本自增，自动重新武装；
- 填充中途若版本变了（有东西失效了缓存），本次结果作废，不要记标记；
- `Get` 内部 catch 后会调 `InvalidateCache()`，那会把所有线程刚写的条目标成旧版本——所以并发时**不要**在 catch 里失效整表，改成只返回兜底对象；
- 每次 `Get` 会调 `GeologicalLandformsAPI.WorldTileInfoHook.Apply(...)`，若外部订阅者不是线程安全的，需要串行化或加开关。

`Get` 里没有 Unity 对象创建（只有 `SurfaceTile`、Def、`Vector3` 算术），`BiomeProperties._cache` 是只读数组，`LandformData._tileData` 是只读字典，所以 16 线程写各自下标是安全的。

### ② `ExtensionPoint.Apply` 空列表快路径 —— 省 CPU + 每次调用 2 个分配

`LunarFramework.Utility.ExtensionPoint.Apply`：

```csharp
return Extensions.Aggregate(initialValue, (current, extension) => extension.Func(context, current));
```

`Aggregate` 走 `IEnumerable<T>` 重载把 `List<T>.Enumerator` **装箱**，lambda 又捕获 `context` **生成 display class**。也就是**一个订阅者都没有时，每次调用仍分配 2 个对象**。

三个调用点在热路径上：

- `WorldTileInfo.Get` 里每 tile 一次 `WorldTileInfoHook.Apply(world, primer)` → 59 万对象；
- `DetermineLandforms` 里每个入选 landform 一次 `LandformEnabled.Apply(lf)`；
- `BiomeTransition.IsTransition` 里每个异 biome 邻居一次 `UnidirectionalBiomeTransitions.Apply(world)`。

改法：`if (Extensions.Count == 0) return initialValue;`，并把 `Aggregate` 换成 `for` 循环 + 局部变量，彻底消掉装箱和闭包。

### ③ `DetermineBiomeVariants` 按 biome 建索引 —— (推测) 省 10~25%

```csharp
foreach (BiomeVariantDef item in DefDatabase<BiomeVariantDef>.AllDefsListForReading) { ... }
```

每个 tile 遍历全部 variant。绝大多数 variant 是绑死某个 biome 的，可以在 `StaticConstructorOnStartup` 建一张 `BiomeDef → 候选 variant 列表`：variant 的 `worldTileConditions` 里若含 biome 限定条件就归到对应 biome，没有 biome 限定的进"通用"桶，每 tile 只枚举 `通用 + 本 biome`。

风险：要从 `XmlDynamicValue` 的表达式树里识别 biome 条件，属于"看情况"的优化，收益不确定。建议先做 ①②，再看还剩多少。

### ④ 每 tile 的 LINQ —— 消分配，顺带省 CPU

`DetermineLandforms` 尾部，每当 `list != null` 就执行：

```csharp
list.RemoveAll(lf => !LandformEnabled.Apply(lf));
foreach (var x in list.SelectMany(lf => lf.ApplyLayers).ToList())   // ← 每 tile 2 个分配
```

以及 `RandAsync.Range(0f, Math.Max(1f, tsc_commonness.Sum()), seed)` ——`Sum()` 对 `List<float>` 走接口枚举，**每层每 tile 一次装箱**。

改成 `for` 循环累加、`ApplyLayers` 直接双层 `for`，能把这部分分配清零。分配不直接等于耗时，但 1.1 GB 的 churn 会持续触发 gen0。

## 6. 不建议改的

- **`WorldTileInfo` 持久化到存档/旁文件**。理论上二次读档可以降到 0.2 s，但失效条件太多（改格、换 mod、GL 版本、API 钩子），错了会静默画错图，不值得。
- **让 `Get` 返回空/跳过整层**。`Get` 有 19 处调用，其中地图生成、寻路通行性、袭击落点都依赖它，跳过会直接改变游戏内容。
- **把 `Get` 改成懒求值（只算绘制需要的字段）**。绘制层需要 `Biome` / `BiomeVariants` / `Landforms`，而 `Landforms` 本身依赖 topology，绕不开。

## 7. 验收

同一个存档、同一次"读档 → 打开世界地图"：

| 指标 | 现状 | 目标 |
|---|---|---|
| `WorldLayer_Landforms` 单步 | 3,509.9 ms | 400~700 ms |
| 该层分配 | 1,100 MB | 约不变（并行不改分配量） |
| 整次重建 | 5,265 ms | 2,200 ms 左右 |
| gen0 次数 | 128 | 持平 |

正确性验收：同一 seed 生成的世界，导出的 `WorldTileInfo`（Topology / Landforms / BiomeVariants / Coast）逐格与改前一致。这部分是纯函数，只要不并行 `GetCommonnessForTile` 之外的可变状态，结果应当 byte-identical。
