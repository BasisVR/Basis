using Basis.Scripts.BasisSdk.Helpers;
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
    public static BasisApplyTranslation Damp(BasisLocalAvatarDriver AvatarDriver, BasisLocalBoneDriver driver, GameObject Parent, Transform Source, BasisBoneTrackedRole Role, float rotationWeight = 1, float positionWeight = 1)
    {
        driver.FindBone(out BasisBoneControl Target, Role);
        GameObject DTData = CreateAndSetParent(Parent.transform, $"Bone Role {Role.ToString()}");
        BasisApplyTranslation DT = BasisHelpers.GetOrAddComponent<BasisApplyTranslation>(DTData);

        DT.data.constrainedObject = Source;
        GeneratedRequiredTransforms(AvatarDriver, Source);
        WriteUpWeights(Target, DT);
        return DT;
    }
    public static void TwistChain(BasisLocalBoneDriver driver, GameObject Parent, Transform root, Transform tip, BasisBoneTrackedRole Root, BasisBoneTrackedRole Tip, float rotationWeight = 1, float positionWeight = 1)
    {
        driver.FindBone(out BasisBoneControl RootTarget, Root);
        driver.FindBone(out BasisBoneControl TipTarget, Tip);
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
    public static void CreateTwoBone(BasisLocalAvatarDriver AvatarDriver, BasisLocalBoneDriver driver, GameObject Parent, Transform root, Transform mid, Transform tip, BasisBoneTrackedRole TargetRole, BasisBoneTrackedRole BendRole, bool UseBoneRole, out BasisTwoBoneIKConstraint TwoBoneIKConstraint, bool maintainTargetPositionOffset, bool maintainTargetRotationOffset)
    {
        driver.FindBone(out BasisBoneControl TargetControl, TargetRole);


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
            if (driver.FindBone(out BasisBoneControl HintControl, BendRole))
            {
                Quaternion HintRotation = HintControl.OutgoingWorldData.rotation;
                TwoBoneIKConstraint.data.HintPosition = HintControl.OutgoingWorldData.position;
                TwoBoneIKConstraint.data.HintRotation = HintRotation.eulerAngles;
            }
        }
        TwoBoneIKConstraint.data.root = root;
        TwoBoneIKConstraint.data.mid = mid;
        TwoBoneIKConstraint.data.tip = tip;
        GeneratedRequiredTransforms(AvatarDriver, tip);
    }
    public static void CreateTwoBoneHand(BasisLocalAvatarDriver AvatarDriver, BasisLocalBoneDriver driver, GameObject Parent, Transform root, Transform mid, Transform tip, BasisBoneTrackedRole TargetRole, BasisBoneTrackedRole BendRole, bool UseBoneRole, out BasisTwoBoneIKConstraintHand TwoBoneIKConstraint, bool maintainTargetPositionOffset, bool maintainTargetRotationOffset)
    {
        driver.FindBone(out BasisBoneControl TargetControl, TargetRole);


        GameObject BoneRole = CreateAndSetParent(Parent.transform, $"Bone Role {TargetRole.ToString()}");
        TwoBoneIKConstraint = BasisHelpers.GetOrAddComponent<BasisTwoBoneIKConstraintHand>(BoneRole);
        Quaternion Rotation = TargetControl.OutgoingWorldData.rotation;
        TwoBoneIKConstraint.data.TargetPosition = TargetControl.OutgoingWorldData.position;
        TwoBoneIKConstraint.data.TargetRotation = Rotation.eulerAngles;
        if (UseBoneRole)
        {
            if (driver.FindBone(out BasisBoneControl HintControl, BendRole))
            {
                Quaternion HintRotation = HintControl.OutgoingWorldData.rotation;
                TwoBoneIKConstraint.data.HintPosition = HintControl.OutgoingWorldData.position;
                TwoBoneIKConstraint.data.HintRotation = HintRotation.eulerAngles;
            }
        }
        TwoBoneIKConstraint.data.root = root;
        TwoBoneIKConstraint.data.mid = mid;
        TwoBoneIKConstraint.data.tip = tip;
        GeneratedRequiredTransforms(AvatarDriver, tip);
    }
    public static void WriteUpWeights(BasisBoneControl Control, BasisApplyTranslation Constraint)
    {
        Control.WeightsChanged += (delegate (float positionWeight, float rotationWeight)
        {
            UpdateIKRig(positionWeight, rotationWeight, Constraint);
        });
    }

    public static void UpdateIKRig(float PositionWeight, float RotationWeight, BasisApplyTranslation Constraint)
    {
        Constraint.weight = PositionWeight;
    }
    public static void GeneratedRequiredTransforms(BasisLocalAvatarDriver Driver, Transform BaseLevel)
    {
        // Go up the hierarchy until you hit the TopLevelParent
        if (BaseLevel != null)
        {
            Transform currentTransform = BaseLevel.parent;
            while (currentTransform != null && currentTransform != Driver.References.Hips)
            {
                // Add component if the current transform doesn't have it
                if (currentTransform.TryGetComponent<RigTransform>(out RigTransform RigTransform))
                {
                    if (Driver.AdditionalTransforms.Contains(RigTransform) == false)
                    {
                        Driver.AdditionalTransforms.Add(RigTransform);
                    }
                }
                else
                {
                    RigTransform = currentTransform.gameObject.AddComponent<RigTransform>();
                    Driver.AdditionalTransforms.Add(RigTransform);
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
