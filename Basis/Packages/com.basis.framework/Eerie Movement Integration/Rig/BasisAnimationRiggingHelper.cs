using Basis.Scripts.Avatar;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;
using Basis.IK;
public static class BasisAnimationRiggingHelper
{
    public static Quaternion CalibratedRotationOffset(Quaternion boneOutgoingWorldRotation, Quaternion avatarBoneRotation)
    {
        return Quaternion.Inverse(boneOutgoingWorldRotation) * avatarBoneRotation;
    }
    public static void CreateBasisFullBodyRIG(BasisLocalPlayer player, BasisTransformMapping Mapping, ref BasisEerieMovement job)
    {
        BasisEerieMovementSetup.SetDefaultValues(ref job);

        Quaternion avatarRootInv = Quaternion.Inverse(player.AvatarTransform.rotation);
        job.offsetRotationHead = Mapping.Hashead ? BasisAvatarIKStageCalibration.PureBind(avatarRootInv, Mapping.head, BasisBoneTrackedRole.Head) : Quaternion.identity;

        job.offsetRotationLeftFoot = Mapping.HasleftFoot ? BasisAvatarIKStageCalibration.PureBind(avatarRootInv, Mapping.leftFoot, BasisBoneTrackedRole.LeftFoot) : Quaternion.identity;
        job.offsetRotationRightFoot = Mapping.HasrightFoot ? BasisAvatarIKStageCalibration.PureBind(avatarRootInv, Mapping.rightFoot, BasisBoneTrackedRole.RightFoot) : Quaternion.identity;

        Quaternion leftLandmarkBind = Quaternion.identity;
        Quaternion rightLandmarkBind = Quaternion.identity;

        Quaternion lastGoodLeftRot = Quaternion.identity;
        Quaternion lastGoodRightRot = Quaternion.identity;
        bool hasLastLeft = false;
        bool hasLastRight = false;

        if (Mapping.HasleftHand)
        {
            Vector3 wrist = Mapping.leftHand.position;
            var a = new[] { GetLM(Mapping.LeftIndex, 0), GetLM(Mapping.LeftIndex, 0), GetLM(Mapping.LeftMiddle, 0) };
            var b = new[] { GetLM(Mapping.LeftLittle, 0), GetLM(Mapping.LeftMiddle, 0), GetLM(Mapping.LeftLittle, 0) };
            leftLandmarkBind = ComputeHandRotationWithFallback(wrist, a, b, ref hasLastLeft, ref lastGoodLeftRot);
        }
        if (Mapping.HasrightHand)
        {
            Vector3 wrist = Mapping.rightHand.position;
            var a = new[] { GetLM(Mapping.RightIndex, 0), GetLM(Mapping.RightIndex, 0), GetLM(Mapping.RightMiddle, 0) };
            var b = new[] { GetLM(Mapping.RightLittle, 0), GetLM(Mapping.RightMiddle, 0), GetLM(Mapping.RightLittle, 0) };
            rightLandmarkBind = ComputeHandRotationWithFallback(wrist, a, b, ref hasLastRight, ref lastGoodRightRot);
        }

        Quaternion leftBoneBind = Mapping.leftHand.rotation;
        Quaternion rightBoneBind = Mapping.rightHand.rotation;

        job.offsetRotationLeftHand = Quaternion.Inverse(leftLandmarkBind) * leftBoneBind;
        job.offsetRotationRightHand = Quaternion.Inverse(rightLandmarkBind) * rightBoneBind;

        job.offsetRotationChest = Mapping.Haschest ? BasisAvatarIKStageCalibration.PureBind(avatarRootInv, Mapping.chest, BasisBoneTrackedRole.Chest) : Quaternion.identity;
        job.offsetRotationLeftToe = Mapping.HasleftToes ? BasisAvatarIKStageCalibration.PureBind(avatarRootInv, Mapping.leftToe, BasisBoneTrackedRole.LeftToes) : Quaternion.identity;
        job.offsetRotationRightToe = Mapping.HasrightToes ? BasisAvatarIKStageCalibration.PureBind(avatarRootInv, Mapping.rightToe, BasisBoneTrackedRole.RightToes) : Quaternion.identity;

        job.offsetRotationLeftShoulder = Mapping.HasleftShoulder ? BasisAvatarIKStageCalibration.PureBind(avatarRootInv, Mapping.leftShoulder, BasisBoneTrackedRole.LeftShoulder) : Quaternion.identity;
        job.offsetRotationRightShoulder = Mapping.HasRightShoulder ? BasisAvatarIKStageCalibration.PureBind(avatarRootInv, Mapping.RightShoulder, BasisBoneTrackedRole.RightShoulder) : Quaternion.identity;

        job.offsetRotationHips = Mapping.HasHips ? BasisAvatarIKStageCalibration.PureBind(avatarRootInv, Mapping.Hips, BasisBoneTrackedRole.Hips) : Quaternion.identity;

        if (BasisCalibrationDebugRecorder.Enabled)
        {
            Transform animRoot = player?.BasisAvatar?.Animator != null ? player.BasisAvatar.Animator.transform : null;
            BasisCalibrationDebugRecorder.Bone("Offsets", "AnimatorRoot", animRoot);
            BasisCalibrationDebugRecorder.Rotation("Offsets", "PlayerRoot", "localToWorld", BasisLocalPlayer.localToWorldMatrix.rotation);

            BasisCalibrationDebugRecorder.Rotation("Offsets", "CalibratedRotationHead", "offset", job.offsetRotationHead);
            BasisCalibrationDebugRecorder.Rotation("Offsets", "CalibrationLeftFootRotation", "offset", job.offsetRotationLeftFoot);
            BasisCalibrationDebugRecorder.Rotation("Offsets", "CalibrationRightFootRotation", "offset", job.offsetRotationRightFoot);
            BasisCalibrationDebugRecorder.Rotation("Offsets", "CalibratedRotationChest", "offset", job.offsetRotationChest);
            BasisCalibrationDebugRecorder.Rotation("Offsets", "CalibratedRotationLeftToe", "offset", job.offsetRotationLeftToe);
            BasisCalibrationDebugRecorder.Rotation("Offsets", "CalibratedRotationRightToe", "offset", job.offsetRotationRightToe);
            BasisCalibrationDebugRecorder.Rotation("Offsets", "CalibratedRotationLeftShoulder", "offset", job.offsetRotationLeftShoulder);
            BasisCalibrationDebugRecorder.Rotation("Offsets", "CalibratedRotationRightShoulder", "offset", job.offsetRotationRightShoulder);
            BasisCalibrationDebugRecorder.Rotation("Offsets", "CalibratedRotationLeftHand", "offset", job.offsetRotationLeftHand);
            BasisCalibrationDebugRecorder.Rotation("Offsets", "CalibratedRotationRightHand", "offset", job.offsetRotationRightHand);
            BasisCalibrationDebugRecorder.Rotation("Offsets", "OffsetRotationHips", "offset", job.offsetRotationHips);

            RecordControlRest("head.ctrl", BasisLocalBoneDriver.HeadControl);
            RecordControlRest("hips.ctrl", BasisLocalBoneDriver.HipsControl);
            RecordControlRest("chest.ctrl", BasisLocalBoneDriver.ChestControl);
            RecordControlRest("neck.ctrl", BasisLocalBoneDriver.NeckControl);
            RecordControlRest("leftFoot.ctrl", BasisLocalBoneDriver.LeftFootControl);
            RecordControlRest("rightFoot.ctrl", BasisLocalBoneDriver.RightFootControl);
        }
    }
    private static void RecordControlRest(string label, Basis.Scripts.TransformBinders.BoneControl.BasisLocalBoneControl c)
    {
        if (c == null)
        {
            return;
        }
        BasisCalibrationDebugRecorder.Pose("Offsets", label + ".OutGoing", "local", c.OutGoingData.position, c.OutGoingData.rotation, Vector3.one);
        BasisCalibrationDebugRecorder.Pose("Offsets", label + ".OutgoingWorld", "world", c.OutgoingWorldData.position, c.OutgoingWorldData.rotation, Vector3.one);
        BasisCalibrationDebugRecorder.Pose("Offsets", label + ".TposeLocal", "local", c.TposeLocal.position, c.TposeLocal.rotation, Vector3.one);
        BasisCalibrationDebugRecorder.Pose("Offsets", label + ".TposeLocalScaled", "local", c.TposeLocalScaled.position, c.TposeLocalScaled.rotation, Vector3.one);
        BasisCalibrationDebugRecorder.Pose("Offsets", label + ".InverseOffsetFromBone", "local", c.InverseOffsetFromBone.position, c.InverseOffsetFromBone.rotation, Vector3.one);
    }
    private static (bool valid, Vector3 pos) GetLM(Transform[] arr, int i)
    {
        if (arr != null && i >= 0 && i < arr.Length && arr[i] != null)
        {
            return (true, arr[i].position);
        }

        return (false, Vector3.zero);
    }
    private static Quaternion ComputeHandRotationWithFallback( Vector3 wrist,(bool valid, Vector3 pos)[] pointsA,(bool valid, Vector3 pos)[] pointsB, ref bool hasLast, ref Quaternion lastRot)
    {
        for (int Index = 0; Index < pointsA.Length; Index++)
        {
            if (!pointsA[Index].valid || !pointsB[Index].valid)
            {
                continue;
            }

            Quaternion rot = HandRotationFromLandmarks(wrist, pointsA[Index].pos, pointsB[Index].pos);
            if (rot == Quaternion.identity) continue;

            lastRot = rot;
            hasLast = true;
            return rot;
        }

        return hasLast ? lastRot : Quaternion.identity;
    }
    public static Quaternion HandRotationFromLandmarks(Vector3 wrist, Vector3 indexMCP, Vector3 pinkyMCP)
    {
        Vector3 right = (pinkyMCP - indexMCP);
        Vector3 knuckleMid = (indexMCP + pinkyMCP) * 0.5f;
        Vector3 forward = (knuckleMid - wrist);

        if (right.sqrMagnitude < 1e-8f || forward.sqrMagnitude < 1e-8f)
        {
            return Quaternion.identity;
        }

        right.Normalize();
        forward.Normalize();

        Vector3 up = Vector3.Cross(forward, right);
        if (up.sqrMagnitude < 1e-8f)
        {
            return Quaternion.identity;
        }
        up.Normalize();
        return Quaternion.LookRotation(forward, up);
    }
}
