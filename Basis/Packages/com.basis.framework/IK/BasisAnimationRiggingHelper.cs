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
        GameObject DTData = CreateAndSetParent(Parent.transform, $"Bone Role {Role}");
        BasisApplyTranslation DT = BasisHelpers.GetOrAddComponent<BasisApplyTranslation>(DTData);

        DT.data.constrainedObject = Source;
        GeneratedRequiredTransforms(player, Source);
        return DT;
    }
    // convenient indices into the arrays
    private const int IDX_HEAD = 0;
    private const int IDX_LLEG = 1;
    private const int IDX_RLEG = 2;

    /// <summary>
    /// Build the combined Full IK constraint from arrays of joints and roles.
    /// root/mid/tip must be length >= 3: [Head, LeftLowerLeg, RightLowerLeg]
    /// TargetRole/BendRole/UseBoneRole correspond index-by-index to those same chains.
    /// </summary>
    public static void CreateMainIKRIG(
        BasisLocalPlayer player,
        GameObject parent,
        Transform[] root,
        Transform[] mid,
        Transform[] tip,
        BasisBoneTrackedRole[] TargetRole,
        BasisBoneTrackedRole[] BendRole,
        out BasisFullIKConstraint constraint,
        Transform hips, 
        BasisBoneTrackedRole hipsTargetRole =  BasisBoneTrackedRole.Hips
    )
    {
        // --- sanity checks (keep them cheap & defensive)
        if (root == null || mid == null || tip == null ||
            TargetRole == null || BendRole == null)
        {
            throw new System.ArgumentNullException("IK arrays cannot be null.");
        }

        if (root.Length < 3 || mid.Length < 3 || tip.Length < 3 ||
            TargetRole.Length < 3 || BendRole.Length < 3)
        {
            throw new System.ArgumentException("Expected at least 3 elements for Head, LeftLowerLeg, RightLowerLeg.");
        }

        // --- make holder and component
        var go = CreateAndSetParent(parent.transform, $"Full IK ({parent.name})");
        constraint = BasisHelpers.GetOrAddComponent<BasisFullIKConstraint>(go);

        // cache data ref — easier to see what we’re setting
        var data = constraint.data;

        // -----------------------------
        // HEAD
        // -----------------------------
        data.rootHead = root[IDX_HEAD];
        data.midHead = mid[IDX_HEAD];
        data.tipHead = tip[IDX_HEAD];

        data.m_CalibratedOffsetHead = Vector3.zero;
        data.m_CalibratedRotationHead = tip[IDX_HEAD] ? tip[IDX_HEAD].rotation : Quaternion.identity;

        if ( player.LocalBoneDriver.FindBone(out BasisLocalBoneControl headTarget, TargetRole[IDX_HEAD]))
        {
            data.TargetPositionHead = headTarget.OutgoingWorldData.position;
            data.TargetRotationHead = headTarget.OutgoingWorldData.rotation;
        }

        if (player.LocalBoneDriver.FindBone(out BasisLocalBoneControl headHint, BendRole[IDX_HEAD]))
        {
            data.HintPositionHead = headHint.OutgoingWorldData.position;
            data.HintRotationHead = headHint.OutgoingWorldData.rotation;
        }

        // -----------------------------
        // LEFT LOWER LEG
        // -----------------------------
        data.rootLeftLowerLeg = root[IDX_LLEG];
        data.midLeftLowerLeg = mid[IDX_LLEG];
        data.tipLeftLowerLeg = tip[IDX_LLEG];

        data.m_CalibratedOffsetLeftLowerLeg = Vector3.zero;
        data.m_CalibratedRotationLeftLowerLeg = tip[IDX_LLEG] ? tip[IDX_LLEG].rotation : Quaternion.identity;

        if (player.LocalBoneDriver.FindBone(out BasisLocalBoneControl lTarget, TargetRole[IDX_LLEG]))
        {
            data.TargetPositionLeftLowerLeg = lTarget.OutgoingWorldData.position;
            data.TargetRotationLeftLowerLeg = lTarget.OutgoingWorldData.rotation;
        }

        if (player.LocalBoneDriver.FindBone(out BasisLocalBoneControl lHint, BendRole[IDX_LLEG]))
        {
            data.HintPositionLeftLowerLeg = lHint.OutgoingWorldData.position;
            data.HintRotationLeftLowerLeg = lHint.OutgoingWorldData.rotation;
        }

        // -----------------------------
        // RIGHT LOWER LEG
        // -----------------------------
        data.rootRightLowerLeg = root[IDX_RLEG];
        data.midRightLowerLeg = mid[IDX_RLEG];
        data.tipRightLowerLeg = tip[IDX_RLEG];

        data.m_CalibratedOffsetRightLowerLeg = Vector3.zero;
        data.m_CalibratedRotationRightLowerLeg = tip[IDX_RLEG] ? tip[IDX_RLEG].rotation : Quaternion.identity;

        if (player.LocalBoneDriver.FindBone(out BasisLocalBoneControl rTarget, TargetRole[IDX_RLEG]))
        {
            data.TargetPositionRightLowerLeg = rTarget.OutgoingWorldData.position;
            data.TargetRotationRightLowerLeg = rTarget.OutgoingWorldData.rotation;
        }

        if (player.LocalBoneDriver.FindBone(out BasisLocalBoneControl rHint, BendRole[IDX_RLEG]))
        {
            data.HintPositionRightLowerLeg = rHint.OutgoingWorldData.position;
            data.HintRotationRightLowerLeg = rHint.OutgoingWorldData.rotation;
        }

        // -----------------------------
        // HIPS (optional minimal driver)
        // -----------------------------
        data.hips = hips;

        if (hips != null && player.LocalBoneDriver.FindBone(out BasisLocalBoneControl hipsCtrl, hipsTargetRole))
        {
            data.TargetPositionHips = hipsCtrl.OutgoingWorldData.position;
            data.TargetRotationEulerHips = hipsCtrl.OutgoingWorldData.rotation;   // Quaternion
            data.OffsetRotationHips = Quaternion.identity;                   // set your T-pose offset if needed
        }

        // write back
        constraint.data = data;

        // If you have any extra setup that needs the tips (like creating targets), do it now.
        GeneratedRequiredTransforms(player, tip[0]);
        GeneratedRequiredTransforms(player, tip[1]);
        GeneratedRequiredTransforms(player, tip[2]);
    }

public static void CreateTwoBone(BasisLocalPlayer player, GameObject Parent, Transform root, Transform mid, Transform tip, BasisBoneTrackedRole TargetRole, BasisBoneTrackedRole BendRole, bool UseBoneRole, out BasisTwoBoneIKConstraint TwoBoneIKConstraint)
    {
        player.LocalBoneDriver.FindBone(out BasisLocalBoneControl TargetControl, TargetRole);

        GameObject BoneRole = CreateAndSetParent(Parent.transform, $"Bone Role {TargetRole}");
        TwoBoneIKConstraint = BasisHelpers.GetOrAddComponent<BasisTwoBoneIKConstraint>(BoneRole);

        TwoBoneIKConstraint.data.M_CalibratedOffset = Vector3.zero;
        TwoBoneIKConstraint.data.M_CalibratedRotation = tip.rotation;

        TwoBoneIKConstraint.data.TargetPosition = TargetControl.OutgoingWorldData.position;
        TwoBoneIKConstraint.data.TargetRotation = TargetControl.OutgoingWorldData.rotation;
        if (UseBoneRole && player.LocalBoneDriver.FindBone(out BasisLocalBoneControl HintControl, BendRole))
        {
            Quaternion HintRotation = HintControl.OutgoingWorldData.rotation;
            TwoBoneIKConstraint.data.HintPosition = HintControl.OutgoingWorldData.position;
            TwoBoneIKConstraint.data.HintRotation = HintRotation;
        }
        TwoBoneIKConstraint.data.root = root;
        TwoBoneIKConstraint.data.mid = mid;
        TwoBoneIKConstraint.data.tip = tip;

        GeneratedRequiredTransforms(player, tip);
    }

    public static void CreateTwoBoneHand(BasisLocalPlayer player, GameObject Parent, Transform ChestStart, Transform ChestEnd, Transform root, Transform mid, Transform tip, Quaternion Rotation, BasisBoneTrackedRole TargetRole, BasisBoneTrackedRole BendRole, bool UseBoneRole, out BasisTwoBoneIKConstraintHand TwoBoneIKConstraint)
    {
        if (!player.LocalBoneDriver.FindBone(out BasisLocalBoneControl TargetControl, TargetRole))
        {
            BasisDebug.LogError($"Missing Targeted Role {TargetRole}", BasisDebug.LogTag.IK);
            TwoBoneIKConstraint = null;
        }
        else
        {
            GameObject BoneRole = CreateAndSetParent(Parent.transform, $"Bone Role {TargetRole}");
            TwoBoneIKConstraint = BasisHelpers.GetOrAddComponent<BasisTwoBoneIKConstraintHand>(BoneRole);

            TwoBoneIKConstraint.data.M_CalibratedOffset = new Vector3(0, 0, 0);
            TwoBoneIKConstraint.data.M_CalibratedRotation = Rotation;

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
}
