using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;
using UnityEngine.Animations.Rigging;

public static class BasisAnimationRiggingHelper
{
    public static void EnableTwoBoneIk(BasisTwoBoneIKConstraint Constraint, Vector3 TargetPositionOffset, Vector3 TargetRotationOffset)
    {
        Constraint.data.M_CalibratedOffset = TargetPositionOffset;
        Constraint.data.M_CalibratedRotation = TargetRotationOffset;
    }

    public static BasisApplyTranslation Damp(BasisLocalPlayer player, GameObject Parent, Transform Source, BasisBoneTrackedRole Role)
    {
        player.LocalBoneDriver.FindBone(out BasisLocalBoneControl Target, Role);
        GameObject DTData = CreateAndSetParent(Parent.transform, $"Bone Role {Role.ToString()}");
        BasisApplyTranslation DT = BasisHelpers.GetOrAddComponent<BasisApplyTranslation>(DTData);

        DT.data.constrainedObject = Source;
        GeneratedRequiredTransforms(player, Source);
        return DT;
    }

    public static void TwistChain(BasisLocalBoneDriver driver, GameObject Parent, Transform root, Transform tip, BasisBoneTrackedRole Root, BasisBoneTrackedRole Tip, float rotationWeight = 1, float positionWeight = 1)
    {
        driver.FindBone(out BasisLocalBoneControl RootTarget, Root);
        driver.FindBone(out BasisLocalBoneControl TipTarget, Tip);
        GameObject DTData = CreateAndSetParent(Parent.transform, $"Bone Role {Root.ToString()}");
        TwistChainConstraint DT = BasisHelpers.GetOrAddComponent<TwistChainConstraint>(DTData);
        Keyframe[] Frame = new Keyframe[2];
        Frame[0] = new Keyframe(0, 0);
        Frame[1] = new Keyframe(1, 1);
        DT.data.curve = new AnimationCurve(Frame);

        DT.data.tip = null;
        DT.data.root = null;
        DT.data.tipTarget = tip;
        DT.data.rootTarget = root;
        //GeneratedRequiredTransforms(root, References.Hips);
    }

    public static void CreateTwoBone(BasisLocalPlayer player, GameObject Parent, Transform root, Transform mid, Transform tip, BasisBoneTrackedRole TargetRole, BasisBoneTrackedRole BendRole, bool UseBoneRole, out BasisTwoBoneIKConstraint TwoBoneIKConstraint, bool maintainTargetPositionOffset, bool maintainTargetRotationOffset)
    {
        player.LocalBoneDriver.FindBone(out BasisLocalBoneControl TargetControl, TargetRole);


        GameObject BoneRole = CreateAndSetParent(Parent.transform, $"Bone Role {TargetRole.ToString()}");
        TwoBoneIKConstraint = BasisHelpers.GetOrAddComponent<BasisTwoBoneIKConstraint>(BoneRole);

        Vector3 PositionOffset = new Vector3(0, 0, 0);

        Quaternion RotationOffset =  tip.rotation;//Quaternion.Inverse(TargetControl.OutgoingWorldData.rotation) *
        EnableTwoBoneIk(TwoBoneIKConstraint, PositionOffset, RotationOffset.eulerAngles);
        Quaternion Rotation = TargetControl.OutgoingWorldData.rotation;
        TwoBoneIKConstraint.data.TargetPosition = TargetControl.OutgoingWorldData.position;
        TwoBoneIKConstraint.data.TargetRotation = Rotation.eulerAngles;
        if (UseBoneRole)
        {
            if (player.LocalBoneDriver.FindBone(out BasisLocalBoneControl HintControl, BendRole))
            {
                Quaternion HintRotation = HintControl.OutgoingWorldData.rotation;
                TwoBoneIKConstraint.data.HintPosition = HintControl.OutgoingWorldData.position;
                TwoBoneIKConstraint.data.HintRotation = HintRotation.eulerAngles;
            }
        }
        TwoBoneIKConstraint.data.root = root;
        TwoBoneIKConstraint.data.mid = mid;
        TwoBoneIKConstraint.data.tip = tip;
        GeneratedRequiredTransforms(player, tip);
    }

    public static void CreateTwoBoneHand(BasisLocalPlayer player, GameObject Parent, Transform root, Transform mid, Transform tip, BasisBoneTrackedRole TargetRole, BasisBoneTrackedRole BendRole, bool UseBoneRole, out BasisTwoBoneIKConstraintHand TwoBoneIKConstraint, bool maintainTargetPositionOffset, bool maintainTargetRotationOffset)
    {
        player.LocalBoneDriver.FindBone(out BasisLocalBoneControl TargetControl, TargetRole);

        GameObject BoneRole = CreateAndSetParent(Parent.transform, $"Bone Role {TargetRole.ToString()}");
        TwoBoneIKConstraint = BasisHelpers.GetOrAddComponent<BasisTwoBoneIKConstraintHand>(BoneRole);
        TwoBoneIKConstraint.data.M_CalibratedOffset = new Vector3(0, 0, 0);
        TwoBoneIKConstraint.data.M_CalibratedRotation = tip.rotation.eulerAngles;
        TwoBoneIKConstraint.data.TargetPosition = TargetControl.OutgoingWorldData.position;
        TwoBoneIKConstraint.data.TargetRotation = TargetControl.OutgoingWorldData.rotation.eulerAngles;

        if (UseBoneRole && player.LocalBoneDriver.FindBone(out BasisLocalBoneControl HintControl, BendRole))
        {
            Quaternion HintRotation = HintControl.OutgoingWorldData.rotation;
            TwoBoneIKConstraint.data.HintPosition = HintControl.OutgoingWorldData.position;
            TwoBoneIKConstraint.data.HintRotation = HintRotation.eulerAngles;
        }
  
        TwoBoneIKConstraint.data.root = root;
        TwoBoneIKConstraint.data.mid = mid;
        TwoBoneIKConstraint.data.tip = tip;
        GeneratedRequiredTransforms(player, tip);
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
}
