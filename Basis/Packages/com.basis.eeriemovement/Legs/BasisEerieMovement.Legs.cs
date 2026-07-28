using System.Runtime.CompilerServices;
using Basis.Scripts.Common;
using Unity.Collections;
using UnityEngine;
namespace Basis.IK
{
    /// <summary>
    /// Legs, knees and toes: two-bone leg IK, knee-swivel smoothing and the procedural toe bend.
    /// </summary>
    public partial struct BasisEerieMovement
    {
        // Two-bone leg IK with bend-normal preference, per leg.
        void SolveLegPass(BasisPoseStream stream)
        {
            SolveLegs(stream, enabledLeftLowerLeg, handleLeftUpperLeg, handleLeftLowerLeg, handleLeftFoot, targetPositionLeftLowerLeg, targetRotationLeftLowerLeg, hintPositionLeftLowerLeg, hintRotationLeftLowerLeg, hintWeightLeftLowerLeg, targetOffsetLeftFoot, kneeBendPrefLeft, hintIsTrackerLeftLowerLeg, footIsTrackerLeftLeg, 0);
            SolveLegs(stream, enabledRightLowerLeg, handleRightUpperLeg, handleRightLowerLeg, handleRightFoot, targetPositionRightLowerLeg, targetRotationRightLowerLeg, hintPositionRightLowerLeg, hintRotationRightLowerLeg, hintWeightRightLowerLeg, targetOffsetRightFoot, kneeBendPrefRight, hintIsTrackerRightLowerLeg, footIsTrackerRightLeg, 1);
        }

        // A toe TRACKER wins outright; otherwise the procedural surface bend from the foot driver
        // articulates the toe over stair noses, kerbs and ramps.
        void SolveToePass(BasisPoseStream stream)
        {
            if (leftToeEnabled) ApplyRotation(stream, true, handleLeftToe, leftDrivenTargetRot, targetOffsetLeftToe);
            else ApplyToeSurfaceBend(stream, handleLeftToe, leftToeBendDeg, leftToeBendAxis);

            if (rightToeEnabled) ApplyRotation(stream, true, handleRightToe, rightDrivenTargetRot, targetOffsetRightToe);
            else ApplyToeSurfaceBend(stream, handleRightToe, rightToeBendDeg, rightToeBendAxis);
        }

        BasisSwivelFrame BuildLegFrame(BasisPoseStream stream)
        {
            if (!handleLeftUpperLeg.IsValid(stream) || !handleRightUpperLeg.IsValid(stream)
                || !handleHips.IsValid(stream) || !handleChest.IsValid(stream))
            {
                return default;
            }

            return BasisSwivelHintCore.BuildFrame(
                handleLeftUpperLeg.GetPosition(stream), handleRightUpperLeg.GetPosition(stream),
                handleHips.GetPosition(stream), handleChest.GetPosition(stream));
        }
        /// <summary>
        /// Evaluates the Two-Bone IK algorithm.
        /// </summary>
        /// <param name="stream">The animation stream to work on.</param>
        /// <param name="root">The transform handle for the root transform.</param>
        /// <param name="mid">The transform handle for the mid transform.</param>
        /// <param name="tip">The transform handle for the tip transform.</param>
        /// <param name="target">The transform handle for the target transform.</param>
        /// <param name="hint">The world-space hint (pole) position.</param>
        /// <param name="HasHint">The weight for which hint transform has an effect on IK calculations. This is a value in between 0 and 1.</param>
        /// <param name="targetOffset">The offset applied to the target transform.</param>
        /// <summary>Returns the shin roll applied to the mid bone, so a preserved (untracked) foot can be carried
        /// by it. Identity whenever no shin roll ran.</summary>
        public Quaternion SolveTwoBone(BasisPoseStream stream, BasisBoneHandle root, BasisBoneHandle mid, BasisBoneHandle tip, BasisAffineTransform target, Vector3 hint, float hintWeight, Quaternion targetOffset, Vector3 BendNormal, float hintDistrust = 0f, int diagSlot = -1, Quaternion hintRotation = default, bool hintIsTracker = false, Vector3 anteriorNormal = default)
        {
            BasisLegSolveInput input = default;
            root.GetPositionAndRotation(stream, out Vector3 rootPos, out Quaternion rootRot);
            mid.GetPositionAndRotation(stream, out Vector3 midPos, out Quaternion midRot);
            input.Root = rootPos;
            input.Mid = midPos;
            input.Tip = tip.GetPosition(stream);
            input.RootRotation = rootRot;
            input.MidRotation = midRot;
            input.TargetPosition = target.translation;
            input.TargetRotation = target.rotation;
            input.HintPosition = hint;
            input.HintWeight = hintWeight;
            input.HintDistrust = hintDistrust;
            input.TargetOffset = targetOffset;
            input.BendNormal = BendNormal;
            // ANTERIOR stays body-frame even when BendNormal rides a lower-leg tracker: otherwise tibial
            // rotation spins the guard's reference and drags a legal knee into its compression band.
            input.AnteriorNormal = anteriorNormal;
            input.HintRotation = hintRotation;
            input.HintIsTracker = hintIsTracker;

            BasisLegSolveCore.Solve(input, out BasisLegSolveResult result);

            if (diagSlot >= 0 && legDiagnostics.IsCreated && diagSlot < legDiagnostics.Length)
            {
                BasisLegDiagnostics d = legDiagnostics[diagSlot];
                d.ReachRatio = result.ReachRatio;
                d.KneeAngleDeg = result.KneeAngleDeg;
                d.AxisSource = result.AxisSource;
                d.HintApplied = result.HintApplied ? 1f : 0f;
                d.HintDistrust = hintDistrust;
                d.ShinRollDeg = result.ShinRollDeg;
                legDiagnostics[diagSlot] = d;
            }

            mid.SetRotation(stream, result.MidDelta * mid.GetRotation(stream));
            root.SetRotation(stream, result.RootDelta * root.GetRotation(stream));
            root.SetRotation(stream, result.HintDelta * root.GetRotation(stream));
            mid.SetRotation(stream, result.MidPostRoll * mid.GetRotation(stream));
            tip.SetRotation(stream, result.TipRotation);
            return result.MidPostRoll;
        }
        public void SolveLegs(BasisPoseStream stream, float enabledProp, BasisBoneHandle root, BasisBoneHandle mid, BasisBoneHandle tip, Vector3 targetPosProp, Quaternion targetRotProp, Vector3 hintPosProp, Quaternion hintRotProp, float hintWeightProp, Quaternion targetOffset, Vector3 bendNormalProp, bool hintIsTrackerProp, bool footIsTrackerProp, int legSlot)
        {
            float posWeight = enabledProp;
            if (posWeight <= 0f)
            {
                return;
            }

            if (!(root.IsValid(stream) && mid.IsValid(stream) && tip.IsValid(stream)))
            {
                return;
            }
            Quaternion origRootRot = root.GetRotation(stream);
            Quaternion origMidRot = mid.GetRotation(stream);
            Quaternion origTipRot = tip.GetRotation(stream);

            // Solve at full strength toward the IK target
            Quaternion tRot = targetRotProp;
            float tRotSqrLen = tRot.x * tRot.x + tRot.y * tRot.y + tRot.z * tRot.z + tRot.w * tRot.w;
            bool preserveTip = !(tRotSqrLen > 0.5f);
            if (preserveTip) tRot = origTipRot;
            float hintW = hintWeightProp;

            BasisAffineTransform target = new BasisAffineTransform(targetPosProp, tRot);
            Vector3 hint = hintPosProp;
            Vector3 bendNormal = bendNormalProp;

            float hintDistrust = 0f;
            bool usedModelHint = false;
            bool fabricatedLeg = !hintIsTrackerProp && !footIsTrackerProp;
            if (!(hintW > 0f) || fabricatedLeg)
            {
                BasisSwivelFrame frame = BuildLegFrame(stream);

                Vector3 hipPos = root.GetPosition(stream);
                float upperLen = (mid.GetPosition(stream) - hipPos).magnitude;
                float lowerLen = (tip.GetPosition(stream) - mid.GetPosition(stream)).magnitude;
                float legLen = upperLen + lowerLen;
                bool isLeft = legSlot == 0;
                if (BasisSwivelHintCore.LegHint(frame, hipPos, target.translation, legLen, isLeft,
                                                out Vector3 modelHint, out float conf, useNeuralPole))
                {
                    hint = modelHint;
                    hintW = 1f;
                    usedModelHint = true;
                    if (legDiagnostics.IsCreated && legSlot < legDiagnostics.Length)
                    {
                        BasisLegDiagnostics d = legDiagnostics[legSlot];
                        d.ModelHintUsed = 1f;
                        d.ModelConfidence = conf;
                        legDiagnostics[legSlot] = d;
                    }
                    hintDistrust = 1f - BasisSwivelHintCore.LegModelTrust(conf);
                }
            }
            Quaternion shinRoll = SolveTwoBone(stream, root, mid, tip, target, hint, hintW, targetOffset, bendNormal, hintDistrust, legSlot,hintIsTrackerProp ? hintRotProp : default, hintIsTrackerProp, kneeAnteriorRef);
            if (posWeight < 1f)
            {
                root.SetRotation(stream, Quaternion.Slerp(origRootRot, root.GetRotation(stream), posWeight));
                mid.SetRotation(stream, Quaternion.Slerp(origMidRot, mid.GetRotation(stream), posWeight));
                tip.SetRotation(stream, Quaternion.Slerp(origTipRot, tip.GetRotation(stream), posWeight));
            }
            if (preserveTip)
            {
                Quaternion carriedTip = shinRoll * origTipRot;
                tip.SetRotation(stream, posWeight < 1f ? Quaternion.Slerp(origTipRot, carriedTip, posWeight) : carriedTip);
            }

            RecordHipDiagnostics(stream, root, mid, legSlot);
            if (legSwivelSmoothing)
            {
                if (hintIsTrackerProp || footIsTrackerProp)
                {
                    bool footDerivedPole = !hintIsTrackerProp && footIsTrackerProp && !usedModelHint;
                    SmoothKneeSwivel(stream, root, mid, tip, legSlot, stream.deltaTime,
                        trackedKneeSwivelMinCutoffHz, trackedKneeSwivelBeta, trackedKneeSwivelDerivCutoffHz,
                        conditionOnPole: !hintIsTrackerProp && (!footDerivedPole || kneeFootPoleConditioning),
                        holdWhenSingular: !footDerivedPole || kneeFootPoleHold);
                }
                else
                {
                    SmoothKneeSwivel(stream, root, mid, tip, legSlot, stream.deltaTime,
                        BasisSwivelFilterCore.MinCutoffHz, BasisSwivelFilterCore.Beta, BasisSwivelFilterCore.DerivCutoffHz,
                        conditionOnPole: true, holdWhenSingular: true);
                }
            }
        }
        void RecordHipDiagnostics(BasisPoseStream stream, BasisBoneHandle root, BasisBoneHandle mid, int slot)
        {
            if (!legDiagnostics.IsCreated || slot < 0 || slot >= legDiagnostics.Length || !handleHips.IsValid(stream))
            {
                return;
            }

            Vector3 femur = mid.GetPosition(stream) - root.GetPosition(stream);
            if (!(femur.sqrMagnitude > 1e-8f))
            {
                return;
            }

            Quaternion hipsRot = handleHips.GetRotation(stream);
            Quaternion hipsInv = Quaternion.Inverse(hipsRot);
            Vector3 femurLocal = (hipsInv * femur).normalized;

            BasisLegDiagnostics d = legDiagnostics[slot];
            // Pelvis frame: -Y is straight down the leg, +Z forward, +X the player's right.
            d.HipFlexionDeg = Mathf.Atan2(femurLocal.z, -femurLocal.y) * Mathf.Rad2Deg;
            d.HipAbductionDeg = Mathf.Atan2(femurLocal.x, -femurLocal.y) * Mathf.Rad2Deg;
            d.FemurTwistDeg = TwistDeg(hipsInv * root.GetRotation(stream), femurLocal);
            legDiagnostics[slot] = d;
        }
        void SmoothKneeSwivel(BasisPoseStream stream, BasisBoneHandle root, BasisBoneHandle mid, BasisBoneHandle tip, int slot, float dt, float minCutoffHz, float beta, float derivCutoffHz, bool conditionOnPole, bool holdWhenSingular)
        {
            if (!legSwivelInit.IsCreated || slot < 0 || slot >= legSwivelInit.Length || !handleHips.IsValid(stream))
            {
                return;
            }
            BasisSwivelSmootherInput input = default;
            input.Root = root.GetPosition(stream);
            input.Mid = mid.GetPosition(stream);
            input.Tip = tip.GetPosition(stream);
            input.BodyRotation = handleHips.GetRotation(stream);
            input.ReferenceLocal = Vector3.forward;
            input.FallbackLocal = Vector3.right;
            input.TransportHomeLocal = Vector3.down;
            input.Dt = dt;
            input.MinCutoffHz = minCutoffHz;
            input.Beta = beta;
            input.DerivCutoffHz = derivCutoffHz;
            input.ConditionOnPole = conditionOnPole;
            input.SingularMinCutoffHz = BasisSwivelFilterCore.MinCutoffHz;
            input.GuardAnteriorHalfSpace = true;
            input.AnteriorSoftDeg = BasisLegSolveCore.KneeAnteriorSoftDeg;
            input.AnteriorHardDeg = BasisLegSolveCore.KneeAnteriorHardDeg;
            input.HoldWhenSingular = holdWhenSingular;
            input.HoldCondLo = BasisSwivelSmootherCore.DefaultHoldCondLo;
            input.HoldCondHi = BasisSwivelSmootherCore.DefaultHoldCondHi;
            input.State = new BasisSwivelFilterState { Raw = legSwivelRaw[slot].x, Vel = legSwivelRaw[slot].y, Smooth = legSwivelSmooth[slot].x };
            input.Seeded = legSwivelInit[slot] != 0;

            BasisSwivelSmootherCore.Solve(input, out BasisSwivelSmootherResult result);
            if (legDiagnostics.IsCreated && slot < legDiagnostics.Length)
            {
                BasisLegDiagnostics d = legDiagnostics[slot];
                d.RawSwivelDeg = result.RawSwivelDeg;
                d.SmoothSwivelDeg = result.SmoothSwivelDeg;
                d.Conditioning = result.Conditioning;
                d.HoldGate = result.HoldGate;
                d.AnteriorGuardApplied = result.AnteriorGuardApplied ? 1f : 0f;
                d.Seeded = result.Seeded ? 1f : 0f;
                legDiagnostics[slot] = d;
            }
            if (result.WriteState)
            {
                legSwivelRaw[slot] = new Vector3(result.State.Raw, result.State.Vel, 0f);
                legSwivelSmooth[slot] = new Vector3(result.State.Smooth, 0f, 0f);
                legSwivelInit[slot] = 1;
            }
            if (!result.Valid)
            {
                return;
            }

            Vector3 preFoot = input.Tip;
            Quaternion preFootRot = tip.GetRotation(stream);
            SwingElbowAroundAC(stream, root, mid, tip, result.DesiredMid);
            tip.SetPosition(stream, preFoot);
            tip.SetRotation(stream, preFootRot);
        }
        public void ApplyToeSurfaceBend(BasisPoseStream stream, BasisBoneHandle handle, float bendDeg, Vector3 axis)
        {
            if (!handle.IsValid(stream)) return;
            if (Mathf.Abs(bendDeg) < 0.01f || axis.sqrMagnitude < 1e-6f) return;

            Quaternion current = handle.GetRotation(stream);
            handle.SetRotation(stream, Quaternion.AngleAxis(-bendDeg, axis.normalized) * current);
        }
    }
}
