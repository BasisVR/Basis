using UnityEngine;

namespace Basis.Scripts.BasisSdk.Constraints
{
    /// <summary>
    /// Drives a transform's local scale from the weighted average of its sources' lossy (world) scales.
    /// The Basis stand-in for <c>UnityEngine.Animations.ScaleConstraint</c>.
    ///
    /// The blend happens in world scale and is divided back through the parent's lossy scale, so a
    /// constrained child tracks its source's apparent size regardless of where either sits in the
    /// hierarchy.
    /// </summary>
    public class BasisScaleConstraint : BasisConstraintBase
    {
        [Tooltip("Local scale used when Weight is 0.")]
        public Vector3 scaleAtRest = Vector3.one;

        [Tooltip("Multiplied into the blended source scale.")]
        public Vector3 scaleOffset = Vector3.one;

        [Tooltip("Axes this constraint may drive. Excluded axes keep their current value.")]
        public BasisConstraintAxes scalingAxis = BasisConstraintAxes.All;

        public override BasisConstraintType constraintType => BasisConstraintType.Scale;

        public override void CaptureRest()
        {
            scaleAtRest = transform.localScale;
        }
    }
}
