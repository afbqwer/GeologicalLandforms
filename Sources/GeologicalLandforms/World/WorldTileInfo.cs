using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using GeologicalLandforms.Defs;
using GeologicalLandforms.GraphEditor;
using HarmonyLib;
using LunarFramework.Utility;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using static GeologicalLandforms.LandformData;
using static Verse.RotationDirection;

#if RW_1_6_OR_GREATER
using static RimWorld.Planet.SurfaceTile;
#else
using static RimWorld.Planet.Tile;
#endif

// ReSharper disable ForeachCanBeConvertedToQueryUsingAnotherGetEnumerator
// ReSharper disable ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator
// ReSharper disable LoopCanBeConvertedToQuery

namespace GeologicalLandforms;

public class WorldTileInfo : IWorldTileInfo
{
    public IReadOnlyList<Landform> Landforms { get; protected set; }
    public IReadOnlyList<BorderingBiome> BorderingBiomes { get; protected set; }
    public IReadOnlyList<BiomeVariantDef> BiomeVariants { get; protected set; }

    public Topology Topology { get; protected set; }
    public float TopologyValue { get; protected set; }
    public Rot4 TopologyDirection { get; protected set; }
    public StructRot4<CoastType> Coast { get; protected set; }
    public RiverType RiverType { get; protected set; }

    public MapParent WorldObject => World.worldObjects?.MapParentAt(TileId);

    public BiomeDef Biome => Tile.biome;
    public Hilliness Hilliness => Tile.hilliness;
    public float Elevation => Tile.elevation;
    public float Temperature => Tile.temperature;
    public float Rainfall => Tile.rainfall;
    public float Swampiness => Tile.swampiness;
    public float Pollution => Tile.pollution;
    public bool HasCaves => World.HasCaves(TileId);

    public RiverDef MainRiver => Tile.LargestRiverLink().river;
    public RoadDef MainRoad => Tile.LargestRoadLink().road;

    public IRiverData Rivers => GetOrCreateTileLinkData();
    public IRoadData Roads => GetOrCreateTileLinkData();

    #if RW_1_6_OR_GREATER
    public Landmark Landmark => Tile.Landmark;
    #endif

    public Vector3 PosInWorld => World.grid.GetTileCenter(TileId);

    public byte DepthInCaveSystem => World.LandformData()?.GetCaveSystemDepthAt(TileId) ?? 0;

    public int StableSeed(int salt) => WorldTileUtils.StableSeedForTile(TileId, salt);

    internal readonly int TileId;

    #if RW_1_6_OR_GREATER
    internal readonly SurfaceTile Tile;
    #else
    internal readonly Tile Tile;
    #endif

    internal readonly World World;

    private TileLinkData _tileLinkData;
    private int _cacheVersion;

    #if RW_1_6_OR_GREATER
    protected WorldTileInfo(int tileId, SurfaceTile tile, World world)
    #else
    protected WorldTileInfo(int tileId, Tile tile, World world)
    #endif
    {
        TileId = tileId;
        Tile = tile;
        World = world;
    }

    private static WorldTileInfoPrimer[] _cache;
    private static int _validCacheVersion = 1;

    private static World _warmedCacheWorld;
    private static int _warmedCacheVersion;
    private static int _parallelFillDepth;

    // Tile centers are read once per tile and once per neighbor (about 3.7 times per tile), and
    // PlanetLayer.GetTileCenter averages six vertices every call. One array of centers is cheaper
    // than a million calls, and the values only change when the planet layer is regenerated.
    private static Vector3[] _centerCache;
    private static World _centerCacheWorld;
    private static int _centerCacheVersion;

    /// <summary>Builds the center cache for the current (world, cache version) pair.</summary>
    private static void EnsureCenterCache(World world, int count, int threads)
    {
        var existing = _centerCache;
        if (existing != null && _centerCacheWorld == world && _centerCacheVersion == _validCacheVersion
            && existing.Length >= count)
        {
            return;
        }

        var centers = new Vector3[count];
        var grid = world.grid;
        var chunk = (count + threads - 1) / threads;

        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, worker =>
        {
            var from = worker * chunk;
            var to = Math.Min(count, from + chunk);

            for (var i = from; i < to; i++) centers[i] = grid.GetTileCenter(i);
        });

        _centerCache = centers;
        _centerCacheWorld = world;
        _centerCacheVersion = _validCacheVersion;
    }

    private static Vector3 CachedTileCenter(World world, int tileId)
    {
        var cache = _centerCache;
        if (cache != null && _centerCacheVersion == _validCacheVersion
            && ReferenceEquals(_centerCacheWorld, world) && (uint) tileId < (uint) cache.Length)
        {
            return cache[tileId];
        }

        return world.grid.GetTileCenter(tileId);
    }

    /// <summary>
    /// Splits [0, count) into <paramref name="threads"/> contiguous ranges with roughly equal
    /// numbers of land tiles. Land is what costs time; water short-circuits before topology and
    /// landforms, so an equal tile split lets the lane with more land set the wall clock. Work is
    /// counted per block, which keeps the partitioning itself a cheap pass over counters.
    /// </summary>
    private static int[] BuildLandBalancedBounds(World world, int count, int threads)
    {
        var bounds = new int[threads + 1];
        bounds[threads] = count;

        const int blockSize = 256;
        var blocks = (count + blockSize - 1) / blockSize;

        void SplitEvenly()
        {
            for (var i = 1; i < threads; i++) bounds[i] = (int) ((long) count * i / threads);
        }

        if (threads <= 1 || blocks < threads)
        {
            SplitEvenly();
            return bounds;
        }

        var grid = world.grid;
        var blockLand = new int[blocks];

        Parallel.For(0, blocks, block =>
        {
            var from = block * blockSize;
            var to = Math.Min(count, from + blockSize);
            var land = 0;

            for (var i = from; i < to; i++)
            {
                var biome = grid[i].biome;
                if (biome != null && !biome.Properties().isWaterCovered) land++;
            }

            blockLand[block] = land;
        });

        var landTotal = 0;
        for (var b = 0; b < blocks; b++) landTotal += blockLand[b];

        if (landTotal == 0)
        {
            SplitEvenly();
            return bounds;
        }

        var lane = 1;
        var land = 0;

        for (var b = 0; b < blocks && lane < threads; b++)
        {
            land += blockLand[b];
            var blockEnd = Math.Min(count, (b + 1) * blockSize);

            while (lane < threads && (long) land * threads >= (long) landTotal * lane
                && blockEnd > bounds[lane - 1])
            {
                bounds[lane] = blockEnd;
                lane++;
            }
        }

        while (lane < threads) bounds[lane++] = count;
        return bounds;
    }

    #if RW_1_6_OR_GREATER
    /// <summary>
    /// Same math as PlanetLayer.GetHeadingFromTo(Vector3, Vector3), except that the tangent basis
    /// facing the north pole is passed in: it only depends on <paramref name="from"/>, so computing
    /// it once per tile instead of once per neighbor removes five sixths of those calls.
    /// </summary>
    private static float HeadingFromTo(Vector3 from, Vector3 to, Vector3 forward, Vector3 right)
    {
        if (from == to) return 0f;

        WorldRendererUtility.GetTangentialVectorFacing(from, to, out var forward2, out _);
        var angle = Vector3.Angle(forward, forward2);
        if (Vector3.Dot(forward2, right) < 0f) angle = 360f - angle;
        return angle;
    }
    #endif

    /// <summary>
    /// Fills the tile info cache for the current world using all available cores. The cache is
    /// populated lazily by <see cref="Get(int,bool)"/> otherwise, which is the single largest
    /// cost when opening the world map after loading a save. Safe to call repeatedly; only
    /// does work once per (world, cache version) pair.
    /// </summary>
    internal static void WarmCache()
    {
        var cache = _cache;
        var world = Find.World;
        if (cache == null || world?.grid == null) return;

        var count = Math.Min(cache.Length, world.grid.TilesCount);
        if (count <= 0) return;

        var version = _validCacheVersion;
        if (_warmedCacheWorld == world && _warmedCacheVersion == version) return;

        // Prime the shared world component lookup so the worker threads only read it
        world.LandformData();

        var threads = Math.Max(1, Math.Min(Environment.ProcessorCount, GetWarmCacheThreadLimit()));

        // Keep the GC out of the fill: it is a closed block of work whose allocation is known from
        // the previous run, so it can be pre-reserved. If the runtime cannot reserve the budget the
        // region simply does not start and everything falls back to the normal behaviour.
        var noGcBudgetMb = GetWarmCacheNoGcBudgetMb();
        var noGcBudget = noGcBudgetMb << 20;
        var noGcEntered = noGcBudget > 0 && TryStartNoGcRegion(noGcBudget);

        Interlocked.Increment(ref _parallelFillDepth);
        try
        {
            EnsureCenterCache(world, count, threads);

            // Water tiles short-circuit before topology and landforms, so an equal tile split makes
            // the lane that happens to contain more land the slowest one and sets the wall clock.
            // Split on land count instead, at block granularity so the partitioning stays cheap.
            var bounds = BuildLandBalancedBounds(world, count, threads);

            PerfProbe.BeginFill(threads);

            Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, worker =>
            {
                var from = bounds[worker];
                var to = bounds[worker + 1];

                PerfProbe.BeginWorker(worker);
                for (var i = from; i < to; i++) Get(i);
                PerfProbe.EndWorker(worker, to - from);
            });
        }
        finally
        {
            // Also reached when the setup above throws, so the no-GC region cannot be left open.
            Interlocked.Decrement(ref _parallelFillDepth);
            if (noGcEntered) EndNoGcRegion();
        }

        PerfProbe.EndFill(count, threads, noGcEntered, noGcBudget);

        // If the cache was invalidated while filling, discard the result and try again next time
        if (_validCacheVersion == version)
        {
            _warmedCacheWorld = world;
            _warmedCacheVersion = version;
        }
    }

    private static int _warmCacheThreadLimit;
    private static long _warmCacheNoGcBudgetMb;
    private static bool _warmCacheSettingsLoaded;

    /// <summary>
    /// Optional settings for the fill, read once from 'GeologicalLandforms-warmcache.txt' in the
    /// RimWorld config folder. Accepted lines: a bare number or 'threads=N' (worker count), and
    /// 'nogc=MB' (budget for the no-GC region; 0 disables it). Lets these be A/B tested without a
    /// rebuild, since a full game restart is needed per measurement anyway.
    /// </summary>
    private static void LoadWarmCacheSettings()
    {
        if (_warmCacheSettingsLoaded) return;
        _warmCacheSettingsLoaded = true;

        _warmCacheThreadLimit = 16;
        _warmCacheNoGcBudgetMb = 512;

        try
        {
            var path = Path.Combine(GenFilePaths.ConfigFolderPath, "GeologicalLandforms-warmcache.txt");
            if (!File.Exists(path)) return;

            foreach (var rawLine in File.ReadAllLines(path))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;

                if (line.StartsWith("nogc", StringComparison.OrdinalIgnoreCase))
                {
                    var value = line.Contains('=') ? line.Substring(line.IndexOf('=') + 1).Trim() : "512";
                    if (int.TryParse(value, out var mb) && mb >= 0) _warmCacheNoGcBudgetMb = mb;
                }
                else
                {
                    var value = line.StartsWith("threads", StringComparison.OrdinalIgnoreCase)
                        ? line.Substring(line.IndexOf('=') + 1).Trim()
                        : line;
                    if (int.TryParse(value, out var workers) && workers > 0) _warmCacheThreadLimit = workers;
                }
            }
        }
        catch
        {
            // Diagnostics only: fall back to the defaults.
        }
    }

    private static int GetWarmCacheThreadLimit()
    {
        LoadWarmCacheSettings();
        return _warmCacheThreadLimit;
    }

    private static long GetWarmCacheNoGcBudgetMb()
    {
        LoadWarmCacheSettings();
        return _warmCacheNoGcBudgetMb;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TryStartNoGcRegion(long budgetBytes)
    {
        try
        {
            return GC.TryStartNoGCRegion(budgetBytes, disallowFullBlockingGC: true);
        }
        catch
        {
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void EndNoGcRegion()
    {
        try
        {
            GC.EndNoGCRegion();
        }
        catch
        {
            // The region is aborted automatically when the budget is exceeded; nothing to undo.
        }
    }

    public static IWorldTileInfo Get(Map map, bool allowFromCache = true)
    {
        #if RW_1_5_OR_GREATER

        // Support for vanilla pocket maps introduced in 1.5
        if (map.IsPocketMap)
        {
            var properties = map.generatorDef?.GetModExtension<PocketMapProperties>();
            return PocketMapInfo.Get(map.pocketTileInfo ?? new Tile { biome = map.Biome }, properties);
        }

        #endif

        // Support for special-purpose maps from mods that patch the map.Biome getter (e.g. DeepRim, SOS2)
        if (map.Biome != map.TileInfo.biome)
        {
            return PocketMapInfo.Get(new Tile { biome = map.Biome });
        }

        return Get(map.Tile, allowFromCache);
    }

    public static IWorldTileInfo Get(int tileId, bool allowFromCache = true)
    {
        try
        {
            var cache = _cache;
            var validVersion = _validCacheVersion;

            var world = Find.World;
            var canUseCache = cache != null && tileId < cache.Length;

            if (canUseCache && allowFromCache)
            {
                var match = cache[tileId];
                if (match != null && match._cacheVersion == validVersion && match.World == world)
                {
                    PerfProbe.Count(ProbeCounter.CacheHits);
                    return match;
                }
            }

            PerfProbe.Count(ProbeCounter.Tiles);
            PerfProbe.BeginTile();

            var info = new WorldTileInfoPrimer(tileId, world.grid[tileId], world);
            PerfProbe.MarkAlloc(ProbeBucket.Primer);

            var props = info.Biome.Properties();
            PerfProbe.MarkAlloc(ProbeBucket.BiomeProps);

            var worldData = world.LandformData();
            var tileData = worldData != null && worldData.TryGet(tileId, out var found) ? found : null;
            PerfProbe.MarkAlloc(ProbeBucket.LandformData);

            if (_tsc_waterTiles == null)
            {
                _tsc_waterTiles = new(6);
                _tsc_landTiles = new(6);
                _tsc_cliffTiles = new(6);
                _tsc_nonCliffTiles = new(6);
                _tsc_caveSystemTiles = new(6);
                _tsc_eligible = new(20);
                _tsc_commonness = new(20);
                _tsc_ids = new(5);
            }

            DetermineTopology(info, worldData, props);
            PerfProbe.MarkAlloc(ProbeBucket.Topology);

            tileData?.ApplyTopology(info);

            DetermineLandforms(info, tileData, props);
            PerfProbe.MarkAlloc(ProbeBucket.Landforms);

            DetermineBiomeVariants(info, tileData, props);
            PerfProbe.MarkAlloc(ProbeBucket.Variants);

            GeologicalLandformsAPI.WorldTileInfoHook.Apply(world, info);
            PerfProbe.MarkAlloc(ProbeBucket.Hook);

            if (canUseCache)
            {
                info._cacheVersion = validVersion;
                cache[tileId] = info;
            }

            PerfProbe.EndTile();

            return info;
        }
        catch (ThreadAbortException) { throw; }
        catch (Exception e)
        {
            // While filling the cache in parallel, invalidating it here would discard the entries
            // that the other threads already computed, so only the failing tile falls back.
            if (Volatile.Read(ref _parallelFillDepth) == 0) InvalidateCache();
            GeologicalLandformsAPI.Logger.Error($"Failed to get info for world tile {tileId}", e);

            #if RW_1_6_OR_GREATER
            return new WorldTileInfo(tileId, new SurfaceTile { biome = BiomeDefOf.Ocean, elevation = 0f }, Find.World);
            #else
            return new WorldTileInfo(tileId, new Tile { biome = BiomeDefOf.Ocean, elevation = 0f }, Find.World);
            #endif
        }
    }

    public static void InvalidateCache()
    {
        unchecked { _validCacheVersion++; }

        #if RW_1_6_OR_GREATER
        TileMutatorsCustomization.ClearCache();
        #endif
    }

    public static void InvalidateCache(int tileId)
    {
        var cache = _cache;

        if (cache != null && tileId >= 0 && tileId <= cache.Length - 1)
        {
            cache[tileId] = null;

            #if RW_1_6_OR_GREATER
            TileMutatorsCustomization.TileHasChanged(tileId);
            #endif
        }
    }

    public static void CreateNewCache()
    {
        InvalidateCache();
        var grid = Find.WorldGrid;
        _cache = grid == null ? null : new WorldTileInfoPrimer[grid.TilesCount];
    }

    public static void RemoveCache()
    {
        InvalidateCache();
        _cache = null;
    }

    [ThreadStatic]
    private static List<string> _tsc_ids;

    [ThreadStatic]
    private static List<Landform> _tsc_eligible;

    [ThreadStatic]
    private static List<float> _tsc_commonness;

    private static void DetermineLandforms(WorldTileInfo info, TileData tileData, BiomeProperties biomeProps)
    {
        // Biomes like ocean or lake disallow every landform, so the whole layer loop below can
        // only ever produce an empty result for them. Skip it, including the world object lookup.
        if (tileData == null && biomeProps.overrideLandforms == null
            && !biomeProps.AllowLandforms && !biomeProps.AllowBiomeTransitions
            && biomeProps.allowedLandforms == null)
        {
            info.Landforms = null;
            return;
        }

        var eligible = _tsc_eligible;
        var commonness = _tsc_commonness;
        PerfProbe.Restart();
        var worldObject = info.WorldObject;
        PerfProbe.MarkAlloc(ProbeBucket.LandformWorldObj);

        List<Landform> landforms = null;

        if (tileData != null)
        {
            if (tileData.Landforms != null)
            {
                foreach (var landformId in tileData.Landforms)
                {
                    var landform = LandformManager.FindById(landformId);

                    if (landform != null)
                    {
                        landforms ??= new(2);
                        landforms.Add(landform);
                    }
                    else
                    {
                        GeologicalLandformsAPI.Logger.Warn($"No landform found with id {landformId}");
                    }
                }
            }
        }
        else
        {
            // Indexed loops: foreach over IReadOnlyList<T> allocates an enumerator per call,
            // and this runs for every land tile.
            var layers = LandformManager.LandformLayers;

            for (var layerIdx = 0; layerIdx < layers.Count; layerIdx++)
            {
                var layer = layers[layerIdx];

                eligible.Clear();
                commonness.Clear();

                var layerLandforms = layer.Landforms;

                for (var landformIdx = 0; landformIdx < layerLandforms.Count; landformIdx++)
                {
                    var landform = layerLandforms[landformIdx];
                    var value = landform.GetCommonnessForTile(info, worldObject);
                    PerfProbe.Count(ProbeCounter.LandformEvals);
                    if (value > 0f && biomeProps.AllowsLandform(landform))
                    {
                        eligible.Add(landform);
                        commonness.Add(value);
                    }
                }

                if (layer.LayerId == "")
                {
                    for (var i = 0; i < eligible.Count; i++)
                    {
                        if (RandAsync.Chance(commonness[i], info.StableSeed(eligible[i].IdHash)))
                        {
                            landforms ??= new(2);
                            landforms.Add(eligible[i]);
                        }
                    }
                }
                else
                {
                    var seed = info.StableSeed(layer.SelectionSeed);

                    var totalCommonness = 0f;
                    for (var i = 0; i < commonness.Count; i++) totalCommonness += commonness[i];

                    var rand = RandAsync.Range(0f, Math.Max(1f, totalCommonness), seed);

                    for (var i = 0; i < eligible.Count; i++)
                    {
                        if (rand < commonness[i])
                        {
                            landforms ??= new(2);
                            landforms.Add(eligible[i]);
                            break;
                        }

                        rand -= commonness[i];
                    }
                }
            }

            PerfProbe.MarkAlloc(ProbeBucket.LandformLoop);
        }

        if (biomeProps.overrideLandforms != null)
        {
            PerfProbe.Restart();

            var ids = _tsc_ids;
            ids.Clear();

            if (landforms != null) ids.AddRange(landforms.Select(lf => lf.Id));

            landforms ??= new(3);
            landforms.Clear();

            foreach (var id in biomeProps.overrideLandforms.Get(new CtxTile(info), ids))
            {
                var landform = LandformManager.FindById(id);
                if (landform != null) landforms.AddDistinct(landform);
            }

            PerfProbe.MarkAlloc(ProbeBucket.LandformOverride);
        }

        if (landforms != null)
        {
            PerfProbe.Restart();
            landforms.RemoveAll(lf => !GeologicalLandformsAPI.LandformEnabled.Apply(lf));
            PerfProbe.MarkAlloc(ProbeBucket.LandformRemoveAll);

            // Iterate over a snapshot of the initial entries, as the loop may append to the list
            var initialCount = landforms.Count;
            PerfProbe.Restart();

            for (var i = 0; i < initialCount; i++)
            {
                var applyLayers = landforms[i].ApplyLayers;

                for (var j = 0; j < applyLayers.Count; j++)
                {
                    var landform = LandformManager.FindById(applyLayers[j].LayerId);
                    if (landform != null) landforms.AddDistinct(landform);
                }
            }

            PerfProbe.MarkAlloc(ProbeBucket.LandformApplyLayers);

            PerfProbe.Restart();
            landforms.Sort((a, b) => a.Priority - b.Priority);
            PerfProbe.MarkAlloc(ProbeBucket.LandformSort);
        }

        info.Landforms = landforms;
    }

    private static void DetermineBiomeVariants(WorldTileInfoPrimer info, TileData tileData, BiomeProperties biomeProps)
    {
        List<BiomeVariantDef> variants = null;

        // CtxTile is a struct, so passing it as ICtxTile boxes it. Box at most once per tile
        // instead of once per variant evaluated below, which is the hot part of this method.
        ICtxTile ctxTile = null;

        if (tileData != null)
        {
            if (tileData.BiomeVariants != null)
            {
                foreach (var defName in tileData.BiomeVariants)
                {
                    var def = DefDatabase<BiomeVariantDef>.GetNamed(defName);

                    if (def != null)
                    {
                        variants ??= new(2);
                        variants.Add(def);
                    }
                    else
                    {
                        GeologicalLandformsAPI.Logger.Warn($"No biome variant found with def name {defName}");
                    }
                }
            }
        }
        else if (biomeProps.AllowBiomeTransitions)
        {
            PerfProbe.Restart();

            foreach (var variant in DefDatabase<BiomeVariantDef>.AllDefsListForReading)
            {
                var conditions = variant.worldTileConditions;
                PerfProbe.Count(ProbeCounter.VariantEvals);

                PerfProbe.Restart();
                var passes = conditions == null || conditions.Get(ctxTile ??= new CtxTile(info));
                PerfProbe.MarkVariantEval(variant.index);

                if (passes)
                {
                    variants ??= new(1);
                    variants.Add(variant);
                }
            }

            PerfProbe.MarkAlloc(ProbeBucket.VariantLoop);
        }

        if (biomeProps.overrideBiomeVariants != null)
        {
            PerfProbe.Restart();
            var ids = _tsc_ids;
            ids.Clear();

            if (variants != null) ids.AddRange(variants.Select(bv => bv.defName));

            variants ??= new(3);
            variants.Clear();

            foreach (var id in biomeProps.overrideBiomeVariants.Get(ctxTile ??= new CtxTile(info), ids))
            {
                var variant = DefDatabase<BiomeVariantDef>.GetNamed(id, false);
                if (variant != null) variants.AddDistinct(variant);
            }

            PerfProbe.Mark(ProbeBucket.VariantOverride);
        }

        if (variants != null)
        {
            PerfProbe.Restart();
            variants.RemoveAll(bv => !GeologicalLandformsAPI.BiomeVariantEnabled.Apply(bv));
            PerfProbe.Mark(ProbeBucket.VariantRemoveAll);
        }

        info.BiomeVariants = variants;
    }

    [ThreadStatic]
    private static List<Rot6> _tsc_waterTiles;

    [ThreadStatic]
    private static List<Rot6> _tsc_landTiles;

    [ThreadStatic]
    private static List<Rot6> _tsc_cliffTiles;

    [ThreadStatic]
    private static List<Rot6> _tsc_nonCliffTiles;

    [ThreadStatic]
    private static List<Rot6> _tsc_caveSystemTiles;

    [ThreadStatic]
    private static List<BorderingBiome> _tsc_borderingBiomes;

    private static void DetermineTopology(WorldTileInfo info, LandformData data, BiomeProperties biomeProps)
    {
        if (biomeProps.isWaterCovered)
        {
            PerfProbe.Restart();
            var coastType = WorldTileUtils.CoastTypeFromTile(info.Tile);
            info.Coast = new StructRot4<CoastType>(coastType);
            info.Topology = Topology.Ocean;
            PerfProbe.Count(ProbeCounter.WaterTiles);
            return;
        }

        var grid = info.World.grid;

        int tileId = info.TileId;
        PerfProbe.Restart();
        var tileCenter = CachedTileCenter(info.World, tileId);

        #if RW_1_6_OR_GREATER
        WorldRendererUtility.GetTangentialVectorFacing(tileCenter, grid.NorthPolePos, out var headingForward, out var headingRight);
        #endif

        PerfProbe.Mark(ProbeBucket.TileCenter);

        var nbData = grid.ExtNbValues();
        var nbOffsets = grid.ExtNbOffsets();

        var nbOffset = nbOffsets[tileId];
        var nbBound = nbOffsets.IdxBoundFor(nbData, tileId);

        var waterTiles = _tsc_waterTiles;
        var landTiles = _tsc_landTiles;
        var cliffTiles = _tsc_cliffTiles;
        var nonCliffTiles = _tsc_nonCliffTiles;
        var caveSystemTiles = _tsc_caveSystemTiles;

        waterTiles.Clear();
        landTiles.Clear();
        cliffTiles.Clear();
        nonCliffTiles.Clear();
        caveSystemTiles.Clear();

        List<BorderingBiome> borderingBiomes = _tsc_borderingBiomes;
        if (borderingBiomes == null) _tsc_borderingBiomes = borderingBiomes = new(2);
        borderingBiomes.Clear();

        var coast = new StructRot4<CoastType>();

        var nbRiverCount = 0;

        for (var nbIdx = nbOffset; nbIdx < nbBound; nbIdx++)
        {
            PerfProbe.Restart();

            var idx = nbIdx - nbOffset;
            var nbId = (int) nbData[nbIdx];
            var nbTile = grid[nbId];

            var nbCenter = CachedTileCenter(info.World, nbId);

            // warning: RotateCW and RotateCCW are not reliable and Rot6 indexes may be reversed
            // because tileIDToNeighbors order is cw for some tiles but ccw for others
            #if RW_1_6_OR_GREATER
            var heading = HeadingFromTo(tileCenter, nbCenter, headingForward, headingRight);
            #else
            var heading = grid.GetHeadingFromTo(tileCenter, nbCenter);
            #endif

            var rot6 = new Rot6(idx, heading);
            PerfProbe.MarkAlloc(ProbeBucket.NbCenter);

            var coastType = WorldTileUtils.CoastTypeFromTile(nbTile);
            PerfProbe.MarkAlloc(ProbeBucket.NbCoast);

            if (coastType != CoastType.None)
            {
                var rot4 = rot6.AsRot4();
                coast[rot4] = WorldTileUtils.CombineCoastTypes(coastType, coast[rot4]);
                nonCliffTiles.Add(rot6);
                waterTiles.Add(rot6);
            }
            else
            {
                PerfProbe.Restart();

                var isTransition = BiomeTransition.IsTransition(tileId, nbId, info.Biome, nbTile.biome);
                PerfProbe.MarkAlloc(ProbeBucket.NbTransition);

                if (isTransition)
                {
                    borderingBiomes.Add(new BorderingBiome(nbTile.biome, rot6.Angle));
                }

                if (info.Tile.hilliness == Hilliness.Impassable)
                {
                    if (nbTile.hilliness < Hilliness.Impassable) nonCliffTiles.Add(rot6);
                    else cliffTiles.Add(rot6);

                    if (data?.GetCaveSystemDepthAt(nbId) > 0) caveSystemTiles.Add(rot6);
                }
                else
                {
                    if ((int) nbTile.hilliness >= 4 && nbTile.hilliness > info.Tile.hilliness) cliffTiles.Add(rot6);
                    else nonCliffTiles.Add(rot6);
                }

                if (nbTile.Rivers is { Count: > 0 }) nbRiverCount++;

                landTiles.Add(rot6);
            }
        }

        PerfProbe.Count(ProbeCounter.Neighbors, nbBound - nbOffset);

        if (info.Tile.potentialRivers != null && info.Biome.allowRivers)
        {
            info.RiverType = info.Tile.potentialRivers.Count switch
            {
                0 => RiverType.None,
                1 => RiverType.Source,
                2 => RiverType.Normal,
                _ => info.Tile.potentialRivers.Count(r => !grid[r.neighbor].WaterCovered) > 1
                    ? RiverType.Confluence
                    : RiverType.Estuary
            };
        }

        if (borderingBiomes.Count > 0)
        {
            PerfProbe.Restart();
            var bordering = new BorderingBiome[borderingBiomes.Count];
            borderingBiomes.CopyTo(bordering);
            info.BorderingBiomes = bordering;
            PerfProbe.MarkAlloc(ProbeBucket.TopoBordering);
        }
        else
        {
            info.BorderingBiomes = null;
        }

        info.Coast = coast;

        if (nbBound - nbOffset != 6)
        {
            info.Topology = Topology.Inland;
            return;
        }

        if (info.Tile.hilliness == Hilliness.Impassable)
        {
            if (data?.GetCaveSystemDepthAt(tileId) > 0)
            {
                PerfProbe.Restart();
                DetermineCaveTopology(info);
                PerfProbe.MarkAlloc(ProbeBucket.TopoHelpers);
                return;
            }

            if (caveSystemTiles.Count > 0)
            {
                info.Topology = Topology.Any;
                return;
            }
        }

        if (waterTiles.Count > 0)
        {
            PerfProbe.Restart();
            DetermineCoastTopology(info);
            PerfProbe.MarkAlloc(ProbeBucket.TopoHelpers);
            return;
        }

        if (info.Tile.hilliness == Hilliness.Impassable && (caveSystemTiles.Count > 0 || nbRiverCount > 0))
        {
            info.Topology = Topology.Any;
            return;
        }

        if (cliffTiles.Count > 0)
        {
            PerfProbe.Restart();
            DetermineCliffTopology(info);
            PerfProbe.MarkAlloc(ProbeBucket.TopoHelpers);
            return;
        }

        info.Topology = Topology.Inland;
        info.TopologyDirection = new Rot4(RandAsync.Range(0, 4, info.StableSeed(0087)));
    }

    private static void DetermineCaveTopology(WorldTileInfo info)
    {
        var nonCliffTiles = _tsc_nonCliffTiles;
        var caveTiles = _tsc_caveSystemTiles;

        if (nonCliffTiles.Count > 0 && caveTiles.Count < 3)
        {
            if (caveTiles.Count > 0)
            {
                var dir = nonCliffTiles.Where(t => caveTiles.Any(t.IsOpposite)).OrderBy(d => d.Slant).FirstOrFallback(Rot6.Invalid);
                if (dir.IsValid)
                {
                    info.Topology = Topology.CaveEntrance;
                    info.TopologyDirection = dir.Opposite.AsRot4();
                    return;
                }
            }

            info.Topology = Topology.CaveEntrance;
            info.TopologyDirection = nonCliffTiles.OrderBy(d => d.Slant).First().Opposite.AsRot4();
            return;
        }

        info.Topology = Topology.CaveTunnel;
        info.TopologyDirection = new Rot4(RandAsync.Range(0, 4, info.StableSeed(0087)));
    }

    private static void DetermineCoastTopology(WorldTileInfo info)
    {
        var waterTiles = _tsc_waterTiles;
        var landTiles = _tsc_landTiles;
        var cliffTiles = _tsc_cliffTiles;

        if (waterTiles.Count == 1)
        {
            info.Topology = cliffTiles.Any(waterTiles[0].IsOpposite) ? Topology.CliffAndCoast : Topology.CoastOneSide;
            info.TopologyDirection = waterTiles[0].AsRot4();
            return;
        }

        if (waterTiles.Count == 2)
        {
            if (waterTiles[0].Adjacent(waterTiles[1]))
            {
                var midPoint = Rot6.MidPoint(waterTiles[0], waterTiles[1]);
                var hasCliff = cliffTiles.Any(o => waterTiles[0].IsOpposite(o) || waterTiles[1].IsOpposite(o));
                info.Topology = hasCliff ? Topology.CliffAndCoast : Topology.CoastOneSide;
                info.TopologyDirection = Rot4.FromAngleFlat(midPoint);
                return;
            }

            if (waterTiles[0].Opposite == waterTiles[1])
            {
                info.Topology = Topology.CoastLandbridge;
                info.TopologyDirection = waterTiles[0].AsRot4().Rotated(Clockwise);
                return;
            }

            var dir = waterTiles[0].Slant < waterTiles[1].Slant ? waterTiles[0] : waterTiles[1];
            info.Topology = cliffTiles.Any(dir.IsOpposite) ? Topology.CliffAndCoast : Topology.CoastOneSide;
            info.TopologyDirection = dir.AsRot4();
            return;
        }

        if (waterTiles.Count == 3)
        {
            var middle = waterTiles.FirstOrFallback(d => waterTiles.All(d.IsSameOrAdjacent), Rot6.Invalid);
            if (middle.IsValid)
            {
                if (middle.Slant > 0.5f)
                {
                    info.Topology = Topology.CoastTwoSides;
                    info.TopologyDirection = Rot4.FromAngleFlat(middle.Angle - 45f);
                    return;
                }

                info.Topology = cliffTiles.Any(middle.IsOpposite) ? Topology.CliffAndCoast : Topology.CoastOneSide;
                info.TopologyDirection = middle.AsRot4();
                return;
            }

            var opp = waterTiles.FirstOrFallback(d => waterTiles.Any(d.IsOpposite), Rot6.Invalid);
            if (opp.IsValid)
            {
                info.Topology = Topology.CoastLandbridge;
                info.TopologyDirection = opp.AsRot4().Rotated(Clockwise);
                return;
            }

            var dir = waterTiles.OrderBy(d => d.Slant).First();
            info.Topology = cliffTiles.Any(dir.IsOpposite) ? Topology.CliffAndCoast : Topology.CoastOneSide;
            info.TopologyDirection = dir.AsRot4();
            return;
        }

        if (waterTiles.Count == 4)
        {
            if (landTiles[0].Adjacent(landTiles[1]))
            {
                info.Topology = Topology.CoastThreeSides;
                info.TopologyDirection = Rot4.FromAngleFlat(Rot6.MidPoint(landTiles[0], landTiles[1]) + 180f);
                return;
            }

            if (landTiles[0] == landTiles[1].Opposite)
            {
                info.Topology = Topology.CoastLandbridge;
                info.TopologyDirection = landTiles[0].AsRot4();
                return;
            }

            var middle = waterTiles.FirstOrFallback(d => waterTiles.Count(d.IsSameOrAdjacent) >= 3, Rot6.Invalid);
            if (middle.IsValid)
            {
                if (middle.Slant > 0.5f)
                {
                    info.Topology = Topology.CoastTwoSides;
                    info.TopologyDirection = Rot4.FromAngleFlat(middle.Angle - 45f);
                    return;
                }

                info.Topology = cliffTiles.Any(middle.IsOpposite) ? Topology.CliffAndCoast : Topology.CoastOneSide;
                info.TopologyDirection = middle.AsRot4();
                return;
            }
        }

        if (waterTiles.Count == 5)
        {
            info.Topology = Topology.CoastThreeSides;
            info.TopologyDirection = landTiles[0].Opposite.AsRot4();
            return;
        }

        if (waterTiles.Count == 6)
        {
            info.Topology = Topology.CoastAllSides;
            info.TopologyDirection = new Rot4(RandAsync.Range(0, 4, info.StableSeed(0087)));
        }
    }

    private static void DetermineCliffTopology(WorldTileInfo info)
    {
        var cliffTiles = _tsc_cliffTiles;
        var nonCliffTiles = _tsc_nonCliffTiles;

        if (cliffTiles.Count == 1)
        {
            info.Topology = Topology.CliffOneSide;
            info.TopologyDirection = cliffTiles[0].AsRot4();
            return;
        }

        if (cliffTiles.Count == 2)
        {
            if (cliffTiles[0].Adjacent(cliffTiles[1]))
            {
                var midPoint = Rot6.MidPoint(cliffTiles[0], cliffTiles[1]);

                if (Rot6.AngleSlant(midPoint) > 0.5f)
                {
                    info.Topology = Topology.CliffTwoSides;
                    info.TopologyDirection = Rot4.FromAngleFlat(midPoint - 45f);
                    return;
                }

                info.Topology = Topology.CliffOneSide;
                info.TopologyDirection = Rot4.FromAngleFlat(midPoint);
                return;
            }

            var dir = cliffTiles[0].Slant < cliffTiles[1].Slant ? cliffTiles[0] : cliffTiles[1];
            info.Topology = Topology.CliffOneSide;
            info.TopologyDirection = dir.AsRot4();
            return;
        }

        if (cliffTiles.Count == 3)
        {
            foreach (var root in cliffTiles)
            {
                var second = cliffTiles.FirstOrFallback(root.IsRotatedCW, Rot6.Invalid);
                if (!second.IsValid) continue;

                var angle = cliffTiles.Any(root.IsRotatedCCW) ? root.Angle :
                    cliffTiles.Any(second.IsRotatedCW) ? second.Angle :
                    Rot6.MidPoint(root, second);

                if (Rot6.AngleSlant(angle) > 0.5f)
                {
                    info.Topology = Topology.CliffTwoSides;
                    info.TopologyDirection = Rot4.FromAngleFlat(angle - 45f);
                    return;
                }

                info.Topology = Topology.CliffOneSide;
                info.TopologyDirection = Rot4.FromAngleFlat(angle);
                return;
            }

            var dir = cliffTiles.OrderBy(d => d.Slant).First();
            info.Topology = Topology.CliffOneSide;
            info.TopologyDirection = dir.AsRot4();
            return;
        }

        if (cliffTiles.Count == 4)
        {
            if (nonCliffTiles[0].Adjacent(nonCliffTiles[1]))
            {
                var midPoint = Rot6.MidPoint(nonCliffTiles[0], nonCliffTiles[1]);

                if (Rot6.AngleSlant(midPoint) > 0.5f)
                {
                    info.Topology = Topology.CliffTwoSides;
                    info.TopologyDirection = Rot4.FromAngleFlat(midPoint + 135f);
                    return;
                }

                info.Topology = Topology.CliffThreeSides;
                info.TopologyDirection = Rot4.FromAngleFlat(midPoint + 180f);
                return;
            }

            var dir = nonCliffTiles[0].Slant < nonCliffTiles[1].Slant ? nonCliffTiles[0] : nonCliffTiles[1];

            if (nonCliffTiles[0].Opposite.IsSameOrAdjacent(nonCliffTiles[1]))
            {
                info.Topology = Topology.CliffValley;
                info.TopologyDirection = dir.AsRot4();
                return;
            }

            info.Topology = Topology.CliffThreeSides;
            info.TopologyDirection = dir.Opposite.AsRot4();
            return;
        }

        if (cliffTiles.Count == 5)
        {
            info.Topology = Topology.CliffThreeSides;
            info.TopologyDirection = nonCliffTiles[0].AsRot4().Opposite;
            return;
        }

        if (cliffTiles.Count == 6)
        {
            info.Topology = Topology.CliffAllSides;
            info.TopologyDirection = new Rot4(RandAsync.Range(0, 4, info.StableSeed(0087)));
        }
    }

    private class TileLinkData : IRiverData, IRoadData
    {
        public float RiverInflowAngle { get; internal set; }
        public float RiverInflowOffset { get; internal set; }
        public float RiverInflowWidth { get; internal set; }
        public float RiverTributaryAngle { get; internal set; }
        public float RiverTributaryOffset { get; internal set; }
        public float RiverTributaryWidth { get; internal set; }
        public float RiverTertiaryWidth { get; internal set; }
        public float RiverTertiaryAngle { get; internal set; }
        public float RiverTertiaryOffset { get; internal set; }
        public float RiverOutflowAngle { get; internal set; }
        public float RiverOutflowWidth { get; internal set; }

        public float RoadPrimaryAngle { get; internal set; }
        public float RoadSecondaryAngle { get; internal set; }
    }

    private TileLinkData GetOrCreateTileLinkData()
    {
        if (_tileLinkData == null)
        {
            var data = new TileLinkData();

            var riverLinks = Tile.Rivers;

            if (riverLinks != null)
            {
                RiverLink inflow = default;
                RiverLink tributary = default;
                RiverLink tertiary = default;
                RiverLink outflow = default;

                foreach (var link in riverLinks)
                {
                    if (WorldTileUtils.IsRiverInflow(World.grid, TileId, link))
                    {
                        if (link.river.WidthOnWorld() > inflow.river.WidthOnWorld())
                        {
                            tertiary = tributary;
                            tributary = inflow;
                            inflow = link;
                        }
                        else if (link.river.WidthOnWorld() > tributary.river.WidthOnWorld())
                        {
                            tertiary = tributary;
                            tributary = link;
                        }
                        else if (link.river.WidthOnWorld() > tertiary.river.WidthOnWorld())
                        {
                            tertiary = link;
                        }
                    }
                    else
                    {
                        if (link.river.WidthOnWorld() > outflow.river.WidthOnWorld()) outflow = link;
                    }
                }

                if (inflow.river != null)
                {
                    var position = WorldTileUtils.RiverPositionForTile(this, 0);
                    data.RiverInflowAngle = World.grid.GetHeadingFromTo(inflow.neighbor, TileId);
                    data.RiverInflowOffset = WorldTileUtils.RiverPositionToOffset(position, data.RiverInflowAngle);
                    data.RiverInflowWidth = inflow.river.widthOnMap;
                }

                if (tributary.river != null)
                {
                    var position = WorldTileUtils.RiverPositionForTile(this, 1);
                    data.RiverTributaryAngle = World.grid.GetHeadingFromTo(tributary.neighbor, TileId);
                    data.RiverTributaryOffset = WorldTileUtils.RiverPositionToOffset(position, data.RiverTributaryAngle);
                    data.RiverTributaryWidth = tributary.river.widthOnMap;
                }

                if (tertiary.river != null)
                {
                    var position = WorldTileUtils.RiverPositionForTile(this, 2);
                    data.RiverTertiaryAngle = World.grid.GetHeadingFromTo(tertiary.neighbor, TileId);
                    data.RiverTertiaryOffset = WorldTileUtils.RiverPositionToOffset(position, data.RiverTertiaryAngle);
                    data.RiverTertiaryWidth = tertiary.river.widthOnMap;
                }

                if (outflow.river != null)
                {
                    var baseAngle = World.grid.GetHeadingFromTo(TileId, outflow.neighbor);
                    data.RiverOutflowAngle = WorldTileUtils.RiverAngleForTile(this, baseAngle);
                    data.RiverOutflowWidth = outflow.river.widthOnMap;
                }
            }

            var roadLinks = Tile.Roads;

            if (roadLinks != null)
            {
                RoadLink primary = default;
                RoadLink secondary = default;

                foreach (var link in roadLinks)
                {
                    if (link.road.WidthOnWorld() > primary.road.WidthOnWorld())
                    {
                        secondary = primary;
                        primary = link;
                    }
                    else if (link.road.WidthOnWorld() > secondary.road.WidthOnWorld())
                    {
                        secondary = link;
                    }
                }

                if (primary.road != null)
                {
                    data.RoadPrimaryAngle = World.grid.GetHeadingFromTo(primary.neighbor, TileId);
                }

                if (secondary.road != null)
                {
                    data.RoadSecondaryAngle = World.grid.GetHeadingFromTo(secondary.neighbor, TileId);
                }
            }

            _tileLinkData = data;
        }

        return _tileLinkData;
    }

    public override string ToString() =>
        $"{nameof(TileId)}: {TileId}, " +
        $"{nameof(Landforms)}: {Landforms?.Join(null, " & ") ?? "None"}, " +
        $"{nameof(BiomeVariants)}: {BiomeVariants?.Join(null, " & ") ?? "None"}, " +
        $"{nameof(Topology)}: {Topology}, " +
        $"{nameof(TopologyDirection)}: {TopologyDirection.ToStringWord()}";
}
