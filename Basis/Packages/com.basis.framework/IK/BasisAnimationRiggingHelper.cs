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
        data.hips =  Mapping.Hips;
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
        data.m_CalibratedRotationLeftFoot = Mapping.Hashead ? Mapping.leftFoot.rotation : Quaternion.identity;
        data.m_CalibratedRotationRightFoot = Mapping.Hashead ? Mapping.rightFoot.rotation : Quaternion.identity;

        // Hands
        data.m_CalibratedRotationLeftHand = Mapping.HasleftHand ? Mapping.leftHand.rotation : Quaternion.identity;
        data.m_CalibratedRotationRightHand = Mapping.HasrightHand ? Mapping.rightHand.rotation : Quaternion.identity;
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
        data.HintPositionHead = BasisLocalBoneDriver.ChestControl.OutgoingWorldData.position;
        data.HintRotationHead = BasisLocalBoneDriver.ChestControl.OutgoingWorldData.rotation;

        // Left leg / foot
        data.LeftFootPosition = BasisLocalBoneDriver.LeftFootControl.OutgoingWorldData.position;
        data.LeftFootRotation = BasisLocalBoneDriver.LeftFootControl.OutgoingWorldData.rotation;
        data.HintPositionLeftLowerLeg = BasisLocalBoneDriver.LeftLowerLegControl.OutgoingWorldData.position;
        data.HintRotationLeftLowerLeg = BasisLocalBoneDriver.LeftLowerLegControl.OutgoingWorldData.rotation;

        // Right leg / foot
        data.RightFootPosition = BasisLocalBoneDriver.RightFootControl.OutgoingWorldData.position;
        data.RightFootRotation = BasisLocalBoneDriver.RightFootControl.OutgoingWorldData.rotation;
        data.HintPositionRightFoot = BasisLocalBoneDriver.RightLowerLegControl.OutgoingWorldData.position;
        data.HintRotationRightFoot = BasisLocalBoneDriver.RightLowerLegControl.OutgoingWorldData.rotation;

        // Hips
        data.PositionHips = BasisLocalBoneDriver.HipsControl.OutgoingWorldData.position;
        data.RotationEulerHips = BasisLocalBoneDriver.HipsControl.OutgoingWorldData.rotation;

        // Hands
        data.PositionLeftHand = BasisLocalBoneDriver.LeftHandControl.OutgoingWorldData.position;
        data.RotationLeftHand = BasisLocalBoneDriver.LeftHandControl.OutgoingWorldData.rotation;
        data.PositionRightHand = BasisLocalBoneDriver.RightHandControl.OutgoingWorldData.position;
        data.RotationRightHand = BasisLocalBoneDriver.RightHandControl.OutgoingWorldData.rotation;

        data.HintPositionLeftHand = BasisLocalBoneDriver.LeftLowerArmControl.OutgoingWorldData.position;
        data.HintRotationLeftHand = BasisLocalBoneDriver.LeftLowerArmControl.OutgoingWorldData.rotation;
        data.HintPositionRightHand = BasisLocalBoneDriver.RightLowerArmControl.OutgoingWorldData.position;
        data.HintRotationRightHand = BasisLocalBoneDriver.RightLowerArmControl.OutgoingWorldData.rotation;

        data.m_TargetRotationLeftShoulder = BasisLocalBoneDriver.LeftShoulderControl.OutgoingWorldData.rotation;
        data.m_TargetRotationRightShoulder = BasisLocalBoneDriver.RightShoulderControl.OutgoingWorldData.rotation;
        // ----------------------------
        // Flags / options
        // ----------------------------
        data.collisionsEnabled = true;
        data.useHandCapsule = true;
        data.protectElbow = true;
        data.EnabledSpineIK = true;
        // ----------------------------
        // Write back once
        // ----------------------------
        BasisFullIKConstraint.data = data;

        GeneratedRequiredTransforms(player, Mapping.head);

        GeneratedRequiredTransforms(player, Mapping.leftFoot);
        GeneratedRequiredTransforms(player, Mapping.rightFoot);

        GeneratedRequiredTransforms(player, Mapping.leftHand);
        GeneratedRequiredTransforms(player, Mapping.rightHand);
    }

    public static void GeneratedRequiredTransforms(BasisLocalPlayer player,Transform baseLevel)
    {
        if (baseLevel == null)
        {
            return;
        }

        Transform hips = BasisLocalAvatarDriver.References.Hips;
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
