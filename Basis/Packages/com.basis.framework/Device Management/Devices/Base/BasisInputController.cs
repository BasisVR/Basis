using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.TransformBinders.BoneControl;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Base class for hand controller input devices.
/// Handles calibration, IK offsets, raycast orientation, and control data propagation.
/// </summary>
public abstract class BasisInputController : BasisInput
{
    [Header("Final Data (normally modified by EyeHeight/AvatarEyeHeight)")]
    /// <summary>
    /// Calibrated hand coordinates (final values after processing offsets/scales).
    /// </summary>
    public BasisCalibratedCoords HandFinal = new BasisCalibratedCoords();

    [Header("IK Offsets")]
    public Vector3 leftHandToIKRotationOffset = new Vector3(0,0,0);
    public Vector3 rightHandToIKRotationOffset = new Vector3(0, 0, 0);

    [Header("Raycast Rotation Offsets")]
    public Vector3 LeftRaycastRotationOffset = new Vector3(0,0, 0);
    public Vector3 RightRaycastRotationOffset = new Vector3(0,0, 0);

    /// <summary>
    /// Active raycast offset (determined by role).
    /// </summary>
    public Quaternion ActiveRaycastOffset;

    [Header("IK Position Offsets")]
    public Vector3 leftHandToIKPositionOffset = Vector3.zero;
    public Vector3 rightHandToIKPositionOffset = Vector3.zero;
    public bool UseIKPositionOffset = true;
    /// <summary>
    /// Applies IK-specific rotation offsets to the incoming hand rotation based on role.
    /// </summary>
    /// <param name="IncomingRotation">Raw rotation from device.</param>
    /// <returns>Adjusted rotation for IK systems.</returns>
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

    /// <summary>
    /// Updates the <see cref="ActiveRaycastOffset"/> based on current tracked role.
    /// </summary>
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

    /// <summary>
    /// Forces the control system to only use this device’s hand data (ignores other inputs).
    /// </summary>
    public void ControlOnlyAsHand(Vector3 Position,Quaternion Rotation)
    {
        if (hasRoleAssigned && Control.HasTracked != BasisHasTracked.HasNoTracker)
        {
            Control.IncomingData.position = Position;
            Control.IncomingData.rotation = Rotation;
        }
    }
    public Vector3 ChangeHandYHeight(Vector3 position)
    {
        if (SMModuleSitStand.IsSteatedMode && BasisDeviceManagement.IsCurrentModeVR())
        {
            position.y += SMModuleSitStand.MissingHeightDelta;
            return position;
        }
        else
        {
            return position;
        }
    }
}
