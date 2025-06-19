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
    public float3 RaycastRotationOffset;
    public quaternion HandleHandFinalRotation(quaternion IncomingRotation)
    {
        quaternion outgoingRotation = IncomingRotation;
        if (TryGetRole(out BasisBoneTrackedRole AssignedRole))
        {
            switch (AssignedRole)
            {
                case BasisBoneTrackedRole.LeftHand:
                    outgoingRotation = math.mul(IncomingRotation, Quaternion.Euler(leftHandToIKRotationOffset));
                    break;
                case BasisBoneTrackedRole.RightHand:
                    outgoingRotation = math.mul(IncomingRotation, Quaternion.Euler(rightHandToIKRotationOffset));
                    break;
            }
        }
        return outgoingRotation;
    }
}
