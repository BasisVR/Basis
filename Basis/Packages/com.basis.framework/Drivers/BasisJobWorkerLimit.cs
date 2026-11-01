using System;
using Unity.Jobs.LowLevel.Unsafe;
using UnityEngine;

namespace Basis.Scripts.Drivers
{
    public static class BasisJobWorkerLimit
    {
        public const int MaxWorkers = 8;
        public static int Limit(int workers) => Math.Min(workers, MaxWorkers);
#if !UNITY_EDITOR
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSplashScreen)]
        private static void Apply()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-job-worker-count") >= 0) return;
            JobsUtility.JobWorkerCount = Limit(JobsUtility.JobWorkerCount);
        }
#endif
    }
}
