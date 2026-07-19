using System;
using Unity.Mathematics;
using UnityEngine;

namespace Basis.Scripts.Constraints
{
    /// <summary>One weighted source driving a <see cref="BasisConstraint"/>.</summary>
    [Serializable]
    public class BasisConstraintSourceBinding
    {
        [Tooltip("Transform this constraint follows. Sources with no transform are skipped.")]
        public Transform SourceTransform;

        [Range(0f, 1f)]
        [Tooltip("How strongly this source pulls, relative to the other sources. Sources are normalised against each other, so two sources at 1 each blend 50/50.")]
        public float Weight = 1f;

        [Tooltip("Positional offset applied in the source's own space before blending.")]
        public Vector3 PositionOffset = Vector3.zero;

        [Tooltip("Rotational offset (euler degrees) applied on top of the source's rotation before blending.")]
        public Vector3 RotationOffset = Vector3.zero;
    }

    /// <summary>
    /// The Basis replacement for the six UnityEngine.Animations constraint components. One component
    /// covers all of them — pick a <see cref="BasisConstraintKind"/> and only the fields that kind
    /// actually uses stay live in the inspector.
    ///
    /// Unlike Unity's constraints this authors into <see cref="BasisConstraintSlot"/>, a flat Burst
    /// friendly record, so a whole avatar's constraints solve as one batch in
    /// <see cref="BasisConstraintSolver"/> instead of one <c>LateUpdate</c> callback per component.
    ///
    /// Add more than one to a GameObject to stack kinds (e.g. Position plus Aim).
    /// </summary>
    [AddComponentMenu("Basis/Constraints/Basis Constraint")]
    public class BasisConstraint : MonoBehaviour
    {
        [Tooltip("Which constraint this behaves as. Mirrors the six UnityEngine.Animations constraint components.")]
        public BasisConstraintKind Kind = BasisConstraintKind.Parent;

        [Tooltip("Off leaves the target's pose completely untouched, as if the component were not here.")]
        public bool ConstraintActive = true;

        [Tooltip("Locked keeps axes this constraint does not drive at their live pose. Unlocked returns those axes to the captured rest pose instead.")]
        public bool Locked = true;

        [Range(0f, 1f)]
        [Tooltip("Overall blend from the target's own pose toward the constrained pose. 0 is a no-op, 1 fully drives the enabled axes.")]
        public float Weight = 1f;

        [Tooltip("Weighted transforms this constraint follows.")]
        public BasisConstraintSourceBinding[] Sources = Array.Empty<BasisConstraintSourceBinding>();

        [Tooltip("World-space pose the unlocked axes fall back to. Captured from the transform when the component is added or via Capture Rest.")]
        public Vector3 TranslationAtRest = Vector3.zero;
        public Vector3 RotationAtRest = Vector3.zero;
        public Vector3 ScaleAtRest = Vector3.one;

        [Tooltip("Offset applied to the blended result, after the sources are combined.")]
        public Vector3 TranslationOffset = Vector3.zero;
        public Vector3 RotationOffset = Vector3.zero;
        public Vector3 ScaleOffset = Vector3.one;

        [Tooltip("Axes this constraint is allowed to drive. Excluded axes keep the live pose (or the rest pose when unlocked).")]
        public BasisConstraintAxis TranslationAxis = BasisConstraintAxis.All;
        public BasisConstraintAxis RotationAxis = BasisConstraintAxis.All;
        public BasisConstraintAxis ScaleAxis = BasisConstraintAxis.All;

        [Tooltip("Local axis pointed at the sources. Aim only — LookAt always aims down local forward.")]
        public Vector3 AimVector = Vector3.forward;

        [Tooltip("Local axis kept aligned with the resolved world up. Aim only.")]
        public Vector3 UpVector = Vector3.up;

        [Tooltip("How the world up used to resolve twist is chosen.")]
        public BasisWorldUpKind WorldUpKind = BasisWorldUpKind.SceneUp;

        [Tooltip("Explicit up direction, used by the Vector and Object Rotation Up modes.")]
        public Vector3 WorldUpVector = Vector3.up;

        [Tooltip("Transform supplying world up for the Object Up and Object Rotation Up modes.")]
        public Transform WorldUpObject;

        [Tooltip("Twist in degrees about the aim axis, applied after aiming. LookAt only.")]
        public float Roll = 0f;

        /// <summary>True when this kind reads <see cref="AimVector"/>, <see cref="UpVector"/> and the world up settings.</summary>
        public bool UsesAim => Kind == BasisConstraintKind.Aim || Kind == BasisConstraintKind.LookAt;

        /// <summary>True when this kind writes position.</summary>
        public bool DrivesPosition => Kind == BasisConstraintKind.Position || Kind == BasisConstraintKind.Parent;

        /// <summary>True when this kind writes rotation.</summary>
        public bool DrivesRotation => Kind == BasisConstraintKind.Rotation || Kind == BasisConstraintKind.Parent || UsesAim;

        /// <summary>True when this kind writes scale.</summary>
        public bool DrivesScale => Kind == BasisConstraintKind.Scale;

        private void Reset()
        {
            CaptureRest();
        }

        /// <summary>
        /// Stores the transform's current world pose as the rest pose the unlocked axes fall back to.
        /// World, not local, because <see cref="BasisConstraintSolver"/> resolves entirely in world space —
        /// a local rest pose would be silently wrong for any transform that is not at the scene root.
        /// </summary>
        public void CaptureRest()
        {
            TranslationAtRest = transform.position;
            RotationAtRest = transform.eulerAngles;
            ScaleAtRest = transform.lossyScale;
        }

        /// <summary>Number of sources that have a transform assigned and would contribute.</summary>
        public int CountValidSources()
        {
            if (Sources == null)
            {
                return 0;
            }
            int valid = 0;
            for (int i = 0; i < Sources.Length; i++)
            {
                BasisConstraintSourceBinding binding = Sources[i];
                if (binding != null && binding.SourceTransform != null && binding.Weight > 0f)
                {
                    valid++;
                }
            }
            return valid;
        }

        /// <summary>
        /// Projects the authored fields onto the flat solver record. Index arguments come from the batch
        /// builder, which owns the transform table; <paramref name="worldUpIndex"/> is -1 when this kind
        /// does not need an up object.
        /// </summary>
        public BasisConstraintSlot ToSlot(int targetIndex, int sourceStart, int sourceCount, int worldUpIndex, int depth, int avatarId)
        {
            BasisConstraintSlot slot = BasisConstraintDefaults.Identity(Kind);
            slot.TargetIndex = targetIndex;
            slot.SourceStart = sourceStart;
            slot.SourceCount = sourceCount;
            slot.WorldUpIndex = worldUpIndex;
            slot.AvatarId = avatarId;
            slot.Depth = depth;

            slot.Weight = Mathf.Clamp01(Weight);
            slot.Roll = Roll;

            slot.TranslationAtRest = TranslationAtRest;
            slot.RotationAtRest = quaternion.Euler(math.radians((float3)RotationAtRest));
            slot.ScaleAtRest = ScaleAtRest;

            slot.TranslationOffset = TranslationOffset;
            slot.RotationOffset = quaternion.Euler(math.radians((float3)RotationOffset));
            slot.ScaleOffset = ScaleOffset;

            slot.AimVector = AimVector;
            slot.UpVector = UpVector;
            slot.WorldUpVector = WorldUpVector;

            slot.WorldUpKind = WorldUpKind;
            slot.TranslationMask = (byte)TranslationAxis;
            slot.RotationMask = (byte)RotationAxis;
            slot.ScaleMask = (byte)ScaleAxis;
            slot.Active = (byte)(ConstraintActive && isActiveAndEnabled ? 1 : 0);
            slot.Locked = (byte)(Locked ? 1 : 0);
            slot.UseUpObject = (byte)(worldUpIndex >= 0 ? 1 : 0);
            return slot;
        }
    }
}
