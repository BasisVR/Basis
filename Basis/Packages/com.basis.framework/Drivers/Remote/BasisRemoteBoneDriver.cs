using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;

namespace Basis.Scripts.Drivers
{
    [System.Serializable]
    public class BasisRemoteBoneDriver : BasisBaseBoneDriver
    {
        public BasisRemotePlayer RemotePlayer;
        public Transform RemotePlayerTransform;
        public Transform HeadAvatar;
        public Transform HipsAvatar;
        public BasisBoneControl Head;
        public BasisBoneControl Hips;
        public BasisBoneControl Mouth;
        public bool HasHead;
        public bool HasHips;
        public void InitializeRemote()
        {
            FindBone(out Head, BasisBoneTrackedRole.Head);
            FindBone(out Hips, BasisBoneTrackedRole.Hips);
            if (Head != null)
            {
                Head.HasTracked = BasisHasTracked.HasTracker;
            }
            if (Hips != null)
            {
                Hips.HasTracked = BasisHasTracked.HasTracker;
            }
            FindBone(out Mouth, BasisBoneTrackedRole.Mouth);
        }
        public void CalculateBoneData()
        {
            Vector3 RRT = RemotePlayerTransform.position;
            if (Head.HasBone && HasHead)
            {
                HeadAvatar.GetPositionAndRotation(out Vector3 Position, out Quaternion Rotation);
                Head.IncomingData.position = Position - RRT;
                Head.IncomingData.rotation = Rotation;
            }
            if (Hips.HasBone && HasHips)
            {
                HipsAvatar.GetPositionAndRotation(out Vector3 Position, out Quaternion Rotation);
                Hips.IncomingData.position = Position - RRT;
                Hips.IncomingData.rotation = Rotation;
            }
        }
        public void OnCalibration(BasisRemotePlayer remotePlayer)
        {
            HeadAvatar = RemotePlayer.BasisAvatar.Animator.GetBoneTransform(HumanBodyBones.Head);
            HasHead = HeadAvatar != null;
            HipsAvatar = RemotePlayer.BasisAvatar.Animator.GetBoneTransform(HumanBodyBones.Hips);
            HasHips = HipsAvatar != null;
            this.RemotePlayer = remotePlayer;
            this.RemotePlayerTransform = RemotePlayer.transform;
        }
    }
}
