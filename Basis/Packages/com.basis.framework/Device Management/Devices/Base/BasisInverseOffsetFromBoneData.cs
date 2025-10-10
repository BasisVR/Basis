using UnityEngine;

namespace Basis.Scripts.Device_Management.Devices
{
    /// <summary>
    /// Represents the positional and rotational offset of a tracker relative to a bone.
    /// Stores both the raw tracked values and the calculated inverse/initial rotations
    /// for aligning tracker and bone data in calibration or runtime tracking.
    /// </summary>
    public struct BasisInverseOffsetFromBoneData
    {
        /// <summary>
        /// The current world position of the tracker.
        /// </summary>
        public Vector3 TrackerPosition;

        /// <summary>
        /// The current world rotation of the tracker.
        /// </summary>
        public Quaternion TrackerRotation;

        /// <summary>
        /// The initial inverse rotation of the tracker relative to the bone.
        /// Used to transform from tracker space back into bone space.
        /// </summary>
        public Quaternion InitialInverseTrackRotation;

        /// <summary>
        /// The initial control rotation of the bone at calibration.
        /// Used to restore or apply consistent alignment.
        /// </summary>
        public Quaternion InitialControlRotation;
    }
}
