using UnityEngine;

namespace Basis.Scripts.BasisSdk.Constraints
{
    /// <summary>
    /// Analytic two-bone IK — the arm and leg solver. The Basis stand-in for Animation Rigging's
    /// <c>TwoBoneIKConstraint</c>.
    ///
    /// Sits on the tip bone (the hand or foot). The mid and root default to walking up the hierarchy,
    /// which is what a limb already looks like, but both can be set explicitly for rigs with spacer
    /// transforms in between. The IK target is the first source and the hint (pole) is the optional
    /// second, so both take part in the dependency ordering — an IK target that is itself constrained
    /// still solves first.
    /// </summary>
    public class BasisTwoBoneIK : BasisConstraintBase
    {
        [Tooltip("Middle bone (elbow or knee). Defaults to this transform's parent.")]
        public Transform mid;

        [Tooltip("Root bone (shoulder or hip). Defaults to the mid bone's parent.")]
        public Transform root;

        [Tooltip("How strongly the tip is pulled onto the target's position.")]
        [Range(0f, 1f)]
        public float targetPositionWeight = 1f;

        [Tooltip("How strongly the tip takes the target's rotation.")]
        [Range(0f, 1f)]
        public float targetRotationWeight = 1f;

        [Tooltip("How strongly the hint steers which way the limb bends.")]
        [Range(0f, 1f)]
        public float hintWeight = 1f;

        [Tooltip("Hold the offset the tip had from the target when the offset was captured, instead " +
                 "of landing exactly on it.")]
        public bool maintainTargetOffset;

        [SerializeField, HideInInspector] private Vector3 targetOffsetPosition;
        [SerializeField, HideInInspector] private Quaternion targetOffsetRotation = Quaternion.identity;

        public Vector3 TargetOffsetPosition => maintainTargetOffset ? targetOffsetPosition : Vector3.zero;
        public Quaternion TargetOffsetRotation =>
            maintainTargetOffset ? targetOffsetRotation : Quaternion.identity;

        /// <summary>The middle bone, explicit or derived. Null when the hierarchy is too shallow.</summary>
        public Transform ResolveMid() => mid != null ? mid : transform.parent;

        /// <summary>The root bone, explicit or derived. Null when the hierarchy is too shallow.</summary>
        public Transform ResolveRoot()
        {
            if (root != null)
            {
                return root;
            }
            Transform resolvedMid = ResolveMid();
            return resolvedMid != null ? resolvedMid.parent : null;
        }

        public override BasisConstraintType constraintType => BasisConstraintType.TwoBoneIK;

        /// <summary>
        /// Freezes how far the tip currently sits from the IK target, so turning the constraint on
        /// does not snap the limb. Only consulted while Maintain Target Offset is set.
        /// </summary>
        public override void CaptureRest()
        {
            Transform target = sourceCount > 0 ? GetSource(0).sourceTransform : null;
            if (target == null)
            {
                targetOffsetPosition = Vector3.zero;
                targetOffsetRotation = Quaternion.identity;
                return;
            }

            target.GetPositionAndRotation(out var targetPosition, out var targetRotation);
            transform.GetPositionAndRotation(out var tipPosition, out var tipRotation);

            targetOffsetPosition = tipPosition - targetPosition;
            targetOffsetRotation = Quaternion.Inverse(targetRotation) * tipRotation;
        }
    }
}
