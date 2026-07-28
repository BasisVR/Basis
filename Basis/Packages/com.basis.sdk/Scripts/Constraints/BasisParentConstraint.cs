using System;
using UnityEngine;

namespace Basis.Scripts.BasisSdk.Constraints
{
    /// <summary>
    /// Drives a transform's local position and rotation together, as if it were parented to the
    /// weighted average of its sources. The Basis stand-in for
    /// <c>UnityEngine.Animations.ParentConstraint</c>.
    ///
    /// Each source carries its own position/rotation offset, so a single constrained transform can
    /// sit at a different relative pose per source — that is what makes a two-source parent
    /// constraint blend smoothly between, say, a hip holster and a hand.
    ///
    /// Not to be confused with <c>Basis.Scripts.BasisSdk.Interactions.BasisParentConstraint</c>,
    /// the serialized helper class the pickup and held-camera interactables evaluate inline. This
    /// is the scene-authorable component; that one is an internal building block.
    /// </summary>
    public class BasisParentConstraint : BasisConstraintBase
    {
        [Tooltip("Local position used when Weight is 0.")]
        public Vector3 translationAtRest;

        [Tooltip("Local euler rotation used when Weight is 0.")]
        public Vector3 rotationAtRest;

        [Tooltip("Per-source position offset, in that source's space. One entry per source.")]
        public Vector3[] translationOffsets = Array.Empty<Vector3>();

        [Tooltip("Per-source euler rotation offset, applied after that source's rotation. One entry per source.")]
        public Vector3[] rotationOffsets = Array.Empty<Vector3>();

        [Tooltip("Position axes this constraint may drive. Excluded axes keep their current value.")]
        public BasisConstraintAxes translationAxis = BasisConstraintAxes.All;

        [Tooltip("Rotation axes this constraint may drive. Excluded axes keep their current value.")]
        public BasisConstraintAxes rotationAxis = BasisConstraintAxes.All;

        public override BasisConstraintType constraintType => BasisConstraintType.Parent;

        public override void CaptureRest()
        {
            transform.GetLocalPositionAndRotation(out var localPosition, out var localRotation);
            translationAtRest = localPosition;
            rotationAtRest = localRotation.eulerAngles;
        }

        /// <summary>
        /// Set every source's offset to the pose that holds the constrained transform exactly where
        /// it sits right now — the equivalent of Unity's "activate and preserve offset". Requires the
        /// sources to be at their current world poses, so call it while the rig is in its bind pose.
        /// </summary>
        [ContextMenu("Capture Offsets From Current Pose")]
        public void CaptureOffsets()
        {
            OnSourcesReshaped();
            // Our own world pose is invariant across the loop — the body only writes the offset arrays.
            transform.GetPositionAndRotation(out var worldPosition, out var worldRotation);
            for (int Index = 0; Index < sourceCount; Index++)
            {
                Transform source = GetSource(Index).sourceTransform;
                if (source == null)
                {
                    continue;
                }
                // The offset that reproduces our current world pose from this source's frame.
                // Rotation-only inverse, deliberately not InverseTransformPoint: the solver applies
                // the offset as position + rotation * offset, with no scale term, so dividing the
                // source's lossy scale out here would make a scaled source snap on activation.
                source.GetPositionAndRotation(out var sourcePosition, out var sourceRotation);
                Quaternion inverseSource = Quaternion.Inverse(sourceRotation);
                translationOffsets[Index] = inverseSource * (worldPosition - sourcePosition);
                rotationOffsets[Index] = (inverseSource * worldRotation).eulerAngles;
            }
        }

        /// <summary>
        /// Keeps the per-source offset arrays the same length as the source list. Growth preserves
        /// existing entries and leaves new ones at identity, so adding a source never disturbs the
        /// offsets already authored for the others.
        /// </summary>
        protected override void OnSourcesReshaped()
        {
            int count = sourceCount;
            if (translationOffsets == null || translationOffsets.Length != count)
            {
                Array.Resize(ref translationOffsets, count);
            }
            if (rotationOffsets == null || rotationOffsets.Length != count)
            {
                Array.Resize(ref rotationOffsets, count);
            }
        }
    }
}
