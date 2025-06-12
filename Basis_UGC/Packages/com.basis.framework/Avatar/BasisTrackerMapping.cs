using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Basis.Scripts.Avatar
{
public static partial class BasisAvatarIKStageCalibration
{
        [System.Serializable]
        public class BasisTrackerMapping
        {
            [SerializeField]
            public BasisBoneControl TargetControl;
            [SerializeField]
            public BasisBoneTrackedRole BasisBoneControlRole;
            [SerializeField]
            public List<CalibrationConnector> Candidates = new List<CalibrationConnector>();
            public List<Vector3> Stored = new List<Vector3>();
            public Vector3 CalibrationPoint;
            public BasisTrackerMapping(BasisBoneControl Bone,Transform AvatarTransform, BasisBoneTrackedRole Role, List<CalibrationConnector> calibration, float calibrationMaxDistance)
            {
                if(AvatarTransform == null)
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
                Candidates = new List<CalibrationConnector>();
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
    public class CalibrationConnector
    {
        [SerializeField]
        public BasisInput BasisInput;
        public float Distance;
    }
    public static BasisGeneralLocation FindGeneralLocation(BasisBoneTrackedRole Role)
    {

        switch (Role)
        {
            case BasisBoneTrackedRole.CenterEye:
                return BasisGeneralLocation.Center;
            case BasisBoneTrackedRole.Head:
                return BasisGeneralLocation.Center;
            case BasisBoneTrackedRole.Neck:
                return BasisGeneralLocation.Center;
            case BasisBoneTrackedRole.Chest:
                return BasisGeneralLocation.Center;
            case BasisBoneTrackedRole.Hips:
                return BasisGeneralLocation.Center;
            case BasisBoneTrackedRole.Spine:
                return BasisGeneralLocation.Center;
            case BasisBoneTrackedRole.Mouth:
                return BasisGeneralLocation.Center;

            case BasisBoneTrackedRole.RightUpperLeg:
                return BasisGeneralLocation.Right;
            case BasisBoneTrackedRole.RightLowerLeg:
                return BasisGeneralLocation.Right;
            case BasisBoneTrackedRole.RightFoot:
                return BasisGeneralLocation.Right;
            case BasisBoneTrackedRole.RightShoulder:
                return BasisGeneralLocation.Right;
            case BasisBoneTrackedRole.RightUpperArm:
                return BasisGeneralLocation.Right;
            case BasisBoneTrackedRole.RightLowerArm:
                return BasisGeneralLocation.Right;
            case BasisBoneTrackedRole.RightHand:
                return BasisGeneralLocation.Right;
            case BasisBoneTrackedRole.RightToes:
                return BasisGeneralLocation.Right;

            case BasisBoneTrackedRole.LeftHand:
                return BasisGeneralLocation.Left;
            case BasisBoneTrackedRole.LeftShoulder:
                return BasisGeneralLocation.Left;
            case BasisBoneTrackedRole.LeftLowerArm:
                return BasisGeneralLocation.Left;
            case BasisBoneTrackedRole.LeftFoot:
                return BasisGeneralLocation.Left;
            case BasisBoneTrackedRole.LeftUpperLeg:
                return BasisGeneralLocation.Left;
            case BasisBoneTrackedRole.LeftLowerLeg:
                return BasisGeneralLocation.Left;
            case BasisBoneTrackedRole.LeftUpperArm:
                return BasisGeneralLocation.Left;
            case BasisBoneTrackedRole.LeftToes:
                return BasisGeneralLocation.Left;

            default:
                Console.WriteLine("Unknown role " + Role);
                return BasisGeneralLocation.Center;
        }
    }
    public static BasisBoneTrackedRole[] desiredOrder = new BasisBoneTrackedRole[]
    {
        BasisBoneTrackedRole.Hips,
        BasisBoneTrackedRole.RightFoot,
        BasisBoneTrackedRole.LeftFoot,
        BasisBoneTrackedRole.LeftLowerLeg,
        BasisBoneTrackedRole.RightLowerLeg,
        BasisBoneTrackedRole.LeftLowerArm,
        BasisBoneTrackedRole.RightLowerArm,

        BasisBoneTrackedRole.CenterEye,
        BasisBoneTrackedRole.Chest,

        BasisBoneTrackedRole.Head,
        BasisBoneTrackedRole.Neck,

        BasisBoneTrackedRole.LeftHand,
        BasisBoneTrackedRole.RightHand,

        BasisBoneTrackedRole.LeftToes,
        BasisBoneTrackedRole.RightToes,

        BasisBoneTrackedRole.LeftUpperArm,
        BasisBoneTrackedRole.RightUpperArm,
        BasisBoneTrackedRole.LeftUpperLeg,
        BasisBoneTrackedRole.RightUpperLeg,
        BasisBoneTrackedRole.LeftShoulder,
        BasisBoneTrackedRole.RightShoulder,
    };
}
}
