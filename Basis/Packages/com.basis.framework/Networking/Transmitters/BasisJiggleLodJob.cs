using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

/// <summary>
/// Burst parallel: the pure-math half of BasisJiggleColliderLOD.ComputeTier and
/// BasisJiggleSimulationLOD.ShouldSimulate, run alongside the transmit tick's
/// distance/reduce/cap/dampen chain instead of inline in CompleteTick's PostProcess loop.
/// Depends on distanceJobHandle (reads distanceSq); the per-receiver "current" inputs are
/// mirrored from managed objects in the same FillPositions pass that already visits every
/// receiver once per tick (see BasisTransmissionResults.ScheduleTick), so this job adds no new
/// managed-object reads of its own.
///
/// Logic is kept in sync BY HAND with BasisJiggleColliderLOD.ComputeTier /
/// BasisJiggleSimulationLOD.ShouldSimulate — Burst jobs can't call into those directly (mutable
/// managed statics aren't job-safe). Threshold fields are copied from the same statics each tick
/// (BasisTransmissionResults.ScheduleTick), so the two can't drift out of agreement with the
/// settings UI.
///
/// Always scheduled, never conditionally skipped: when a feature is disabled, or a receiver has
/// nothing jiggle-capable, the corresponding output is just its mirrored current value copied
/// back unchanged. PostProcess's own per-feature/per-receiver gates (unchanged from before this
/// job existed) decide whether to act on the output, so a "disabled" output is always dead data,
/// never misapplied — see the call site.
/// </summary>
[BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Standard)]
public struct BasisJiggleLodJob : IJobParallelFor
{
    public bool ColliderLodEnabled;
    public float NearSqr;
    public float MidSqr;
    public float FarSqr;
    public float ColliderHysteresisSqr;

    public bool SimulationLodEnabled;
    public float SimCutoffSqr;
    public float SimHysteresisSqr;

    [ReadOnly] public NativeArray<float> distanceSq;

    [ReadOnly] public NativeArray<bool> HasJiggleColliders;
    [ReadOnly] public NativeArray<BasisJiggleColliderTier> CurrentColliderTier;
    [ReadOnly] public NativeArray<bool> HasJiggleRigs;
    [ReadOnly] public NativeArray<bool> CurrentlySimulating;

    [WriteOnly] public NativeArray<BasisJiggleColliderTier> TargetColliderTier;
    [WriteOnly] public NativeArray<bool> TargetShouldSimulate;

    public void Execute(int i)
    {
        BasisJiggleColliderTier curTier = CurrentColliderTier[i];
        BasisJiggleColliderTier tier;
        if (ColliderLodEnabled && HasJiggleColliders[i])
        {
            float d2 = distanceSq[i];
            int cur = (int)curTier;
            tier = BasisJiggleColliderTier.Full;
            if (PastBoundary(d2, NearSqr, cur > 0, ColliderHysteresisSqr))
            {
                tier = BasisJiggleColliderTier.NoFingers;
                if (PastBoundary(d2, MidSqr, cur > 1, ColliderHysteresisSqr))
                {
                    tier = BasisJiggleColliderTier.HandsOnly;
                    if (PastBoundary(d2, FarSqr, cur > 2, ColliderHysteresisSqr))
                    {
                        tier = BasisJiggleColliderTier.None;
                    }
                }
            }
        }
        else
        {
            // Disabled, or nothing to trim for this receiver: mirror the current value back.
            // ComputeTier's own "!Enabled -> Full" default only ever mattered when the caller
            // skipped calling it otherwise, which PostProcess still does live at the call site.
            tier = ColliderLodEnabled ? curTier : BasisJiggleColliderTier.Full;
        }
        TargetColliderTier[i] = tier;

        bool simulating = CurrentlySimulating[i];
        bool shouldSimulate;
        if (SimulationLodEnabled && HasJiggleRigs[i])
        {
            float threshold = simulating ? (SimCutoffSqr * SimHysteresisSqr) : (SimCutoffSqr / SimHysteresisSqr);
            shouldSimulate = distanceSq[i] < threshold;
        }
        else
        {
            shouldSimulate = !SimulationLodEnabled || simulating;
        }
        TargetShouldSimulate[i] = shouldSimulate;
    }

    private static bool PastBoundary(float distSq, float boundarySq, bool alreadyPast, float hysteresisSqr)
    {
        float threshold = alreadyPast ? boundarySq / hysteresisSqr : boundarySq * hysteresisSqr;
        return distSq > threshold;
    }
}
