using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Basis.Scripts.Avatar
{
    [System.Serializable]
    public class BasisTrackerMapping
    {
        [SerializeField]
        public BasisLocalBoneControl TargetControl;
        [SerializeField]
        public BasisBoneTrackedRole BasisBoneControlRole;
        [SerializeField]
        public List<BasisCalibrationConnector> Candidates = new List<BasisCalibrationConnector>();
        public List<Vector3> Stored = new List<Vector3>();
        public Vector3 CalibrationPoint;
        public BasisTrackerMapping(BasisLocalBoneControl Bone, Transform AvatarTransform, BasisBoneTrackedRole Role, List<BasisCalibrationConnector> calibration, float calibrationMaxDistance)
        {
            if (AvatarTransform == null)
            {
                Debug.LogWarning("Missing Avatar Transform");
                CalibrationPoint = Bone.OutgoingWorldData.position;
            }
            else
            {
                CalibrationPoint = AvatarTransform.position;
            }
            TargetControl = Bone;
            BasisBoneControlRole = Role;
            Candidates = new List<BasisCalibrationConnector>();
            for (int Index = 0; Index < calibration.Count; Index++)
            {
                Vector3 Input = calibration[Index].BasisInput.transform.position;
                calibration[Index].Distance = Vector3.Distance(CalibrationPoint, Input);

                if (calibration[Index].Distance < calibrationMaxDistance)
                {
                    Debug.DrawLine(CalibrationPoint, Input, TargetControl.Color, 40f);
                    Candidates.Add(calibration[Index]);
                }
                else
                {
                    // Debug.DrawLine(BoneControl, Input, Color.red, 40f);
                }
            }
            Candidates.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        }
    }
}
