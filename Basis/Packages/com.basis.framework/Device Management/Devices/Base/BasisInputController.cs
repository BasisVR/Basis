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

    public Vector3 leftHandToIKRotationOffset;
    public Vector3 rightHandToIKRotationOffset;
    public Vector3 LeftRaycastRotationOffset;
    public Vector3 RightRaycastRotationOffset;
    public Quaternion ActiveRaycastOffset;
    public quaternion HandleHandFinalRotation(quaternion IncomingRotation)
    {
        quaternion outgoingRotation = IncomingRotation;
        if (TryGetRole(out BasisBoneTrackedRole AssignedRole))
        {
            switch (AssignedRole)
            {
                case BasisBoneTrackedRole.LeftHand:
                    outgoingRotation = IncomingRotation * Quaternion.Euler(leftHandToIKRotationOffset);
                    ActiveRaycastOffset = Quaternion.Euler(LeftRaycastRotationOffset);
                    break;
                case BasisBoneTrackedRole.RightHand:
                    outgoingRotation = IncomingRotation * Quaternion.Euler(rightHandToIKRotationOffset);
                    ActiveRaycastOffset = Quaternion.Euler(RightRaycastRotationOffset);
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
            Control.IncomingData.rotation = HandFinal.rotation;
        }
    }

    public void ComputeRaycastDirection()
    {
        RaycastCoord.position = HandFinal.position;
        RaycastCoord.rotation = HandFinal.rotation * ActiveRaycastOffset;
    }
}
