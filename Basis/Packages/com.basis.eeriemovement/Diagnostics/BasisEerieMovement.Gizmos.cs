using Unity.Collections;
using UnityEngine;
namespace Basis.IK
{
    public partial struct BasisEerieMovement
    {
        const uint gizmoTarget = 0xFF00D7FFu, gizmoHint = 0xFFFFFF00u, gizmoResidual = 0xFF3030FFu;
        const uint gizmoRaw = 0xFF909090u, gizmoLeft = 0xFF4090FFu, gizmoRight = 0xFFFF9040u, gizmoReach = 0x60FFFFFFu;
        static uint SideColor(bool isLeft) => isLeft ? gizmoLeft : gizmoRight;
        void RecordTargetGizmos()
        {
            const BasisIKGizmoStage stage = BasisIKGizmoStage.Targets;
            if (!gizmos.Wants(stage))
            {
                return;
            }

            gizmos.Point(stage, targetPositionHead, gizmoTarget);
            gizmos.Axes(stage, targetPositionHead, targetRotationHead * offsetRotationHead);
            gizmos.Label(stage, targetPositionHead, "Head target");

            gizmos.Point(stage, targetPositionHips, gizmoTarget);
            gizmos.Axes(stage, targetPositionHips, targetRotationHips * offsetRotationHips);
            if (plan.hipsTracked)
            {
                gizmos.Label(stage, targetPositionHips, "Hips target (tracked)");
            }
            else
            {
                gizmos.Label(stage, targetPositionHips, "Hips target (derived)");
            }
            gizmos.Line(stage, targetPositionHead, targetPositionHips, gizmoTarget);
            gizmos.Direction(stage, targetPositionHips, playerUp, gizmos.AxisLength * 3f, BasisIKGizmoPalette.Green);

            if (plan.chestTracked)
            {
                gizmos.Point(stage, targetPositionChest, gizmoTarget);
                gizmos.Point(stage, targetPositionChestRaw, gizmoRaw);
                gizmos.Line(stage, targetPositionChestRaw, targetPositionChest, gizmoRaw);
                gizmos.Axes(stage, targetPositionChest, targetRotationChest * offsetRotationChest);
                gizmos.Label(stage, targetPositionChest, "Chest target");
            }

            RecordHandTarget(stage, plan.leftArm.weight, targetPositionLeftHand, targetRotationLeftHand * offsetRotationLeftHand, hintPositionLeftHand, plan.leftArm.trackerHint, true);
            RecordHandTarget(stage, plan.rightArm.weight, targetPositionRightHand, targetRotationRightHand * offsetRotationRightHand, hintPositionRightHand, plan.rightArm.trackerHint, false);

            RecordFootTarget(stage, plan.leftLeg.weight, targetPositionLeftLowerLeg, targetRotationLeftLowerLeg * offsetRotationLeftFoot, hintPositionLeftLowerLeg, plan.leftLeg.hintWeight, kneeBendPrefLeft, true);
            RecordFootTarget(stage, plan.rightLeg.weight, targetPositionRightLowerLeg, targetRotationRightLowerLeg * offsetRotationRightFoot, hintPositionRightLowerLeg, plan.rightLeg.hintWeight, kneeBendPrefRight, false);
        }
        void RecordHandTarget(BasisIKGizmoStage stage, float enabled, Vector3 target, Quaternion rotation, Vector3 hint, bool hasHint, bool isLeft)
        {
            if (!(enabled > 0f))
            {
                return;
            }
            uint color = SideColor(isLeft);
            gizmos.Point(stage, target, color);
            gizmos.Axes(stage, target, rotation);
            if (isLeft)
            {
                gizmos.Label(stage, target, "L hand target");
            }
            else
            {
                gizmos.Label(stage, target, "R hand target");
            }

            if (!hasHint)
            {
                return;
            }
            gizmos.Point(stage, hint, gizmoHint);
            gizmos.Line(stage, target, hint, gizmoHint);
            if (isLeft)
            {
                gizmos.Label(stage, hint, "L elbow hint", gizmoHint);
            }
            else
            {
                gizmos.Label(stage, hint, "R elbow hint", gizmoHint);
            }
        }
        void RecordFootTarget(BasisIKGizmoStage stage, float enabled, Vector3 target, Quaternion rotation, Vector3 hint, float hintWeight, Vector3 bendPref, bool isLeft)
        {
            if (!(enabled > 0f))
            {
                return;
            }
            uint color = SideColor(isLeft);
            gizmos.Point(stage, target, color);
            gizmos.Axes(stage, target, rotation);
            if (isLeft)
            {
                gizmos.Label(stage, target, "L foot target");
            }
            else
            {
                gizmos.Label(stage, target, "R foot target");
            }

            if (!(hintWeight > 0f))
            {
                return;
            }
            gizmos.Point(stage, hint, gizmoHint);
            gizmos.Line(stage, target, hint, gizmoHint);
            if (isLeft)
            {
                gizmos.Label(stage, hint, "L knee hint", gizmoHint);
            }
            else
            {
                gizmos.Label(stage, hint, "R knee hint", gizmoHint);
            }
            if (bendPref.sqrMagnitude > sqrEpsilon)
            {
                gizmos.Normal(stage, hint, bendPref.normalized, gizmos.AxisLength, BasisIKGizmoPalette.Magenta);
            }
        }
        void RecordSpineGizmos()
        {
            const BasisIKGizmoStage stage = BasisIKGizmoStage.Spine;
            if (!gizmos.Wants(stage) || !plan.hasSpineChain)
            {
                return;
            }

            uint color = gizmos.StageColor(stage);
            int length = chainHeadToSpine.Length;
            for (int i = length - 1; i > 0; i--)
            {
                gizmos.Chain(stage, ref poseStream, chainHeadToSpine[i], chainHeadToSpine[i - 1], color);
            }

            Vector3 solvedHead = poseStream.GetPosition(chainHeadToSpine[0]);
            gizmos.Point(stage, solvedHead, color);
            gizmos.Line(stage, solvedHead, targetPositionHead, gizmoResidual);
            gizmos.Label(stage, solvedHead, "Head pin residual", gizmoResidual);

            gizmos.BoneAxes(stage, ref poseStream, handleHips, gizmos.AxisLength);
            gizmos.BoneAxes(stage, ref poseStream, handleChest, gizmos.AxisLength);
            gizmos.BoneAxes(stage, ref poseStream, handleNeck, gizmos.AxisLength);

            if (plan.chestIdx >= 0 && plan.chestIdx < length)
            {
                gizmos.Label(stage, poseStream.GetPosition(chainHeadToSpine[plan.chestIdx]), "Chest joint");
            }
            if (plan.hasHips)
            {
                Vector3 hipsPos = poseStream.GetPosition(handleHips);
                if (plan.prone)
                {
                    gizmos.Label(stage, hipsPos, "Hips (prone)");
                }
                else
                {
                    gizmos.Label(stage, hipsPos, "Hips");
                }
            }
        }
        void RecordShoulderGizmos()
        {
            const BasisIKGizmoStage stage = BasisIKGizmoStage.Shoulders;
            if (!gizmos.Wants(stage))
            {
                return;
            }

            if (plan.hasLeftShoulder) RecordClavicle(stage, handleLeftShoulder, handleLeftUpperArm, true);
            if (plan.hasRightShoulder) RecordClavicle(stage, handleRightShoulder, handleRightUpperArm, false);
        }
        void RecordClavicle(BasisIKGizmoStage stage, BasisBoneHandle shoulder, BasisBoneHandle upperArm, bool isLeft)
        {
            uint color = SideColor(isLeft);
            gizmos.Chain(stage, ref poseStream, handleChest, shoulder, gizmoRaw);
            gizmos.Chain(stage, ref poseStream, shoulder, upperArm, color);
            gizmos.BoneAxes(stage, ref poseStream, shoulder, gizmos.AxisLength);
            Vector3 position = poseStream.GetPosition(shoulder);
            if (isLeft)
            {
                gizmos.Label(stage, position, "L clavicle", color);
            }
            else
            {
                gizmos.Label(stage, position, "R clavicle", color);
            }
        }
        void RecordLegGizmos()
        {
            const BasisIKGizmoStage stage = BasisIKGizmoStage.Legs;
            if (!gizmos.Wants(stage))
            {
                return;
            }

            RecordLeg(stage, handleLeftUpperLeg, handleLeftLowerLeg, handleLeftFoot, plan.leftLeg, targetPositionLeftLowerLeg, hintPositionLeftLowerLeg, true);
            RecordLeg(stage, handleRightUpperLeg, handleRightLowerLeg, handleRightFoot, plan.rightLeg, targetPositionRightLowerLeg, hintPositionRightLowerLeg, false);

            if (kneeAnteriorRef.sqrMagnitude > sqrEpsilon && plan.hasHips)
            {
                gizmos.Normal(stage, poseStream.GetPosition(handleHips), kneeAnteriorRef.normalized, gizmos.AxisLength, BasisIKGizmoPalette.Magenta);
            }
        }
        void RecordLeg(BasisIKGizmoStage stage, BasisBoneHandle root, BasisBoneHandle mid, BasisBoneHandle tip, in BasisEerieLegPlan leg, Vector3 target, Vector3 hint, bool isLeft)
        {
            if (!leg.has || !(leg.weight > 0f))
            {
                return;
            }

            uint color = SideColor(isLeft);
            Vector3 hipPos = poseStream.GetPosition(root), kneePos = poseStream.GetPosition(mid);
            Vector3 footPos = poseStream.GetPosition(tip);

            gizmos.Bone(stage, hipPos, kneePos, color);
            gizmos.Bone(stage, kneePos, footPos, color);
            gizmos.Point(stage, footPos, color);
            gizmos.BoneAxes(stage, ref poseStream, tip, gizmos.AxisLength);
            gizmos.Line(stage, footPos, target, gizmoResidual);

            Vector3 limbAxis = footPos - hipPos, bendPlane = Vector3.Cross(kneePos - hipPos, footPos - kneePos);
            if (bendPlane.sqrMagnitude > sqrEpsilon)
            {
                gizmos.Normal(stage, kneePos, bendPlane.normalized, gizmos.AxisLength * 0.6f, BasisIKGizmoPalette.Cyan);
            }
            if (limbAxis.sqrMagnitude > sqrEpsilon && bendPlane.sqrMagnitude > sqrEpsilon)
            {
                Vector3 pole = Vector3.Cross(limbAxis.normalized, bendPlane.normalized);
                if (pole.sqrMagnitude > sqrEpsilon)
                {
                    gizmos.Direction(stage, kneePos, pole.normalized, gizmos.AxisLength * 1.5f, BasisIKGizmoPalette.Cyan);
                }
            }

            if (leg.hintWeight > 0f)
            {
                gizmos.Line(stage, kneePos, hint, gizmoHint);
            }

            if (isLeft)
            {
                gizmos.Label(stage, kneePos, "L knee", color);
            }
            else
            {
                gizmos.Label(stage, kneePos, "R knee", color);
            }
        }
        void RecordArmGizmos()
        {
            const BasisIKGizmoStage stage = BasisIKGizmoStage.Arms;
            if (!gizmos.Wants(stage))
            {
                return;
            }

            RecordArm(stage, handleLeftUpperArm, handleLeftLowerArm, handleLeftHand, plan.leftArm, targetPositionLeftHand, hintPositionLeftHand, tposeShoulderToHandLeft, armLeft, true);
            RecordArm(stage, handleRightUpperArm, handleRightLowerArm, handleRightHand, plan.rightArm, targetPositionRightHand, hintPositionRightHand, tposeShoulderToHandRight, armRight, false);
        }
        void RecordArm(BasisIKGizmoStage stage, BasisBoneHandle root, BasisBoneHandle mid, BasisBoneHandle tip, in BasisEerieArmPlan arm, Vector3 target, Vector3 hint, float reach, int swingSlot, bool isLeft)
        {
            if (!arm.has || !(arm.weight > 0f))
            {
                return;
            }

            uint color = SideColor(isLeft);
            Vector3 shoulderPos = poseStream.GetPosition(root), elbowPos = poseStream.GetPosition(mid);
            Vector3 handPos = poseStream.GetPosition(tip);

            gizmos.Bone(stage, shoulderPos, elbowPos, color);
            gizmos.Bone(stage, elbowPos, handPos, color);
            gizmos.Point(stage, handPos, color);
            gizmos.BoneAxes(stage, ref poseStream, tip, gizmos.AxisLength);
            gizmos.Line(stage, handPos, target, gizmoResidual);

            if (reach > 0f)
            {
                gizmos.Circle(stage, shoulderPos, playerUp, reach, gizmoReach);
            }

            if (arm.trackerHint)
            {
                gizmos.Line(stage, elbowPos, hint, gizmoHint);
            }

            float armLength = (handPos - shoulderPos).magnitude;
            float hintLength = armLength > minMag ? armLength * 0.5f : gizmos.AxisLength * 2f;

            if (plan.hasArmState)
            {
                BasisArmState armSlot = armState[swingSlot];
                if (armSlot.Seeded)
                {
                    if (armSlot.PriorDir.sqrMagnitude > sqrEpsilon) gizmos.Direction(stage, shoulderPos, armSlot.PriorDir.normalized, hintLength, BasisIKGizmoPalette.Magenta);
                    if (armSlot.ElbowDir.sqrMagnitude > sqrEpsilon) gizmos.Direction(stage, shoulderPos, armSlot.ElbowDir.normalized, hintLength, BasisIKGizmoPalette.Yellow);
                    if (armSlot.Switched) gizmos.Circle(stage, elbowPos, handPos - shoulderPos, gizmos.PointSize * 3f, BasisIKGizmoPalette.Red);
                    FixedString64Bytes text = "swivel ";
                    text.Append(armSlot.SwivelDeg);
                    gizmos.Label(stage, elbowPos + playerUp * gizmos.PointSize * 4f, text, BasisIKGizmoPalette.Yellow);
                }
            }

            if (isLeft)
            {
                gizmos.Label(stage, elbowPos, "L elbow", color);
            }
            else
            {
                gizmos.Label(stage, elbowPos, "R elbow", color);
            }
        }
        void RecordToeGizmos()
        {
            const BasisIKGizmoStage stage = BasisIKGizmoStage.Toes;
            if (!gizmos.Wants(stage))
            {
                return;
            }

            if (plan.hasLeftToe) RecordToe(stage, handleLeftFoot, handleLeftToe, plan.leftToeTracked, leftToeBendAxis, leftToeBendDeg, true);
            if (plan.hasRightToe) RecordToe(stage, handleRightFoot, handleRightToe, plan.rightToeTracked, rightToeBendAxis, rightToeBendDeg, false);
        }
        void RecordToe(BasisIKGizmoStage stage, BasisBoneHandle foot, BasisBoneHandle toe, bool driven, Vector3 bendAxis, float bendDeg, bool isLeft)
        {
            uint color = SideColor(isLeft);
            gizmos.Chain(stage, ref poseStream, foot, toe, color);
            gizmos.BoneAxes(stage, ref poseStream, toe, gizmos.AxisLength * 0.5f);

            Vector3 toePos = poseStream.GetPosition(toe);
            if (!driven && bendAxis.sqrMagnitude > sqrEpsilon && bendDeg != 0f)
            {
                gizmos.Direction(stage, toePos, bendAxis.normalized, gizmos.AxisLength, BasisIKGizmoPalette.Orange);
            }

            if (driven)
            {
                if (isLeft)
                {
                    gizmos.Label(stage, toePos, "L toe (tracked)", color);
                }
                else
                {
                    gizmos.Label(stage, toePos, "R toe (tracked)", color);
                }
            }
            else
            {
                if (isLeft)
                {
                    gizmos.Label(stage, toePos, "L toe (surface)", color);
                }
                else
                {
                    gizmos.Label(stage, toePos, "R toe (surface)", color);
                }
            }
        }
        void RecordOverrideGizmos()
        {
            const BasisIKGizmoStage stage = BasisIKGizmoStage.Overrides;
            if (!gizmos.Wants(stage))
            {
                return;
            }

            uint color = gizmos.StageColor(stage);
            for (int i = 0; i < slotPositions.Length; i++)
            {
                if (!slotWeights[i] || (plan.boundSlots & (1u << i)) == 0)
                {
                    continue;
                }
                Vector3 position = slotPositions[i];
                gizmos.Point(stage, position, color);
                gizmos.Axes(stage, position, slotRotations[i] * slotOffsets[i], gizmos.AxisLength * 0.75f);
            }
        }
        void RecordFrameGizmos()
        {
            const BasisIKGizmoStage stage = BasisIKGizmoStage.Frames;
            if (!gizmos.Wants(stage))
            {
                return;
            }

            float len = gizmos.AxisLength * 1.5f;

            if (plan.hasHips)
            {
                poseStream.GetPositionAndRotation(handleHips, out Vector3 hipsPos, out Quaternion hipsRot);
                gizmos.Direction(stage, hipsPos, playerUp, len * 2f, BasisIKGizmoPalette.Green);
                gizmos.Label(stage, hipsPos + playerUp * len * 2f, "playerUp");

                Quaternion hipsAnat = hipsRot * Quaternion.Inverse(offsetRotationHips);
                gizmos.Axes(stage, hipsPos, hipsAnat, len);
                gizmos.Label(stage, hipsPos, "hips anat");
            }

            if (plan.hasChest)
            {
                poseStream.GetPositionAndRotation(handleChest, out Vector3 chestPos, out Quaternion chestRot);
                gizmos.Axes(stage, chestPos, chestRot, len);
                gizmos.Label(stage, chestPos, "chest");

                if (plan.hasBodyRight)
                {
                    Vector3 bodyRight = poseStream.GetPosition(handleRightUpperArm) - poseStream.GetPosition(handleLeftUpperArm);
                    if (bodyRight.sqrMagnitude > sqrEpsilon)
                    {
                        gizmos.Direction(stage, chestPos, bodyRight.normalized, len, BasisIKGizmoPalette.Red);
                        gizmos.Label(stage, chestPos + bodyRight.normalized * len, "bodyRight");
                    }
                }
            }

            RecordSpineRestFrames(stage, len * 0.6f);
        }
        void RecordSpineRestFrames(BasisIKGizmoStage stage, float len)
        {
            if (!plan.hasSpineRestFrames)
            {
                return;
            }

            int length = chainHeadToSpine.Length;
            for (int i = 1; i <= length - 2; i++)
            {
                BasisSpineRestFrame frame = chainSpineRestFrames[i];
                if (!frame.Valid)
                {
                    continue;
                }

                Quaternion parentRot = poseStream.GetRotation(chainHeadToSpine[i + 1]);
                Vector3 pos = poseStream.GetPosition(chainHeadToSpine[i]);
                gizmos.Line(stage, pos, pos + parentRot * frame.Right * len, BasisIKGizmoPalette.Red);
                gizmos.Line(stage, pos, pos + parentRot * frame.Up * len, BasisIKGizmoPalette.Green);
                gizmos.Line(stage, pos, pos + parentRot * frame.Forward * len, BasisIKGizmoPalette.Blue);
            }
        }
        void RecordLimitGizmos()
        {
            const BasisIKGizmoStage stage = BasisIKGizmoStage.Limits;
            if (!gizmos.Wants(stage))
            {
                return;
            }

            RecordSpineRomCones(stage);

            float radius = gizmos.AxisLength;
            if (plan.leftArm.has) RecordJointAngle(stage, handleLeftUpperArm, handleLeftLowerArm, handleLeftHand, radius, "L elbow");
            if (plan.rightArm.has) RecordJointAngle(stage, handleRightUpperArm, handleRightLowerArm, handleRightHand, radius, "R elbow");
            if (plan.leftLeg.has) RecordJointAngle(stage, handleLeftUpperLeg, handleLeftLowerLeg, handleLeftFoot, radius, "L knee");
            if (plan.rightLeg.has) RecordJointAngle(stage, handleRightUpperLeg, handleRightLowerLeg, handleRightFoot, radius, "R knee");
        }
        void RecordJointAngle(BasisIKGizmoStage stage, BasisBoneHandle root, BasisBoneHandle mid, BasisBoneHandle tip, float radius, in FixedString64Bytes label)
        {
            Vector3 midPos = poseStream.GetPosition(mid), toRoot = poseStream.GetPosition(root) - midPos;
            Vector3 toTip = poseStream.GetPosition(tip) - midPos;
            if (toRoot.sqrMagnitude <= sqrEpsilon || toTip.sqrMagnitude <= sqrEpsilon)
            {
                return;
            }
            gizmos.Angle(stage, midPos, toRoot, toTip, radius, BasisIKGizmoPalette.Yellow);
            gizmos.Label(stage, midPos, label, BasisIKGizmoPalette.Yellow);
        }
        void RecordSpineRomCones(BasisIKGizmoStage stage)
        {
            if (!plan.spineRom)
            {
                return;
            }

            int length = chainHeadToSpine.Length;
            for (int i = 1; i <= length - 2; i++)
            {
                BasisSpineRestFrame frame = chainSpineRestFrames[i];
                if (!frame.Valid)
                {
                    continue;
                }

                Quaternion parentRot = poseStream.GetRotation(chainHeadToSpine[i + 1]);
                Quaternion boneRot = poseStream.GetRotation(chainHeadToSpine[i]);
                Quaternion local = BasisSpineAnatomyCore.Conj(parentRot) * boneRot;
                BasisSpineRom rom = BasisSpineAnatomy.Rom(frame.Segment);
                BasisSpineAnatomyCore.Clamp(local, frame, rom, out BasisSpineClampInfo info);

                Vector3 pos = poseStream.GetPosition(chainHeadToSpine[i]), up = parentRot * frame.Up;
                Vector3 right = parentRot * frame.Right, forward = parentRot * frame.Forward;
                float coneLength = (poseStream.GetPosition(chainHeadToSpine[i - 1]) - pos).magnitude;
                if (coneLength <= minMag)
                {
                    continue;
                }

                uint color = info.SwingClamped ? BasisIKGizmoPalette.Red : gizmos.StageColor(stage);
                gizmos.SwingCone(stage, pos, up, right, forward, rom.LatDeg, rom.FlexDeg, rom.ExtDeg, coneLength, color);

                if (info.TwistClamped)
                {
                    gizmos.Normal(stage, pos, up, coneLength * 0.35f, BasisIKGizmoPalette.Orange);
                }
            }
        }
        void RecordReachGizmos()
        {
            const BasisIKGizmoStage stage = BasisIKGizmoStage.Reach;
            if (!gizmos.Wants(stage))
            {
                return;
            }

            if (plan.leftArm.has) RecordArmReach(stage, handleLeftUpperArm, handleLeftHand, tposeShoulderToHandLeft, "L arm");
            if (plan.rightArm.has) RecordArmReach(stage, handleRightUpperArm, handleRightHand, tposeShoulderToHandRight, "R arm");
            if (plan.leftLeg.has) RecordLegReach(stage, handleLeftUpperLeg, handleLeftLowerLeg, handleLeftFoot, "L leg");
            if (plan.rightLeg.has) RecordLegReach(stage, handleRightUpperLeg, handleRightLowerLeg, handleRightFoot, "R leg");
        }
        void RecordArmReach(BasisIKGizmoStage stage, BasisBoneHandle root, BasisBoneHandle tip, float maxReach, in FixedString64Bytes label)
        {
            if (!(maxReach > minMag))
            {
                return;
            }
            Vector3 rootPos = poseStream.GetPosition(root);
            float current = (poseStream.GetPosition(tip) - rootPos).magnitude;
            RecordReachRatio(stage, rootPos, maxReach, current / maxReach, label);
        }
        void RecordLegReach(BasisIKGizmoStage stage, BasisBoneHandle root, BasisBoneHandle mid, BasisBoneHandle tip, in FixedString64Bytes label)
        {
            Vector3 rootPos = poseStream.GetPosition(root), midPos = poseStream.GetPosition(mid);
            Vector3 tipPos = poseStream.GetPosition(tip);
            float maxReach = (midPos - rootPos).magnitude + (tipPos - midPos).magnitude;
            if (!(maxReach > minMag))
            {
                return;
            }
            RecordReachRatio(stage, rootPos, maxReach, (tipPos - rootPos).magnitude / maxReach, label);
        }
        void RecordReachRatio(BasisIKGizmoStage stage, Vector3 rootPos, float maxReach, float ratio, in FixedString64Bytes label)
        {

            float t = Mathf.Clamp01((ratio - 0.75f) / 0.25f);
            uint color = BasisIKGizmoPalette.Rgba((byte)(60f + 195f * t), (byte)(255f - 195f * t), 60, 255);

            gizmos.Sphere(stage, rootPos, maxReach, BasisIKGizmoPalette.WithAlpha(color, 70));
            gizmos.Point(stage, rootPos, color);

            if (!gizmos.WantLabels)
            {
                return;
            }
            FixedString64Bytes text = label;
            text.Append(' ');
            text.Append(ratio);
            gizmos.Label(stage, rootPos, text, color);
        }
        void RecordNumberGizmos()
        {
            const BasisIKGizmoStage stage = BasisIKGizmoStage.Numbers;
            if (!gizmos.Wants(stage) || !gizmos.WantLabels)
            {
                return;
            }

            if (plan.leftLeg.has) RecordLegNumbers(stage, 0, handleLeftLowerLeg);
            if (plan.rightLeg.has) RecordLegNumbers(stage, 1, handleRightLowerLeg);

            if (plan.leftArm.has) RecordResidual(stage, handleLeftHand, targetPositionLeftHand, plan.leftArm.weight, "L hand off");
            if (plan.rightArm.has) RecordResidual(stage, handleRightHand, targetPositionRightHand, plan.rightArm.weight, "R hand off");
            if (plan.leftLeg.has) RecordResidual(stage, handleLeftFoot, targetPositionLeftLowerLeg, plan.leftLeg.weight, "L foot off");
            if (plan.rightLeg.has) RecordResidual(stage, handleRightFoot, targetPositionRightLowerLeg, plan.rightLeg.weight, "R foot off");

            if (plan.hasSpineChain)
            {
                Vector3 solvedHead = poseStream.GetPosition(chainHeadToSpine[0]);
                FixedString64Bytes text = "head off ";
                text.Append((solvedHead - targetPositionHead).magnitude);
                gizmos.Label(stage, solvedHead, text, gizmoResidual);
            }
        }
        void RecordResidual(BasisIKGizmoStage stage, BasisBoneHandle tip, Vector3 target, float enabled, in FixedString64Bytes label)
        {
            if (!(enabled > 0f))
            {
                return;
            }
            Vector3 pos = poseStream.GetPosition(tip);
            FixedString64Bytes text = label;
            text.Append(' ');
            text.Append((pos - target).magnitude);
            gizmos.Label(stage, pos, text, gizmoResidual);
        }
        void RecordLegNumbers(BasisIKGizmoStage stage, int slot, BasisBoneHandle knee)
        {
            if (!plan.hasLegDiagnostics)
            {
                return;
            }

            BasisLegDiagnostics d = legDiagnostics[slot];
            Vector3 pos = poseStream.GetPosition(knee);
            uint color = SideColor(slot == 0);
            float step = gizmos.AxisLength * 0.6f;
            FixedString64Bytes reach = "reach ";
            reach.Append(d.ReachRatio);
            reach.Append(' ');
            reach.Append(d.KneeAngleDeg);
            gizmos.Label(stage, pos, reach, color);

            FixedString64Bytes swivel = "swivel ";
            swivel.Append(d.RawSwivelDeg);
            swivel.Append(' ');
            swivel.Append(d.SmoothSwivelDeg);
            gizmos.Label(stage, pos + Vector3.up * step, swivel, color);

            FixedString64Bytes hip = "hip ";
            hip.Append(d.HipFlexionDeg);
            hip.Append(' ');
            hip.Append(d.HipAbductionDeg);
            hip.Append(' ');
            hip.Append(d.FemurTwistDeg);
            gizmos.Label(stage, pos + Vector3.up * (step * 2f), hip, color);

            FixedString64Bytes trust = "distrust ";
            trust.Append(d.HintDistrust);
            gizmos.Label(stage, pos + Vector3.up * (step * 3f), trust, color);
        }
        void RecordSkeletonGizmos()
        {
            const BasisIKGizmoStage stage = BasisIKGizmoStage.Skeleton;
            if (!gizmos.Wants(stage) || !poseStream.Parent.IsCreated)
            {
                return;
            }

            uint color = gizmos.StageColor(stage);
            for (int i = 0; i < poseStream.Count; i++)
            {
                int parent = poseStream.Parent[i];
                if (parent < 0)
                {
                    continue;
                }
                gizmos.Line(stage, poseStream.GetWorldPosition(parent), poseStream.GetWorldPosition(i), color);
            }
        }
    }
}
