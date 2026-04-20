using Basis.Scripts.Common;
using Basis.Scripts.TransformBinders.BoneControl;

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
