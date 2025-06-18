using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.TransformBinders.BoneControl;
using Unity.Mathematics;
using UnityEngine;
public abstract class BasisInputController : BasisInput
{
    [Header("Final Data normally just modified by EyeHeight/AvatarEyeHeight)")]
    public float3 HandFinalPosition;
    public quaternion HandFinalRotation;

    public float3 leftHandToIKRotationOffset;
    public float3 rightHandToIKRotationOffset;
    public float3 IkOffsetPosition;
    public void HandleHandFinalRotation()
    {
        quaternion ConvertedRotation = HandFinalRotation;
        if (TryGetRole(out BasisBoneTrackedRole AssignedRole))
        {
            switch (AssignedRole)
            {
                case BasisBoneTrackedRole.LeftHand:
                    ConvertedRotation = math.mul(HandFinalRotation, Quaternion.Euler(leftHandToIKRotationOffset));
                    break;
                case BasisBoneTrackedRole.RightHand:
                    ConvertedRotation = math.mul(HandFinalRotation, Quaternion.Euler(rightHandToIKRotationOffset));
                    break;
            }
        }
        HandFinalRotation = math.mul(DeviceFinalRotation, ConvertedRotation);
    }
}
