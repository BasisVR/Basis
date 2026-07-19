using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

namespace Basis.Scripts.Constraints
{
    /// <summary>
    /// Evaluates <see cref="BasisConstraintSlot"/>s against a flat array of world poses. This is the
    /// solver behind the Basis replacements for the six UnityEngine.Animations constraint components,
    /// expressed over plain data so it stays Burst friendly and testable without a scene.
    ///
    /// Every kind shares one blending contract:
    /// <list type="bullet">
    /// <item><c>Active == 0</c> — the target keeps its incoming pose, untouched.</item>
    /// <item>no source with weight above zero — the target keeps its incoming pose, untouched.</item>
    /// <item>axes excluded by a mask keep the incoming pose when <c>Locked != 0</c>, or fall back to
    /// the slot's <c>*AtRest</c> pose when <c>Locked == 0</c>.</item>
    /// <item>the masked result is then blended from the incoming pose by <c>Weight</c>, so
    /// <c>Weight == 0</c> is always a no-op and <c>Weight == 1</c> fully drives the enabled axes.</item>
    /// </list>
    /// </summary>
    [BurstCompile]
    public static class BasisConstraintSolver
    {
        /// <summary>
        /// Solves every slot in order, reading and writing <paramref name="world"/> in place. Slots must
        /// already be ordered shallowest-first (see <c>BasisConstraintBatch</c>) so a constraint that
        /// reads a parent as a source sees that parent's solved pose rather than its input pose.
        /// </summary>
        public static void SolveAll(
            ref NativeArray<BasisConstraintWorld> world,
            in NativeArray<BasisConstraintSlot> slots,
            in NativeArray<BasisConstraintSource> sources)
        {
            for (int i = 0; i < slots.Length; i++)
            {
                BasisConstraintSlot slot = slots[i];
                if (slot.TargetIndex < 0 || slot.TargetIndex >= world.Length)
                {
                    continue;
                }
                world[slot.TargetIndex] = SolveSlot(slot, world[slot.TargetIndex], sources, world);
            }
        }

        /// <summary>Solves one slot and returns the resulting pose, leaving <paramref name="current"/> untouched.</summary>
        public static BasisConstraintWorld SolveSlot(
            in BasisConstraintSlot slot,
            in BasisConstraintWorld current,
            in NativeArray<BasisConstraintSource> sources,
            in NativeArray<BasisConstraintWorld> world)
        {
            if (slot.Active == 0 || slot.SourceCount <= 0)
            {
                return current;
            }

            float weight = math.saturate(slot.Weight);
            if (weight <= BasisConstraintDefaults.WeightEpsilon)
            {
                return current;
            }

            switch (slot.Kind)
            {
                case BasisConstraintKind.Position:
                    return SolvePosition(slot, current, sources, world, weight);
                case BasisConstraintKind.Rotation:
                    return SolveRotation(slot, current, sources, world, weight);
                case BasisConstraintKind.Scale:
                    return SolveScale(slot, current, sources, world, weight);
                case BasisConstraintKind.Parent:
                    return SolveParent(slot, current, sources, world, weight);
                case BasisConstraintKind.Aim:
                    return SolveAim(slot, current, sources, world, weight, new float3(0f, 0f, 1f), new float3(0f, 1f, 0f), false);
                case BasisConstraintKind.LookAt:
                    return SolveAim(slot, current, sources, world, weight, new float3(0f, 0f, 1f), new float3(0f, 1f, 0f), true);
                default:
                    return current;
            }
        }

        private static BasisConstraintWorld SolvePosition(
            in BasisConstraintSlot slot,
            in BasisConstraintWorld current,
            in NativeArray<BasisConstraintSource> sources,
            in NativeArray<BasisConstraintWorld> world,
            float weight)
        {
            float3 blended = BasisConstraintMath.BlendPositions(sources, world, slot.SourceStart, slot.SourceCount, true, out float total);
            if (total <= BasisConstraintDefaults.WeightEpsilon)
            {
                return current;
            }

            float3 target = blended + slot.TranslationOffset;
            target = BasisConstraintMath.MaskAxis(MaskBasePosition(slot, current), target, slot.TranslationMask);

            BasisConstraintWorld result = current;
            result.Position = math.lerp(current.Position, target, weight);
            return result;
        }

        private static BasisConstraintWorld SolveRotation(
            in BasisConstraintSlot slot,
            in BasisConstraintWorld current,
            in NativeArray<BasisConstraintSource> sources,
            in NativeArray<BasisConstraintWorld> world,
            float weight)
        {
            quaternion blended = BasisConstraintMath.BlendRotations(sources, world, slot.SourceStart, slot.SourceCount, true, out float total);
            if (total <= BasisConstraintDefaults.WeightEpsilon)
            {
                return current;
            }

            quaternion target = math.mul(blended, slot.RotationOffset);
            target = BasisConstraintMath.MaskEuler(MaskBaseRotation(slot, current), target, slot.RotationMask);

            BasisConstraintWorld result = current;
            result.Rotation = math.nlerp(current.Rotation, target, weight);
            return result;
        }

        private static BasisConstraintWorld SolveScale(
            in BasisConstraintSlot slot,
            in BasisConstraintWorld current,
            in NativeArray<BasisConstraintSource> sources,
            in NativeArray<BasisConstraintWorld> world,
            float weight)
        {
            float3 blended = BasisConstraintMath.BlendScales(sources, world, slot.SourceStart, slot.SourceCount, out float total);
            if (total <= BasisConstraintDefaults.WeightEpsilon)
            {
                return current;
            }

            float3 target = blended * slot.ScaleOffset;
            target = BasisConstraintMath.MaskAxis(MaskBaseScale(slot, current), target, slot.ScaleMask);

            BasisConstraintWorld result = current;
            result.Scale = math.lerp(current.Scale, target, weight);
            return result;
        }

        private static BasisConstraintWorld SolveParent(
            in BasisConstraintSlot slot,
            in BasisConstraintWorld current,
            in NativeArray<BasisConstraintSource> sources,
            in NativeArray<BasisConstraintWorld> world,
            float weight)
        {
            float3 blendedPosition = BasisConstraintMath.BlendPositions(sources, world, slot.SourceStart, slot.SourceCount, true, out float positionTotal);
            quaternion blendedRotation = BasisConstraintMath.BlendRotations(sources, world, slot.SourceStart, slot.SourceCount, true, out float rotationTotal);
            if (positionTotal <= BasisConstraintDefaults.WeightEpsilon || rotationTotal <= BasisConstraintDefaults.WeightEpsilon)
            {
                return current;
            }

            float3 targetPosition = blendedPosition + slot.TranslationOffset;
            targetPosition = BasisConstraintMath.MaskAxis(MaskBasePosition(slot, current), targetPosition, slot.TranslationMask);

            quaternion targetRotation = math.mul(blendedRotation, slot.RotationOffset);
            targetRotation = BasisConstraintMath.MaskEuler(MaskBaseRotation(slot, current), targetRotation, slot.RotationMask);

            BasisConstraintWorld result = current;
            result.Position = math.lerp(current.Position, targetPosition, weight);
            result.Rotation = math.nlerp(current.Rotation, targetRotation, weight);
            return result;
        }

        /// <summary>
        /// Shared body for Aim and LookAt. Aim orients the slot's own <c>AimVector</c>/<c>UpVector</c> at the
        /// blended source point; LookAt pins those to forward/up and instead spins by <c>Roll</c> about the
        /// aim axis, matching how the two Unity components differ.
        /// </summary>
        private static BasisConstraintWorld SolveAim(
            in BasisConstraintSlot slot,
            in BasisConstraintWorld current,
            in NativeArray<BasisConstraintSource> sources,
            in NativeArray<BasisConstraintWorld> world,
            float weight,
            float3 lookAtAim,
            float3 lookAtUp,
            bool isLookAt)
        {
            float3 blended = BasisConstraintMath.BlendPositions(sources, world, slot.SourceStart, slot.SourceCount, true, out float total);
            if (total <= BasisConstraintDefaults.WeightEpsilon)
            {
                return current;
            }

            float3 localAim = isLookAt ? lookAtAim : slot.AimVector;
            float3 localUp = isLookAt ? lookAtUp : slot.UpVector;

            float3 direction = blended - current.Position;
            if (math.lengthsq(direction) <= BasisConstraintDefaults.WeightEpsilon)
            {
                return current;
            }

            float3 worldUp = BasisConstraintMath.ResolveWorldUp(slot, current, world);
            quaternion target = BasisConstraintMath.AimRotation(direction, worldUp, localAim, localUp);

            if (math.abs(slot.Roll) > 0f)
            {
                float3 rollAxis = math.normalizesafe(localAim, new float3(0f, 0f, 1f));
                target = math.mul(target, quaternion.AxisAngle(rollAxis, math.radians(slot.Roll)));
            }

            target = math.mul(target, slot.RotationOffset);
            target = BasisConstraintMath.MaskEuler(MaskBaseRotation(slot, current), target, slot.RotationMask);

            BasisConstraintWorld result = current;
            result.Rotation = math.nlerp(current.Rotation, target, weight);
            return result;
        }

        private static float3 MaskBasePosition(in BasisConstraintSlot slot, in BasisConstraintWorld current)
            => slot.Locked != 0 ? current.Position : slot.TranslationAtRest;

        private static quaternion MaskBaseRotation(in BasisConstraintSlot slot, in BasisConstraintWorld current)
            => slot.Locked != 0 ? current.Rotation : slot.RotationAtRest;

        private static float3 MaskBaseScale(in BasisConstraintSlot slot, in BasisConstraintWorld current)
            => slot.Locked != 0 ? current.Scale : slot.ScaleAtRest;
    }
}
