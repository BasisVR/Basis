using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Drivers;
using UnityEngine;
using UnityEngine.Animations.Rigging;

public static class BasisAnimationRiggingHelper
{
    /// <summary>
    /// Build the combined Full IK constraint from arrays of joints and roles.
    /// root/mid/tip must be length >= 3: [Head, LeftLowerLeg, RightLowerLeg]
    /// TargetRole/BendRole/UseBoneRole correspond index-by-index to those same chains.
    /// </summary>
    public static void CreateBasisFullBodyRIG(BasisLocalPlayer player, GameObject parent, BasisTransformMapping Mapping, out BasisFullBodyIK BasisFullIKConstraint)
    {
        // Holder + component
        var go = CreateAndSetParent(parent.transform, $"Full IK ({parent.name})");
        BasisFullIKConstraint = BasisHelpers.GetOrAddComponent<BasisFullBodyIK>(go);

        // ----------------------------
        // Core: grab data (local copy)
        // ----------------------------
        var data = BasisFullIKConstraint.data;

        // ----------------------------
        // Skeleton references
        // ----------------------------
        // Torso / head chain
        data.hips = Mapping.Hips;
        data.spine = Mapping.spine;
        data.chest = Mapping.chest;
        data.upperChest = Mapping.Upperchest;
        data.neck = Mapping.neck;
        data.head = Mapping.head;

        // Shoulders
        data.LeftShoulder = Mapping.leftShoulder;
        data.RightShoulder = Mapping.RightShoulder;

        // Arms
        data.leftUpperArm = Mapping.leftUpperArm;
        data.leftLowerArm = Mapping.leftLowerArm;
        data.LeftHand = Mapping.leftHand;
        data.RightUpperArm = Mapping.RightUpperArm;
        data.RightLowerArm = Mapping.RightLowerArm;
        data.RightHand = Mapping.rightHand;

        // Legs
        data.LeftUpperLeg = Mapping.LeftUpperLeg;
        data.LeftLowerLeg = Mapping.LeftLowerLeg;
        data.leftFoot = Mapping.leftFoot;

        data.RightUpperLeg = Mapping.RightUpperLeg;
        data.RightLowerLeg = Mapping.RightLowerLeg;
        data.RightFoot = Mapping.rightFoot;

        // Toes
        data.LeftToe = Mapping.leftToe;
        data.RightToe = Mapping.rightToe;

        // ----------------------------
        // Calibration defaults
        // ----------------------------
        // Head
        data.m_CalibratedRotationHead = Mapping.Hashead ? Mapping.head.rotation : Quaternion.identity;

        // Feet
        data.M_CalibrationLeftFootRotation = Mapping.Hashead ? Mapping.leftFoot.rotation : Quaternion.identity;
        data.M_CalibrationRightFootRotation = Mapping.Hashead ? Mapping.rightFoot.rotation : Quaternion.identity;

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

        data.m_CalibratedRotationLeftHand = Quaternion.Inverse(leftLandmarkBind) * leftBoneBind;
        data.m_CalibratedRotationRightHand = Quaternion.Inverse(rightLandmarkBind) * rightBoneBind;

        data.m_CalibratedRotationChest = Mapping.Haschest ? Mapping.chest.rotation : Quaternion.identity;
        data.m_CalibratedRotationNeck = Mapping.Hasneck ? Mapping.neck.rotation : Quaternion.identity;
        data.m_CalibratedRotationLeftToe = Mapping.HasleftToes ? Mapping.leftToe.rotation : Quaternion.identity;
        data.m_CalibratedRotationRightToe = Mapping.HasrightToes ? Mapping.rightToe.rotation : Quaternion.identity;


        data.m_CalibratedRotationLeftShoulder = Mapping.HasleftShoulder ? Mapping.leftShoulder.rotation : Quaternion.identity;
        data.m_CalibratedRotationRightShoulder = Mapping.HasRightShoulder ? Mapping.RightShoulder.rotation : Quaternion.identity;
        // Hips reference rotation
        data.OffsetRotationHips = Mapping.HasHips ? Mapping.Hips.rotation : Quaternion.identity;


        // ----------------------------
        // Targets & hints
        // ----------------------------
        // Head
        data.PositionHead = BasisLocalBoneDriver.HeadControl.OutgoingWorldData.position;
        data.RotationHead = BasisLocalBoneDriver.HeadControl.OutgoingWorldData.rotation;

        // Left foot
        data.LeftFootPosition = BasisLocalBoneDriver.LeftFootControl.OutgoingWorldData.position;
        data.LeftFootRotation = BasisLocalBoneDriver.LeftFootControl.OutgoingWorldData.rotation;

        // Right  foot
        data.RightFootPosition = BasisLocalBoneDriver.RightFootControl.OutgoingWorldData.position;
        data.RightFootRotation = BasisLocalBoneDriver.RightFootControl.OutgoingWorldData.rotation;

        // Hips
        data.PositionHips = BasisLocalBoneDriver.HipsControl.OutgoingWorldData.position;
        data.RotationHips = BasisLocalBoneDriver.HipsControl.OutgoingWorldData.rotation;

        // Hands
        data.PositionLeftHand = BasisLocalBoneDriver.LeftHandControl.OutgoingWorldData.position;
        data.RotationLeftHand = BasisLocalBoneDriver.LeftHandControl.OutgoingWorldData.rotation;

        data.PositionRightHand = BasisLocalBoneDriver.RightHandControl.OutgoingWorldData.position;
        data.RotationRightHand = BasisLocalBoneDriver.RightHandControl.OutgoingWorldData.rotation;

        // Cache world data once per control (less property spam, easier to read)
        var leftLowerArm = BasisLocalBoneDriver.LeftLowerArmControl.OutgoingWorldData;
        var rightLowerArm = BasisLocalBoneDriver.RightLowerArmControl.OutgoingWorldData;

        var chest = BasisLocalBoneDriver.ChestControl.OutgoingWorldData;

        var leftLowerLeg = BasisLocalBoneDriver.LeftLowerLegControl.OutgoingWorldData;
        var rightLowerLeg = BasisLocalBoneDriver.RightLowerLegControl.OutgoingWorldData;

        var leftShoulder = BasisLocalBoneDriver.LeftShoulderControl.OutgoingWorldData;
        var rightShoulder = BasisLocalBoneDriver.RightShoulderControl.OutgoingWorldData;

        // --- Arms ---
        data.LeftLowerArmPosition = BasisLocalRigDriver.ApplyHintBias(Basis.Scripts.TransformBinders.BoneControl.BasisBoneTrackedRole.LeftLowerArm, leftLowerArm.position, leftLowerArm.rotation);
        data.LeftLowerArmRotation = leftLowerArm.rotation;

        data.RightLowerArmPosition = BasisLocalRigDriver.ApplyHintBias(Basis.Scripts.TransformBinders.BoneControl.BasisBoneTrackedRole.RightLowerArm, rightLowerArm.position, rightLowerArm.rotation);
        data.RightLowerArmRotation = rightLowerArm.rotation;

        // --- Shoulders (rotation only in your data model) ---
        data.LeftShoulderRotation = leftShoulder.rotation;
        data.RightShoulderRotation = rightShoulder.rotation;

        // --- Legs ---
        data.PositionLeftLowerLeg = BasisLocalRigDriver.ApplyHintBias(Basis.Scripts.TransformBinders.BoneControl.BasisBoneTrackedRole.LeftLowerLeg, leftLowerLeg.position, leftLowerLeg.rotation);
        data.RotationLeftLowerLeg = leftLowerLeg.rotation;

        data.PositionRightLowerLeg = BasisLocalRigDriver.ApplyHintBias(Basis.Scripts.TransformBinders.BoneControl.BasisBoneTrackedRole.RightLowerLeg, rightLowerLeg.position, rightLowerLeg.rotation);
        data.RotationRightLowerLeg = rightLowerLeg.rotation;

        // --- Chest ---
        data.ChestPosition = BasisLocalRigDriver.ApplyHintBias(Basis.Scripts.TransformBinders.BoneControl.BasisBoneTrackedRole.Chest, chest.position, chest.rotation);
        data.ChestRotation = chest.rotation;

        data.CollisionsEnabled = true;
        data.UseHandCapsule = true;
        data.ProtectElbow = true;
        data.EnabledSpineIK = true;

        BasisFullIKConstraint.data = data;

        GeneratedRequiredTransforms(player, Mapping.head);

        GeneratedRequiredTransforms(player, Mapping.leftFoot);
        GeneratedRequiredTransforms(player, Mapping.rightFoot);

        GeneratedRequiredTransforms(player, Mapping.leftHand);
        GeneratedRequiredTransforms(player, Mapping.rightHand);
    }
    private static (bool valid, Vector3 pos) GetLM(Transform[] arr, int i)
    {
        if (arr != null && i >= 0 && i < arr.Length && arr[i] != null)
            return (true, arr[i].position);

        return (false, Vector3.zero);
    }
    private static Quaternion ComputeHandRotationWithFallback( Vector3 wrist,(bool valid, Vector3 pos)[] pointsA,(bool valid, Vector3 pos)[] pointsB, ref bool hasLast, ref Quaternion lastRot)
    {
        // pointsA[i] pairs with pointsB[i] as a candidate
        for (int i = 0; i < pointsA.Length; i++)
        {
            if (!pointsA[i].valid || !pointsB[i].valid) continue;

            Quaternion rot = HandRotationFromLandmarks(wrist, pointsA[i].pos, pointsB[i].pos);
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
        right = Vector3.Cross(up, forward).normalized;

        return Quaternion.LookRotation(forward, up);
    }
    public static void GeneratedRequiredTransforms(BasisLocalPlayer player,Transform baseLevel)
    {
        if (baseLevel == null)
        {
            return;
        }

        Transform hips = BasisLocalAvatarDriver.Mapping.Hips;
        Transform current = baseLevel;

        // Stop when we reach either the hips or the player root.
        while (current != null && current != hips && current != player.transform)
        {
            AddRigTransformIfMissing(player, current);
            current = current.parent;
        }

        AddRigTransformIfMissing(player, hips);
    }


    private static void AddRigTransformIfMissing(BasisLocalPlayer player, Transform t)
    {
        if (!t.TryGetComponent<RigTransform>(out var rig))
        {
            rig = t.gameObject.AddComponent<RigTransform>();
        }

        var list = player.LocalRigDriver.AdditionalTransforms;
        if (!list.Contains(rig))
        {
            list.Add(rig);
        }
    }
    public static GameObject CreateAndSetParent(Transform parent, string name)
    {
        Transform[] Children = parent.transform.GetComponentsInChildren<Transform>();
        foreach (Transform child in Children)
        {
            if (child.name == $"Bone Role {name}")
            {
                return child.gameObject;
            }
        }

        // Create a new empty GameObject
        GameObject newObject = new GameObject(name);

        // Set its parent
        newObject.transform.SetParent(parent);
        return newObject;
    }
}
