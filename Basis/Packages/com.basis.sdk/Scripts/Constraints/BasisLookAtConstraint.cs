using UnityEngine;

namespace Basis.Scripts.BasisSdk.Constraints
{
    /// <summary>
    /// Rotates a transform so its local +Z points at the weighted average of its sources' positions.
    /// The Basis stand-in for <c>UnityEngine.Animations.LookAtConstraint</c> — an aim constraint with
    /// the axes fixed to the Unity convention (+Z forward, +Y up) and a roll angle instead of a full
    /// world-up configuration.
    /// </summary>
    public class BasisLookAtConstraint : BasisConstraintBase
    {
        [Tooltip("Extra roll about the look axis, in degrees.")]
        public float roll;

        [Tooltip("Roll toward World Up Object instead of scene up.")]
        public bool useUpObject;

        [Tooltip("Reference transform used when Use Up Object is set.")]
        public Transform worldUpObject;

        [Tooltip("Local euler rotation used when Weight is 0.")]
        public Vector3 rotationAtRest;

        [Tooltip("Local euler offset applied after looking.")]
        public Vector3 rotationOffset;

        public override BasisConstraintType constraintType => BasisConstraintType.LookAt;

        public override void CaptureRest()
        {
            rotationAtRest = transform.localRotation.eulerAngles;
        }
    }
}
