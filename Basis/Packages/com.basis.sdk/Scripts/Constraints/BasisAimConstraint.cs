using UnityEngine;

namespace Basis.Scripts.BasisSdk.Constraints
{
    /// <summary>
    /// Rotates a transform so its <see cref="aimVector"/> points at the weighted average of its
    /// sources' positions, rolled so its <see cref="upVector"/> lines up with the resolved world up.
    /// The Basis stand-in for <c>UnityEngine.Animations.AimConstraint</c>.
    ///
    /// Position is untouched — only rotation is driven. Use <see cref="BasisLookAtConstraint"/> for
    /// the simpler "point +Z at it" case.
    /// </summary>
    public class BasisAimConstraint : BasisConstraintBase
    {
        [Tooltip("Local axis aimed at the sources.")]
        public Vector3 aimVector = Vector3.forward;

        [Tooltip("Local axis rolled toward the resolved world up.")]
        public Vector3 upVector = Vector3.up;

        [Tooltip("How the world up is resolved.")]
        public BasisConstraintWorldUp worldUpType = BasisConstraintWorldUp.SceneUp;

        [Tooltip("Reference transform for the Object Up and Object Rotation Up modes.")]
        public Transform worldUpObject;

        [Tooltip("Up direction for the Vector and Object Rotation Up modes.")]
        public Vector3 worldUpVector = Vector3.up;

        [Tooltip("Local euler rotation used when Weight is 0.")]
        public Vector3 rotationAtRest;

        [Tooltip("Local euler offset applied after aiming.")]
        public Vector3 rotationOffset;

        [Tooltip("Axes this constraint may drive. Excluded axes keep their current value.")]
        public BasisConstraintAxes rotationAxis = BasisConstraintAxes.All;

        public override BasisConstraintType constraintType => BasisConstraintType.Aim;

        public override void CaptureRest()
        {
            rotationAtRest = transform.localRotation.eulerAngles;
        }
    }
}
