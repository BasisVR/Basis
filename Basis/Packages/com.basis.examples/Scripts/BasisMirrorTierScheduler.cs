using System.Collections.Generic;
using Basis.Scripts.Drivers;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Batches every active mirror's distance-tier evaluation into one Burst job per frame instead of
/// per-mirror managed math inside onBeforeRender. Rates: 0 = frozen (beyond CullDistance, keeps the
/// last image), 1 = every frame, 2/4 = every 2nd/4th frame. The half/quarter tiers only engage on
/// Android standalone; on PC only the cull distance applies.
/// </summary>
public static class BasisMirrorTierScheduler
{
    private static readonly List<BasisSDKMirror> Mirrors = new List<BasisSDKMirror>();
    private static int[] rates = System.Array.Empty<int>();
    private static int evaluatedCount;
    private static int lastEvaluatedFrame = int.MinValue;

    public static void Register(BasisSDKMirror mirror)
    {
        if (mirror == null || mirror.TierIndex >= 0) return;
        mirror.TierIndex = Mirrors.Count;
        Mirrors.Add(mirror);
    }

    public static void Unregister(BasisSDKMirror mirror)
    {
        if (mirror == null) return;
        int index = mirror.TierIndex;
        if (index < 0 || index >= Mirrors.Count) return;
        mirror.TierIndex = -1;

        int last = Mirrors.Count - 1;
        if (index != last)
        {
            Mirrors[index] = Mirrors[last];
            Mirrors[index].TierIndex = index;
        }
        Mirrors.RemoveAt(last);
    }

    public static int GetRate(BasisSDKMirror mirror)
    {
        if (lastEvaluatedFrame != Time.frameCount)
        {
            lastEvaluatedFrame = Time.frameCount;
            Evaluate();
        }
        // Mirrors registered after this frame's evaluation ran default to full rate.
        int index = mirror.TierIndex;
        return (uint)index < (uint)evaluatedCount ? rates[index] : 1;
    }

    private static void Evaluate()
    {
        int count = Mirrors.Count;
        if (count == 0) return;
        if (rates.Length < count)
        {
            rates = new int[Mathf.NextPowerOfTwo(count)];
        }

        var inputs = new NativeArray<TierInput>(count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
        var results = new NativeArray<int>(count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

        float3 eyePosition = BasisLocalCameraDriver.Position;
        for (int i = 0; i < count; i++)
        {
            BasisSDKMirror mirror = Mirrors[i];
            if (mirror == null || mirror.Renderer == null)
            {
                inputs[i] = new TierInput
                {
                    BoundsMin = eyePosition,
                    BoundsMax = eyePosition,
                    FullRateSqr = float.MaxValue,
                    HalfRateSqr = float.MaxValue,
                    CullSqr = float.MaxValue,
                };
                continue;
            }

            // Bounds are read against the closest point of the surface, so a room-wide wall mirror
            // is judged by how close you stand, not by its center.
            Bounds bounds = mirror.Renderer.bounds;
            inputs[i] = new TierInput
            {
                BoundsMin = bounds.min,
                BoundsMax = bounds.max,
#if UNITY_ANDROID && !UNITY_EDITOR
                FullRateSqr = mirror.FullRateDistance * mirror.FullRateDistance,
                HalfRateSqr = mirror.HalfRateDistance * mirror.HalfRateDistance,
#else
                FullRateSqr = float.MaxValue,
                HalfRateSqr = float.MaxValue,
#endif
                CullSqr = mirror.CullDistance * mirror.CullDistance,
            };
        }

        new TierJob
        {
            Inputs = inputs,
            Rates = results,
            EyePosition = eyePosition,
        }.Run(count);

        for (int i = 0; i < count; i++)
        {
            rates[i] = results[i];
        }
        evaluatedCount = count;

        inputs.Dispose();
        results.Dispose();
    }

    private struct TierInput
    {
        public float3 BoundsMin;
        public float3 BoundsMax;
        public float FullRateSqr;
        public float HalfRateSqr;
        public float CullSqr;
    }

    [BurstCompile]
    private struct TierJob : IJobFor
    {
        [ReadOnly] public NativeArray<TierInput> Inputs;
        [WriteOnly] public NativeArray<int> Rates;
        public float3 EyePosition;

        public void Execute(int index)
        {
            TierInput input = Inputs[index];
            float3 closest = math.clamp(EyePosition, input.BoundsMin, input.BoundsMax);
            float sqrDist = math.distancesq(EyePosition, closest);
            Rates[index] = sqrDist > input.CullSqr ? 0
                : sqrDist <= input.FullRateSqr ? 1
                : sqrDist <= input.HalfRateSqr ? 2 : 4;
        }
    }
}
