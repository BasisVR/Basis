using Basis.Scripts.Common;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.TransformBinders.BoneControl;
using Unity.Mathematics;
using UnityEngine;
public abstract class BasisInputController : BasisInput
{
    [Header("Final Data normally just modified by EyeHeight/AvatarEyeHeight)")]
    public BasisCalibratedCoords HandFinal = new BasisCalibratedCoords();
    public BasisCalibratedCoords HandRaw = new BasisCalibratedCoords();

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
    public void ControlOnlyAsHand()
    {
        if (hasRoleAssigned && Control.HasTracked != BasisHasTracked.HasNoTracker)
        {
            Control.IncomingData.position = HandFinal.position;
            Control.IncomingData.rotation = HandleHandFinalRotation(HandFinal.rotation);
        }
    }
    public void ComputeRaycastDirection()
    {
        RaycastCoord.position = HandFinal.position;

        RaycastCoord.rotation = math.mul(HandFinal.rotation, Quaternion.Euler(RaycastRotationOffset));
    }
}
