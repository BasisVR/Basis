using System;
using Unity.Mathematics;

namespace Basis.Scripts.Constraints
{
    public enum BasisConstraintKind : byte
    {
        Position = 0,
        Rotation = 1,
        Scale = 2,
        Parent = 3,
        Aim = 4,
        LookAt = 5,
        Blend = 6,
        Override = 7,
        Damped = 8,
        TwistCorrection = 9,
        TwoBoneIK = 10,
        ChainIK = 11,
        TwistChain = 12,
        Referential = 13,
    }

    /// <summary>
    /// How an override constraint interprets its override pose. Mirrors Animation Rigging's
    /// <c>OverrideTransformJob.Space</c>.
    /// </summary>
    public enum BasisOverrideSpace : byte
    {
        /// <summary>The override is a world pose.</summary>
        World = 0,
        /// <summary>The override is a local pose, replacing the target's own.</summary>
        Local = 1,
        /// <summary>The override composes onto the target's current local pose.</summary>
        Pivot = 2,
    }

    public enum BasisWorldUpKind : byte
    {
        SceneUp = 0,
        ObjectUp = 1,
        ObjectRotationUp = 2,
        Vector = 3,
        None = 4,
    }

    [Flags]
    public enum BasisConstraintAxis : byte
    {
        None = 0,
        X = 1,
        Y = 2,
        Z = 4,
        All = X | Y | Z,
    }

    public struct BasisConstraintSource
    {
        public int TransformIndex;
        public float Weight;
        public float3 PositionOffset;
        public quaternion RotationOffset;
    }

    public struct BasisConstraintTransform
    {
        public float3 LocalPosition;
        public quaternion LocalRotation;
        public float3 LocalScale;
        public int ParentIndex;
    }

    public struct BasisConstraintWorld
    {
        public float3 Position;
        public quaternion Rotation;
        public float3 Scale;
    }

    /// <summary>
    /// The damped kind's memory of where it was last frame. It cannot be re-derived from the
    /// transform: whatever posed the bone this frame — an animator, another constraint — has already
    /// overwritten it, so lagging behind that pose needs somewhere of its own to lag from.
    /// Survives a rebuild by being carried on the managed registration.
    /// </summary>
    public struct BasisConstraintDampState
    {
        public float3 PreviousPosition;
        public quaternion PreviousRotation;
        /// <summary>0 until the first solve seeds it from the target's live pose.</summary>
        public byte Initialized;
    }

    public struct BasisConstraintSlot
    {
        public int TargetIndex;
        public int SourceStart;
        public int SourceCount;
        public int WorldUpIndex;
        public int AvatarId;
        public int Depth;

        /// <summary>
        /// Range into the shared chain buffer for the kinds that drive a whole bone chain rather
        /// than a single transform — two-bone IK and friends. Each entry pairs the bone's row in the
        /// sampled transform table with the results row it writes, so one slot can pose several
        /// bones without the write job ever seeing two slots fight over one transform.
        /// </summary>
        public int ChainStart;
        public int ChainCount;

        /// <summary>Referential kind only: which chain member leads this frame. Re-read every frame,
        /// so swapping the driver at runtime costs nothing and needs no rebuild.</summary>
        public int DriverIndex;

        /// <summary>
        /// The order this constraint was authored in, baked once when the content was converted and
        /// never recomputed. Animation Rigging evaluates a rig in a fixed authored sequence, so that
        /// sequence is the answer — recomputing an order every rebuild would be both slower and less
        /// faithful than carrying across the one the rig already had. Lower runs first; slots with no
        /// baked order fall back to hierarchy depth.
        /// </summary>
        public int AuthoredOrder;

        public float Weight;
        public float Roll;

        /// <summary>
        /// Per-channel amount, on top of <see cref="Weight"/>. The exact meaning is per kind:
        /// Blend reads it as where between the first source (0) and the second (1) the pose lands;
        /// Override reads it as how far toward the override pose each channel travels.
        /// </summary>
        public float PositionChannelWeight;
        public float RotationChannelWeight;

        /// <summary>IK kinds only: how strongly the hint (pole) steers the bend.</summary>
        public float HintWeight;

        /// <summary>Chain IK only: how close is close enough, and when to give up reaching.</summary>
        public float Tolerance;
        public int MaxIterations;

        /// <summary>Override kind only: the explicit pose used when the constraint has no source.</summary>
        public float3 OverridePosition;
        public quaternion OverrideRotation;

        /// <summary>
        /// A pose frozen at capture time, so what reaches the target afterwards is movement since
        /// then rather than an absolute pose. Meaning is per kind: Override stores the inverse of the
        /// source's local pose, and Damped stores the target's pose expressed in the source's frame.
        /// </summary>
        public float3 BindPosition;
        public quaternion BindRotation;

        /// <summary>Override kind only: carries the source's delta into the space being driven.</summary>
        public quaternion SourceToSpaceRotation;

        public float3 TranslationAtRest;
        public quaternion RotationAtRest;
        public float3 ScaleAtRest;

        public float3 TranslationOffset;
        public quaternion RotationOffset;
        public float3 ScaleOffset;

        public float3 AimVector;
        public float3 UpVector;
        public float3 WorldUpVector;

        public BasisConstraintKind Kind;
        public BasisWorldUpKind WorldUpKind;
        public BasisOverrideSpace OverrideSpace;
        /// <summary>Damped kind only: 1 when the target keeps aiming at its source while it lags.</summary>
        public byte MaintainAim;
        /// <summary>Override kind only: 1 when a source drives the override, 0 for explicit values.</summary>
        public byte UseOverrideSource;
        public byte TranslationMask;
        public byte RotationMask;
        public byte ScaleMask;
        public byte Active;
        public byte Locked;
        public byte UseUpObject;
    }

    public static class BasisConstraintDefaults
    {
        public const float WeightEpsilon = 1e-6f;

        public static BasisConstraintSlot Identity(BasisConstraintKind kind)
        {
            return new BasisConstraintSlot
            {
                TargetIndex = -1,
                SourceStart = 0,
                SourceCount = 0,
                WorldUpIndex = -1,
                AvatarId = -1,
                Depth = 0,
                ChainStart = 0,
                ChainCount = 0,
                DriverIndex = 0,
                AuthoredOrder = int.MaxValue,
                Weight = 1f,
                Roll = 0f,
                PositionChannelWeight = kind == BasisConstraintKind.Blend ? 0.5f : 1f,
                RotationChannelWeight = kind == BasisConstraintKind.Blend ? 0.5f : 1f,
                HintWeight = 0f,
                Tolerance = 0.0001f,
                MaxIterations = 15,
                OverridePosition = float3.zero,
                OverrideRotation = quaternion.identity,
                BindPosition = float3.zero,
                BindRotation = quaternion.identity,
                SourceToSpaceRotation = quaternion.identity,
                TranslationAtRest = float3.zero,
                RotationAtRest = quaternion.identity,
                ScaleAtRest = new float3(1f, 1f, 1f),
                TranslationOffset = float3.zero,
                RotationOffset = quaternion.identity,
                ScaleOffset = new float3(1f, 1f, 1f),
                AimVector = new float3(0f, 0f, 1f),
                UpVector = new float3(0f, 1f, 0f),
                WorldUpVector = new float3(0f, 1f, 0f),
                Kind = kind,
                WorldUpKind = BasisWorldUpKind.SceneUp,
                OverrideSpace = BasisOverrideSpace.World,
                MaintainAim = 0,
                UseOverrideSource = 0,
                TranslationMask = (byte)BasisConstraintAxis.All,
                RotationMask = (byte)BasisConstraintAxis.All,
                ScaleMask = (byte)BasisConstraintAxis.All,
                Active = 1,
                Locked = 1,
                UseUpObject = 0,
            };
        }
    }
}
