namespace Basis.Scripts.TransformBinders.BoneControl
{
    public enum BasisBoneTrackedRole
    {
        CenterEye = 0,
        Head = 1,
        Neck = 2,
        Chest = 3,
        Hips = 4,
        Spine = 5,

        LeftUpperLeg = 6,
        RightUpperLeg = 7,

        LeftLowerLeg = 8,
        RightLowerLeg = 9,

        LeftFoot = 10,
        RightFoot = 11,

        LeftShoulder = 12,
        RightShoulder = 13,

        LeftUpperArm = 14,
        RightUpperArm = 15,

        LeftLowerArm = 16,
        RightLowerArm = 17,

        LeftHand = 18,
        RightHand = 19,

        LeftToes = 20,
        RightToes = 21,

        Mouth = 22,


    }

    public static class BasisBoneTrackedRoleCommonCheck
    {
        public static bool CheckItsFBTracker(BasisBoneTrackedRole role)
        {
            return role != BasisBoneTrackedRole.LeftHand
                && role != BasisBoneTrackedRole.RightHand

                && role != BasisBoneTrackedRole.LeftUpperLeg
                && role != BasisBoneTrackedRole.RightUpperLeg

                && role != BasisBoneTrackedRole.RightUpperArm
                && role != BasisBoneTrackedRole.LeftUpperArm

                && role != BasisBoneTrackedRole.CenterEye
                && role != BasisBoneTrackedRole.Head
                && role != BasisBoneTrackedRole.Neck
                && role != BasisBoneTrackedRole.Mouth
                && role != BasisBoneTrackedRole.Spine;
        }
        /// <summary>
        /// True if the role is explicitly a left-side body part.
        /// </summary>
        public static bool IsLeft(this BasisBoneTrackedRole role)
        {
            return role switch
            {
                BasisBoneTrackedRole.LeftUpperLeg => true,
                BasisBoneTrackedRole.LeftLowerLeg => true,
                BasisBoneTrackedRole.LeftFoot => true,
                BasisBoneTrackedRole.LeftToes => true,

                BasisBoneTrackedRole.LeftShoulder => true,
                BasisBoneTrackedRole.LeftUpperArm => true,
                BasisBoneTrackedRole.LeftLowerArm => true,
                BasisBoneTrackedRole.LeftHand => true,

                _ => false
            };
        }

        /// <summary>
        /// True if the role is explicitly a right-side body part.
        /// </summary>
        public static bool IsRight(this BasisBoneTrackedRole role)
        {
            return role switch
            {
                BasisBoneTrackedRole.RightUpperLeg => true,
                BasisBoneTrackedRole.RightLowerLeg => true,
                BasisBoneTrackedRole.RightFoot => true,
                BasisBoneTrackedRole.RightToes => true,

                BasisBoneTrackedRole.RightShoulder => true,
                BasisBoneTrackedRole.RightUpperArm => true,
                BasisBoneTrackedRole.RightLowerArm => true,
                BasisBoneTrackedRole.RightHand => true,

                _ => false
            };
        }

        /// <summary>
        /// True if the role is not a left/right specific limb (centerline body).
        /// </summary>
        public static bool IsCenter(this BasisBoneTrackedRole role) => !role.IsLeft() && !role.IsRight();

        /// <summary>
        /// Returns -1 for left, +1 for right, 0 for center.
        /// Useful for compact side checks.
        /// </summary>
        public static int SideSign(this BasisBoneTrackedRole role)
        {
            if (role.IsLeft()) return -1;
            if (role.IsRight()) return 1;
            return 0;
        }
    }
}
