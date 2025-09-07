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
    public Vector3 leftHandToIKPositionOffset = Vector3.zero;
    public Vector3 rightHandToIKPositionOffset = Vector3.zero;
    public quaternion HandleHandFinalRotation(quaternion IncomingRotation)
    {
        if (TryGetRole(out BasisBoneTrackedRole AssignedRole))
        {
            switch (AssignedRole)
            {
                case BasisBoneTrackedRole.LeftHand:
                    IncomingRotation = IncomingRotation * Quaternion.Euler(leftHandToIKRotationOffset);
                    break;
                case BasisBoneTrackedRole.RightHand:
                    IncomingRotation = IncomingRotation * Quaternion.Euler(rightHandToIKRotationOffset);
                    break;
            }
        }
        return IncomingRotation;
    }
    public void UpdateRaycastOffset()
    {
        if (TryGetRole(out BasisBoneTrackedRole AssignedRole))
        {
            switch (AssignedRole)
            {
                case BasisBoneTrackedRole.LeftHand:
                    ActiveRaycastOffset = Quaternion.Euler(LeftRaycastRotationOffset);
                    break;
                case BasisBoneTrackedRole.RightHand:
                    ActiveRaycastOffset = Quaternion.Euler(RightRaycastRotationOffset);
                    break;
            }
        }
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
