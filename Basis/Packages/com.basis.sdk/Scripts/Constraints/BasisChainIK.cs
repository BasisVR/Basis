using UnityEngine;

namespace Basis.Scripts.BasisSdk.Constraints
{
    /// <summary>
    /// Reaches a chain of any length toward a target — tails, tentacles, spines, anything longer than
    /// the two bones <see cref="BasisTwoBoneIK"/> handles. The Basis stand-in for Animation Rigging's
    /// <c>ChainIKConstraint</c>.
    ///
    /// Sits on the tip bone; <see cref="root"/> names where the chain starts and must be an ancestor
    /// of it. The IK target is the first source, so a target that is itself constrained still solves
    /// first. Solved with FABRIK, which reaches forward from the tip and back from the root until the
    /// tip is within tolerance or the iteration budget runs out.
    /// </summary>
    public class BasisChainIK : BasisConstraintBase
    {
        [Tooltip("First bone of the chain. Must be an ancestor of this transform.")]
        public Transform root;

        [Tooltip("How strongly the chain bends toward the target.")]
        [Range(0f, 1f)]
        public float chainRotationWeight = 1f;

        [Tooltip("How strongly the tip takes the target's rotation.")]
        [Range(0f, 1f)]
        public float tipRotationWeight = 1f;

        [Tooltip("How close the tip must get before the solve stops early.")]
        [Min(0f)]
        public float tolerance = 0.0001f;

        [Tooltip("Iteration budget per frame. Longer chains need more to converge.")]
        [Range(1, 50)]
        public int maxIterations = 15;

        public override BasisConstraintType constraintType => BasisConstraintType.ChainIK;

        /// <summary>
        /// Chain IK reaches from wherever the chain currently is, so there is no rest pose to freeze.
        /// </summary>
        public override void CaptureRest()
        {
        }
    }
}
