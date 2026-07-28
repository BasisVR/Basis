using UnityEngine;

namespace Basis.Scripts.BasisSdk.Constraints
{
    /// <summary>
    /// Spreads the twist between a chain's two ends across the bones in between, so a wrist or waist
    /// turn distributes along the chain instead of shearing at one joint. The Basis stand-in for
    /// Animation Rigging's <c>TwistChainConstraint</c>.
    ///
    /// Animation Rigging drives a whole chain from one component and samples an AnimationCurve at
    /// bind time to decide each bone's share. Basis drives one transform per constraint, so a
    /// converted chain becomes one of these per bone, each carrying the share the curve already gave
    /// it — which is also why no curve has to survive into the solve.
    ///
    /// Source 0 is the root end, source 1 is the tip end.
    /// </summary>
    public class BasisTwistChain : BasisConstraintBase
    {
        [Tooltip("Where between the root end (0) and the tip end (1) this bone sits. A converted " +
                 "chain gets this from the curve, sampled at the bone's distance along the chain.")]
        [Range(0f, 1f)]
        public float blend;

        [Tooltip("Local rotation in degrees used when Weight is 0.")]
        public Vector3 rotationAtRest;

        [SerializeField, HideInInspector] private Quaternion bindOffset = Quaternion.identity;

        /// <summary>
        /// What this bone was holding relative to the blended ends when it was captured. Without it
        /// an already-posed chain would collapse onto a straight interpolation of its two ends.
        /// </summary>
        public Quaternion BindOffset => bindOffset;

        public override BasisConstraintType constraintType => BasisConstraintType.TwistChain;

        /// <summary>
        /// Freezes this bone's pose relative to the blend of the two ends. Call it with the chain in
        /// the shape the twist should treat as neutral.
        /// </summary>
        public override void CaptureRest()
        {
            rotationAtRest = transform.localRotation.eulerAngles;

            Transform rootEnd = sourceCount > 0 ? GetSource(0).sourceTransform : null;
            Transform tipEnd = sourceCount > 1 ? GetSource(1).sourceTransform : null;
            if (rootEnd == null || tipEnd == null)
            {
                bindOffset = Quaternion.identity;
                return;
            }

            Quaternion blended = Quaternion.Slerp(rootEnd.rotation, tipEnd.rotation, Mathf.Clamp01(blend));
            bindOffset = Quaternion.Inverse(blended) * transform.rotation;
        }
    }
}
