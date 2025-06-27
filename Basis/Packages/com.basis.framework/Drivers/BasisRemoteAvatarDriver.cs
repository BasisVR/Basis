using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.TransformBinders.BoneControl;
namespace Basis.Scripts.Drivers
{
    [System.Serializable]
    public class BasisRemoteAvatarDriver : BasisAvatarDriver
    {
        public void RemoteCalibration(BasisRemotePlayer remotePlayer)
        {
            if (IsAble(remotePlayer))
            {
              //  BasisDebug.Log("RemoteCalibration Underway", BasisDebug.LogTag.Avatar);
            }
            else
            {
                return;
            }
            Calibration(remotePlayer.BasisAvatar);
            remotePlayer.EyeFollow.Initalize(this, remotePlayer);
            SetAllMatrixRecalculation(false);
            updateWhenOffscreen(false);
            remotePlayer.BasisAvatar.Animator.logWarnings = false;
            for (int Index = 0; Index < SkinnedMeshRenderer.Length; Index++)
            {
                SkinnedMeshRenderer[Index].forceMatrixRecalculationPerRender = false;
            }
            CalculateTransformPositions(remotePlayer, remotePlayer.RemoteBoneDriver);
            ComputeOffsets(remotePlayer.RemoteBoneDriver);
            remotePlayer.BasisAvatar.Animator.enabled = false;
            CalibrationComplete?.Invoke();
        }
        public void ComputeOffsets(BasisBaseBoneDriver BBD)
        {
            SetAndCreateLock(BBD, BasisBoneTrackedRole.Head, BasisBoneTrackedRole.Neck, 40, 12, true);

            SetAndCreateLock(BBD, BasisBoneTrackedRole.Head, BasisBoneTrackedRole.CenterEye, 40, 12, true);

            SetAndCreateLock(BBD, BasisBoneTrackedRole.Head, BasisBoneTrackedRole.Mouth, 40, 12, true);


            SetAndCreateLock(BBD, BasisBoneTrackedRole.Neck, BasisBoneTrackedRole.Chest, 40, 12, true);
            SetAndCreateLock(BBD, BasisBoneTrackedRole.Chest, BasisBoneTrackedRole.Spine, 40, 12, true);
            SetAndCreateLock(BBD, BasisBoneTrackedRole.Spine, BasisBoneTrackedRole.Hips, 40, 12, true);
        }
        public bool IsAble(BasisRemotePlayer remotePlayer)
        {
            if (IsNull(remotePlayer.BasisAvatar))
            {
                return false;
            }
            if (remotePlayer.RemoteBoneDriver == null)
            {
                return false;
            }
            if (IsNull(remotePlayer.BasisAvatar.Animator))
            {
                return false;
            }

            if (IsNull(remotePlayer))
            {
                return false;
            }
            return true;
        }
    }
}
