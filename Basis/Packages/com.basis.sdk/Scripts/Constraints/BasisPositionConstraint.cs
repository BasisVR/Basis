using UnityEngine;

namespace Basis.Scripts.BasisSdk.Constraints
{
    /// <summary>
    /// Drives a transform's local position from the weighted average of its sources' world positions.
    /// The Basis stand-in for <c>UnityEngine.Animations.PositionConstraint</c>, with the same field
    /// names and semantics; unlike the built-in it costs no per-instance <c>Update</c>, because every
    /// enabled constraint in the scene is solved in one batched job.
    /// </summary>
    public class BasisPositionConstraint : BasisConstraintBase
    {
        [Tooltip("Local position used when Weight is 0.")]
        public Vector3 translationAtRest;

        [Tooltip("Local-space offset added to the blended source position.")]
        public Vector3 translationOffset;

        [Tooltip("Axes this constraint may drive. Excluded axes keep their current value.")]
        public BasisConstraintAxes translationAxis = BasisConstraintAxes.All;

        public override BasisConstraintType constraintType => BasisConstraintType.Position;

        public override void CaptureRest()
        {
            translationAtRest = transform.localPosition;
        }
    }
}
