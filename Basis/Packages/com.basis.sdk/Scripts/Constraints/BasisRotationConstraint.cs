using UnityEngine;

namespace Basis.Scripts.BasisSdk.Constraints
{
    /// <summary>
    /// Drives a transform's local rotation from the weighted average of its sources' world rotations.
    /// The Basis stand-in for <c>UnityEngine.Animations.RotationConstraint</c>.
    ///
    /// Blending is the hemisphere-corrected quaternion average used throughout Basis, then masked per
    /// axis in ZXY euler order — the same order Unity's constraint uses, so authored per-axis rigs
    /// carry over unchanged.
    /// </summary>
    public class BasisRotationConstraint : BasisConstraintBase
    {
        [Tooltip("Local euler rotation used when Weight is 0.")]
        public Vector3 rotationAtRest;

        [Tooltip("Local euler offset applied after the blended source rotation.")]
        public Vector3 rotationOffset;

        [Tooltip("Axes this constraint may drive. Excluded axes keep their current value.")]
        public BasisConstraintAxes rotationAxis = BasisConstraintAxes.All;

        public override BasisConstraintType constraintType => BasisConstraintType.Rotation;

        public override void CaptureRest()
        {
            rotationAtRest = transform.localRotation.eulerAngles;
        }
    }
}
