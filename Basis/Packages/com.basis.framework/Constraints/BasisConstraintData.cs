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

    public struct BasisConstraintSlot
    {
        public int TargetIndex;
        public int SourceStart;
        public int SourceCount;
        public int WorldUpIndex;
        public int AvatarId;
        public int Depth;

        public float Weight;
        public float Roll;

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
                Weight = 1f,
                Roll = 0f,
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
