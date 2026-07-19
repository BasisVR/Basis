using System;
using UnityEngine;

namespace Basis.Scripts.BasisSdk.Constraints
{
    /// <summary>
    /// Which local axes a constraint is allowed to drive. Mirrors <c>UnityEngine.Animations.Axis</c>
    /// so a rig ported off the built-in constraints keeps identical masking behaviour.
    /// An axis left out of the mask keeps whatever value the transform already had this frame —
    /// the constraint's weight does not pull it toward the at-rest pose either.
    /// </summary>
    [Flags]
    public enum BasisConstraintAxes : byte
    {
        None = 0,
        X = 1,
        Y = 2,
        Z = 4,
        All = X | Y | Z,
    }

    /// <summary>
    /// How an aim / look-at constraint resolves the world up vector it rolls against.
    /// Mirrors <c>UnityEngine.Animations.WorldUpType</c>.
    /// </summary>
    public enum BasisConstraintWorldUp : byte
    {
        /// <summary>Global +Y.</summary>
        SceneUp = 0,
        /// <summary>Direction from the constrained transform toward <c>worldUpObject</c>.</summary>
        ObjectUp = 1,
        /// <summary><c>worldUpVector</c> rotated into <c>worldUpObject</c>'s space.</summary>
        ObjectRotationUp = 2,
        /// <summary><c>worldUpVector</c> taken as a world-space direction.</summary>
        Vector = 3,
        /// <summary>
        /// No up hint. Unlike Unity's <c>WorldUpType.None</c>, which leaves roll unconstrained, the
        /// batched solver still needs a basis and falls back to scene up — so today this behaves
        /// the same as <see cref="SceneUp"/>.
        /// </summary>
        None = 4,
    }

    /// <summary>
    /// Discriminates the concrete constraint components without a per-kind type test in the
    /// batched solver. Values are kept in step with the framework-side <c>BasisConstraintKind</c>.
    /// </summary>
    public enum BasisConstraintType : byte
    {
        Position = 0,
        Rotation = 1,
        Scale = 2,
        Parent = 3,
        Aim = 4,
        LookAt = 5,
    }

    /// <summary>
    /// One weighted driver of a constraint. Mirrors <c>UnityEngine.Animations.ConstraintSource</c>.
    /// Weights are relative, not normalized — two sources at 1 each blend 50/50, exactly as the
    /// built-in constraints do.
    /// </summary>
    [Serializable]
    public struct BasisConstraintSourceEntry
    {
        public Transform sourceTransform;
        [Min(0f)]
        public float weight;

        public BasisConstraintSourceEntry(Transform sourceTransform, float weight = 1f)
        {
            this.sourceTransform = sourceTransform;
            this.weight = weight;
        }
    }
}
