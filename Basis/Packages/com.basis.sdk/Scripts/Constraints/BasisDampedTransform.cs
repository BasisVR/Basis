using UnityEngine;

namespace Basis.Scripts.BasisSdk.Constraints
{
    /// <summary>
    /// Lets a transform lag behind its source instead of following it rigidly — the floppy secondary
    /// motion on ears, tails, straps and the like. The Basis stand-in for Animation Rigging's
    /// <c>DampedTransform</c>.
    ///
    /// The damp values read as resistance, matching Unity: 0 snaps straight onto the source, 1 never
    /// moves at all. Motion is integrated in fixed 60Hz sub-steps, so the lag looks the same
    /// regardless of framerate.
    /// </summary>
    public class BasisDampedTransform : BasisConstraintBase
    {
        [Tooltip("How much the position resists following: 0 snaps to the source, 1 never moves.")]
        [Range(0f, 1f)]
        public float dampPosition = 0.5f;

        [Tooltip("How much the rotation resists following: 0 snaps to the source, 1 never moves.")]
        [Range(0f, 1f)]
        public float dampRotation = 0.5f;

        [Tooltip("Keep pointing at the source while lagging behind it.")]
        public bool maintainAim = true;

        [SerializeField, HideInInspector] private Vector3 bindPosition;
        [SerializeField, HideInInspector] private Quaternion bindRotation = Quaternion.identity;
        [SerializeField, HideInInspector] private Vector3 aimBindAxis;

        /// <summary>This transform's pose expressed in the source's frame at capture time.</summary>
        public Vector3 BindPosition => bindPosition;
        public Quaternion BindRotation => bindRotation;

        /// <summary>
        /// The local-space axis that pointed at the source at capture time, or zero when aim is not
        /// maintained or the two sat on top of each other and no direction could be derived.
        /// </summary>
        public Vector3 AimBindAxis => aimBindAxis;

        public override BasisConstraintType constraintType => BasisConstraintType.Damped;

        /// <summary>
        /// A damped transform has no rest pose — it always chases its source — so capturing rest is
        /// where the follow offset gets frozen instead. Call it with the rig in the pose the lag
        /// should treat as neutral.
        /// </summary>
        public override void CaptureRest()
        {
            Transform source = sourceCount > 0 ? GetSource(0).sourceTransform : null;
            if (source == null)
            {
                bindPosition = Vector3.zero;
                bindRotation = Quaternion.identity;
                aimBindAxis = Vector3.zero;
                return;
            }

            source.GetPositionAndRotation(out var sourcePosition, out var sourceRotation);
            transform.GetPositionAndRotation(out var ownPosition, out var ownRotation);

            Quaternion inverseSource = Quaternion.Inverse(sourceRotation);
            bindPosition = inverseSource * (ownPosition - sourcePosition);
            bindRotation = inverseSource * ownRotation;

            Vector3 toSource = sourcePosition - ownPosition;
            aimBindAxis = maintainAim && toSource.sqrMagnitude > 0f
                ? Quaternion.Inverse(ownRotation) * toSource.normalized
                : Vector3.zero;
        }
    }
}
