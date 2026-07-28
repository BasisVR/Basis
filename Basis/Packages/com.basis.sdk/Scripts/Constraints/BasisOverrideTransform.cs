using UnityEngine;

namespace Basis.Scripts.BasisSdk.Constraints
{
    /// <summary>
    /// Replaces a transform's pose with an override — either explicit values or the first source's
    /// movement since its bind was captured. The Basis stand-in for Animation Rigging's
    /// <c>OverrideTransform</c>.
    ///
    /// Unlike the other kinds this blends away from the transform's <em>current</em> pose rather than
    /// its rest pose, matching Animation Rigging: an override layers on top of whatever posed the
    /// transform this frame instead of fighting it. <see cref="BasisConstraintBase.weight"/> still
    /// scales the whole effect, and the per-channel weights scale position and rotation separately.
    /// </summary>
    public class BasisOverrideTransform : BasisConstraintBase
    {
        /// <summary>How the override pose is interpreted. Mirrors Animation Rigging's Space enum.</summary>
        public enum Space : byte
        {
            /// <summary>The override is a world pose.</summary>
            World = 0,
            /// <summary>The override is a local pose, replacing this transform's own.</summary>
            Local = 1,
            /// <summary>The override composes onto this transform's current local pose.</summary>
            Pivot = 2,
        }

        [Tooltip("Drive the override from the first source's movement since bind, instead of the " +
                 "explicit position and rotation below.")]
        public bool useSource;

        [Tooltip("How the override pose is interpreted.")]
        public Space space = Space.World;

        [Tooltip("Override position, used when Use Source is off.")]
        public Vector3 position;

        [Tooltip("Override rotation in degrees, used when Use Source is off.")]
        public Vector3 rotation;

        [Tooltip("How far the position travels toward the override.")]
        [Range(0f, 1f)]
        public float positionWeight = 1f;

        [Tooltip("How far the rotation travels toward the override.")]
        [Range(0f, 1f)]
        public float rotationWeight = 1f;

        [Tooltip("Position axes this constraint may drive. Excluded axes keep their current value.")]
        public BasisConstraintAxes translationAxis = BasisConstraintAxes.All;

        [Tooltip("Rotation axes this constraint may drive. Excluded axes keep their current value.")]
        public BasisConstraintAxes rotationAxis = BasisConstraintAxes.All;

        [SerializeField, HideInInspector] private Vector3 sourceInvBindPosition;
        [SerializeField, HideInInspector] private Quaternion sourceInvBindRotation = Quaternion.identity;
        [SerializeField, HideInInspector] private Quaternion sourceToSpaceRotation = Quaternion.identity;

        public Vector3 SourceInvBindPosition => sourceInvBindPosition;
        public Quaternion SourceInvBindRotation => sourceInvBindRotation;
        public Quaternion SourceToSpaceRotation => sourceToSpaceRotation;

        public override BasisConstraintType constraintType => BasisConstraintType.Override;

        /// <summary>
        /// An override has no rest pose of its own — it blends from wherever the transform already
        /// is — so capturing rest is where the source bind gets taken instead.
        /// </summary>
        public override void CaptureRest()
        {
            CaptureSourceBind();
        }

        /// <summary>
        /// Freezes the source's current local pose as the zero point, so what reaches this transform
        /// afterwards is only how far the source has moved since. Also caches the rotation that
        /// carries that delta from the source's frame into whichever space this constraint drives.
        /// Call it with the rig in the pose the override should treat as neutral.
        /// </summary>
        public void CaptureSourceBind()
        {
            Transform source = sourceCount > 0 ? GetSource(0).sourceTransform : null;
            if (source == null)
            {
                sourceInvBindPosition = Vector3.zero;
                sourceInvBindRotation = Quaternion.identity;
                sourceToSpaceRotation = Quaternion.identity;
                return;
            }

            source.GetLocalPositionAndRotation(out var sourceLocalPosition, out var sourceLocalRotation);
            Quaternion inverseLocal = Quaternion.Inverse(sourceLocalRotation);
            sourceInvBindRotation = inverseLocal;
            sourceInvBindPosition = inverseLocal * -sourceLocalPosition;

            Quaternion inverseSourceWorld = Quaternion.Inverse(source.rotation);
            switch (space)
            {
                case Space.World:
                    sourceToSpaceRotation = inverseSourceWorld;
                    break;
                case Space.Local:
                    // Relative to whatever this transform is parented under; with no parent there is
                    // no separate local frame, so it collapses onto the pivot case.
                    Transform parent = transform.parent;
                    sourceToSpaceRotation = parent != null
                        ? inverseSourceWorld * parent.rotation
                        : inverseSourceWorld * transform.rotation;
                    break;
                default:
                    sourceToSpaceRotation = inverseSourceWorld * transform.rotation;
                    break;
            }
        }
    }
}
