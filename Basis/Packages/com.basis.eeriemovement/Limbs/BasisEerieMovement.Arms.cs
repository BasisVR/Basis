using System.Runtime.CompilerServices;
using Basis.Scripts.Common;
using Unity.Collections;
using UnityEngine;
namespace Basis.IK
{
    public partial struct BasisEerieMovement
    {
        void SolveShoulderPass()
        {
            SolveShoulder(true);
            SolveShoulder(false);
            if (plan.leftShoulder == BasisEerieShoulderMode.Tracker) poseStream.SetRotation(handleLeftShoulder, targetRotationLeftShoulder * offsetRotationLeftShoulder);
            if (plan.rightShoulder == BasisEerieShoulderMode.Tracker) poseStream.SetRotation(handleRightShoulder, targetRotationRightShoulder * offsetRotationRightShoulder);
            if (plan.shoulderSlide)
            {
                ApplyShoulderSlide();
            }
        }
        void SolveArmPass()
        {
            SolveHand(true);
            SolveHand(false);

            if (plan.leftArm.solve)
            {
                ApplySwingContinuity(swingLeftElbow, handleLeftUpperArm, handleLeftLowerArm, handleLeftHand, targetPositionLeftHand);
            }

            if (plan.rightArm.solve)
            {
                ApplySwingContinuity(swingRightElbow, handleRightUpperArm, handleRightLowerArm, handleRightHand, targetPositionRightHand);
            }

            //if (plan.leftArm.lowerTwist) SolveArmTwist(handleLeftLowerArm, handleLeftHand, handleLeftLowerArmTwist, lowerArmTwistFraction, tposeLeftLowerArmChildBind, tposeLeftLowerArmTwistBind);
            //if (plan.rightArm.lowerTwist) SolveArmTwist(handleRightLowerArm, handleRightHand, handleRightLowerArmTwist, lowerArmTwistFraction, tposeRightLowerArmChildBind, tposeRightLowerArmTwistBind);
            //if (plan.leftArm.upperTwist) SolveArmTwist(handleLeftUpperArm, handleLeftLowerArm, handleLeftUpperArmTwist, upperArmTwistFraction, tposeLeftUpperArmChildBind, tposeLeftUpperArmTwistBind);
            //if (plan.rightArm.upperTwist) SolveArmTwist(handleRightUpperArm, handleRightLowerArm, handleRightUpperArmTwist, upperArmTwistFraction, tposeRightUpperArmChildBind, tposeRightUpperArmTwistBind);
        }
        void ApplyShoulderSlide()
        {
            Quaternion hipsRot = poseStream.GetRotation(handleHips) * Quaternion.Inverse(offsetRotationHips);
            Quaternion chestRot = poseStream.GetRotation(handleChest);
            Quaternion chestLocal = Quaternion.Inverse(hipsRot) * chestRot;
            float chestYaw = BasisTwistSolveCore.SignedTwistAngleDeg(chestLocal, Vector3.up);
            float excess = Mathf.Abs(chestYaw) - shoulderSlideStartDeg;
            if (excess <= 0f)
                return;

            float counterYaw = -Mathf.Sign(chestYaw) * Mathf.Min(excess * shoulderSlideFraction, shoulderSlideMaxDeg);
            if (plan.hasLeftShoulder) ApplyShoulderYaw(handleLeftShoulder, hipsRot, counterYaw);
            if (plan.hasRightShoulder) ApplyShoulderYaw(handleRightShoulder, hipsRot, counterYaw);
        }
        void ApplyShoulderYaw(BasisBoneHandle shoulder, Quaternion hipsRot, float yawDeg)
        {
            Quaternion delta = hipsRot * Quaternion.AngleAxis(yawDeg, Vector3.up) * Quaternion.Inverse(hipsRot);
            poseStream.SetRotation(shoulder, delta * poseStream.GetRotation(shoulder));
        }
        void ApplyArmSwingChestFollow()
        {
            float factor = chestArmSwingFactor;
            bool leftEnabled = plan.leftArm.weight > 0f, rightEnabled = plan.rightArm.weight > 0f;
            Vector3 leftPos = leftEnabled ? targetPositionLeftHand : Vector3.zero;
            Vector3 rightPos = rightEnabled ? targetPositionRightHand : Vector3.zero;
            Vector3 handMid = leftEnabled && rightEnabled ? (leftPos + rightPos) * 0.5f : leftEnabled ? leftPos : rightPos;
            Vector3 hipsPos = poseStream.GetPosition(handleHips);
            Quaternion hipsAnat = poseStream.GetRotation(handleHips) * Quaternion.Inverse(offsetRotationHips);
            Quaternion invHipsAnat = Quaternion.Inverse(hipsAnat);
            Vector3 localMid = invHipsAnat * (handMid - hipsPos);
            float forwardDist = Mathf.Max(0.1f, Mathf.Abs(localMid.z));
            float yawDeg = Mathf.Atan2(localMid.x, forwardDist) * Mathf.Rad2Deg * factor;
            Vector3 localMidChest = invHipsAnat * (handMid - poseStream.GetPosition(handleChest));
            float pitchDeg = Mathf.Atan2(-localMidChest.y, forwardDist) * Mathf.Rad2Deg * factor;
            float maxDeg = chestArmSwingMaxDeg;
            if (maxDeg > 0f)
            {
                yawDeg = Mathf.Clamp(yawDeg, -maxDeg, maxDeg);
                pitchDeg = Mathf.Clamp(pitchDeg, -maxDeg, maxDeg);
            }

            Quaternion local = Quaternion.AngleAxis(yawDeg, Vector3.up) * Quaternion.AngleAxis(pitchDeg, Vector3.right);
            Quaternion deltaWorld = hipsAnat * local * invHipsAnat;

            if (plan.hasUpperChest)
            {
                Quaternion chestPart = Quaternion.Slerp(Quaternion.identity, deltaWorld, chestFollowChestShare);
                Quaternion upperPart = Quaternion.Slerp(Quaternion.identity, deltaWorld, 1f - chestFollowChestShare);
                poseStream.SetRotation(handleChest, chestPart * poseStream.GetRotation(handleChest));
                poseStream.SetRotation(handleUpperChest, upperPart * poseStream.GetRotation(handleUpperChest));
            }
            else
            {
                poseStream.SetRotation(handleChest, deltaWorld * poseStream.GetRotation(handleChest));
            }
        }
        void SolveArmTwist(BasisBoneHandle parent, BasisBoneHandle child, BasisBoneHandle twist, float fraction, Quaternion childBind, Quaternion twistBind)
        {
            Vector3 parentPos = poseStream.GetPosition(parent), childPos = poseStream.GetPosition(child);
            float positionFraction = BasisTwistSolveCore.SegmentPositionFraction(parentPos, childPos, poseStream.GetPosition(twist));

            if (BasisTwistSolveCore.Solve(poseStream.GetRotation(parent), poseStream.GetRotation(child), childPos - parentPos, positionFraction * fraction, childBind, twistBind, out Quaternion twistWorld, out _, out _))
            {
                poseStream.SetRotation(twist, twistWorld);
            }
        }
        public void SolveShoulder(bool isLeft)
        {
            if ((isLeft ? plan.leftShoulder : plan.rightShoulder) != BasisEerieShoulderMode.Solve)
            {
                return;
            }
            BasisBoneHandle shoulderHandle = isLeft ? handleLeftShoulder : handleRightShoulder;

            BasisShoulderSolveInput input;
            input.ShoulderPos = poseStream.GetPosition(shoulderHandle);
            input.HandTargetPos = isLeft ? targetPositionLeftHand : targetPositionRightHand;
            input.ElbowPos = isLeft ? hintPositionLeftHand : hintPositionRightHand;
            input.HasElbow = isLeft ? plan.leftArm.trackerHint : plan.rightArm.trackerHint;
            input.HasShoulderTracker = isLeft ? plan.leftShoulderTracked : plan.rightShoulderTracked;

            input.ChestRot = plan.hasChestRef ? poseStream.GetRotation(plan.chestRef) : Quaternion.identity;
            input.TposeChestRot = tposeChestRot;
            input.TposeShoulderRot = isLeft ? tposeLeftShoulderRot : tposeRightShoulderRot;
            input.TposeArmDirWorld = isLeft ? tposeLeftShoulderLocalDir : tposeRightShoulderLocalDir;
            input.TposeArmLength = isLeft ? tposeShoulderToHandLeft : tposeShoulderToHandRight;
            input.TposeClavicleLength = isLeft ? tposeClavicleLenLeft : tposeClavicleLenRight;
            input.TposeElbowLength = isLeft ? tposeShoulderToElbowLeft : tposeShoulderToElbowRight;
            input.ShrugEnabled = shoulderShrugEnabled;
            input.ElevationFactor = shoulderElevationFactor;
            input.ProtractionFactor = shoulderProtractionFactor;
            input.CoupleRatio = shoulderCoupleRatio;
            input.MaxShoulderDeg = shoulderMaxDeg;
            input.TrackerFinal = isLeft ? targetRotationLeftShoulder * offsetRotationLeftShoulder : targetRotationRightShoulder * offsetRotationRightShoulder;
            input.IsLeft = isLeft;

            BasisShoulderSolveCore.Solve(input, out BasisShoulderSolveResult result);
            if (result.Apply)
            {
                poseStream.SetRotation(shoulderHandle, result.ShoulderRotation);
            }
        }
        BasisSwivelFrame BuildArmFrame()
        {
            if (!plan.hasBodyRight || !plan.hasTorso)
            {
                return default;
            }

            return BasisSwivelHintCore.BuildFrame( poseStream.GetPosition(handleLeftUpperArm), poseStream.GetPosition(handleRightUpperArm), poseStream.GetPosition(plan.torsoFrom), poseStream.GetPosition(plan.torsoTo));
        }
        public void SwingElbowAroundAC(BasisBoneHandle root, BasisBoneHandle mid, BasisBoneHandle tip, Vector3 desiredB)
        {
            Vector3 A = poseStream.GetPosition(root), C = poseStream.GetPosition(tip), B = poseStream.GetPosition(mid);
            Vector3 AC = C - A;
            float acSqr = Vector3.Dot(AC, AC);
            if (acSqr <= sqrEpsilon) return;

            Vector3 n = AC / Mathf.Sqrt(acSqr);
            Vector3 v1 = B - A; v1 -= n * Vector3.Dot(v1, n);
            Vector3 v2 = desiredB - A; v2 -= n * Vector3.Dot(v2, n);

            float v1Sqr = Vector3.Dot(v1, v1), v2Sqr = Vector3.Dot(v2, v2);
            if (v1Sqr <= sqrEpsilon || v2Sqr <= sqrEpsilon) return;

            v1 /= Mathf.Sqrt(v1Sqr);
            v2 /= Mathf.Sqrt(v2Sqr);

            float dot = Mathf.Clamp(Vector3.Dot(v1, v2), -1f, 1f), ang = Mathf.Acos(dot);
            Vector3 cross = Vector3.Cross(v1, v2);
            float dir = Mathf.Sign(Vector3.Dot(cross, n));
            Quaternion swing = Quaternion.AngleAxis(ang * dir * Mathf.Rad2Deg, n);

            poseStream.SetRotation(root, swing * poseStream.GetRotation(root));
        }
        void ApplySwingContinuity(int slot, BasisBoneHandle root, BasisBoneHandle mid, BasisBoneHandle tip, Vector3 targetPos)
        {
            if (!plan.hasSwingState)
            {
                return;
            }

            Vector3 a = poseStream.GetPosition(root), c = poseStream.GetPosition(tip), b = poseStream.GetPosition(mid);

            ref BasisSwingContinuityState state = ref Ref(swingContinuity, slot);
            int collided = plan.hasArmState ? armState[slot].Collided : 0;

            if (!BasisSwingContinuityCore.Step(ref state, a, b, c, targetPos, collided, swingSmoothRateDeg, poseStream.deltaTime, out bool applySwing, out Vector3 newDir))
            {
                return;
            }

            if (applySwing)
            {
                Quaternion preservedHandRot = poseStream.GetRotation(tip);
                SwingElbowAroundAC(root, mid, tip, a + newDir);
                poseStream.SetPosition(tip, c);
                poseStream.SetRotation(tip, preservedHandRot);
            }
            state.Seeded = true;
        }
        public void SolveHand(bool isLeft)
        {
            BasisEerieArmPlan arm = isLeft ? plan.leftArm : plan.rightArm;
            if (!arm.solve)
            {
                return;
            }
            float weight = arm.weight;
            BasisBoneHandle root = isLeft ? handleLeftUpperArm : handleRightUpperArm;
            BasisBoneHandle mid = isLeft ? handleLeftLowerArm : handleRightLowerArm;
            BasisBoneHandle tip = isLeft ? handleLeftHand : handleRightHand;
            BasisBoneHandle rootTwist = isLeft ? handleLeftUpperArmTwist : handleRightUpperArmTwist;
            BasisBoneHandle midTwist = isLeft ? handleLeftLowerArmTwist : handleRightLowerArmTwist;
            Vector3 tgtPos = isLeft ? targetPositionLeftHand : targetPositionRightHand;
            Quaternion tgtRot = isLeft ? targetRotationLeftHand : targetRotationRightHand;
            Vector3 hintPos = isLeft ? hintPositionLeftHand : hintPositionRightHand;
            Quaternion hintRot = isLeft ? hintRotationLeftHand : hintRotationRightHand;
            Quaternion targetOffset = isLeft ? offsetRotationLeftHand : offsetRotationRightHand;
            int swingSlot = isLeft ? swingLeftElbow : swingRightElbow;
            bool slotOk = plan.hasArmState;
            Quaternion origRootRot = poseStream.GetRotation(root), origMidRot = poseStream.GetRotation(mid);
            Quaternion origTipRot = poseStream.GetRotation(tip);
            ResetToRest(root, mid, tip);
            if (arm.hasUpperTwist) poseStream.ResetToRest(isLeft ? handleLeftUpperArmTwist : handleRightUpperArmTwist);
            if (arm.hasLowerTwist) poseStream.ResetToRest(isLeft ? handleLeftLowerArmTwist : handleRightLowerArmTwist);
            Vector3 bodyRight = plan.hasBodyRight ? poseStream.GetPosition(handleRightUpperArm) - poseStream.GetPosition(handleLeftUpperArm) : Vector3.zero;
            bool usedModel = false;

            if (!arm.trackerHint)
            {
                BasisArmSlotState none = default;
                ref BasisArmSlotState hintState = ref (slotOk ? ref Ref(armState, swingSlot) : ref none);
                usedModel = BasisArmHintCore.Solve(BuildArmFrame(), poseStream.GetPosition(root), poseStream.GetPosition(mid), poseStream.GetPosition(tip), tgtPos, isLeft, plan.hasHips ? poseStream.GetRotation(handleHips) : Quaternion.identity, slotOk, ref hintState, arm.elbowDrag, elbowDragHz, poseStream.deltaTime, out Vector3 modelHint);
                if (usedModel)
                {
                    hintPos = modelHint;
                }
            }
            if (!usedModel && slotOk)
            {
                Ref(armState, swingSlot).HintSeeded = false;
            }

            bool hasHint = arm.trackerHint || usedModel, hintIsTracker = arm.trackerHint;
            BasisArmSolveInput input = default;
            poseStream.GetPositionAndRotation(root, out Vector3 rootPos, out Quaternion rootRot);
            poseStream.GetPositionAndRotation(mid, out Vector3 elbowPos, out Quaternion elbowRot);
            poseStream.GetPositionAndRotation(tip, out Vector3 handPos, out Quaternion handRot);
            input.Shoulder = rootPos;
            input.Elbow = elbowPos;
            input.Hand = handPos;
            input.RootRotation = rootRot;
            input.MidRotation = elbowRot;
            input.TargetPosition = tgtPos;
            input.TargetRotation = tgtRot;
            input.HintPosition = hintPos;
            input.HintWeight = hasHint;
            input.TargetOffset = targetOffset;
            input.PlayerUp = playerUp;
            input.HintIsTracker = hintIsTracker;
            input.HintMaxStepDeg = float.MaxValue;
            input.TipRotation = handRot;
            input.HintRotation = hintRot;
            input.HasHintRotation = arm.hintRoll;
            input.ForearmFollowWeight = 1f;
            input.ElbowLateralOut = isLeft ? -bodyRight : bodyRight;

            input.TorsoUp = plan.hasTorso ? poseStream.GetPosition(plan.torsoTo) - poseStream.GetPosition(plan.torsoFrom) : Vector3.zero;

            bool anchorSlot = arm.poleAnchor;
            if (slotOk)
            {
                BasisArmSlotState armSlot = armState[swingSlot];
                input.PrevGuardSide = armSlot.GuardSide;
                if (anchorSlot && armSlot.PoleValid)
                {
                    input.PrevPoleDir = armSlot.PoleDir;
                    input.PrevHintRotation = armSlot.PoleRot;
                    input.HasPrevPole = true;
                }
            }

            BasisArmSolveCore.Solve(input, out BasisArmSolveResult result, gizmos);

            if (slotOk)
            {
                ref BasisArmSlotState armSlot = ref Ref(armState, swingSlot);
                armSlot.GuardSide = result.GuardSideUsed;
                if (anchorSlot)
                {
                    if (result.PoleAnchorValid)
                    {
                        armSlot.PoleDir = result.PoleDirUsed;
                        armSlot.PoleRot = result.PoleRotUsed;
                        armSlot.PoleValid = true;
                    }
                }
                else
                {
                    armSlot.PoleValid = false;
                }
            }

            //poseStream.SetRotation(mid, result.MidDelta * poseStream.GetRotation(mid));
            //poseStream.SetRotation(root, result.RootDelta * poseStream.GetRotation(root));
            //poseStream.SetRotation(root, result.HintDelta * poseStream.GetRotation(root));
            //poseStream.SetRotation(mid, result.MidPostRoll * poseStream.GetRotation(mid));
            poseStream.SetRotation(root, result.RootRotation);
            poseStream.SetRotation(mid, result.MidRotation);
            if (arm.hasUpperTwist) poseStream.SetRotation(rootTwist, poseStream.GetRotation(root));
            if (arm.hasLowerTwist) poseStream.SetRotation(midTwist, poseStream.GetRotation(mid));
            poseStream.SetRotation(tip, result.TipRotation);

            int collisionState = 0;
            if (arm.elbowProtect)
            {
                BasisElbowProtectInput epi = default;
                epi.Shoulder = poseStream.GetPosition(root);
                epi.Elbow = poseStream.GetPosition(mid);
                epi.Hand = poseStream.GetPosition(tip);
                epi.HasHips = plan.hasHips;
                epi.HasSpine = plan.hasSpine;
                epi.HipsPos = epi.HasHips ? poseStream.GetPosition(handleHips) : Vector3.zero;
                epi.SpinePos = epi.HasSpine ? poseStream.GetPosition(handleSpine) : Vector3.zero;
                epi.ChestPos = poseStream.GetPosition(handleChest);
                epi.NeckPos = poseStream.GetPosition(handleNeck);
                epi.ChestRadiusBase = chestRadius;
                epi.CollisionSkin = collisionSkin;
                epi.HandRadius = handRadius;
                epi.HandSkin = handSkin;
                epi.PlayerUp = playerUp;
                epi.BodyRight = bodyRight;

                BasisElbowProtectCore.Solve(epi, out BasisElbowProtectResult epr);
                if (epr.Engaged)
                {
                    poseStream.GetPositionAndRotation(tip, out Vector3 preservedHandPos, out Quaternion preservedHandRot);
                    SwingElbowAroundAC(root, mid, tip, epr.DesiredElbow);
                    poseStream.SetPosition(tip, preservedHandPos);
                    poseStream.SetRotation(tip, preservedHandRot);
                }
                collisionState = epr.CollisionState;
            }

            if (slotOk)
            {
                Ref(armState, swingSlot).Collided = collisionState;
            }

            if (weight < 1f)
            {
                poseStream.SetRotation(root, Quaternion.Slerp(origRootRot, poseStream.GetRotation(root), weight));
                poseStream.SetRotation(mid, Quaternion.Slerp(origMidRot, poseStream.GetRotation(mid), weight));
                poseStream.SetRotation(tip, Quaternion.Slerp(origTipRot, poseStream.GetRotation(tip), weight));
            }
        }
    }
}
