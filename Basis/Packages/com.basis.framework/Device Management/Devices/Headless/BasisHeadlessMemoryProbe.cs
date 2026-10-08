using Unity.Scripting.LifecycleManagement;
using System;
using System.Diagnostics;
using Basis.Scripts.Networking;
using UnityEngine;
using UnityEngine.Profiling;

/// <summary>
/// Main-thread memory sampler for headless builds. The health endpoint answers on a
/// background HttpListener thread, and the Unity memory counters are main-thread APIs,
/// so every number is sampled here during Update and published to
/// <see cref="BasisHeadlessRuntimeStatus"/> for the listener to read from cache.
/// </summary>
[AutoStaticsCleanup]
public static partial class BasisHeadlessMemoryProbe
{
    /// <summary>Seconds between cheap counter samples (Profiler totals, GC heap, working set).</summary>
    public static float CounterIntervalSeconds = 2f;

    /// <summary>
    /// Seconds between asset sweeps. The sweep walks every loaded Texture and Mesh to
    /// attribute their runtime size, which allocates a full object array and is O(assets) —
    /// far too costly per frame, but exactly the number that shows whether avatar texture
    /// stripping is working, so it runs on a slow cadence.
    /// </summary>
    public static float AssetSweepIntervalSeconds = 30f;

    /// <summary>Set false to stop both samplers (asset sweep included).</summary>
    public static bool Enabled = true;

    private static float nextCounterSampleTime;
    private static float nextAssetSweepTime;
    [NoAutoStaticsCleanup] private static Process currentProcess;

    /// <summary>Forces the next <see cref="Tick"/> to sample both counters and assets.</summary>
    public static void RequestImmediateSample()
    {
        nextCounterSampleTime = 0f;
        nextAssetSweepTime = 0f;
    }

    /// <summary>Call once per frame from the headless main loop.</summary>
    public static void Tick()
    {
        if (!Enabled)
        {
            return;
        }

        float now = Time.unscaledTime;
        if (now >= nextCounterSampleTime)
        {
            nextCounterSampleTime = now + Mathf.Max(0.1f, CounterIntervalSeconds);
            SampleCounters();
        }

        if (now >= nextAssetSweepTime)
        {
            nextAssetSweepTime = now + Mathf.Max(1f, AssetSweepIntervalSeconds);
            SampleAssets();
        }
    }

    private static void SampleCounters()
    {
        long workingSet = 0;
        try
        {
            currentProcess ??= Process.GetCurrentProcess();
            currentProcess.Refresh();
            workingSet = currentProcess.WorkingSet64;
        }
        catch (Exception)
        {
            // Working set is a diagnostic nicety; a platform that refuses it must not
            // take the rest of the sample down with it.
            currentProcess = null;
        }

        BasisHeadlessRuntimeStatus.PublishMemoryCounters(
            monoHeapBytes: Profiler.GetMonoHeapSizeLong(),
            monoUsedBytes: Profiler.GetMonoUsedSizeLong(),
            totalAllocatedBytes: Profiler.GetTotalAllocatedMemoryLong(),
            totalReservedBytes: Profiler.GetTotalReservedMemoryLong(),
            totalUnusedReservedBytes: Profiler.GetTotalUnusedReservedMemoryLong(),
            gcHeapBytes: GC.GetTotalMemory(false),
            workingSetBytes: workingSet,
            remotePlayerCount: BasisNetworkPlayers.RemotePlayers.Count);
    }

    private static void SampleAssets()
    {
        long textureBytes = SumRuntimeSize<Texture>(out int textureCount);
        long meshBytes = SumRuntimeSize<Mesh>(out int meshCount);
        long audioClipBytes = SumRuntimeSize<AudioClip>(out int audioClipCount);

        BasisHeadlessRuntimeStatus.PublishAssetMemory(
            textureBytes,
            textureCount,
            meshBytes,
            meshCount,
            audioClipBytes,
            audioClipCount);
    }

    private static long SumRuntimeSize<T>(out int count) where T : UnityEngine.Object
    {
        T[] assets = Resources.FindObjectsOfTypeAll<T>();
        count = assets.Length;

        long total = 0;
        for (int index = 0; index < assets.Length; index++)
        {
            total += Profiler.GetRuntimeMemorySizeLong(assets[index]);
        }

        return total;
    }
}
