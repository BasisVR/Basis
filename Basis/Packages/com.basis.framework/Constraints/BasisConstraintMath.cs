using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

namespace Basis.Scripts.Constraints
{
    [BurstCompile]
    public static class BasisConstraintMath
    {
        public static float3 MaskAxis(float3 current, float3 value, byte mask)
        {
            return new float3(
                (mask & 1) != 0 ? value.x : current.x,
                (mask & 2) != 0 ? value.y : current.y,
                (mask & 4) != 0 ? value.z : current.z);
        }

        public static quaternion MaskEuler(quaternion current, quaternion value, byte mask)
        {
            if (mask == (byte)BasisConstraintAxis.All)
            {
                return value;
            }
            float3 currentEuler = ToEulerZXY(current);
            float3 valueEuler = ToEulerZXY(value);
            return FromEulerZXY(MaskAxis(currentEuler, valueEuler, mask));
        }

        public static float3 ToEulerZXY(quaternion q)
        {
            float4 v = q.value;
            float sinX = 2f * (v.w * v.x - v.y * v.z);
            sinX = math.clamp(sinX, -1f, 1f);
            float x = math.asin(sinX);
            float y = math.atan2(2f * (v.w * v.y + v.x * v.z), 1f - 2f * (v.x * v.x + v.y * v.y));
            float z = math.atan2(2f * (v.w * v.z + v.x * v.y), 1f - 2f * (v.x * v.x + v.z * v.z));
            return new float3(x, y, z);
        }

        public static quaternion FromEulerZXY(float3 euler)
        {
            return quaternion.EulerZXY(euler);
        }

        public static quaternion BlendRotations(
            in NativeArray<BasisConstraintSource> sources,
            in NativeArray<BasisConstraintWorld> world,
            int start,
            int count,
            bool applySourceOffsets,
            out float totalWeight)
        {
            totalWeight = 0f;
            float4 accumulated = float4.zero;
            for (int i = 0; i < count; i++)
            {
                BasisConstraintSource source = sources[start + i];
                if (source.Weight <= 0f || source.TransformIndex < 0)
                {
                    continue;
                }
                quaternion rotation = world[source.TransformIndex].Rotation;
                if (applySourceOffsets)
                {
                    rotation = math.mul(rotation, source.RotationOffset);
                }
                float4 candidate = rotation.value;
                if (totalWeight > 0f && math.dot(math.normalizesafe(accumulated), candidate) < 0f)
                {
                    candidate = -candidate;
                }
                accumulated += candidate * source.Weight;
                totalWeight += source.Weight;
            }
            if (totalWeight <= BasisConstraintDefaults.WeightEpsilon)
            {
                return quaternion.identity;
            }
            return math.normalize(new quaternion(accumulated / totalWeight));
        }

        public static float3 BlendPositions(
            in NativeArray<BasisConstraintSource> sources,
            in NativeArray<BasisConstraintWorld> world,
            int start,
            int count,
            bool applySourceOffsets,
            out float totalWeight)
        {
            totalWeight = 0f;
            float3 accumulated = float3.zero;
            for (int i = 0; i < count; i++)
            {
                BasisConstraintSource source = sources[start + i];
                if (source.Weight <= 0f || source.TransformIndex < 0)
                {
                    continue;
                }
                BasisConstraintWorld w = world[source.TransformIndex];
                float3 position = w.Position;
                if (applySourceOffsets)
                {
                    position += math.mul(w.Rotation, source.PositionOffset);
                }
                accumulated += position * source.Weight;
                totalWeight += source.Weight;
            }
            if (totalWeight <= BasisConstraintDefaults.WeightEpsilon)
            {
                return float3.zero;
            }
            return accumulated / totalWeight;
        }

        public static float3 BlendScales(
            in NativeArray<BasisConstraintSource> sources,
            in NativeArray<BasisConstraintWorld> world,
            int start,
            int count,
            out float totalWeight)
        {
            totalWeight = 0f;
            float3 accumulated = float3.zero;
            for (int i = 0; i < count; i++)
            {
                BasisConstraintSource source = sources[start + i];
                if (source.Weight <= 0f || source.TransformIndex < 0)
                {
                    continue;
                }
                accumulated += world[source.TransformIndex].Scale * source.Weight;
                totalWeight += source.Weight;
            }
            if (totalWeight <= BasisConstraintDefaults.WeightEpsilon)
            {
                return new float3(1f, 1f, 1f);
            }
            return accumulated / totalWeight;
        }

        public static float3 WorldToParentPoint(in BasisConstraintWorld parent, float3 worldPoint)
        {
            float3 delta = worldPoint - parent.Position;
            float3 rotated = math.mul(math.conjugate(parent.Rotation), delta);
            return rotated / SafeScale(parent.Scale);
        }

        public static quaternion WorldToParentRotation(in BasisConstraintWorld parent, quaternion worldRotation)
        {
            return math.mul(math.conjugate(parent.Rotation), worldRotation);
        }

        public static float3 SafeScale(float3 scale)
        {
            return new float3(
                math.abs(scale.x) < 1e-8f ? 1e-8f : scale.x,
                math.abs(scale.y) < 1e-8f ? 1e-8f : scale.y,
                math.abs(scale.z) < 1e-8f ? 1e-8f : scale.z);
        }

        public static quaternion AimRotation(float3 aimDirection, float3 worldUp, float3 localAim, float3 localUp)
        {
            float3 forward = math.normalizesafe(aimDirection, new float3(0f, 0f, 1f));
            float3 up = math.normalizesafe(worldUp, new float3(0f, 1f, 0f));
            if (math.abs(math.dot(forward, up)) > 0.9999f)
            {
                up = math.abs(forward.y) > 0.9999f ? new float3(0f, 0f, 1f) : new float3(0f, 1f, 0f);
            }
            float3 right = math.normalizesafe(math.cross(up, forward), new float3(1f, 0f, 0f));
            float3 orthoUp = math.cross(forward, right);
            float3x3 targetBasis = new float3x3(right, orthoUp, forward);

            float3 srcForward = math.normalizesafe(localAim, new float3(0f, 0f, 1f));
            float3 srcUp = math.normalizesafe(localUp, new float3(0f, 1f, 0f));
            if (math.abs(math.dot(srcForward, srcUp)) > 0.9999f)
            {
                srcUp = math.abs(srcForward.y) > 0.9999f ? new float3(0f, 0f, 1f) : new float3(0f, 1f, 0f);
            }
            float3 srcRight = math.normalizesafe(math.cross(srcUp, srcForward), new float3(1f, 0f, 0f));
            float3 srcOrthoUp = math.cross(srcForward, srcRight);
            float3x3 sourceBasis = new float3x3(srcRight, srcOrthoUp, srcForward);

            return math.mul(new quaternion(targetBasis), math.conjugate(new quaternion(sourceBasis)));
        }

        public static float3 ResolveWorldUp(
            in BasisConstraintSlot slot,
            in BasisConstraintWorld target,
            in NativeArray<BasisConstraintWorld> world)
        {
            switch (slot.WorldUpKind)
            {
                case BasisWorldUpKind.SceneUp:
                    return new float3(0f, 1f, 0f);
                case BasisWorldUpKind.ObjectUp:
                    if (slot.WorldUpIndex < 0)
                    {
                        return new float3(0f, 1f, 0f);
                    }
                    return math.normalizesafe(world[slot.WorldUpIndex].Position - target.Position, new float3(0f, 1f, 0f));
                case BasisWorldUpKind.ObjectRotationUp:
                    if (slot.WorldUpIndex < 0)
                    {
                        return new float3(0f, 1f, 0f);
                    }
                    return math.mul(world[slot.WorldUpIndex].Rotation, math.normalizesafe(slot.WorldUpVector, new float3(0f, 1f, 0f)));
                case BasisWorldUpKind.Vector:
                    return math.normalizesafe(slot.WorldUpVector, new float3(0f, 1f, 0f));
                default:
                    return float3.zero;
            }
        }
    }
}
