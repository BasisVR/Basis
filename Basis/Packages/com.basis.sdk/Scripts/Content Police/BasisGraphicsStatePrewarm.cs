using System.Collections.Generic;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

/// <summary>
/// Record-then-replay PSO cache layered on top of <see cref="BasisShaderPrewarm"/>.
/// ShaderVariantCollection.WarmUp only compiles the shader variant; on the explicit-pipeline
/// backends (D3D12 / Vulkan / Metal) the first-draw hitch is the full Pipeline State Object —
/// variant + vertex layout + blend/depth/stencil + render-target formats — which the legacy
/// path can't know ahead of time. GraphicsStateCollection traces the real PSOs that render and
/// persists them, so a later session can pre-create them.
///
/// This is complementary, not a replacement: BasisShaderPrewarm stays the proactive first-load
/// warm for never-before-seen content; this drains a chunk of last session's traced PSOs at each
/// content-load point, where the matching shaders are now resident. The reliable win is the base
/// app plus any content the player re-loads across sessions.
///
/// Two instances of the same file are kept because Unity forbids warming a collection that is
/// actively tracing: <see cref="_warm"/> is loaded read-only and warmed at content loads, while
/// <see cref="_trace"/> is loaded and traced all session, then evicted-to-cap and saved at flush.
///
/// Growth is bounded by a hard variant cap with best-effort eviction at flush. True per-variant LRU
/// isn't possible here: the API exposes no usage timestamp, warming pre-creates a PSO so the trace
/// never re-observes its reuse, and a variant whose shader has unloaded has no stable identity to
/// track. Eviction drops not-currently-resident variants first, then the most recently traced
/// (protecting the long-lived base-app set). The cap alone guarantees the file can't grow without limit.
/// </summary>
public static class BasisGraphicsStatePrewarm
{
    // Developer toggle, default off. Set from BasisSettingsDefaults.EnableGraphicsStatePrewarm.
    // Gated at init so flipping it off keeps the subsystem dormant (no trace, no warm, no file).
    public static bool Enabled = false;

    // PSO caches are graphics-API + engine-version specific; a file traced on one is meaningless
    // on another. Encoding both in the filename means a driver/API/Unity change simply finds no
    // matching file and starts clean, instead of replaying stale or invalid state.
    private static bool _initialized;
    private static bool _supported;
    private static bool _tracing;
    private static string _filePath;
    private static GraphicsStateCollection _warm;
    private static GraphicsStateCollection _seed;
    private static GraphicsStateCollection _trace;
    private static JobHandle _warmHandle;
    private static float _initTime;
    private static float _lastFlushTime;
    private static int _variantsAtLastFlush;
    private static bool _drainLogged;

    public static bool Active => _initialized && _supported;
    public static int SeedVariantCount { get; private set; }
    public static int UserVariantCount { get; private set; }
    public static int TracedVariantCount => _trace != null ? _trace.variantCount : 0;
    public static int PrunedVariantCount { get; private set; }

    // Warming creates GPU pipeline objects; cap per call so a large collection can't schedule an
    // unbounded burst on one content load. Each load drains another chunk.
    public static int MaxWarmupPerCall = 128;
    public static int MaxWarmupPerPump = 8;

    // Hard ceiling on persisted variants. A busy social instance traces low thousands; this leaves
    // generous headroom while still bounding disk + next-launch warm cost. When exceeded, the
    // lowest-priority variants are evicted at flush.
    private const int MaxVariants = 8192;

    public static float FlushIntervalSeconds = 120f;
    public static int FlushVariantDelta = 64;
    private const string SeedResourcePath = "BasisPso/basis_pso_seed";

    // Only the explicit-PSO backends benefit. On GL / D3D11 the driver builds pipeline state
    // lazily and a precompiled cache buys nothing, so stay off and don't touch disk there.
    private static bool BackendBenefits()
    {
        switch (SystemInfo.graphicsDeviceType)
        {
            case GraphicsDeviceType.Direct3D12:
            case GraphicsDeviceType.Vulkan:
            case GraphicsDeviceType.Metal:
                return true;
            default:
                return false;
        }
    }

    public static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }
        // Don't latch _initialized while disabled, so a later enable can still bring it up.
        if (!Enabled)
        {
            return;
        }
        _initialized = true;
        _supported = BackendBenefits();
        if (!_supported)
        {
            return;
        }

        try
        {
            string dir = System.IO.Path.Combine(Application.persistentDataPath, "GraphicsState");
            System.IO.Directory.CreateDirectory(dir);
            _filePath = System.IO.Path.Combine(dir, $"basis_pso.{SystemInfo.graphicsDeviceType}.{Application.unityVersion}.gpsc");

            bool onDisk = System.IO.File.Exists(_filePath);

            // Warm source: last session's PSOs, never traced so it stays warmable.
            _warm = LoadCollection(onDisk);
            int loaded = _warm != null ? _warm.variantCount : 0;
            PrunedVariantCount = PruneUnresolved(_warm);

            // Trace sink: starts from the same on-disk set and appends this session's real PSOs.
            _trace = LoadCollection(onDisk);
            PruneUnresolved(_trace);
            _trace.BeginTrace();
            _tracing = _trace.isTracing;

            _seed = LoadSeed();
            SeedVariantCount = _seed != null ? _seed.variantCount : 0;
            UserVariantCount = _warm != null ? _warm.variantCount : 0;
            _initTime = Time.realtimeSinceStartup;
            _lastFlushTime = _initTime;
            _variantsAtLastFlush = _trace.variantCount;
            BasisDebug.Log($"BasisGraphicsStatePrewarm: {SystemInfo.graphicsDeviceType} seed {SeedVariantCount} variant(s), user cache {UserVariantCount} of {loaded} replayable ({PrunedVariantCount} unresolvable pruned)", BasisDebug.LogTag.Event);
        }
        catch (System.Exception e)
        {
            BasisDebug.LogWarning($"BasisGraphicsStatePrewarm: init failed, disabling ({e.Message})", BasisDebug.LogTag.Event);
            _supported = false;
            _warm = null;
            _seed = null;
            _trace = null;
        }
    }

    private static GraphicsStateCollection LoadCollection(bool onDisk)
    {
        GraphicsStateCollection collection = new GraphicsStateCollection();
        if (onDisk)
        {
            // A corrupt or version-mismatched file throws or loads nothing; either way fall back to
            // an empty collection so this session still functions.
            try
            {
                collection.LoadFromFile(_filePath);
            }
            catch (System.Exception load)
            {
                BasisDebug.LogWarning($"BasisGraphicsStatePrewarm: load failed, starting fresh ({load.Message})", BasisDebug.LogTag.Event);
                collection = new GraphicsStateCollection();
            }
        }
        return collection;
    }

    private static int PruneUnresolved(GraphicsStateCollection collection)
    {
        if (collection == null || collection.variantCount == 0)
        {
            return 0;
        }
        int before = collection.variantCount;
        List<GraphicsStateCollection.ShaderVariant> variants = new List<GraphicsStateCollection.ShaderVariant>();
        collection.GetVariants(variants);
        for (int i = 0; i < variants.Count; i++)
        {
            if (variants[i].shader != null)
            {
                continue;
            }
            try
            {
                collection.RemoveVariant(variants[i].shader, variants[i].passId, variants[i].keywords);
            }
            catch (System.Exception)
            {
            }
        }
        return before - collection.variantCount;
    }

    public static string SeedResourceNameFor(GraphicsDeviceType api)
    {
        return $"{SeedResourcePath}_{api}";
    }

    private static GraphicsStateCollection LoadSeed()
    {
        TextAsset asset = Resources.Load<TextAsset>(SeedResourceNameFor(SystemInfo.graphicsDeviceType));
        if (asset == null)
        {
            return null;
        }
        try
        {
            string staged = System.IO.Path.Combine(Application.temporaryCachePath, $"basis_pso_seed.{SystemInfo.graphicsDeviceType}.gpsc");
            System.IO.File.WriteAllBytes(staged, asset.bytes);
            GraphicsStateCollection collection = new GraphicsStateCollection();
            collection.LoadFromFile(staged);
            return collection.variantCount > 0 ? collection : null;
        }
        catch (System.Exception e)
        {
            BasisDebug.LogWarning($"BasisGraphicsStatePrewarm: seed load failed, ignoring ({e.Message})", BasisDebug.LogTag.Event);
            return null;
        }
        finally
        {
            Resources.UnloadAsset(asset);
        }
    }

    /// <summary>
    /// Warms a bounded chunk of the persisted PSOs. Called from the same loading-screen point as
    /// <see cref="BasisShaderPrewarm.Warm"/>, where the shaders for the content just loaded are
    /// resident. Job-based, so it amortizes rather than stalling; no-ops once the source is drained.
    /// </summary>
    public static void WarmResident(string label)
    {
        if (!Enabled)
        {
            return;
        }
        EnsureInitialized();
        if (!_supported)
        {
            return;
        }
        Drain(MaxWarmupPerCall, label);
    }

    public static void Pump()
    {
        if (!Enabled)
        {
            return;
        }
        EnsureInitialized();
        if (!_supported)
        {
            return;
        }
        Drain(MaxWarmupPerPump, null);
        MaybeFlush();
    }

    private static void Drain(int budget, string label)
    {
        try
        {
            if (TryWarm(_seed, budget) || TryWarm(_warm, budget))
            {
                return;
            }
            if (!_drainLogged && SeedVariantCount + UserVariantCount > 0)
            {
                _drainLogged = true;
                BasisDebug.Log($"BasisGraphicsStatePrewarm: {SeedVariantCount + UserVariantCount} cached variant(s) warmed in {Time.realtimeSinceStartup - _initTime:F1}s", BasisDebug.LogTag.Event);
            }
        }
        catch (System.Exception e)
        {
            BasisDebug.LogWarning($"BasisGraphicsStatePrewarm: warm '{label ?? "pump"}' failed ({e.Message})", BasisDebug.LogTag.Event);
        }
    }

    private static bool TryWarm(GraphicsStateCollection collection, int budget)
    {
        if (collection == null || budget <= 0 || collection.variantCount == 0 || collection.isWarmedUp)
        {
            return false;
        }
        // Chain on the previous warm so successive loads queue instead of racing.
        _warmHandle = collection.WarmUpProgressively(budget, _warmHandle);
        return true;
    }

    /// <summary>
    /// Stops tracing, evicts down to the variant cap, and persists the trace sink. Call on app quit /
    /// play-mode exit so the PSOs traced this session survive to warm the next one.
    /// </summary>
    public static void Flush()
    {
        Persist(false);
    }

    private static void MaybeFlush()
    {
        if (!_tracing || _trace == null)
        {
            return;
        }
        float now = Time.realtimeSinceStartup;
        if (now - _lastFlushTime < FlushIntervalSeconds)
        {
            return;
        }
        _lastFlushTime = now;
        if (_trace.variantCount - _variantsAtLastFlush < FlushVariantDelta)
        {
            return;
        }
        Persist(true);
    }

    private static void Persist(bool resumeTracing)
    {
        if (!_initialized || !_supported || _trace == null)
        {
            return;
        }
        try
        {
            _warmHandle.Complete();

            if (_tracing)
            {
                _trace.EndTrace();
                _tracing = false;
            }

            EvictToCap();

            if (_filePath != null && _trace.variantCount > 0)
            {
                _trace.SaveToFile(_filePath);
            }
            _variantsAtLastFlush = _trace.variantCount;

            if (resumeTracing)
            {
                _trace.BeginTrace();
                _tracing = _trace.isTracing;
            }
        }
        catch (System.Exception e)
        {
            BasisDebug.LogWarning($"BasisGraphicsStatePrewarm: flush failed ({e.Message})", BasisDebug.LogTag.Event);
        }
    }

    // When over the cap, rank variants and drop the lowest-priority ones. Priority signal, best
    // available from this API: a variant whose shader is no longer resident (its content isn't
    // loaded) goes first; ties break by trace order, newest-first, so the long-lived base-app
    // variants traced early in the session are the last to be evicted.
    private static void EvictToCap()
    {
        if (_trace.variantCount <= MaxVariants)
        {
            return;
        }

        List<GraphicsStateCollection.ShaderVariant> variants = new List<GraphicsStateCollection.ShaderVariant>();
        _trace.GetVariants(variants);
        int count = variants.Count;
        if (count <= MaxVariants)
        {
            return;
        }

        bool[] resident = new bool[count];
        int[] order = new int[count];
        for (int i = 0; i < count; i++)
        {
            resident[i] = variants[i].shader != null;
            order[i] = i;
        }
        System.Array.Sort(order, (a, b) =>
        {
            if (resident[a] != resident[b])
            {
                return resident[a] ? 1 : -1; // non-resident (false) sorts first => evicted first
            }
            return b.CompareTo(a);           // newest trace index first within the same residency
        });

        int toRemove = count - MaxVariants;
        for (int r = 0; r < toRemove; r++)
        {
            GraphicsStateCollection.ShaderVariant victim = variants[order[r]];
            try
            {
                _trace.RemoveVariant(victim.shader, victim.passId, victim.keywords);
            }
            catch (System.Exception removeEx)
            {
                // best-effort; a refused removal just leaves it cached this round
                BasisDebug.LogWarning($"BasisGraphicsStatePrewarm: variant eviction refused ({removeEx.Message})", BasisDebug.LogTag.Event);
            }
        }
    }
}
