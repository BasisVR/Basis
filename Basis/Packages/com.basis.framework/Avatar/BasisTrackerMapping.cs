using Basis.Scripts.TransformBinders.BoneControl;
using System.Collections.Generic;
using UnityEngine;
using static Basis.Scripts.Avatar.BasisAvatarIKStageCalibration;
namespace Basis.Scripts.Avatar
{
    /// <summary>
    /// the Basis Tracker Mapper goal is to calculate Distances between Input Devices
    /// </summary>
    [System.Serializable]
    public class BasisTrackerMapping
    {
        [SerializeField]
        public BasisLocalBoneControl TargetControl;
        [SerializeField]
        public BasisBoneTrackedRole BasisBoneControlRole;
        [SerializeField]
        public List<BasisCalibrationData> Candidates = new List<BasisCalibrationData>();
        public Vector3 CalibrationPoint;
        public BasisTrackerMapping(BasisLocalBoneControl Bone, Transform AvatarTransform, BasisBoneTrackedRole Role, List<BasisCalibrationData> calibration, float calibrationMaxDistance)
        {
            if (AvatarTransform == null)
            {
                CalibrationPoint = Bone.OutgoingWorldData.position;
            }
            else
            {
                CalibrationPoint = AvatarTransform.position;
            }
            TargetControl = Bone;
            BasisBoneControlRole = Role;
            Candidates = new List<BasisCalibrationData>();
            for (int Index = 0; Index < calibration.Count; Index++)
            {
                Vector3 Input = calibration[Index].BasisInput.transform.position;
                calibration[Index].Distance = Vector3.Distance(CalibrationPoint, Input);
                if (calibration[Index].Distance < calibrationMaxDistance)
                {
                    Candidates.Add(calibration[Index]);
                }
            }
            Candidates.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        }
    }
}
