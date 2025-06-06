using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.TransformBinders.BoneControl;

namespace Basis.Scripts.Drivers
{
    [System.Serializable]
    public class BasisLocalBoneDriver : BasisBaseBoneDriver
    {
        public static BasisBoneControl Head;
        public static BasisBoneControl Hips;
        public static BasisBoneControl Eye;
        public static BasisBoneControl Mouth;
        public static BasisBoneControl HeadControl;
        public static BasisBoneControl LeftFootControl;
        public static BasisBoneControl RightFootControl;
        public static BasisBoneControl LeftHandControl;
        public static BasisBoneControl RightHandControl;
        public static BasisBoneControl ChestControl;
        public static BasisBoneControl LeftLowerLegControl;
        public static BasisBoneControl RightLowerLegControl;
        public static BasisBoneControl LeftLowerArmControl;
        public static BasisBoneControl RightLowerArmControl;
        public static bool HasEye;

        public void Initialize(BasisLocalPlayer localPlayer)
        {
            HasEye = FindBone(out Eye, BasisBoneTrackedRole.CenterEye);
            FindBone(out Head, BasisBoneTrackedRole.Head);
            FindBone(out Hips, BasisBoneTrackedRole.Hips);
            FindBone(out Mouth, BasisBoneTrackedRole.Mouth);

            // --- Bone Lookup ---
            FindBone(out HeadControl, BasisBoneTrackedRole.Head);
            FindBone(out LeftFootControl, BasisBoneTrackedRole.LeftFoot);
            FindBone(out RightFootControl, BasisBoneTrackedRole.RightFoot);
            FindBone(out LeftHandControl, BasisBoneTrackedRole.LeftHand);
            FindBone(out RightHandControl, BasisBoneTrackedRole.RightHand);

            FindBone(out ChestControl, BasisBoneTrackedRole.Chest);
            FindBone(out LeftLowerLegControl, BasisBoneTrackedRole.LeftLowerLeg);
            FindBone(out RightLowerLegControl, BasisBoneTrackedRole.RightLowerLeg);
            FindBone(out LeftLowerArmControl, BasisBoneTrackedRole.LeftLowerArm);
            FindBone(out RightLowerArmControl, BasisBoneTrackedRole.RightLowerArm);
        }
        public void PostSimulateBonePositions()
        {
            SimulateWorldDestinations(BasisLocalPlayer.Instance.transform);
        }
    }
}
