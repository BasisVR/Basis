using System.Runtime.CompilerServices;
using Basis.Scripts.Common;
using Unity.Collections;
using UnityEngine;
namespace Basis.IK
{
    public partial struct BasisEerieMovement
    {
        void SolveLegPass()
        {
            SolveLeg(0);
            SolveLeg(1);
        }
        void SolveToePass()
        {
            if (plan.leftToeDriven) poseStream.SetRotation(handleLeftToe, leftDrivenTargetRot * offsetRotationLeftToe);
            else if (plan.leftToeSurface) ApplyToeSurfaceBend(handleLeftToe, leftToeBendDeg, leftToeBendAxis);

            if (plan.rightToeDriven) poseStream.SetRotation(handleRightToe, rightDrivenTargetRot * offsetRotationRightToe);
            else if (plan.rightToeSurface) ApplyToeSurfaceBend(handleRightToe, rightToeBendDeg, rightToeBendAxis);
        }
        BasisSwivelFrame BuildLegFrame()
        {
            if (!plan.hasLegFrame)
            {
                return default;
            }

            return BasisSwivelHintCore.BuildFrame( poseStream.GetPosition(handleLeftUpperLeg), poseStream.GetPosition(handleRightUpperLeg), poseStream.GetPosition(handleHips), poseStream.GetPosition(plan.legFrameTo));
        }
        // Hips facing used to place our own knee hint. Falls back to the hips bone rotation if the leg frame can't be built.
        BasisSwivelFrame BuildLegBodyFrame()
        {
            BasisSwivelFrame f = BuildLegFrame();
            if (f.Valid || !plan.hasHips)
            {
                return f;
            }

            Quaternion hipsRot = poseStream.GetRotation(handleHips) * Quaternion.Inverse(offsetRotationHips);
            f.Up = hipsRot * Vector3.up;
            f.Right = hipsRot * Vector3.right;
            f.Forward = hipsRot * Vector3.forward;
            f.Valid = true;
            return f;
        }
        public void SolveLeg(int legSlot)
        {
            bool isLeft = legSlot == 0;
            BasisEerieLegPlan leg = isLeft ? plan.leftLeg : plan.rightLeg;
            if (!leg.solve)
            {
                return;
            }

            float posWeight = leg.weight;
            BasisBoneHandle root = isLeft ? handleLeftUpperLeg : handleRightUpperLeg;
            BasisBoneHandle mid = isLeft ? handleLeftLowerLeg : handleRightLowerLeg;
            BasisBoneHandle tip = isLeft ? handleLeftFoot : handleRightFoot;
            Vector3 hint = isLeft ? hintPositionLeftLowerLeg : hintPositionRightLowerLeg;
            Quaternion hintRot = isLeft ? hintRotationLeftLowerLeg : hintRotationRightLowerLeg;
            Quaternion targetOffset = isLeft ? offsetRotationLeftFoot : offsetRotationRightFoot;
            Vector3 bendNormal = isLeft ? kneeBendPrefLeft : kneeBendPrefRight;
            bool hintIsTracker = leg.kneeTracked;
            Quaternion origRootRot = poseStream.GetRotation(root), origMidRot = poseStream.GetRotation(mid);
            Quaternion origTipRot = poseStream.GetRotation(tip);
            ResetToRest(root, mid, tip);
            Quaternion tRot = isLeft ? targetRotationLeftLowerLeg : targetRotationRightLowerLeg;
            bool preserveTip = leg.preserveTip;
            if (preserveTip) tRot = origTipRot;

            Vector3 targetPos = isLeft ? targetPositionLeftLowerLeg : targetPositionRightLowerLeg;

            // If no knee tracker, then we build our own knee hint from the hips frame
            BasisSwivelFrame bodyFrame = hintIsTracker ? default : BuildLegBodyFrame();

            BasisLegSolveInput input = default;
            poseStream.GetPositionAndRotation(root, out Vector3 rootPos, out Quaternion rootRot);
            poseStream.GetPositionAndRotation(mid, out Vector3 midPos, out Quaternion midRot);
            input.Root = rootPos;
            input.Mid = midPos;
            input.Tip = poseStream.GetPosition(tip);
            input.RootRotation = rootRot;
            input.MidRotation = midRot;
            input.TargetPosition = targetPos;
            input.TargetRotation = tRot;
            input.HintPosition = hint;
            input.HintWeight = hintIsTracker ? 1f : 0f;
            input.TargetOffset = targetOffset;
            input.BendNormal = bendNormal;
            input.AnteriorNormal = kneeAnteriorRef;
            input.HintRotation = hintRot;
            input.HasHintRotation = leg.hintRoll;
            input.HintIsTracker = hintIsTracker;
            input.HasBodyFrame = bodyFrame.Valid;
            input.HipsForward = bodyFrame.Forward;
            input.FootForward = (isLeft ? targetRotationLeftLowerLeg : targetRotationRightLowerLeg) * Vector3.forward; // Foot target's control rotation, before any preserveTip swap
            input.Up = playerUp;

            BasisLegSolveCore.Solve(input, out BasisLegSolveResult result);

            if (plan.hasLegDiagnostics)
            {
                ref BasisLegDiagnostics d = ref Ref(legDiagnostics, legSlot);
                d.ReachRatio = result.ReachRatio;
                d.KneeAngleDeg = result.KneeAngleDeg;
                d.AxisSource = result.AxisSource;
                d.HintApplied = result.HintApplied ? 1f : 0f;
                d.ShinRollDeg = result.ShinRollDeg;
            }

            poseStream.SetRotation(mid, result.MidDelta * poseStream.GetRotation(mid));
            poseStream.SetRotation(root, result.RootDelta * poseStream.GetRotation(root));
            poseStream.SetRotation(root, result.HintDelta * poseStream.GetRotation(root));
            poseStream.SetRotation(mid, result.MidPostRoll * poseStream.GetRotation(mid));
            poseStream.SetRotation(tip, result.TipRotation);
            Quaternion shinRoll = result.MidPostRoll;

            if (posWeight < 1f)
            {
                Quaternion solvedRootRot = poseStream.GetRotation(root), solvedMidRot = poseStream.GetRotation(mid), solvedTipRot = poseStream.GetRotation(tip);
                poseStream.SetRotation(root, Quaternion.Slerp(origRootRot, solvedRootRot, posWeight));
                poseStream.SetRotation(mid, Quaternion.Slerp(origMidRot, solvedMidRot, posWeight));
                poseStream.SetRotation(tip, Quaternion.Slerp(origTipRot, solvedTipRot, posWeight));
            }
            if (preserveTip)
            {
                Quaternion carriedTip = shinRoll * origTipRot;
                poseStream.SetRotation(tip, posWeight < 1f ? Quaternion.Slerp(origTipRot, carriedTip, posWeight) : carriedTip);
            }

            RecordHipDiagnostics(root, mid, legSlot);

            // Store gizmo data, same as the arm state does.
            if (plan.hasLegState)
            {
                ref BasisLegSlotState state = ref Ref(legState, legSlot);
                Vector3 hipPos = poseStream.GetPosition(root), axis = poseStream.GetPosition(tip) - hipPos, kneeDir = poseStream.GetPosition(mid) - hipPos;
                if (axis.sqrMagnitude > sqrEpsilon) kneeDir = Vector3.ProjectOnPlane(kneeDir, axis.normalized);
                state.KneeDir = kneeDir.sqrMagnitude > sqrEpsilon ? kneeDir.normalized : Vector3.zero;
                state.PoleDir = result.PoleDir;
                state.ConstrainedHintPosition = result.HintPosition;
            }
        }
        void RecordHipDiagnostics(BasisBoneHandle root, BasisBoneHandle mid, int slot)
        {
            if (!plan.hasLegDiagnostics || !plan.hasHips)
            {
                return;
            }

            Vector3 femur = poseStream.GetPosition(mid) - poseStream.GetPosition(root);
            if (!(femur.sqrMagnitude > 1e-8f))
            {
                return;
            }

            Quaternion hipsRot = poseStream.GetRotation(handleHips) * Quaternion.Inverse(offsetRotationHips), hipsInv = Quaternion.Inverse(hipsRot);
            Vector3 femurLocal = (hipsInv * femur).normalized;

            ref BasisLegDiagnostics d = ref Ref(legDiagnostics, slot);
            d.HipFlexionDeg = Mathf.Atan2(femurLocal.z, -femurLocal.y) * Mathf.Rad2Deg;
            d.HipAbductionDeg = Mathf.Atan2(femurLocal.x, -femurLocal.y) * Mathf.Rad2Deg;
            d.FemurTwistDeg = TwistDeg(hipsInv * poseStream.GetRotation(root), femurLocal);
        }
        public void ApplyToeSurfaceBend(BasisBoneHandle handle, float bendDeg, Vector3 axis)
        {
            Quaternion current = poseStream.GetRotation(handle);
            poseStream.SetRotation(handle, Quaternion.AngleAxis(-bendDeg, axis.normalized) * current);
        }
    }
}
