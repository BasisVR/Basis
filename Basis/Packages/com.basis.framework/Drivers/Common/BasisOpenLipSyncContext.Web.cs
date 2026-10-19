#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using OpenLipSync.Inference.OVRCompat;
using UnityEngine;

namespace Basis.Scripts.Drivers
{
    public partial class BasisOpenLipSyncContext
    {
        private static void ProcessPendingOnMainThread()
        {
            int count = _pendingInference.Count;
            if (count == 0) return;
            for (int index = 0; index < count; index++)
            {
                BasisOpenLipSyncContext ctx = _pendingInference[index];
                if (ctx == null) continue;
                try
                {
                    if (ctx._disposed) continue;
                    int generation = ctx._publishedGeneration + 1;
                    Frame target = ctx._resultFrames[generation & 1];
                    if (BasisOpenLipSyncDriver.ProcessFrame(ctx._contextHandle, ctx._audioChunk, ctx._frozenSampleCount, target) == Result.Success) ctx._publishedGeneration = generation;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[OpenLipSync] Inference error for context {ctx._contextHandle}: {ex.Message}");
                }
                finally
                {
                    ctx._readyForInference = false;
                }
            }
            _pendingInference.RemoveRange(0, count);
            BasisOpenLipSyncDriver.FlushWeb();
        }
    }
}
#endif
