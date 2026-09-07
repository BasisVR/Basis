using System;
using UnityEngine;
using UnityEngine.Profiling;

/// <summary>
/// Worlds load and unload additively, and Unity only sweeps unreferenced assets on its own for a
/// single (non-additive) LoadScene, so without this nothing on the client ever reclaims what a
/// world change orphans.
/// <para>Deliberately NOT driven off <c>SceneManager.sceneUnloaded</c>: BasisRuntimeSpawnRegistry
/// unloads prop scenes while the player is in the world, and both halves of a pass (a blocking
/// collection, then a sweep of every loaded asset) cost far more than a frame. Callers pick moments
/// where a stall is already happening. <see cref="BasisSceneFactory"/> requests one when the last
/// BasisScene has gone and the loading screen is coming up.</para>
/// </summary>
public static class BasisMemoryReclaim
{
    public static bool Enabled = true;
    public static float MinimumIntervalSeconds = 15f;

    private static bool isRunning;
    private static float lastCompletedRealtime = float.NegativeInfinity;

    public static bool IsRunning => isRunning;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Hook()
    {
        isRunning = false;
        lastCompletedRealtime = float.NegativeInfinity;
        Application.lowMemory -= OnLowMemory;
        Application.lowMemory += OnLowMemory;
    }

    private static void OnLowMemory()
    {
        // The OS is about to reclaim the process on mobile, so this pass ignores the interval gate.
        lastCompletedRealtime = float.NegativeInfinity;
        Request("low memory warning", collectManagedFirst: true);
    }

    public static bool Request(string reason, bool collectManagedFirst = true)
    {
        if (!Enabled || isRunning)
        {
            return false;
        }
        float now = Time.realtimeSinceStartup;
        if (now - lastCompletedRealtime < MinimumIntervalSeconds)
        {
            return false;
        }

        long reservedBefore = Profiler.GetTotalReservedMemoryLong();
        long heapBefore = GC.GetTotalMemory(false);

        if (collectManagedFirst)
        {
            // UnloadUnusedAssets only frees what no managed reference reaches, so an uncollected
            // wrapper for a destroyed clone keeps that clone's textures resident for the pass.
            GC.Collect();
        }

        AsyncOperation operation = Resources.UnloadUnusedAssets();
        if (operation == null)
        {
            lastCompletedRealtime = Time.realtimeSinceStartup;
            return false;
        }

        isRunning = true;
        operation.completed += _ => Complete(reason, reservedBefore, heapBefore);
        return true;
    }

    private static void Complete(string reason, long reservedBefore, long heapBefore)
    {
        isRunning = false;
        lastCompletedRealtime = Time.realtimeSinceStartup;

        const double ToMb = 1024d * 1024d;
        BasisDebug.Log(
            $"Memory reclaim after {reason}: reserved {reservedBefore / ToMb:F1} -> {Profiler.GetTotalReservedMemoryLong() / ToMb:F1} MB, " +
            $"managed heap {heapBefore / ToMb:F1} -> {GC.GetTotalMemory(false) / ToMb:F1} MB.",
            BasisDebug.LogTag.Scene);
    }
}
