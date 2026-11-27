using Basis.BasisUI;
using UnityEngine;

namespace Basis.Scripts.Device_Management.Devices.Pairing
{
    /// <summary>
    /// The fusion tunables, read once per frame. Lives apart from the live settings so the offline temporal
    /// sweep can drive the exact same math with fixed numbers (and so the per-frame read stays cheap).
    /// <see cref="Default"/> mirrors the BasisSettingsDefaults.Pairing* defaults; <see cref="FromSettings"/>
    /// is what the runtime feeds.
    /// </summary>
    public struct BasisMidpointFusionTunables
    {
        public const float ReferenceFrameRate = 90f;
        private const float DefaultTransitionHalfLife = 0.08f;
        private const float DefaultFrozenSeconds = 0.15f;
        private const float DefaultRotationLearnHalfLife = 0.5f;

        public float SurprisePenalty;
        public float SurpriseClamp;
        public float EmaFloor;
        public float MaxCorrectionStrength;
        public float SoftSnapHalfLife;
        public float LockstepTolerance;
        public float EmaAlpha;
        public float DistanceEmaAlpha;
        public float WeightSmoothing;
        public float RotationHalfLife;
        public float TransitionHalfLife;
        public float FrozenSeconds;
        public float RotationLearnHalfLife;

        public static BasisMidpointFusionTunables Default()
        {
            return new BasisMidpointFusionTunables
            {
                SurprisePenalty = 2f,
                SurpriseClamp = 3f,
                EmaFloor = 0.005f,
                MaxCorrectionStrength = 0.3f,
                SoftSnapHalfLife = 0.05f,
                LockstepTolerance = 0.05f,
                EmaAlpha = 0.1f,
                DistanceEmaAlpha = 0.05f,
                WeightSmoothing = 0.25f,
                RotationHalfLife = 0f, // off by default -- the bone sim already smooths; a second low-pass lags fast turns
                TransitionHalfLife = DefaultTransitionHalfLife,
                FrozenSeconds = DefaultFrozenSeconds,
                RotationLearnHalfLife = DefaultRotationLearnHalfLife,
            };
        }

        public static BasisMidpointFusionTunables FromSettings()
        {
            return new BasisMidpointFusionTunables
            {
                SurprisePenalty = BasisSettingsDefaults.PairingSurprisePenalty.RawValue,
                SurpriseClamp = BasisSettingsDefaults.PairingSurpriseClamp.RawValue,
                EmaFloor = BasisSettingsDefaults.PairingEmaFloor.RawValue,
                MaxCorrectionStrength = BasisSettingsDefaults.PairingMaxCorrectionStrength.RawValue,
                SoftSnapHalfLife = BasisSettingsDefaults.PairingSoftSnapHalfLife.RawValue,
                LockstepTolerance = BasisSettingsDefaults.PairingLockstepTolerance.RawValue,
                EmaAlpha = BasisSettingsDefaults.PairingEmaAlpha.RawValue,
                DistanceEmaAlpha = BasisSettingsDefaults.PairingDistanceEmaAlpha.RawValue,
                WeightSmoothing = BasisSettingsDefaults.PairingWeightSmoothing.RawValue,
                RotationHalfLife = BasisSettingsDefaults.PairingRotationHalfLife.RawValue,
                TransitionHalfLife = DefaultTransitionHalfLife,
                FrozenSeconds = DefaultFrozenSeconds,
                RotationLearnHalfLife = DefaultRotationLearnHalfLife,
            };
        }
    }

    public enum BasisMidpointFusionMode : byte
    {
        Blend,
        FollowA,
        FollowB,
        Hold,
    }

    /// <summary>
    /// Per-pair fusion state carried across frames (velocity EMAs, smoothed confidence weights, the captured
    /// rest distance + half-rest offset, and the rotation low-pass). A fresh pair starts at <see cref="Fresh"/>.
    /// </summary>
    public struct BasisMidpointFusionState
    {
        public bool HasLastPositions;
        public Vector3 LastA;
        public Vector3 LastB;
        public Quaternion LastRotA;
        public Quaternion LastRotB;
        public bool WasUsableA;
        public bool WasUsableB;
        // Last published midpoint, carried so a partner dropping out can keep the midpoint
        // anchored to the live partner instead of collapsing toward origin.
        public Vector3 LastMid;
        public Quaternion LastMidRot;
        public float EmaVelA;
        public float EmaVelB;
        public float ExpectedDistance;
        // Smoothed confidence weights -- smoothing is what stops the midpoint from snapping when a moving
        // tracker briefly looks surprising relative to its at-rest baseline.
        public float SmoothedWA;
        public float SmoothedWB;
        // Half of the rest-pose relative rotation captured on the first frame; see BasisMidpointRotationFusion.
        public Quaternion HalfRestOffset;
        // Temporal low-pass state for the fused rotation; strength is RotationHalfLife.
        public Quaternion SmoothedMidRot;
        public bool HasSmoothedRot;
        public BasisMidpointFusionMode Mode;
        public Vector3 FollowLocalGap;
        public Quaternion FollowLocalRot;
        public Quaternion PredRotA;
        public Quaternion PredRotB;
        public Vector3 PosOffset;
        public Quaternion RotOffset;
        public bool HasRawSample;
        public Vector3 RawLastA;
        public Vector3 RawLastB;
        public Quaternion RawLastRotA;
        public Quaternion RawLastRotB;
        public float FrozenTimeA;
        public float FrozenTimeB;

        public static BasisMidpointFusionState Fresh()
        {
            return new BasisMidpointFusionState
            {
                SmoothedWA = 1f,
                SmoothedWB = 1f,
                HalfRestOffset = Quaternion.identity,
                SmoothedMidRot = Quaternion.identity,
                LastRotA = Quaternion.identity,
                LastRotB = Quaternion.identity,
                LastMidRot = Quaternion.identity,
                RotOffset = Quaternion.identity,
                FollowLocalRot = Quaternion.identity,
                PredRotA = Quaternion.identity,
                PredRotB = Quaternion.identity,
            };
        }
    }

    /// <summary>
    /// The exact per-frame fusion <see cref="BasisVirtualMidpointInput"/> runs: a confidence-weighted position
    /// blend with a continuous pull toward the calibrated rest distance, a rest-offset-projected rotation slerp,
    /// and a rotation low-pass. Pulled out of the MonoBehaviour so the offline temporal sweep drives the
    /// identical stateful math frame-by-frame with no scene or play mode.
    /// </summary>
    public static class BasisMidpointFusionCore
    {
        public static float PerFrameToRate(float perFrameAlpha, float dt)
        {
            float alpha = Mathf.Clamp01(perFrameAlpha);
            if (alpha >= 1f) return 1f;
            return 1f - Mathf.Pow(1f - alpha, dt * BasisMidpointFusionTunables.ReferenceFrameRate);
        }

        public static void Step(
            ref BasisMidpointFusionState s,
            Vector3 a, Vector3 b, Quaternion aRot, Quaternion bRot,
            bool aLost, bool bLost,
            in BasisMidpointFusionTunables t, float dt,
            out Vector3 mid, out Quaternion midRot, out float wA, out float wB, out float blendT)
        {
            blendT = 0.5f;

            float referenceDt = 1f / BasisMidpointFusionTunables.ReferenceFrameRate;
            float stepDt = dt > 1e-5f ? dt : referenceDt;

            if (s.HasRawSample)
            {
                s.FrozenTimeA = a.Equals(s.RawLastA) && aRot.Equals(s.RawLastRotA) ? s.FrozenTimeA + stepDt : 0f;
                s.FrozenTimeB = b.Equals(s.RawLastB) && bRot.Equals(s.RawLastRotB) ? s.FrozenTimeB + stepDt : 0f;
            }
            s.RawLastA = a;
            s.RawLastB = b;
            s.RawLastRotA = aRot;
            s.RawLastRotB = bRot;
            s.HasRawSample = true;

            bool frozenA = t.FrozenSeconds > 0f && s.FrozenTimeA >= t.FrozenSeconds;
            bool frozenB = t.FrozenSeconds > 0f && s.FrozenTimeB >= t.FrozenSeconds;
            bool aUsable = !aLost && !frozenA && BasisMidpointRotationFusion.IsUsable(aRot);
            bool bUsable = !bLost && !frozenB && BasisMidpointRotationFusion.IsUsable(bRot);

            if (!s.HasLastPositions)
            {
                mid = (a + b) * 0.5f;
                midRot = BasisMidpointRotationFusion.ProjectAndSlerp(aRot, bRot, 0.5f, s.HalfRestOffset);
                wA = s.SmoothedWA;
                wB = s.SmoothedWB;
                // Defer the one-shot prime (rest distance + half-rest offset) until both
                // partners actually have a pose, so a pair created mid-dropout can't
                // capture a garbage baseline that biases every later frame.
                if (aUsable && bUsable)
                {
                    s.ExpectedDistance = Vector3.Distance(a, b);
                    s.EmaVelA = 0f;
                    s.EmaVelB = 0f;
                    s.SmoothedWA = 1f;
                    s.SmoothedWB = 1f;
                    s.HalfRestOffset = BasisMidpointRotationFusion.ComputeHalfRestOffset(aRot, bRot);
                    s.PredRotA = s.HalfRestOffset;
                    s.PredRotB = Quaternion.Inverse(s.HalfRestOffset);
                    midRot = BlendRotation(aRot * s.PredRotA, bRot * s.PredRotB);
                    s.HasLastPositions = true;
                    s.Mode = BasisMidpointFusionMode.Blend;
                    s.PosOffset = Vector3.zero;
                    s.RotOffset = Quaternion.identity;
                    s.SmoothedMidRot = midRot;
                    s.HasSmoothedRot = true;
                }
                StoreLast(ref s, a, b, aRot, bRot, aUsable, bUsable, mid, midRot);
                return;
            }

            bool continuousA = aUsable && s.WasUsableA;
            bool continuousB = bUsable && s.WasUsableB;
            bool rejoinA = aUsable && !s.WasUsableA;
            bool rejoinB = bUsable && !s.WasUsableB;
            if (rejoinA)
            {
                s.LastA = a;
                s.LastRotA = aRot;
                s.SmoothedWA = continuousB ? s.SmoothedWB : 1f;
            }
            if (rejoinB)
            {
                s.LastB = b;
                s.LastRotB = bRot;
                s.SmoothedWB = continuousA ? s.SmoothedWA : 1f;
            }

            float softSnapHalfLife = Mathf.Max(t.SoftSnapHalfLife, 1e-4f);
            float weightRate = PerFrameToRate(t.WeightSmoothing, stepDt);
            float velocityScale = referenceDt / stepDt;

            float velA = aUsable ? Vector3.Distance(a, s.LastA) * velocityScale : 0f;
            float velB = bUsable ? Vector3.Distance(b, s.LastB) * velocityScale : 0f;

            // Shared baseline: judge BOTH trackers against the higher recent EMA (plus floor) so a frozen
            // tracker doesn't get a "low baseline -> easy to look surprising" effect and a real bilateral
            // motion onset doesn't fool both into looking surprising.
            float sharedBaseline = Mathf.Max(s.EmaVelA, s.EmaVelB) + t.EmaFloor;
            float surpriseA = velA / sharedBaseline;
            float surpriseB = velB / sharedBaseline;
            float excessA = Mathf.Max(0f, surpriseA - 1f);
            float excessB = Mathf.Max(0f, surpriseB - 1f);
            float instantWA = 1f / (1f + excessA * excessA * t.SurprisePenalty);
            float instantWB = 1f / (1f + excessB * excessB * t.SurprisePenalty);

            if (aUsable) s.SmoothedWA = Mathf.Lerp(s.SmoothedWA, instantWA, weightRate);
            if (bUsable) s.SmoothedWB = Mathf.Lerp(s.SmoothedWB, instantWB, weightRate);
            wA = s.SmoothedWA;
            wB = s.SmoothedWB;

            BasisMidpointFusionMode mode = aUsable && bUsable ? BasisMidpointFusionMode.Blend
                : aUsable ? BasisMidpointFusionMode.FollowA
                : bUsable ? BasisMidpointFusionMode.FollowB
                : BasisMidpointFusionMode.Hold;
            bool modeChanged = mode != s.Mode;

            if (mode == BasisMidpointFusionMode.Hold)
            {
                mid = s.LastMid;
                midRot = s.LastMidRot;
                s.PosOffset = Vector3.zero;
                s.RotOffset = Quaternion.identity;
                s.Mode = mode;
                StoreLast(ref s, a, b, aRot, bRot, aUsable, bUsable, mid, midRot);
                return;
            }

            bool followA = mode == BasisMidpointFusionMode.FollowA;
            Vector3 livePos = followA ? a : b;
            Quaternion liveRot = followA ? aRot : bRot;

            Vector3 rawMid;
            if (mode == BasisMidpointFusionMode.Blend)
            {
                float currentDistance = Vector3.Distance(a, b);
                float distanceError = currentDistance - s.ExpectedDistance;
                float absDistanceError = Mathf.Abs(distanceError);

                float weightSum = Mathf.Max(wA + wB, 1e-6f);
                // Soft pull toward the rigid rest-distance solution, ramping continuously with the divergence;
                // disabled when weights are imbalanced so the symmetric pull can't drag the trusted tracker.
                float weightBalance = 4f * wA * wB / (weightSum * weightSum);
                float correctionStrength = t.MaxCorrectionStrength
                                           * weightBalance
                                           * (1f - 1f / (1f + absDistanceError / softSnapHalfLife));
                Vector3 dir = currentDistance > 1e-6f ? (b - a) / currentDistance : Vector3.zero;
                Vector3 aIdeal = a + dir * (distanceError * 0.5f);
                Vector3 bIdeal = b - dir * (distanceError * 0.5f);
                Vector3 aSoft = Vector3.Lerp(a, aIdeal, correctionStrength);
                Vector3 bSoft = Vector3.Lerp(b, bIdeal, correctionStrength);

                float tB = wB / weightSum;
                blendT = tB;
                rawMid = aSoft * (1f - tB) + bSoft * tB;

                // Update the rest-distance EMA only when both trackers look healthy AND inside lockstep.
                if (surpriseA <= t.SurpriseClamp && surpriseB <= t.SurpriseClamp && absDistanceError < t.LockstepTolerance)
                {
                    s.ExpectedDistance = Mathf.Lerp(s.ExpectedDistance, currentDistance, PerFrameToRate(t.DistanceEmaAlpha, stepDt));
                }
            }
            else
            {
                blendT = followA ? 0f : 1f;
                if (modeChanged)
                {
                    bool continuous = followA ? continuousA : continuousB;
                    Vector3 anchor = continuous ? (followA ? s.LastA : s.LastB) : livePos;
                    Quaternion anchorRot = continuous ? (followA ? s.LastRotA : s.LastRotB) : liveRot;
                    s.FollowLocalGap = Quaternion.Inverse(anchorRot) * (s.LastMid - anchor);
                    s.FollowLocalRot = Quaternion.Inverse(anchorRot) * s.LastMidRot;
                }
                rawMid = livePos + liveRot * s.FollowLocalGap;
            }

            // Rotation uses a FIXED 0.5 blend, decoupled from the position-confidence tB: the learned per-tracker
            // offsets make it t-invariant for a rigid pair, and fixing it stops the fused rotation
            // swinging across the midA/midB gap as tB shifts with motion under flex -- the snap the rotation
            // low-pass used to mask. Position keeps the confidence blend (averaging two trackers helps there).
            Quaternion rawRot = mode == BasisMidpointFusionMode.Blend
                ? BlendRotation(aRot * s.PredRotA, bRot * s.PredRotB)
                : (liveRot * s.FollowLocalRot).normalized;

            if (mode == BasisMidpointFusionMode.Blend && t.RotationLearnHalfLife > 1e-5f
                && surpriseA <= t.SurpriseClamp && surpriseB <= t.SurpriseClamp)
            {
                float learnRate = 1f - Mathf.Pow(2f, -stepDt / t.RotationLearnHalfLife);
                s.PredRotA = Quaternion.Slerp(s.PredRotA, Quaternion.Inverse(aRot) * rawRot, learnRate).normalized;
                s.PredRotB = Quaternion.Slerp(s.PredRotB, Quaternion.Inverse(bRot) * rawRot, learnRate).normalized;
            }

            if (aUsable && surpriseA <= t.SurpriseClamp) s.EmaVelA = Mathf.Lerp(s.EmaVelA, velA, PerFrameToRate(t.EmaAlpha, stepDt));
            if (bUsable && surpriseB <= t.SurpriseClamp) s.EmaVelB = Mathf.Lerp(s.EmaVelB, velB, PerFrameToRate(t.EmaAlpha, stepDt));

            // Low-pass the fused rotation. Half-life <= 0 disables.
            if (!s.HasSmoothedRot || t.RotationHalfLife <= 1e-5f)
            {
                s.SmoothedMidRot = rawRot;
                s.HasSmoothedRot = true;
            }
            else
            {
                float rotAlpha = 1f - Mathf.Pow(2f, -stepDt / t.RotationHalfLife);
                s.SmoothedMidRot = Quaternion.Slerp(s.SmoothedMidRot, rawRot, rotAlpha);
            }
            rawRot = s.SmoothedMidRot;

            if (modeChanged)
            {
                Vector3 predictedMid = s.LastMid;
                Quaternion predictedRot = s.LastMidRot;
                if (continuousA || continuousB)
                {
                    Vector3 anchorNow = continuousA ? a : b;
                    Quaternion anchorRotNow = continuousA ? aRot : bRot;
                    Vector3 anchorLast = continuousA ? s.LastA : s.LastB;
                    Quaternion anchorRotLast = continuousA ? s.LastRotA : s.LastRotB;
                    Quaternion anchorDelta = anchorRotNow * Quaternion.Inverse(anchorRotLast);
                    predictedMid = anchorNow + anchorDelta * (s.LastMid - anchorLast);
                    predictedRot = anchorDelta * s.LastMidRot;
                }
                s.PosOffset = predictedMid - rawMid;
                s.RotOffset = predictedRot * Quaternion.Inverse(rawRot);
                s.Mode = mode;
            }
            else
            {
                float decay = t.TransitionHalfLife > 1e-5f ? Mathf.Pow(2f, -stepDt / t.TransitionHalfLife) : 0f;
                s.PosOffset *= decay;
                s.RotOffset = Quaternion.Slerp(Quaternion.identity, s.RotOffset, decay);
            }

            mid = rawMid + s.PosOffset;
            midRot = (s.RotOffset * rawRot).normalized;

            StoreLast(ref s, a, b, aRot, bRot, aUsable, bUsable, mid, midRot);
        }

        private static Quaternion BlendRotation(Quaternion midA, Quaternion midB)
        {
            if (Quaternion.Dot(midA, midB) < 0f)
            {
                midB = new Quaternion(-midB.x, -midB.y, -midB.z, -midB.w);
            }
            return Quaternion.Slerp(midA, midB, 0.5f).normalized;
        }

        private static void StoreLast(ref BasisMidpointFusionState s, Vector3 a, Vector3 b, Quaternion aRot, Quaternion bRot,
            bool aUsable, bool bUsable, Vector3 mid, Quaternion midRot)
        {
            if (aUsable)
            {
                s.LastA = a;
                s.LastRotA = aRot;
            }
            if (bUsable)
            {
                s.LastB = b;
                s.LastRotB = bRot;
            }
            s.WasUsableA = aUsable;
            s.WasUsableB = bUsable;
            s.LastMid = mid;
            s.LastMidRot = midRot;
        }
    }
}
