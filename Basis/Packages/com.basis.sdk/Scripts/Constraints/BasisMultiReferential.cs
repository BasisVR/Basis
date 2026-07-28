using System.Collections.Generic;
using UnityEngine;

namespace Basis.Scripts.BasisSdk.Constraints
{
    /// <summary>
    /// Keeps a set of transforms in the same arrangement relative to each other, with one of them
    /// leading. The Basis stand-in for Animation Rigging's <c>MultiReferentialConstraint</c>.
    ///
    /// Which member leads is <see cref="driver"/>, and it can change at runtime: the offsets are
    /// derived from the poses captured at bind rather than from wherever things happen to be, so
    /// handing leadership to a different member is just a different index — no rebuild, no drift, and
    /// the same arrangement either way.
    /// </summary>
    public class BasisMultiReferential : BasisConstraintBase
    {
        [Tooltip("The transforms held in a fixed arrangement. Order matters only in that Driver " +
                 "indexes into it.")]
        public List<Transform> members = new List<Transform>();

        [Tooltip("Which member leads. Everything else follows it, holding the arrangement captured " +
                 "at bind. Safe to change at runtime.")]
        [Min(0)]
        public int driver;

        [SerializeField, HideInInspector] private Vector3[] bindPositions = System.Array.Empty<Vector3>();
        [SerializeField, HideInInspector] private Quaternion[] bindRotations = System.Array.Empty<Quaternion>();

        /// <summary>World poses of each member at capture; the arrangement everything is held in.</summary>
        public Vector3[] BindPositions => bindPositions;
        public Quaternion[] BindRotations => bindRotations;

        /// <summary>The driver index clamped into the member list, so a bad value cannot escape.</summary>
        public int ResolvedDriver => members.Count == 0 ? 0 : Mathf.Clamp(driver, 0, members.Count - 1);

        public override BasisConstraintType constraintType => BasisConstraintType.Referential;

        /// <summary>
        /// Freezes where every member sits relative to the others. Call it with them arranged the way
        /// they should stay; whichever one leads afterwards, this is the shape they hold.
        /// </summary>
        public override void CaptureRest()
        {
            int count = members.Count;
            bindPositions = new Vector3[count];
            bindRotations = new Quaternion[count];
            for (int Index = 0; Index < count; Index++)
            {
                Transform member = members[Index];
                if (member == null)
                {
                    bindRotations[Index] = Quaternion.identity;
                    continue;
                }
                member.GetPositionAndRotation(out var position, out var rotation);
                bindPositions[Index] = position;
                bindRotations[Index] = rotation;
            }
        }
    }
}
