using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Threading;
using GeologicalLandforms.Defs;
using GeologicalLandforms.GraphEditor;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace GeologicalLandforms;

// ---------------------------------------------------------------------------------------------------
// Performance diagnostics for the parallel tile cache fill.
//
// This file is self-contained on purpose: every entry point is [Conditional("GL_PERF_PROBE")], so a
// build without that symbol contains no probe code and none of the call sites in WorldTileInfo.cs
// emit anything. Enable it for a measurement build with:
//
//     dotnet build ... -p:GLPerfProbe=true
//
// Keeping it in its own file means upstream merges only ever touch the one-line hooks in
// WorldTileInfo.cs, never this file.
// ---------------------------------------------------------------------------------------------------

internal enum ProbeBucket
{
    Primer, BiomeProps, LandformData, Topology, Landforms, Variants, Hook,
    TileCenter, NbCenter, NbCoast, NbTransition, TopoBordering, TopoHelpers,
    LandformWorldObj, LandformLoop, LandformOverride, LandformRemoveAll, LandformApplyLayers, LandformSort,
    VariantLoop, VariantRemoveAll, VariantOverride, VariantEval,
    Count
}

internal enum ProbeCounter
{
    Tiles, WaterTiles, CacheHits, Neighbors, LandformEvals, VariantEvals,
    Count
}

internal static class PerfProbe
{
    private const string Symbol = "GL_PERF_PROBE";

    [ThreadStatic] private static bool _on;
    [ThreadStatic] private static long[] _ticks;
    [ThreadStatic] private static long[] _allocs;
    [ThreadStatic] private static int[] _counts;
    [ThreadStatic] private static long[] _variantAlloc;
    [ThreadStatic] private static int[] _variantCount;
    [ThreadStatic] private static long _segTicks;
    [ThreadStatic] private static long _segAlloc;
    [ThreadStatic] private static long _tileStartTicks;
    [ThreadStatic] private static long _maxTileTicks;
    [ThreadStatic] private static int _slowTiles;
    [ThreadStatic] private static long _workerStartTicks;
    [ThreadStatic] private static long _workerAllocStart;

    private static int _workers;
    private static double[] _workerWall;
    private static int[] _workerTiles;
    private static long[] _workerAlloc;
    private static int[] _workerThreadIds;
    private static long[][] _workerTicks;
    private static long[][] _workerAllocs;
    private static int[][] _workerCounts;
    private static long[][] _workerVariantAlloc;
    private static int[][] _workerVariantCount;
    private static long[] _workerMaxTile;
    private static int[] _workerSlowTiles;

    private static long _fillStartTicks;
    private static int _gen0Start, _gen1Start, _gen2Start;
    private static double _pauseStartMs;
    private static long _heapStart;
    private static long _cpuStartTicks;

    [Conditional(Symbol)]
    internal static void BeginFill(int workers)
    {
        _workers = workers;
        _workerWall = new double[workers];
        _workerTiles = new int[workers];
        _workerAlloc = new long[workers];
        _workerThreadIds = new int[workers];
        _workerTicks = new long[workers][];
        _workerAllocs = new long[workers][];
        _workerCounts = new int[workers][];
        _workerVariantAlloc = new long[workers][];
        _workerVariantCount = new int[workers][];
        _workerMaxTile = new long[workers];
        _workerSlowTiles = new int[workers];

        _gen0Start = GC.CollectionCount(0);
        _gen1Start = GC.CollectionCount(1);
        _gen2Start = GC.CollectionCount(2);
        _pauseStartMs = ReadTotalPauseMs();
        _heapStart = ReadTotalHeapBytes();
        _cpuStartTicks = ReadProcessCpuTicks();
        _fillStartTicks = Stopwatch.GetTimestamp();
    }

    [Conditional(Symbol)]
    internal static void BeginWorker(int worker)
    {
        _ticks = new long[(int) ProbeBucket.Count];
        _allocs = new long[(int) ProbeBucket.Count];
        _counts = new int[(int) ProbeCounter.Count];
        _variantAlloc = new long[DefDatabase<BiomeVariantDef>.DefCount];
        _variantCount = new int[DefDatabase<BiomeVariantDef>.DefCount];

        _maxTileTicks = 0;
        _slowTiles = 0;
        _on = true;

        _workerStartTicks = Stopwatch.GetTimestamp();
        _workerAllocStart = GC.GetAllocatedBytesForCurrentThread();
        Restart();
    }

    [Conditional(Symbol)]
    internal static void EndWorker(int worker, int tiles)
    {
        var now = Stopwatch.GetTimestamp();
        _workerWall[worker] = (now - _workerStartTicks) * 1000.0 / Stopwatch.Frequency;
        _workerAlloc[worker] = GC.GetAllocatedBytesForCurrentThread() - _workerAllocStart;
        _workerTiles[worker] = tiles;
        _workerThreadIds[worker] = Thread.CurrentThread.ManagedThreadId;
        _workerTicks[worker] = _ticks;
        _workerAllocs[worker] = _allocs;
        _workerCounts[worker] = _counts;
        _workerVariantAlloc[worker] = _variantAlloc;
        _workerVariantCount[worker] = _variantCount;
        _workerMaxTile[worker] = _maxTileTicks;
        _workerSlowTiles[worker] = _slowTiles;

        _on = false;
        _ticks = null;
        _allocs = null;
        _counts = null;
        _variantAlloc = null;
        _variantCount = null;
    }

    [Conditional(Symbol)]
    internal static void BeginTile()
    {
        if (!_on) return;
        _tileStartTicks = Stopwatch.GetTimestamp();
    }

    [Conditional(Symbol)]
    internal static void EndTile()
    {
        if (!_on) return;
        var elapsed = Stopwatch.GetTimestamp() - _tileStartTicks;
        if (elapsed > _maxTileTicks) _maxTileTicks = elapsed;
        if (elapsed > Stopwatch.Frequency / 1000) _slowTiles++;
    }

    [Conditional(Symbol)]
    internal static void Restart()
    {
        if (!_on) return;
        _segTicks = Stopwatch.GetTimestamp();
        _segAlloc = GC.GetAllocatedBytesForCurrentThread();
    }

    [Conditional(Symbol)]
    internal static void Mark(ProbeBucket bucket)
    {
        if (!_on) return;
        var now = Stopwatch.GetTimestamp();
        _ticks[(int) bucket] += now - _segTicks;
        _segTicks = now;
    }

    [Conditional(Symbol)]
    internal static void MarkAlloc(ProbeBucket bucket)
    {
        if (!_on) return;
        var now = Stopwatch.GetTimestamp();
        var alloc = GC.GetAllocatedBytesForCurrentThread();
        _ticks[(int) bucket] += now - _segTicks;
        _allocs[(int) bucket] += alloc - _segAlloc;
        _segTicks = now;
        _segAlloc = alloc;
    }

    /// <summary>Attributes the allocation since the last mark to one BiomeVariantDef (and the total).</summary>
    [Conditional(Symbol)]
    internal static void MarkVariantEval(int variantIndex)
    {
        if (!_on) return;
        var alloc = GC.GetAllocatedBytesForCurrentThread();
        var delta = alloc - _segAlloc;
        _segAlloc = alloc;

        _allocs[(int) ProbeBucket.VariantEval] += delta;
        _counts[(int) ProbeCounter.VariantEvals]++;

        if (variantIndex >= 0 && _variantAlloc != null && variantIndex < _variantAlloc.Length)
        {
            _variantAlloc[variantIndex] += delta;
            _variantCount[variantIndex]++;
        }
    }

    [Conditional(Symbol)]
    internal static void Count(ProbeCounter counter, int amount = 1)
    {
        if (!_on) return;
        _counts[(int) counter] += amount;
    }

    [Conditional(Symbol)]
    internal static void EndFill(int tileCount, int threads, bool noGcEntered, long noGcBudget)
    {
        var fillTicks = Stopwatch.GetTimestamp() - _fillStartTicks;

        var gen0 = GC.CollectionCount(0) - (int) _gen0Start;
        var gen1 = GC.CollectionCount(1) - (int) _gen1Start;
        var gen2 = GC.CollectionCount(2) - (int) _gen2Start;

        var pauseEndMs = ReadTotalPauseMs();
        var pauseMs = _pauseStartMs >= 0 && pauseEndMs >= 0 ? pauseEndMs - _pauseStartMs : -1.0;

        var heapEnd = ReadTotalHeapBytes();
        var heapDeltaMb = _heapStart >= 0 && heapEnd >= 0 ? (heapEnd - _heapStart) / (1024.0 * 1024.0) : -1.0;

        var cpuTicks = ReadProcessCpuTicks() - _cpuStartTicks;
        var busyCores = cpuTicks > 0 && fillTicks > 0 ? cpuTicks / (double) fillTicks : 0.0;

        var ticks = new long[(int) ProbeBucket.Count];
        var allocs = new long[(int) ProbeBucket.Count];
        var counts = new int[(int) ProbeCounter.Count];
        long allocTotal = 0;
        long wallSumTicks = 0;
        long maxTileTicks = 0;
        var slowTiles = 0;
        var distinctThreads = 0;

        for (var w = 0; w < threads; w++)
        {
            var wTicks = _workerTicks[w];
            var wAllocs = _workerAllocs[w];
            var wCounts = _workerCounts[w];
            if (wTicks == null || wAllocs == null || wCounts == null) continue;

            for (var i = 0; i < ticks.Length; i++)
            {
                ticks[i] += wTicks[i];
                allocs[i] += wAllocs[i];
            }

            for (var i = 0; i < counts.Length; i++) counts[i] += wCounts[i];

            allocTotal += _workerAlloc[w];
            wallSumTicks += (long) (_workerWall[w] * Stopwatch.Frequency / 1000.0);
            if (_workerMaxTile[w] > maxTileTicks) maxTileTicks = _workerMaxTile[w];
            slowTiles += _workerSlowTiles[w];

            var id = _workerThreadIds[w];
            if (id == 0) continue;
            var seen = false;
            for (var p = 0; p < w; p++)
            {
                if (_workerThreadIds[p] == id) { seen = true; break; }
            }
            if (!seen) distinctThreads++;
        }

        double ToMs(long t) => t * 1000.0 / Stopwatch.Frequency;
        var wallSumMs = Math.Max(1.0, ToMs(wallSumTicks));
        string Share(long t) => (ToMs(t) * 100.0 / wallSumMs).ToString("F0") + "%";
        string Ms(long t) => ToMs(t).ToString("F0");
        string Kb(long bytes) => (bytes / 1024).ToString();

        var tileCountSafe = Math.Max(1, counts[(int) ProbeCounter.Tiles]);

        GeologicalLandformsAPI.Logger.Log(
            "WorldTileInfo.WarmCache: " + tileCount + " tiles in " + ToMs(fillTicks).ToString("F0") + " ms on "
            + threads + " workers, gen0=" + gen0 + " gen1=" + gen1 + " gen2=" + gen2
            + " coresBusy=" + busyCores.ToString("F1")
            + ", alloc=" + (allocTotal / (1024.0 * 1024.0)).ToString("F0") + " MB ("
            + (allocTotal / tileCountSafe) + " B/tile)"
            + ", slowTiles(>1ms)=" + slowTiles + ", maxTile=" + Ms(maxTileTicks) + " ms"
        );

        GeologicalLandformsAPI.Logger.Log(
            "  runtime: gcPause=" + (pauseMs >= 0 ? pauseMs.ToString("F0") + " ms" : "n/a")
            + ", heapDelta=" + (heapDeltaMb >= 0 ? heapDeltaMb.ToString("F0") + " MB" : "n/a")
            + ", noGc=" + (noGcEntered
                ? "on(" + (noGcBudget >> 20) + " MB)"
                : noGcBudget > 0 ? "failed(" + (noGcBudget >> 20) + " MB)" : "off")
            + ", serverGC=" + ReadIsServerGc() + ", latencyMode=" + ReadLatencyMode()
            + ", affinity=0x" + ReadAffinity().ToString("X") + ", envCores=" + Environment.ProcessorCount
            + ", distinctWorkerThreads=" + distinctThreads
        );

        GeologicalLandformsAPI.Logger.Log(
            "  phases (" + wallSumMs.ToString("F0") + " ms of thread time): "
            + "primer=" + Ms(ticks[(int) ProbeBucket.Primer]) + " (" + Share(ticks[(int) ProbeBucket.Primer]) + "), "
            + "biomeProps=" + Ms(ticks[(int) ProbeBucket.BiomeProps]) + " (" + Share(ticks[(int) ProbeBucket.BiomeProps]) + "), "
            + "landformData=" + Ms(ticks[(int) ProbeBucket.LandformData]) + " (" + Share(ticks[(int) ProbeBucket.LandformData]) + "), "
            + "topology=" + Ms(ticks[(int) ProbeBucket.Topology]) + " (" + Share(ticks[(int) ProbeBucket.Topology]) + "), "
            + "landforms=" + Ms(ticks[(int) ProbeBucket.Landforms]) + " (" + Share(ticks[(int) ProbeBucket.Landforms]) + "), "
            + "variants=" + Ms(ticks[(int) ProbeBucket.Variants]) + " (" + Share(ticks[(int) ProbeBucket.Variants]) + "), "
            + "hook=" + Ms(ticks[(int) ProbeBucket.Hook]) + " (" + Share(ticks[(int) ProbeBucket.Hook]) + ")"
        );

        var measuredNeighbors = ticks[(int) ProbeBucket.NbCenter] + ticks[(int) ProbeBucket.NbCoast]
            + ticks[(int) ProbeBucket.NbTransition];

        GeologicalLandformsAPI.Logger.Log(
            "  topology: tileCenter=" + Ms(ticks[(int) ProbeBucket.TileCenter])
            + ", neighbors=" + Ms(measuredNeighbors) + " (center=" + Ms(ticks[(int) ProbeBucket.NbCenter])
            + ", coast=" + Ms(ticks[(int) ProbeBucket.NbCoast])
            + ", transition=" + Ms(ticks[(int) ProbeBucket.NbTransition]) + ")"
            + ", bordering=" + Ms(ticks[(int) ProbeBucket.TopoBordering])
            + ", helpers=" + Ms(ticks[(int) ProbeBucket.TopoHelpers])
            + "; alloc: total=" + Kb(allocs[(int) ProbeBucket.Topology]) + " kB"
            + ", center=" + Kb(allocs[(int) ProbeBucket.NbCenter]) + " kB"
            + ", coast=" + Kb(allocs[(int) ProbeBucket.NbCoast]) + " kB"
            + ", transition=" + Kb(allocs[(int) ProbeBucket.NbTransition]) + " kB"
            + ", bordering=" + Kb(allocs[(int) ProbeBucket.TopoBordering]) + " kB"
            + ", helpers=" + Kb(allocs[(int) ProbeBucket.TopoHelpers]) + " kB"
        );

        GeologicalLandformsAPI.Logger.Log(
            "  landforms: worldObj=" + Ms(ticks[(int) ProbeBucket.LandformWorldObj])
            + ", loop=" + Ms(ticks[(int) ProbeBucket.LandformLoop])
            + ", override=" + Ms(ticks[(int) ProbeBucket.LandformOverride])
            + ", removeAll=" + Ms(ticks[(int) ProbeBucket.LandformRemoveAll])
            + ", applyLayers=" + Ms(ticks[(int) ProbeBucket.LandformApplyLayers])
            + ", sort=" + Ms(ticks[(int) ProbeBucket.LandformSort])
            + "; alloc: loop=" + Kb(allocs[(int) ProbeBucket.Landforms]) + " kB"
            + ", override=" + Kb(allocs[(int) ProbeBucket.LandformOverride]) + " kB"
            + ", removeAll=" + Kb(allocs[(int) ProbeBucket.LandformRemoveAll]) + " kB"
            + ", applyLayers=" + Kb(allocs[(int) ProbeBucket.LandformApplyLayers]) + " kB"
            + ", sort=" + Kb(allocs[(int) ProbeBucket.LandformSort]) + " kB"
        );

        GeologicalLandformsAPI.Logger.Log(
            "  variants: loop=" + Ms(ticks[(int) ProbeBucket.VariantLoop])
            + ", removeAll=" + Ms(ticks[(int) ProbeBucket.VariantRemoveAll])
            + ", override=" + Ms(ticks[(int) ProbeBucket.VariantOverride])
            + "; alloc: loop=" + Kb(allocs[(int) ProbeBucket.Variants]) + " kB"
            + ", perEval=" + Kb(allocs[(int) ProbeBucket.VariantEval]) + " kB ("
            + (allocs[(int) ProbeBucket.VariantEval] / Math.Max(1, counts[(int) ProbeCounter.VariantEvals]))
            + " B/eval)"
        );

        var variantDefs = DefDatabase<BiomeVariantDef>.AllDefsListForReading;
        var variantAllocTotal = new long[variantDefs.Count];
        var variantEvalTotal = new int[variantDefs.Count];
        for (var w = 0; w < threads; w++)
        {
            var wAlloc = _workerVariantAlloc[w];
            var wCount = _workerVariantCount[w];
            if (wAlloc == null || wCount == null) continue;

            var n = Math.Min(variantDefs.Count, Math.Min(wAlloc.Length, wCount.Length));
            for (var i = 0; i < n; i++)
            {
                variantAllocTotal[i] += wAlloc[i];
                variantEvalTotal[i] += wCount[i];
            }
        }

        var topVariants = "";
        for (var rank = 0; rank < 5; rank++)
        {
            var best = -1;
            for (var i = 0; i < variantAllocTotal.Length; i++)
            {
                if (variantAllocTotal[i] <= 0) continue;
                if (best >= 0 && variantAllocTotal[i] <= variantAllocTotal[best]) continue;
                best = i;
            }

            if (best < 0) break;
            if (topVariants.Length > 0) topVariants += ", ";
            topVariants += variantDefs[best].defName + "=" + (variantAllocTotal[best] / 1024) + "kB/"
                + variantEvalTotal[best] + "evals("
                + (variantAllocTotal[best] / Math.Max(1, variantEvalTotal[best])) + "B)";
            variantAllocTotal[best] = 0;
        }

        GeologicalLandformsAPI.Logger.Log(
            "  variants by alloc: " + (topVariants.Length > 0 ? topVariants : "(none, defs=" + variantDefs.Count + ")")
        );

        GeologicalLandformsAPI.Logger.Log(
            "  counts: waterTiles=" + counts[(int) ProbeCounter.WaterTiles]
            + ", cacheHits=" + counts[(int) ProbeCounter.CacheHits]
            + ", neighbors=" + counts[(int) ProbeCounter.Neighbors]
            + ", landformEvals=" + counts[(int) ProbeCounter.LandformEvals]
            + ", variantEvals=" + counts[(int) ProbeCounter.VariantEvals]
            + ", layers=" + LandformManager.LandformLayers.Count
            + ", alloc: primer=" + Kb(allocs[(int) ProbeBucket.Primer]) + " kB"
            + ", biomeProps=" + Kb(allocs[(int) ProbeBucket.BiomeProps]) + " kB"
            + ", landformData=" + Kb(allocs[(int) ProbeBucket.LandformData]) + " kB"
            + ", topology=" + Kb(allocs[(int) ProbeBucket.Topology]) + " kB"
            + ", hook=" + Kb(allocs[(int) ProbeBucket.Hook]) + " kB"
        );

        GeologicalLandformsAPI.Logger.Log(
            "  extension points: LandformEnabled=" + ExtensionCount(GeologicalLandformsAPI.LandformEnabled)
            + ", BiomeVariantEnabled=" + ExtensionCount(GeologicalLandformsAPI.BiomeVariantEnabled)
            + ", WorldTileInfoHook=" + ExtensionCount(GeologicalLandformsAPI.WorldTileInfoHook)
            + ", UnidirectionalBiomeTransitions=" + ExtensionCount(BiomeTransition.UnidirectionalBiomeTransitions)
            + ", PostProcessBiomeTransitions=" + ExtensionCount(BiomeTransition.PostProcessBiomeTransitions)
        );

        var workerReport = "";
        for (var w = 0; w < threads; w++)
        {
            if (_workerTiles[w] <= 0) continue;
            if (workerReport.Length > 0) workerReport += ", ";
            workerReport += w.ToString() + ": " + _workerWall[w].ToString("F0") + " ms/" + _workerTiles[w]
                + " tiles/" + (_workerAlloc[w] / (1024.0 * 1024.0)).ToString("F0") + " MB";
        }

        GeologicalLandformsAPI.Logger.Log("  workers: " + workerReport);
    }

    private static long ReadProcessCpuTicks()
    {
        try
        {
            return Process.GetCurrentProcess().TotalProcessorTime.Ticks;
        }
        catch
        {
            return 0;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static double ReadTotalPauseMs()
    {
        try
        {
            return GC.GetTotalPauseDuration().TotalMilliseconds;
        }
        catch
        {
            return -1;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long ReadTotalHeapBytes()
    {
        try
        {
            return GC.GetTotalMemory(false);
        }
        catch
        {
            return -1;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string ReadIsServerGc()
    {
        try
        {
            return GCSettings.IsServerGC.ToString();
        }
        catch
        {
            return "?";
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string ReadLatencyMode()
    {
        try
        {
            return GCSettings.LatencyMode.ToString();
        }
        catch
        {
            return "?";
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
#pragma warning disable CA1416 // Windows/Linux only, guarded by try/catch
    private static long ReadAffinity()
    {
        try
        {
            return Process.GetCurrentProcess().ProcessorAffinity.ToInt64();
        }
        catch
        {
            return -1;
        }
    }
#pragma warning restore CA1416

    private static int ExtensionCount(object extensionPoint)
    {
        try
        {
            var field = extensionPoint.GetType().GetField("Extensions", BindingFlags.Instance | BindingFlags.NonPublic);
            return field?.GetValue(extensionPoint) is System.Collections.ICollection list ? list.Count : -1;
        }
        catch
        {
            return -2;
        }
    }
}
