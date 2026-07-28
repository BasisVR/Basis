using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Drivers;
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
            Control.SetIncoming(Position, Rotation);
        }
    }

    /// <summary>
    /// The wrist the avatar's hand bone is actually driven to, expressed in the same unscaled player
    /// space as <see cref="BasisInput.UnscaledDeviceCoord"/>.
    ///
    /// Every backend builds <see cref="HandFinal"/> as OffsetCoords ⊗ (wrist × DeviceScale), so undoing
    /// that recovers the wrist wherever the backend's raw pose happens to sit. OpenVR already bakes the
    /// wrist into the device coord, so this lands back on the same point and nothing changes; OpenXR
    /// leaves the device coord on the grip pose while the bone goes to the palm minus a wrist offset, so
    /// there the two differ by several cm per hand.
    ///
    /// Arm-span calibration has to sample this rather than the raw device pose: measuring a landmark the
    /// hand bone never occupies makes the player look longer-armed than they are, and the body fit spends
    /// the whole difference stretching the avatar's arms.
    /// </summary>
    public Vector3 UnscaledHandTarget
    {
        get
        {
            float scale = BasisHeightDriver.DeviceScale;
            // HandFinal is written by the pose update, which on some backends is a subsystem callback
            // rather than the poll — before the first one lands it is still default, and a zero world
            // position is not a pose a tracked hand can hold.
            if (HandFinal.position.sqrMagnitude < 1e-8f || scale <= 1e-6f || float.IsNaN(scale) || float.IsInfinity(scale))
            {
                return UnscaledDeviceCoord.position;
            }
            return (Quaternion.Inverse(OffsetCoords.rotation) * (HandFinal.position - OffsetCoords.position)) / scale;
        }
    }

    public Vector3 ChangeHandYHeight(Vector3 position)
    {
        // Mirror BasisInput.ComputeUnscaledDeviceCoord so the avatar's hand bones (driven by HandFinal,
        // a separate path) lift with the play-space mover's vertical offset and seated mode — not just
        // the head/body/controller models. VR only.
        if (BasisDeviceManagement.IsCurrentModeVR())
        {
            position.y += BasisLocalPlayspaceMover.VerticalOffset;
            if (SMModuleSitStand.IsSteatedMode)
            {
                position.y += SMModuleSitStand.MissingHeightDelta;
            }
            position.y += BasisHeightDriver.HeightModeGroundingOffset;
        }
        return position;
    }
}
