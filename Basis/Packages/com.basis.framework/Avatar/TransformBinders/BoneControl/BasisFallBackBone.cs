using UnityEngine;

namespace Basis.Scripts.TransformBinders.BoneControl
{
    /// <summary>
    /// Represents a fallback bone reference used when primary tracking data
    /// is unavailable. Provides both raw and percentage-based positions for
    /// consistent retargeting across avatars.
    /// </summary>
    [System.Serializable]
    public class BasisFallBackBone
    {
        /// <summary>
        /// The absolute position of the fallback bone (in world or local space).
        /// </summary>
        [SerializeField]
        public Vector3 Position;

        /// <summary>
        /// The normalized position of the fallback bone, usually expressed as
        /// a percentage relative to avatar proportions.
        /// </summary>
        [SerializeField]
        public Vector3 PositionPercentage;

        /// <summary>
        /// Unity’s humanoid bone type that this fallback maps to.
        /// </summary>
        [SerializeField]
        public HumanBodyBones HumanBone;

        /// <summary>
        /// The custom tracked role (e.g., head, left hand, right foot) this
        /// fallback is associated with.
        /// </summary>
        [SerializeField]
        public BasisBoneTrackedRole Role;
    }
}
