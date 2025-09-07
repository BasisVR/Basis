using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;
using UnityEngine.Animations.Rigging;

public static class BasisAnimationRiggingHelper
{
    public static BasisApplyTranslation Damp(BasisLocalPlayer player, GameObject Parent, Transform Source, BasisBoneTrackedRole Role)
    {
        player.LocalBoneDriver.FindBone(out BasisLocalBoneControl Target, Role);
        GameObject DTData = CreateAndSetParent(Parent.transform, $"Bone Role {Role.ToString()}");
        BasisApplyTranslation DT = BasisHelpers.GetOrAddComponent<BasisApplyTranslation>(DTData);

        DT.data.constrainedObject = Source;
        GeneratedRequiredTransforms(player, Source);
        return DT;
    }

    public static void CreateTwoBone(BasisLocalPlayer player, GameObject Parent, Transform root, Transform mid, Transform tip, BasisBoneTrackedRole TargetRole, BasisBoneTrackedRole BendRole, bool UseBoneRole, out BasisTwoBoneIKConstraint TwoBoneIKConstraint, bool maintainTargetPositionOffset, bool maintainTargetRotationOffset)
    {
        player.LocalBoneDriver.FindBone(out BasisLocalBoneControl TargetControl, TargetRole);

        GameObject BoneRole = CreateAndSetParent(Parent.transform, $"Bone Role {TargetRole.ToString()}");
        TwoBoneIKConstraint = BasisHelpers.GetOrAddComponent<BasisTwoBoneIKConstraint>(BoneRole);

        TwoBoneIKConstraint.data.M_CalibratedOffset = Vector3.zero;
        TwoBoneIKConstraint.data.M_CalibratedRotation = tip.rotation;

        TwoBoneIKConstraint.data.TargetPosition = TargetControl.OutgoingWorldData.position;
        TwoBoneIKConstraint.data.TargetRotation = TargetControl.OutgoingWorldData.rotation;
        if (UseBoneRole)
        {
            if (player.LocalBoneDriver.FindBone(out BasisLocalBoneControl HintControl, BendRole))
            {
                Quaternion HintRotation = HintControl.OutgoingWorldData.rotation;
                TwoBoneIKConstraint.data.HintPosition = HintControl.OutgoingWorldData.position;
                TwoBoneIKConstraint.data.HintRotation = HintRotation;
            }
        }
        TwoBoneIKConstraint.data.root = root;
        TwoBoneIKConstraint.data.mid = mid;
        TwoBoneIKConstraint.data.tip = tip;

        GeneratedRequiredTransforms(player, tip);
    }

    public static void CreateTwoBoneHand(BasisLocalPlayer player, GameObject Parent, Transform ChestStart, Transform ChestEnd, Transform root, Transform mid, Transform tip, BasisBoneTrackedRole TargetRole, BasisBoneTrackedRole BendRole, bool UseBoneRole, out BasisTwoBoneIKConstraintHand TwoBoneIKConstraint, bool maintainTargetPositionOffset, bool maintainTargetRotationOffset)
    {
        player.LocalBoneDriver.FindBone(out BasisLocalBoneControl TargetControl, TargetRole);

        GameObject BoneRole = CreateAndSetParent(Parent.transform, $"Bone Role {TargetRole.ToString()}");
        TwoBoneIKConstraint = BasisHelpers.GetOrAddComponent<BasisTwoBoneIKConstraintHand>(BoneRole);

        TwoBoneIKConstraint.data.M_CalibratedOffset = new Vector3(0, 0, 0);
        TwoBoneIKConstraint.data.M_CalibratedRotation = tip.rotation;

        TwoBoneIKConstraint.data.TargetPosition = TargetControl.OutgoingWorldData.position;
        TwoBoneIKConstraint.data.TargetRotation = TargetControl.OutgoingWorldData.rotation;

        if (UseBoneRole && player.LocalBoneDriver.FindBone(out BasisLocalBoneControl HintControl, BendRole))
        {
            var outgoing = HintControl.OutgoingWorldData;
            TwoBoneIKConstraint.data.HintPosition = outgoing.position;
            TwoBoneIKConstraint.data.HintRotation = outgoing.rotation;
        }

        TwoBoneIKConstraint.data.root = root;
        TwoBoneIKConstraint.data.mid = mid;
        TwoBoneIKConstraint.data.tip = tip;
        TwoBoneIKConstraint.data.collisionsEnabled = true;
        TwoBoneIKConstraint.data.chestCapsuleEnd = ChestEnd;
        TwoBoneIKConstraint.data.chestCapsuleStart = ChestStart;
        TwoBoneIKConstraint.data.useHandCapsule = true;
        TwoBoneIKConstraint.data.protectElbow = true;
        SetHandCollisionScale(TwoBoneIKConstraint, player.CurrentHeight.SelectedAvatarToAvatarDefaultScale);
        GeneratedRequiredTransforms(player, tip);
    }
    public static void SetHandCollisionScale(BasisTwoBoneIKConstraintHand TwoBoneIKConstraint, float Scale)
    {
        //1.6m is the default values for below.
        TwoBoneIKConstraint.data.collisionSkin = 0.05f * Scale;
        TwoBoneIKConstraint.data.handRadius = 0.01f * Scale;
        TwoBoneIKConstraint.data.handSkin = 0.03f * Scale;
        TwoBoneIKConstraint.data.chestRadius = 0.07f * Scale;
    }
    public static void GeneratedRequiredTransforms(BasisLocalPlayer player, Transform BaseLevel)
    {
        // Go up the hierarchy until you hit the TopLevelParent
        if (BaseLevel != null)
        {
            Transform currentTransform = BaseLevel.parent;
            while (currentTransform != null && currentTransform != BasisLocalAvatarDriver.References.Hips)
            {
                // Add component if the current transform doesn't have it
                if (currentTransform.TryGetComponent<RigTransform>(out RigTransform RigTransform))
                {
                    if (player.LocalRigDriver.AdditionalTransforms.Contains(RigTransform) == false)
                    {
                        player.LocalRigDriver.AdditionalTransforms.Add(RigTransform);
                    }
                }
                else
                {
                    RigTransform = currentTransform.gameObject.AddComponent<RigTransform>();
                    player.LocalRigDriver.AdditionalTransforms.Add(RigTransform);
                }
                // Move to the parent for the next iteration
                currentTransform = currentTransform.parent;
            }
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
    public static void CreateSpine(BasisLocalPlayer player, GameObject parent, Transform hips, Transform head, BasisBoneTrackedRole hipRole, out BasisHipsHeadIKConstraint SpineIKConstraint)
    {
        player.LocalBoneDriver.FindBone(out BasisLocalBoneControl hipControl, hipRole);

        var boneRole = CreateAndSetParent(parent.transform, $"Bone Role {hipRole.ToString()}");
        SpineIKConstraint = BasisHelpers.GetOrAddComponent<BasisHipsHeadIKConstraint>(boneRole);

        // Set the transform references FIRST
        SpineIKConstraint.data.hips = hips;
        //SpineIKConstraint.data.hipsOffsetRotation = Quaternion.identity;
        SpineIKConstraint.data.hipsOffsetRotation = hips.rotation;
        GeneratedRequiredTransforms(player, head);
    }
}
