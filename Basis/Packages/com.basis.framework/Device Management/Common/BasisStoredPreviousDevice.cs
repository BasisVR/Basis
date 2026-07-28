using Basis.Scripts.Common;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;

namespace Basis.Scripts.Device_Management
{
    /// <summary>
    /// Persisted snapshot of a previously bound device so it can be restored later.
    /// Stores calibration offset, tracked role assignment, and identifiers.
    /// </summary>
    [System.Serializable]
    public class BasisStoredPreviousDevice
    {
        /// <summary>
        /// The inverse offset from the avatar bone that was used to align this device.
        /// Typically applied as <c>bonePose * InverseOffsetFromBone</c> during reconstruction.
        /// </summary>
        public BasisCalibratedCoords InverseOffsetFromBone;

        /// <summary>
        /// The device's scale-free calibration snapshot (BasisInput.CalibratedUnscaled*), carried across
        /// a disconnect so a restored device can still have its position offset re-derived for a new
        /// avatar/DeviceScale. The head anchor travels with it — the snapshot is only meaningful in the
        /// frame of the head it was captured against.
        /// </summary>
        public bool HasCalibratedOffsetSnapshot;
        public Vector3 CalibratedUnscaledPosition;
        public Quaternion CalibratedUnscaledRotation = Quaternion.identity;
        public Vector3 CalibratedUnscaledHeadPosition;
        public Quaternion CalibratedUnscaledHeadRotation = Quaternion.identity;

        /// <summary>
        /// The tracked role (e.g., Head, LeftHand) that this device was assigned to.
        /// </summary>
        public BasisBoneTrackedRole trackedRole;

        /// <summary>
        /// Whether a valid <see cref="trackedRole"/> was assigned at the time of storage.
        /// </summary>
        public bool hasRoleAssigned = false;

        /// <summary>
        /// Name of the input subsystem/provider this device came from (e.g., OpenXR, OpenVR).
        /// </summary>
        public string SubSystemIdentifier;

        /// <summary>
        /// Stable unique identifier for the physical/logical device instance.
        /// </summary>
        public string UniqueDeviceIdentifier;
    }
}
