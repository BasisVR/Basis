using System;
using Unity.Jobs.LowLevel.Unsafe;
using UnityEngine;

namespace Basis.Scripts.Drivers
{
    /// <summary>
    /// Sizes Unity's job worker pool to use the whole CPU on desktop: one worker per logical core,
    /// the main thread's core included, up to Unity's maximum. The main thread already runs queued
    /// jobs while it waits in Complete(), but it also sits idle on vsync / present / the XR frame
    /// wait, and Unity's default (cores - 1) leaves that core empty then. Basis's own threads (Steam
    /// Audio, mic, lip sync, network compute, SteamVR input) wait on events rather than spin, so
    /// reserving cores for them only left the CPU idle — and Split graphics jobs record on these
    /// same workers.
    /// Mobile platforms keep Unity's default, which avoids scheduling workers onto the slow
    /// efficiency cores where every job fence would wait on the straggler.
    /// Launching with <c>-job-worker-count N</c> overrides this.
    /// </summary>
    public static class BasisJobWorkerLimit
    {
        /// <summary>Workers for <paramref name="logicalCores"/> logical cores: one per core.</summary>
        public static int TargetWorkers(int logicalCores) => Math.Max(1, logicalCores);

        /// <summary>This machine's target, clamped to what the job system can run.</summary>
        public static int Limit(int maximumWorkers) => Math.Min(maximumWorkers, TargetWorkers(SystemInfo.processorCount));
#if !UNITY_EDITOR
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSplashScreen)]
        private static void Apply()
        {
            if (Application.isMobilePlatform) return;
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-job-worker-count") >= 0) return;
            int before = JobsUtility.JobWorkerCount;
            int target = Limit(JobsUtility.JobWorkerMaximumCount);
            if (target != before)
            {
                JobsUtility.JobWorkerCount = target;
            }
            // The pool's maximum is fixed at startup, so the target can be clamped below one per core.
            Debug.Log($"[Jobs] workers {before} -> {JobsUtility.JobWorkerCount} (logical cores {SystemInfo.processorCount}, maximum {JobsUtility.JobWorkerMaximumCount})");
        }
#endif
    }
}
