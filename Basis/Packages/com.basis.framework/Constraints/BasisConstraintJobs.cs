using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine.Jobs;

namespace Basis.Scripts.Constraints
{
    /// <summary>
    /// One constraint's solved local pose, plus which channels it actually drives. A constraint
    /// whose sources all carry zero weight writes nothing at all rather than snapping the transform
    /// to a rest pose nobody asked for, which is what the per-channel flags encode.
    /// </summary>
    public struct BasisConstraintResult
    {
        public float3 LocalPosition;
        public quaternion LocalRotation;
        public float3 LocalScale;

        public byte WritePosition;
        public byte WriteRotation;
        public byte WriteScale;
    }

    /// <summary>
    /// Samples every tracked transform once — targets, sources, world-up references and the
    /// targets' parents all live in the same array, so a source that is itself constrained is read
    /// exactly once no matter how many constraints reference it.
    /// </summary>
    [BurstCompile]
    public struct BasisConstraintReadJob : IJobParallelForTransform
    {
        public NativeArray<BasisConstraintWorld> World;
        public NativeArray<BasisConstraintTransform> Local;

        public void Execute(int index, TransformAccess transform)
        {
            if (!transform.isValid)
            {
                return;
            }

            float4x4 localToWorld = transform.localToWorldMatrix;
            World[index] = new BasisConstraintWorld
            {
                Position = transform.position,
                Rotation = transform.rotation,
                Scale = LossyScale(localToWorld),
            };

            // ParentIndex is authored at rebuild and must survive the per-frame sample.
            BasisConstraintTransform local = Local[index];
            local.LocalPosition = transform.localPosition;
            local.LocalRotation = transform.localRotation;
            local.LocalScale = transform.localScale;
            Local[index] = local;
        }

        /// <summary>
        /// Column magnitudes of the local-to-world matrix. <c>Transform.lossyScale</c> is not
        /// reachable through <see cref="TransformAccess"/>, and this is what it computes anyway
        /// (sign of a mirrored basis is not recovered, matching Unity).
        /// </summary>
        public static float3 LossyScale(in float4x4 localToWorld)
        {
            return new float3(
                math.length(localToWorld.c0.xyz),
                math.length(localToWorld.c1.xyz),
                math.length(localToWorld.c2.xyz));
        }
    }

    /// <summary>
    /// Solves every constraint in one pass, walking <see cref="Order"/> — a topological order over
    /// the source→target dependency graph, so a constraint driven by another constraint's target
    /// always sees the already-solved pose regardless of where either sits in the hierarchy. After
    /// each slot resolves, the target's entry in <see cref="World"/> is recomposed from its parent,
    /// which is what makes those chains work.
    ///
    /// Two caveats worth knowing. Only *constrained* transforms are recomposed: an unconstrained
    /// transform sitting between a constrained parent and a constrained child is read at its
    /// pre-solve world pose for the frame. And a dependency cycle has no valid order at all — it is
    /// broken at the shallowest member, which lags one frame.
    ///
    /// Single-threaded by design. The work per slot is a handful of quaternion blends, and a
    /// parallel job would have to give up the sequential dependency the depth ordering buys.
    /// </summary>
    [BurstCompile]
    public struct BasisConstraintSolveJob : IJob
    {
        [ReadOnly] public NativeArray<BasisConstraintSlot> Slots;
        [ReadOnly] public NativeArray<BasisConstraintSource> Sources;
        [ReadOnly] public NativeArray<BasisConstraintTransform> Local;
        [ReadOnly] public NativeArray<int> Order;

        /// <summary>
        /// Slot index → row in <see cref="Results"/>. Rows are per *target transform*, not per slot,
        /// so stacking a position and a rotation constraint on one object merges into a single write
        /// instead of two parallel writes racing over the same transform.
        /// </summary>
        [ReadOnly] public NativeArray<int> TargetRow;

        public NativeArray<BasisConstraintWorld> World;
        public NativeArray<BasisConstraintResult> Results;

        public void Execute()
        {
            // Results accumulate across slots sharing a target, so start from a clean slate.
            for (int Index = 0; Index < Results.Length; Index++)
            {
                Results[Index] = default;
            }

            for (int Index = 0; Index < Order.Length; Index++)
            {
                Solve(Order[Index]);
            }
        }

        private void Solve(int slotIndex)
        {
            BasisConstraintSlot slot = Slots[slotIndex];
            BasisConstraintResult result = default;
            result.LocalRotation = quaternion.identity;

            int target = slot.TargetIndex;
            int row = TargetRow[slotIndex];
            if (slot.Active == 0 || target < 0 || slot.SourceCount <= 0)
            {
                return;
            }

            BasisConstraintTransform local = Local[target];
            BasisConstraintWorld targetWorld = World[target];
            BasisConstraintWorld parent = local.ParentIndex >= 0 ? World[local.ParentIndex] : IdentityWorld();
            float weight = math.saturate(slot.Weight);

            switch (slot.Kind)
            {
                case BasisConstraintKind.Position:
                    SolvePosition(in slot, in local, in parent, weight, false, ref result);
                    break;
                case BasisConstraintKind.Rotation:
                    SolveRotation(in slot, in local, in parent, weight, false, ref result);
                    break;
                case BasisConstraintKind.Scale:
                    SolveScale(in slot, in local, in parent, weight, ref result);
                    break;
                case BasisConstraintKind.Parent:
                    SolvePosition(in slot, in local, in parent, weight, true, ref result);
                    SolveRotation(in slot, in local, in parent, weight, true, ref result);
                    break;
                case BasisConstraintKind.Aim:
                case BasisConstraintKind.LookAt:
                    SolveAim(in slot, in local, in parent, in targetWorld, weight, ref result);
                    break;
            }

            // Merge per channel: a slot only overwrites what it actually drives, so a position and a
            // rotation constraint on the same transform compose rather than cancel.
            BasisConstraintResult merged = Results[row];
            if (result.WritePosition != 0)
            {
                merged.LocalPosition = result.LocalPosition;
                merged.WritePosition = 1;
            }
            if (result.WriteRotation != 0)
            {
                merged.LocalRotation = result.LocalRotation;
                merged.WriteRotation = 1;
            }
            if (result.WriteScale != 0)
            {
                merged.LocalScale = result.LocalScale;
                merged.WriteScale = 1;
            }
            Results[row] = merged;

            // Only recompose when something was actually written. The sampled world row is exact;
            // the recomposition is a lossy-scale TRS reconstruction, so refreshing a row nothing
            // touched would inject decomposition error under a rotated, non-uniformly scaled
            // ancestor and hand it to every constraint sourcing this transform.
            if (merged.WritePosition != 0 || merged.WriteRotation != 0 || merged.WriteScale != 0)
            {
                RefreshWorld(target, in local, in parent, in merged);
            }
        }

        private void SolvePosition(
            in BasisConstraintSlot slot,
            in BasisConstraintTransform local,
            in BasisConstraintWorld parent,
            float weight,
            bool applySourceOffsets,
            ref BasisConstraintResult result)
        {
            float3 blended = BasisConstraintMath.BlendPositions(
                Sources, World, slot.SourceStart, slot.SourceCount, applySourceOffsets, out float totalWeight);
            if (totalWeight <= BasisConstraintDefaults.WeightEpsilon)
            {
                return;
            }

            float3 driven = BasisConstraintMath.WorldToParentPoint(parent, blended) + slot.TranslationOffset;
            float3 current = local.LocalPosition;
            // Masked-out axes fall through as "current" on both sides of the lerp, so the weight
            // blend cannot drag them toward the rest pose either.
            float3 masked = BasisConstraintMath.MaskAxis(current, driven, slot.TranslationMask);
            float3 rest = BasisConstraintMath.MaskAxis(current, slot.TranslationAtRest, slot.TranslationMask);

            result.LocalPosition = math.lerp(rest, masked, weight);
            result.WritePosition = 1;
        }

        private void SolveRotation(
            in BasisConstraintSlot slot,
            in BasisConstraintTransform local,
            in BasisConstraintWorld parent,
            float weight,
            bool applySourceOffsets,
            ref BasisConstraintResult result)
        {
            quaternion blended = BasisConstraintMath.BlendRotations(
                Sources, World, slot.SourceStart, slot.SourceCount, applySourceOffsets, out float totalWeight);
            if (totalWeight <= BasisConstraintDefaults.WeightEpsilon)
            {
                return;
            }

            quaternion driven = math.mul(
                BasisConstraintMath.WorldToParentRotation(parent, blended), slot.RotationOffset);
            quaternion current = local.LocalRotation;
            quaternion masked = BasisConstraintMath.MaskEuler(current, driven, slot.RotationMask);
            quaternion rest = BasisConstraintMath.MaskEuler(current, slot.RotationAtRest, slot.RotationMask);

            result.LocalRotation = math.slerp(rest, masked, weight);
            result.WriteRotation = 1;
        }

        private void SolveScale(
            in BasisConstraintSlot slot,
            in BasisConstraintTransform local,
            in BasisConstraintWorld parent,
            float weight,
            ref BasisConstraintResult result)
        {
            float3 blended = BasisConstraintMath.BlendScales(
                Sources, World, slot.SourceStart, slot.SourceCount, out float totalWeight);
            if (totalWeight <= BasisConstraintDefaults.WeightEpsilon)
            {
                return;
            }

            // Sources blend in world scale, so divide the parent back out to land in local space.
            float3 driven = blended / BasisConstraintMath.SafeScale(parent.Scale) * slot.ScaleOffset;
            float3 current = local.LocalScale;
            float3 masked = BasisConstraintMath.MaskAxis(current, driven, slot.ScaleMask);
            float3 rest = BasisConstraintMath.MaskAxis(current, slot.ScaleAtRest, slot.ScaleMask);

            result.LocalScale = math.lerp(rest, masked, weight);
            result.WriteScale = 1;
        }

        private void SolveAim(
            in BasisConstraintSlot slot,
            in BasisConstraintTransform local,
            in BasisConstraintWorld parent,
            in BasisConstraintWorld targetWorld,
            float weight,
            ref BasisConstraintResult result)
        {
            float3 aimPoint = BasisConstraintMath.BlendPositions(
                Sources, World, slot.SourceStart, slot.SourceCount, false, out float totalWeight);
            if (totalWeight <= BasisConstraintDefaults.WeightEpsilon)
            {
                return;
            }

            float3 aimDirection = aimPoint - targetWorld.Position;
            float3 worldUp = BasisConstraintMath.ResolveWorldUp(in slot, in targetWorld, World);
            quaternion driven = BasisConstraintMath.AimRotation(
                aimDirection, worldUp, slot.AimVector, slot.UpVector);

            if (math.abs(slot.Roll) > 0f)
            {
                float3 rollAxis = math.normalizesafe(slot.AimVector, new float3(0f, 0f, 1f));
                driven = math.mul(driven, quaternion.AxisAngle(rollAxis, math.radians(slot.Roll)));
            }

            quaternion localDriven = math.mul(
                BasisConstraintMath.WorldToParentRotation(parent, driven), slot.RotationOffset);
            quaternion current = local.LocalRotation;
            quaternion masked = BasisConstraintMath.MaskEuler(current, localDriven, slot.RotationMask);
            quaternion rest = BasisConstraintMath.MaskEuler(current, slot.RotationAtRest, slot.RotationMask);

            result.LocalRotation = math.slerp(rest, masked, weight);
            result.WriteRotation = 1;
        }

        /// <summary>
        /// Recompose the solved target's world pose so slots later in depth order read the
        /// constrained result rather than the stale sample.
        /// </summary>
        private void RefreshWorld(
            int target,
            in BasisConstraintTransform local,
            in BasisConstraintWorld parent,
            in BasisConstraintResult result)
        {
            float3 localPosition = result.WritePosition != 0 ? result.LocalPosition : local.LocalPosition;
            quaternion localRotation = result.WriteRotation != 0 ? result.LocalRotation : local.LocalRotation;
            float3 localScale = result.WriteScale != 0 ? result.LocalScale : local.LocalScale;

            World[target] = new BasisConstraintWorld
            {
                Position = parent.Position + math.mul(parent.Rotation, localPosition * parent.Scale),
                Rotation = math.mul(parent.Rotation, localRotation),
                Scale = parent.Scale * localScale,
            };
        }

        public static BasisConstraintWorld IdentityWorld()
        {
            return new BasisConstraintWorld
            {
                Position = float3.zero,
                Rotation = quaternion.identity,
                Scale = new float3(1f, 1f, 1f),
            };
        }
    }

    /// <summary>
    /// Writes the solved local poses back. Runs over the target-only transform array, so a
    /// transform driven by no constraint is never touched.
    /// </summary>
    [BurstCompile]
    public struct BasisConstraintWriteJob : IJobParallelForTransform
    {
        [ReadOnly] public NativeArray<BasisConstraintResult> Results;

        public void Execute(int index, TransformAccess transform)
        {
            if (!transform.isValid)
            {
                return;
            }

            BasisConstraintResult result = Results[index];

            if (result.WritePosition != 0 || result.WriteRotation != 0)
            {
                // Set both at once: two separate property writes dirty the hierarchy twice.
                float3 position = result.WritePosition != 0 ? result.LocalPosition : (float3)transform.localPosition;
                quaternion rotation = result.WriteRotation != 0 ? result.LocalRotation : (quaternion)transform.localRotation;
                transform.SetLocalPositionAndRotation(position, rotation);
            }

            if (result.WriteScale != 0)
            {
                transform.localScale = result.LocalScale;
            }
        }
    }
}
