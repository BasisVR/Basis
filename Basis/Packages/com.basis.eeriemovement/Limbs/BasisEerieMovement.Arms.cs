using Basis.Scripts.Common;
using Unity.Collections;
using UnityEngine;
namespace Basis.IK
{
    public partial struct BasisEerieMovement
    {
        public const int armLeft = 0, armRight = 1, armCount = 2;
        BasisSwivelFrame BuildArmFrame()
        {
            if (plan.hasBodyRight && plan.torsoTo.IsBound)
            {
                BasisBoneHandle from = plan.hasHips ? handleHips : plan.torsoFrom;
                BasisSwivelFrame f = BasisSwivelHintCore.BuildFrame(poseStream.GetPosition(handleLeftUpperArm), poseStream.GetPosition(handleRightUpperArm), poseStream.GetPosition(from), poseStream.GetPosition(plan.torsoTo));
                if (f.Valid) return f;
            }
            BasisSwivelFrame fallback = default;
            Vector3 right = targetRotationHead * Vector3.right;
            right -= playerUp * Vector3.Dot(right, playerUp);
            if (right.sqrMagnitude < sqrEpsilon) return fallback;
            fallback.Up = playerUp;
            fallback.Right = right.normalized;
            fallback.Forward = Vector3.Cross(fallback.Right, fallback.Up);
            fallback.Valid = true;
            return fallback;
        }
        void SolveShoulderPass()
        {
            if (plan.leftShoulder == BasisEerieShoulderMode.None && plan.rightShoulder == BasisEerieShoulderMode.None)
            {
                return;
            }
            BasisSwivelFrame frame = BuildArmFrame();
            SolveShoulder(true, plan.leftShoulder, frame);
            SolveShoulder(false, plan.rightShoulder, frame);
        }
        void SolveShoulder(bool isLeft, BasisEerieShoulderMode mode, in BasisSwivelFrame frame)
        {
            BasisBoneHandle shoulder = isLeft ? handleLeftShoulder : handleRightShoulder, upperArm = isLeft ? handleLeftUpperArm : handleRightUpperArm;
            if (mode == BasisEerieShoulderMode.Tracker)
            {
                poseStream.SetRotation(shoulder, (isLeft ? targetRotationLeftShoulder : targetRotationRightShoulder) * (isLeft ? offsetRotationLeftShoulder : offsetRotationRightShoulder));
                return;
            }
            BasisEerieArmPlan arm = isLeft ? plan.leftArm : plan.rightArm;
            if (mode != BasisEerieShoulderMode.Solve || !frame.Valid || !arm.solve)
            {
                return;
            }
            Quaternion origRot = poseStream.GetRotation(shoulder);
            poseStream.ResetToRest(shoulder);
            BasisShoulderSolveInput input = default;
            input.ShoulderPos = poseStream.GetPosition(shoulder);
            input.UpperArmPos = poseStream.GetPosition(upperArm);
            input.HandTargetPos = isLeft ? targetPositionLeftHand : targetPositionRightHand;
            input.ArmLength = (isLeft ? tposeShoulderToHandLeft - tposeClavicleLenLeft : tposeShoulderToHandRight - tposeClavicleLenRight);
            input.TorsoUp = frame.Up;
            input.TorsoForward = frame.Forward;
            input.TorsoOut = isLeft ? -frame.Right : frame.Right;
            input.ShrugEnabled = shoulderShrugEnabled;
            input.ElevationFactor = shoulderElevationFactor;
            input.ProtractionFactor = shoulderProtractionFactor;
            input.MaxDeg = shoulderMaxDeg;
            BasisShoulderSolveCore.Solve(input, out BasisShoulderSolveResult result);
            if (!result.Apply)
            {
                return;
            }
            Quaternion solved = result.Delta * poseStream.GetRotation(shoulder);
            poseStream.SetRotation(shoulder, arm.weight < 1f ? Quaternion.Slerp(origRot, solved, arm.weight) : solved);
        }
        void SolveArmPass()
        {
            BasisSwivelFrame frame = BuildArmFrame();
            SolveArm(armLeft, frame);
            SolveArm(armRight, frame);
        }
        public void SolveHand(bool isLeft) => SolveArm(isLeft ? armLeft : armRight, BuildArmFrame());
        public void SolveArm(int slot, in BasisSwivelFrame frame)
        {
            bool isLeft = slot == armLeft;
            BasisEerieArmPlan arm = isLeft ? plan.leftArm : plan.rightArm;
            if (!arm.solve || !frame.Valid)
            {
                return;
            }
            BasisBoneHandle root = isLeft ? handleLeftUpperArm : handleRightUpperArm, mid = isLeft ? handleLeftLowerArm : handleRightLowerArm, tip = isLeft ? handleLeftHand : handleRightHand;
            Quaternion origRootRot = poseStream.GetRotation(root), origMidRot = poseStream.GetRotation(mid), origTipRot = poseStream.GetRotation(tip);
            ResetToRest(root, mid, tip);
            poseStream.GetPositionAndRotation(root, out Vector3 shoulder, out Quaternion restRootRot);
            poseStream.GetPositionAndRotation(mid, out Vector3 elbow, out Quaternion restMidRot);
            poseStream.GetPositionAndRotation(tip, out Vector3 hand, out Quaternion restTipRot);
            BasisArmSolveInput input = default;
            input.Shoulder = shoulder;
            input.RestElbow = elbow;
            input.RestHand = hand;
            input.RestHandRotation = restTipRot;
            input.TargetPosition = isLeft ? targetPositionLeftHand : targetPositionRightHand;
            input.TargetRotation = (isLeft ? targetRotationLeftHand : targetRotationRightHand) * (isLeft ? offsetRotationLeftHand : offsetRotationRightHand);
            input.HasHead = plan.hasHead;
            input.HeadPosition = plan.hasHead ? poseStream.GetPosition(handleHead) : Vector3.zero;
            input.TorsoUp = frame.Up;
            input.TorsoForward = frame.Forward;
            input.TorsoOut = isLeft ? -frame.Right : frame.Right;
            input.IsLeft = isLeft;
            input.HasHint = arm.trackerHint;
            input.HintPosition = isLeft ? hintPositionLeftHand : hintPositionRightHand;
            input.HasHintRotation = arm.hintRoll;
            input.HintRotation = isLeft ? hintRotationLeftHand : hintRotationRightHand;
            input.TorsoCapsule = arm.elbowProtect;
            if (arm.elbowProtect)
            {
                input.TorsoA = poseStream.GetPosition(plan.hasHips ? handleHips : handleChest);
                input.TorsoB = poseStream.GetPosition(handleNeck);
                input.TorsoRadius = chestRadius + collisionSkin;
            }
            input.JointLimits = armJointLimits;
            input.Limits = new BasisArmLimits { PronationMaxDeg = forearmPronationMaxDeg, SupinationMaxDeg = forearmSupinationMaxDeg, HumeralInternalMaxDeg = humeralInternalMaxDeg, HumeralExternalMaxDeg = humeralExternalMaxDeg, WristFlexionMaxDeg = wristFlexionMaxDeg, WristExtensionMaxDeg = wristExtensionMaxDeg, WristRadialMaxDeg = wristRadialMaxDeg, WristUlnarMaxDeg = wristUlnarMaxDeg };
            input.Dt = poseStream.deltaTime;
            input.ReachSoftness = armReachSoftness;
            input.SmoothTime = armSwivelSmoothTime;
            input.MaxRateDeg = armSwivelMaxRateDeg;
            input.SwitchDwell = armSwivelSwitchDwell;
            input.PriorWeight = armPriorWeight;
            input.PreviousWeight = armPreviousWeight;
            BasisArmState scratch = default;
            ref BasisArmState state = ref (plan.hasArmState ? ref Ref(armState, slot) : ref scratch);
            BasisArmSolveCore.Solve(input, ref state, out BasisArmSolveResult result);
            if (!result.Valid)
            {
                return;
            }
            BasisArmSolveCore.Pose(input, result, restRootRot, restMidRot, out Quaternion upperRot, out Quaternion lowerRot);
            poseStream.SetRotation(root, upperRot);
            poseStream.SetRotation(mid, lowerRot);
            poseStream.SetRotation(tip, input.TargetRotation);
            float posWeight = arm.weight;
            if (posWeight < 1f)
            {
                poseStream.SetRotation(root, Quaternion.Slerp(origRootRot, poseStream.GetRotation(root), posWeight));
                poseStream.SetRotation(mid, Quaternion.Slerp(origMidRot, poseStream.GetRotation(mid), posWeight));
                poseStream.SetRotation(tip, Quaternion.Slerp(origTipRot, poseStream.GetRotation(tip), posWeight));
            }
            if (arm.upperTwist) ApplyArmTwist(isLeft ? handleLeftUpperArmTwist : handleRightUpperArmTwist, root, mid, upperArmTwistFraction, isLeft ? tposeLeftUpperArmChildBind : tposeRightUpperArmChildBind, isLeft ? tposeLeftUpperArmTwistBind : tposeRightUpperArmTwistBind);
            if (arm.lowerTwist) ApplyArmTwist(isLeft ? handleLeftLowerArmTwist : handleRightLowerArmTwist, mid, tip, lowerArmTwistFraction, isLeft ? tposeLeftLowerArmChildBind : tposeRightLowerArmChildBind, isLeft ? tposeLeftLowerArmTwistBind : tposeRightLowerArmTwistBind);
        }
        void ApplyArmTwist(BasisBoneHandle twist, BasisBoneHandle parent, BasisBoneHandle child, float fraction, Quaternion childBind, Quaternion twistBind)
        {
            poseStream.GetPositionAndRotation(parent, out Vector3 parentPos, out Quaternion parentRot);
            poseStream.GetPositionAndRotation(child, out Vector3 childPos, out Quaternion childRot);
            if (BasisTwistSolveCore.Solve(parentRot, childRot, childPos - parentPos, fraction, childBind, twistBind, out Quaternion twistWorld, out _, out _))
            {
                poseStream.SetRotation(twist, twistWorld);
            }
        }
        void ApplyArmSwingChestFollow()
        {
            BasisSwivelFrame frame = BuildArmFrame();
            if (!frame.Valid || chestArmSwingMaxDeg <= 0f)
            {
                return;
            }
            float wl = plan.leftArm.solve ? plan.leftArm.weight : 0f, wr = plan.rightArm.solve ? plan.rightArm.weight : 0f, wSum = wl + wr;
            if (wSum <= 0f)
            {
                return;
            }
            Vector3 chestPos = poseStream.GetPosition(handleChest), hands = (targetPositionLeftHand * wl + targetPositionRightHand * wr) / wSum, toHands = hands - chestPos;
            toHands -= playerUp * Vector3.Dot(toHands, playerUp);
            Vector3 forward = frame.Forward - playerUp * Vector3.Dot(frame.Forward, playerUp);
            if (toHands.sqrMagnitude < sqrEpsilon || forward.sqrMagnitude < sqrEpsilon)
            {
                return;
            }
            float yaw = Mathf.Clamp(Vector3.SignedAngle(forward.normalized, toHands.normalized, playerUp) * chestArmSwingFactor, -chestArmSwingMaxDeg, chestArmSwingMaxDeg) * Mathf.Min(wSum, 1f);
            if (Mathf.Abs(yaw) < 1e-3f)
            {
                return;
            }
            float chestShare = plan.hasUpperChest ? Mathf.Clamp01(chestFollowChestShare) : 1f;
            poseStream.SetRotation(handleChest, Quaternion.AngleAxis(yaw * chestShare, playerUp) * poseStream.GetRotation(handleChest));
            if (plan.hasUpperChest && chestShare < 1f)
            {
                poseStream.SetRotation(handleUpperChest, Quaternion.AngleAxis(yaw * (1f - chestShare), playerUp) * poseStream.GetRotation(handleUpperChest));
            }
        }
    }
}
