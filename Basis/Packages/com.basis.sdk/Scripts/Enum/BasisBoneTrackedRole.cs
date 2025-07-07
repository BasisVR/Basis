namespace Basis.Scripts.TransformBinders.BoneControl
{
    public enum BasisBoneTrackedRole
    {
        CenterEye,
        Head,
        Neck,
        Chest,
        Hips,
        Spine,

        LeftUpperLeg,
        RightUpperLeg,
        LeftLowerLeg,
        RightLowerLeg,
        LeftFoot,
        RightFoot,
        LeftShoulder,
        RightShoulder,
        LeftUpperArm,
        RightUpperArm,
        LeftLowerArm,
        RightLowerArm,
        LeftHand,
        RightHand,
        LeftToes,
        RightToes,

        Mouth,
    }
    public static class BasisBoneTrackedRoleCommonCheck
    {
        public static bool CheckItsFBTracker(BasisBoneTrackedRole role)
        {
            return CheckIfHeadAreaTracker(role) == false &&
                   role != BasisBoneTrackedRole.LeftHand &&
                   role != BasisBoneTrackedRole.RightHand &&
                   role != BasisBoneTrackedRole.LeftUpperLeg &&
                   role != BasisBoneTrackedRole.RightUpperLeg &&
                   role != BasisBoneTrackedRole.LeftUpperArm &&
                   role != BasisBoneTrackedRole.RightUpperArm &&
                   role != BasisBoneTrackedRole.Spine;
        }
        public static bool CheckIfHintRole(BasisBoneTrackedRole role)
        {
            bool IsHintRole = (role == BasisBoneTrackedRole.LeftLowerArm || role == BasisBoneTrackedRole.RightLowerArm || role == BasisBoneTrackedRole.LeftLowerLeg || role == BasisBoneTrackedRole.RightLowerLeg);
            BasisDebug.Log($"was hint {IsHintRole} {role.ToString()}", BasisDebug.LogTag.IK);
            return IsHintRole;
        }
        public static bool CheckIfHeadAreaTracker(BasisBoneTrackedRole role)
        {
            return role == BasisBoneTrackedRole.CenterEye || role == BasisBoneTrackedRole.Head || role == BasisBoneTrackedRole.Neck || role == BasisBoneTrackedRole.Mouth;
        }
    }
}
