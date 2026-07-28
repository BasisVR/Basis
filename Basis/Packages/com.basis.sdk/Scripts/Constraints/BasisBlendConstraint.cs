using UnityEngine;

namespace Basis.Scripts.BasisSdk.Constraints
{
    /// <summary>
    /// Blends a transform between two sources rather than averaging a whole source list: the first
    /// source is the 0 end, the second is the 1 end. The Basis stand-in for Animation Rigging's
    /// <c>BlendConstraint</c>.
    ///
    /// Sources past the first two are ignored for the blend itself, but they still register as
    /// dependencies, so a source driven by another constraint is still solved first.
    /// </summary>
    public class BasisBlendConstraint : BasisConstraintBase
    {
        [Tooltip("Blend the position between the two sources.")]
        public bool blendPosition = true;

        [Tooltip("Blend the rotation between the two sources.")]
        public bool blendRotation = true;

        [Tooltip("Where the position lands: 0 is the first source, 1 is the second.")]
        [Range(0f, 1f)]
        public float positionWeight = 0.5f;

        [Tooltip("Where the rotation lands: 0 is the first source, 1 is the second.")]
        [Range(0f, 1f)]
        public float rotationWeight = 0.5f;

        [Tooltip("Local position used when Weight is 0.")]
        public Vector3 translationAtRest;

        [Tooltip("Local rotation in degrees used when Weight is 0.")]
        public Vector3 rotationAtRest;

        [Tooltip("Position axes this constraint may drive. Excluded axes keep their current value.")]
        public BasisConstraintAxes translationAxis = BasisConstraintAxes.All;

        [Tooltip("Rotation axes this constraint may drive. Excluded axes keep their current value.")]
        public BasisConstraintAxes rotationAxis = BasisConstraintAxes.All;

        public override BasisConstraintType constraintType => BasisConstraintType.Blend;

        public override void CaptureRest()
        {
            transform.GetLocalPositionAndRotation(out var localPosition, out var localRotation);
            translationAtRest = localPosition;
            rotationAtRest = localRotation.eulerAngles;
        }
    }
}
