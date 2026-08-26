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
            float hintW = leg.hintWeight;
            Quaternion targetOffset = isLeft ? offsetRotationLeftFoot : offsetRotationRightFoot;
            Vector3 bendNormal = isLeft ? kneeBendPrefLeft : kneeBendPrefRight;
            bool hintIsTracker = leg.kneeTracked, footIsTracker = leg.tracked;
            Quaternion origRootRot = poseStream.GetRotation(root), origMidRot = poseStream.GetRotation(mid);
            Quaternion origTipRot = poseStream.GetRotation(tip);
            ResetToRest(root, mid, tip);
            Quaternion tRot = isLeft ? targetRotationLeftLowerLeg : targetRotationRightLowerLeg;
            bool preserveTip = leg.preserveTip;
            if (preserveTip) tRot = origTipRot;

            Vector3 targetPos = isLeft ? targetPositionLeftLowerLeg : targetPositionRightLowerLeg, modelHint = default;
            float hintDistrust = 0f, conf = 0f;
            bool usedModelHint = leg.modelHint && BasisLegHintCore.Solve(BuildLegFrame(), poseStream.GetPosition(root), poseStream.GetPosition(mid), poseStream.GetPosition(tip), targetPos, isLeft, out modelHint, out conf, out hintDistrust);
            if (usedModelHint)
            {
                hint = modelHint;
                hintW = 1f;
                if (plan.hasLegDiagnostics)
                {
                    ref BasisLegDiagnostics d = ref Ref(legDiagnostics, legSlot);
                    d.ModelHintUsed = 1f;
                    d.ModelConfidence = conf;
                }
            }

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
            input.HintWeight = hintW;
            input.HintDistrust = hintDistrust;
            input.TargetOffset = targetOffset;
            input.BendNormal = bendNormal;
            input.AnteriorNormal = kneeAnteriorRef;
            input.HintRotation = hintRot;
            input.HasHintRotation = leg.hintRoll;
            input.HintIsTracker = hintIsTracker;

            BasisLegSolveCore.Solve(input, out BasisLegSolveResult result, ref gizmos);

            if (plan.hasLegDiagnostics)
            {
                ref BasisLegDiagnostics d = ref Ref(legDiagnostics, legSlot);
                d.ReachRatio = result.ReachRatio;
                d.KneeAngleDeg = result.KneeAngleDeg;
                d.AxisSource = result.AxisSource;
                d.HintApplied = result.HintApplied ? 1f : 0f;
                d.HintDistrust = hintDistrust;
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
                poseStream.SetRotation(root, Quaternion.Slerp(origRootRot, poseStream.GetRotation(root), posWeight));
                poseStream.SetRotation(mid, Quaternion.Slerp(origMidRot, poseStream.GetRotation(mid), posWeight));
                poseStream.SetRotation(tip, Quaternion.Slerp(origTipRot, poseStream.GetRotation(tip), posWeight));
            }
            if (preserveTip)
            {
                Quaternion carriedTip = shinRoll * origTipRot;
                poseStream.SetRotation(tip, posWeight < 1f ? Quaternion.Slerp(origTipRot, carriedTip, posWeight) : carriedTip);
            }

            RecordHipDiagnostics(root, mid, legSlot);
            if (leg.swivel)
            {
                if (leg.swivelTracked)
                {
                    bool footDerivedPole = !hintIsTracker && footIsTracker && !usedModelHint;
                    SmoothKneeSwivel(root, mid, tip, legSlot, trackedKneeSwivelMinCutoffHz, trackedKneeSwivelBeta, trackedKneeSwivelDerivCutoffHz, conditionOnPole: !hintIsTracker && (!footDerivedPole || kneeFootPoleConditioning), holdWhenSingular: !footDerivedPole || kneeFootPoleHold);
                }
                else
                {
                    SmoothKneeSwivel(root, mid, tip, legSlot, BasisSwivelFilterCore.MinCutoffHz, BasisSwivelFilterCore.Beta, BasisSwivelFilterCore.DerivCutoffHz, conditionOnPole: true, holdWhenSingular: true);
                }
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

            Quaternion hipsRot = poseStream.GetRotation(handleHips), hipsInv = Quaternion.Inverse(hipsRot);
            Vector3 femurLocal = (hipsInv * femur).normalized;

            ref BasisLegDiagnostics d = ref Ref(legDiagnostics, slot);
            d.HipFlexionDeg = Mathf.Atan2(femurLocal.z, -femurLocal.y) * Mathf.Rad2Deg;
            d.HipAbductionDeg = Mathf.Atan2(femurLocal.x, -femurLocal.y) * Mathf.Rad2Deg;
            d.FemurTwistDeg = TwistDeg(hipsInv * poseStream.GetRotation(root), femurLocal);
        }
        void SmoothKneeSwivel(BasisBoneHandle root, BasisBoneHandle mid, BasisBoneHandle tip, int slot, float minCutoffHz, float beta, float derivCutoffHz, bool conditionOnPole, bool holdWhenSingular)
        {
            ref BasisLegSlotState leg = ref Ref(legState, slot);
            BasisSwivelSmootherInput input = default;
            input.Root = poseStream.GetPosition(root);
            input.Mid = poseStream.GetPosition(mid);
            input.Tip = poseStream.GetPosition(tip);
            input.BodyRotation = poseStream.GetRotation(handleHips);
            input.ReferenceLocal = Vector3.forward;
            input.FallbackLocal = Vector3.right;
            input.TransportHomeLocal = Vector3.down;
            input.Dt = poseStream.deltaTime;
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
            input.State = leg.Swivel;
            input.Seeded = leg.SwivelSeeded;

            BasisSwivelSmootherCore.Solve(input, out BasisSwivelSmootherResult result, ref gizmos);
            if (plan.hasLegDiagnostics)
            {
                ref BasisLegDiagnostics d = ref Ref(legDiagnostics, slot);
                d.RawSwivelDeg = result.RawSwivelDeg;
                d.SmoothSwivelDeg = result.SmoothSwivelDeg;
                d.Conditioning = result.Conditioning;
                d.HoldGate = result.HoldGate;
                d.AnteriorGuardApplied = result.AnteriorGuardApplied ? 1f : 0f;
                d.Seeded = result.Seeded ? 1f : 0f;
            }
            if (result.WriteState)
            {
                leg.Swivel = result.State;
                leg.SwivelSeeded = true;
            }
            if (!result.Valid)
            {
                return;
            }

            Vector3 preFoot = input.Tip;
            Quaternion preFootRot = poseStream.GetRotation(tip);
            SwingElbowAroundAC(root, mid, tip, result.DesiredMid);
            poseStream.SetPosition(tip, preFoot);
            poseStream.SetRotation(tip, preFootRot);
        }
        public void ApplyToeSurfaceBend(BasisBoneHandle handle, float bendDeg, Vector3 axis)
        {
            Quaternion current = poseStream.GetRotation(handle);
            poseStream.SetRotation(handle, Quaternion.AngleAxis(-bendDeg, axis.normalized) * current);
        }
    }
}
