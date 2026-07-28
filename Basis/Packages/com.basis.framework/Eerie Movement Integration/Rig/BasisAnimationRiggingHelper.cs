using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;
using Basis.IK;

public static class BasisAnimationRiggingHelper
{
    /// <summary>Maps a limb tracker's raw rotation onto the BONE it is strapped to, using the reference
    /// captured at calibration. Zero (no reference yet) is the solve's "feature off" sentinel.</summary>
    static Quaternion TrackerImpliedBoneRotation(Basis.Scripts.TransformBinders.BoneControl.BasisBoneTrackedRole role, Quaternion trackerRotation)
    {
        return Basis.Scripts.Avatar.BasisAvatarIKStageCalibration.BasisLimbRollStore.TryGet(role, out Quaternion trackerToBone)
            ? trackerRotation * trackerToBone
            : default;
    }

    /// <summary>Per-effector bind offset captured in T-pose with the avatar root aligned to the player:
    /// Inverse(bone-sim world outgoing) * avatar bone, the same form FBT calibration uses.</summary>
    public static Quaternion CalibratedRotationOffset(BasisLocalBoneControl control, Transform animatorRoot, Transform avatarBone)
    {
        return CalibratedRotationOffset(control.OutgoingWorldData.rotation, avatarBone.rotation);
    }

    /// <summary>Pure form (shared with the Calibration Math sweep): maps the bone-sim world outgoing onto the
    /// avatar bone. Both captured against the same aligned root, so the offset carries no spawn-orientation
    /// leak across an avatar swap.</summary>
    public static Quaternion CalibratedRotationOffset(Quaternion boneOutgoingWorldRotation, Quaternion avatarBoneRotation)
    {
        return Quaternion.Inverse(boneOutgoingWorldRotation) * avatarBoneRotation;
    }

    /// <summary>
    /// Build the combined Full IK constraint from arrays of joints and roles.
    /// root/mid/tip must be length >= 3: [Head, LeftLowerLeg, RightLowerLeg]
    /// TargetRole/BendRole/UseBoneRole correspond index-by-index to those same chains.
    /// </summary>
    public static void CreateBasisFullBodyRIG(BasisLocalPlayer player, BasisTransformMapping Mapping, ref BasisEerieMovement job)
    {
        BasisEerieMovementSetup.SetDefaultValues(ref job);
        // Head
        Quaternion avatarRootInv = Quaternion.Inverse(player.AvatarTransform.rotation);
        job.offsetRotationHead = Mapping.Hashead ? avatarRootInv * Mapping.head.rotation : Quaternion.identity;
        // Feet
        job.offsetRotationLeftFoot = Mapping.HasleftFoot ? CalibratedRotationOffset(BasisLocalBoneDriver.LeftFootControl, Mapping.AnimatorRoot, Mapping.leftFoot) : Quaternion.identity;
        job.offsetRotationRightFoot = Mapping.HasrightFoot ? CalibratedRotationOffset(BasisLocalBoneDriver.RightFootControl, Mapping.AnimatorRoot, Mapping.rightFoot) : Quaternion.identity;

        Quaternion leftLandmarkBind = Quaternion.identity;
        Quaternion rightLandmarkBind = Quaternion.identity;

        Quaternion _lastGoodLeftRot = Quaternion.identity;
        Quaternion _lastGoodRightRot = Quaternion.identity;
        bool _hasLastLeft = false;
        bool _hasLastRight = false;

        if (Mapping.HasleftHand)
        {
            Vector3 wrist = Mapping.leftHand.position;
            var a = new[] { GetLM(Mapping.LeftIndex, 0), GetLM(Mapping.LeftIndex, 0), GetLM(Mapping.LeftMiddle, 0) };
            var b = new[] { GetLM(Mapping.LeftLittle, 0), GetLM(Mapping.LeftMiddle, 0), GetLM(Mapping.LeftLittle, 0) };
            leftLandmarkBind = ComputeHandRotationWithFallback(wrist, a, b, ref _hasLastLeft, ref _lastGoodLeftRot);
        }
        if (Mapping.HasrightHand)
        {
            Vector3 wrist = Mapping.rightHand.position;
            var a = new[] { GetLM(Mapping.RightIndex, 0), GetLM(Mapping.RightIndex, 0), GetLM(Mapping.RightMiddle, 0) };
            var b = new[] { GetLM(Mapping.RightLittle, 0), GetLM(Mapping.RightMiddle, 0), GetLM(Mapping.RightLittle, 0) };
            rightLandmarkBind = ComputeHandRotationWithFallback(wrist, a, b, ref _hasLastRight, ref _lastGoodRightRot);
        }
        // Bone bind rotations (world space)
        Quaternion leftBoneBind = Mapping.leftHand.rotation;
        Quaternion rightBoneBind = Mapping.rightHand.rotation;

        job.offsetRotationLeftHand = Quaternion.Inverse(leftLandmarkBind) * leftBoneBind;
        job.offsetRotationRightHand = Quaternion.Inverse(rightLandmarkBind) * rightBoneBind;

        job.offsetRotationChest = Mapping.Haschest ? avatarRootInv * Mapping.chest.rotation : Quaternion.identity;
        job.offsetRotationLeftToe = Mapping.HasleftToes ? CalibratedRotationOffset(BasisLocalBoneDriver.LeftToeControl, Mapping.AnimatorRoot, Mapping.leftToe) : Quaternion.identity;
        job.offsetRotationRightToe = Mapping.HasrightToes ? CalibratedRotationOffset(BasisLocalBoneDriver.RightToeControl, Mapping.AnimatorRoot, Mapping.rightToe) : Quaternion.identity;


        job.offsetRotationLeftShoulder = Mapping.HasleftShoulder ? CalibratedRotationOffset(BasisLocalBoneDriver.LeftShoulderControl, Mapping.AnimatorRoot, Mapping.leftShoulder) : Quaternion.identity;
        job.offsetRotationRightShoulder = Mapping.HasRightShoulder ? CalibratedRotationOffset(BasisLocalBoneDriver.RightShoulderControl, Mapping.AnimatorRoot, Mapping.RightShoulder) : Quaternion.identity;
        // Hips reference rotation
        job.offsetRotationHips = Mapping.HasHips ? avatarRootInv * Mapping.Hips.rotation : Quaternion.identity;
        // Head
        var head = BasisLocalBoneDriver.HeadControl.OutgoingWorldData;
        job.targetPositionHead = head.position;
        job.targetRotationHead = head.rotation;

        // Left foot
        var leftFoot = BasisLocalBoneDriver.LeftFootControl.OutgoingWorldData;
        job.targetPositionLeftLowerLeg = leftFoot.position;
        job.targetRotationLeftLowerLeg = leftFoot.rotation;

        // Right  foot
        var rightFoot = BasisLocalBoneDriver.RightFootControl.OutgoingWorldData;
        job.targetPositionRightLowerLeg = rightFoot.position;
        job.targetRotationRightLowerLeg = rightFoot.rotation;

        // Hips
        var hips = BasisLocalBoneDriver.HipsControl.OutgoingWorldData;
        job.targetPositionHips = hips.position;
        job.targetRotationHips = hips.rotation;

        // Hands
        var leftHand = BasisLocalBoneDriver.LeftHandControl.OutgoingWorldData;
        job.targetPositionLeftHand = leftHand.position;
        job.targetRotationLeftHand = leftHand.rotation;

        var rightHand = BasisLocalBoneDriver.RightHandControl.OutgoingWorldData;
        job.targetPositionRightHand = rightHand.position;
        job.targetRotationRightHand = rightHand.rotation;

        // Cache world data once per control (less property spam, easier to read)
        var leftLowerArm = BasisLocalBoneDriver.LeftLowerArmControl.OutgoingWorldData;
        var rightLowerArm = BasisLocalBoneDriver.RightLowerArmControl.OutgoingWorldData;
        var chest = BasisLocalBoneDriver.ChestControl.OutgoingWorldData;
        var leftLowerLeg = BasisLocalBoneDriver.LeftLowerLegControl.OutgoingWorldData;
        var rightLowerLeg = BasisLocalBoneDriver.RightLowerLegControl.OutgoingWorldData;
        var leftShoulder = BasisLocalBoneDriver.LeftShoulderControl.OutgoingWorldData;
        var rightShoulder = BasisLocalBoneDriver.RightShoulderControl.OutgoingWorldData;

        // --- Arms ---
        job.hintPositionLeftHand = BasisLocalRigDriver.ApplyHintBias(Basis.Scripts.TransformBinders.BoneControl.BasisBoneTrackedRole.LeftLowerArm, leftLowerArm.position, leftLowerArm.rotation);
        job.hintRotationLeftHand = TrackerImpliedBoneRotation(Basis.Scripts.TransformBinders.BoneControl.BasisBoneTrackedRole.LeftLowerArm, leftLowerArm.rotation);
        job.hintPositionRightHand = BasisLocalRigDriver.ApplyHintBias(Basis.Scripts.TransformBinders.BoneControl.BasisBoneTrackedRole.RightLowerArm, rightLowerArm.position, rightLowerArm.rotation);
        job.hintRotationRightHand = TrackerImpliedBoneRotation(Basis.Scripts.TransformBinders.BoneControl.BasisBoneTrackedRole.RightLowerArm, rightLowerArm.rotation);

        // --- Shoulders ---
        job.targetRotationLeftShoulder = leftShoulder.rotation;
        job.targetRotationRightShoulder = rightShoulder.rotation;

        // --- Legs ---
        job.hintPositionLeftLowerLeg = BasisLocalRigDriver.ApplyHintBias(Basis.Scripts.TransformBinders.BoneControl.BasisBoneTrackedRole.LeftLowerLeg, leftLowerLeg.position, leftLowerLeg.rotation);
        job.hintPositionRightLowerLeg = BasisLocalRigDriver.ApplyHintBias(Basis.Scripts.TransformBinders.BoneControl.BasisBoneTrackedRole.RightLowerLeg, rightLowerLeg.position, rightLowerLeg.rotation);
        job.hintRotationLeftLowerLeg = TrackerImpliedBoneRotation(Basis.Scripts.TransformBinders.BoneControl.BasisBoneTrackedRole.LeftLowerLeg, leftLowerLeg.rotation);
        job.hintRotationRightLowerLeg = TrackerImpliedBoneRotation(Basis.Scripts.TransformBinders.BoneControl.BasisBoneTrackedRole.RightLowerLeg, rightLowerLeg.rotation);

        // --- Chest ---
        // Raw (un-hinted) chest for the chest IK target; the hinted one below is a head-solve hint.
        job.targetPositionChestRaw = chest.position;
        job.targetPositionChest = BasisLocalRigDriver.ApplyHintBias(Basis.Scripts.TransformBinders.BoneControl.BasisBoneTrackedRole.Chest, chest.position, chest.rotation);
        job.targetRotationChest = chest.rotation;

        // Developer diagnostics: dump the calibrated offsets, the runtime targets, and the avatar root.
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

            BasisCalibrationDebugRecorder.Pose("Offsets", "TargetHead", "target", job.targetPositionHead, job.targetRotationHead, Vector3.one);
            BasisCalibrationDebugRecorder.Pose("Offsets", "TargetHips", "target", job.targetPositionHips, job.targetRotationHips, Vector3.one);
            BasisCalibrationDebugRecorder.Pose("Offsets", "TargetLeftHand", "target", job.targetPositionLeftHand, job.targetRotationLeftHand, Vector3.one);
            BasisCalibrationDebugRecorder.Pose("Offsets", "TargetRightHand", "target", job.targetPositionRightHand, job.targetRotationRightHand, Vector3.one);
            BasisCalibrationDebugRecorder.Pose("Offsets", "TargetLeftFoot", "target", job.targetPositionLeftLowerLeg, job.targetRotationLeftLowerLeg, Vector3.one);
            BasisCalibrationDebugRecorder.Pose("Offsets", "TargetRightFoot", "target", job.targetPositionRightLowerLeg, job.targetRotationRightLowerLeg, Vector3.one);

            // Bone-control rest frames (OutgoingWorld = ParentRotation * OutGoing).
            RecordControlRest("head.ctrl", BasisLocalBoneDriver.HeadControl);
            RecordControlRest("hips.ctrl", BasisLocalBoneDriver.HipsControl);
            RecordControlRest("chest.ctrl", BasisLocalBoneDriver.ChestControl);
            RecordControlRest("neck.ctrl", BasisLocalBoneDriver.NeckControl);
            RecordControlRest("leftFoot.ctrl", BasisLocalBoneDriver.LeftFootControl);
            RecordControlRest("rightFoot.ctrl", BasisLocalBoneDriver.RightFootControl);
        }

        job.collisionsEnabled = true;
        job.protectElbow = true;
        job.collideTrackedElbow = false;
        // Without these the struct default (false / 0 Hz) would leave the no-tracker elbow drag off on this
        // setup path, so the feature would silently depend on which path built the job.
        job.elbowDragEnabled = Basis.BasisUI.BasisSettingsDefaults.FBIKElbowDrag.RawValue;
        job.elbowDragHz = Basis.BasisUI.BasisSettingsDefaults.FBIKElbowDragHz.RawValue;
        job.enabledSpineIK = true;
        job.ikLockMode = SMModuleCalibration.CurrentIKLockMode;

        // Shoulder pre-solve defaults
        job.shoulderSolveEnabled = true;
        job.shoulderShrugEnabled = true;
        job.shoulderElevationFactor = 0.4f;
        job.shoulderProtractionFactor = 0.3f;

    }

    /// <summary>
    /// Records a bone control's rest frames (local + world outgoing, T-pose local/scaled, and the
    /// inverse-offset-from-bone) for the calibration CSV. No-op unless the developer toggle is on.
    /// </summary>
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
            return Quaternion.identity; // caller will treat as "no usable landmark rotation"
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
