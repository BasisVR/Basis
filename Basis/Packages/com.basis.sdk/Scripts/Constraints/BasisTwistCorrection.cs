using UnityEngine;

namespace Basis.Scripts.BasisSdk.Constraints
{
    /// <summary>
    /// Passes a share of the source's twist about one axis onto this transform — the forearm and
    /// upper-arm rolls that stop a wrist from shearing the mesh when a hand turns over. The Basis
    /// stand-in for Animation Rigging's <c>TwistCorrection</c>.
    ///
    /// Animation Rigging drives a whole list of twist nodes from a single component. Basis drives one
    /// target per constraint, so a converted rig gets one of these per node, each on the node it
    /// drives and each carrying that node's own share.
    /// </summary>
    public class BasisTwistCorrection : BasisConstraintBase
    {
        /// <summary>Which local axis of the source the twist is measured about.</summary>
        public enum TwistAxis : byte
        {
            X = 0,
            Y = 1,
            Z = 2,
        }

        [Tooltip("The axis of the source the twist is read from.")]
        public TwistAxis twistAxis = TwistAxis.X;

        [Tooltip("How much of the source's twist this transform takes. Negative counters the twist " +
                 "instead of following it.")]
        [Range(-1f, 1f)]
        public float twistWeight = 1f;

        [SerializeField, HideInInspector] private Quaternion sourceInverseBindRotation = Quaternion.identity;
        [SerializeField, HideInInspector] private Quaternion twistBindRotation = Quaternion.identity;

        /// <summary>Inverse of the source's local rotation at capture, so only roll since then counts.</summary>
        public Quaternion SourceInverseBindRotation => sourceInverseBindRotation;

        /// <summary>This transform's own local rotation at capture, held when weight is 0.</summary>
        public Quaternion TwistBindRotation => twistBindRotation;

        /// <summary>The twist axis as a mask vector the solver can multiply through.</summary>
        public Vector3 TwistAxisVector => twistAxis switch
        {
            TwistAxis.Y => Vector3.up,
            TwistAxis.Z => Vector3.forward,
            _ => Vector3.right,
        };

        public override BasisConstraintType constraintType => BasisConstraintType.TwistCorrection;

        /// <summary>
        /// Freezes both the source's roll and this transform's own rotation as the neutral pose.
        /// Call it with the rig in its bind pose, or every frame afterwards reads as already twisted.
        /// </summary>
        public override void CaptureRest()
        {
            twistBindRotation = transform.localRotation;

            Transform source = sourceCount > 0 ? GetSource(0).sourceTransform : null;
            sourceInverseBindRotation = source != null
                ? Quaternion.Inverse(source.localRotation)
                : Quaternion.identity;
        }
    }
}
