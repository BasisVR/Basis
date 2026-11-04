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
            transform.GetPositionAndRotation(out var position, out var rotation);
            World[index] = new BasisConstraintWorld
            {
                Position = position,
                Rotation = rotation,
                Scale = LossyScale(localToWorld),
            };

            // ParentIndex is authored at rebuild and must survive the per-frame sample.
            transform.GetLocalPositionAndRotation(out var localPosition, out var localRotation);
            BasisConstraintTransform local = Local[index];
            local.LocalPosition = localPosition;
            local.LocalRotation = localRotation;
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
    /// Blanks the results table ahead of a solve. Rows accumulate across every slot that shares a
    /// target, so they have to start from nothing.
    ///
    /// Its own job rather than a preamble inside the solve: it depends on nothing the sample
    /// produces, so it runs alongside that instead of behind it, and a solve split across groups
    /// could not do it anyway — a group blanking the whole table would wipe rows another group had
    /// already solved.
    /// </summary>
    [BurstCompile]
    public struct BasisConstraintClearJob : IJobParallelFor
    {
        [WriteOnly] public NativeArray<BasisConstraintResult> Results;

        public void Execute(int index)
        {
            Results[index] = default;
        }
    }

    /// <summary>
    /// Solves every constraint, walking <see cref="Order"/> — a topological order over
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
    /// One iteration per <see cref="Groups"/> entry, each a contiguous run of <see cref="Order"/>.
    /// A group is a connected component of that same dependency graph, so no two groups share a
    /// transform row either of them writes and neither can observe the other — which is what lets
    /// them run at once without giving up the ordering the solve depends on. The sequence inside a
    /// group is exactly what it was, so a room full of avatars simply stops being solved one avatar
    /// at a time. Every write lands through that ordering rather than through the iteration index,
    /// which is what the disabled range checks below are for.
    /// </summary>
    [BurstCompile]
    public struct BasisConstraintSolveJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<BasisConstraintSlot> Slots;
        [ReadOnly] public NativeArray<BasisConstraintSource> Sources;
        [ReadOnly] public NativeArray<BasisConstraintTransform> Local;
        [ReadOnly] public NativeArray<int> Order;

        /// <summary>
        /// (start, count) into <see cref="Order"/>, one entry per independently solvable group.
        /// Every slot appears in exactly one, so the groups together are the whole table.
        /// </summary>
        [ReadOnly] public NativeArray<int2> Groups;

        /// <summary>
        /// Slot index → row in <see cref="Results"/>. Rows are per *target transform*, not per slot,
        /// so stacking a position and a rotation constraint on one object merges into a single write
        /// instead of two parallel writes racing over the same transform.
        /// </summary>
        [ReadOnly] public NativeArray<int> TargetRow;

        [NativeDisableParallelForRestriction] public NativeArray<BasisConstraintWorld> World;
        [NativeDisableParallelForRestriction] public NativeArray<BasisConstraintResult> Results;

        /// <summary>Per-slot lag memory for the damped kind; untouched by every other kind.</summary>
        [NativeDisableParallelForRestriction] public NativeArray<BasisConstraintDampState> DampState;

        /// <summary>(sampled row, results row) per bone, for the kinds that pose a whole chain.</summary>
        [ReadOnly] public NativeArray<int2> Chain;

        /// <summary>Each chain member's pose at capture, index-parallel to <see cref="Chain"/>.</summary>
        [ReadOnly] public NativeArray<BasisConstraintWorld> ChainBind;

        /// <summary>
        /// Scratch for the chain IK reach, <see cref="ChainStride"/> entries per group so two groups
        /// reaching at the same time cannot land in one buffer. Held on the job rather than allocated
        /// per solve: this runs every frame.
        /// </summary>
        [NativeDisableParallelForRestriction] public NativeArray<float3> ChainPositions;
        [NativeDisableParallelForRestriction] public NativeArray<float> ChainLengths;

        /// <summary>Chain scratch entries reserved per group — the longest chain in the table.</summary>
        public int ChainStride;

        /// <summary>Unscaled frame delta, for the damped kind's fixed-step integration.</summary>
        public float DeltaTime;

        public void Execute(int groupIndex)
        {
            int2 group = Groups[groupIndex];
            int chainBase = groupIndex * ChainStride;
            for (int Index = 0; Index < group.y; Index++)
            {
                Solve(Order[group.x + Index], chainBase);
            }
        }

        private void Solve(int slotIndex, int chainBase)
        {
            BasisConstraintSlot slot = Slots[slotIndex];
            BasisConstraintResult result = default;
            result.LocalRotation = quaternion.identity;

            int target = slot.TargetIndex;
            int row = TargetRow[slotIndex];
            // Two kinds drive from something other than a source list: an override on explicit
            // values, and a referential holding its members in the chain. Every other kind is a
            // no-op without at least one source.
            bool sourceless = slot.Kind == BasisConstraintKind.Referential
                || (slot.Kind == BasisConstraintKind.Override && slot.UseOverrideSource == 0);
            if (slot.Active == 0 || target < 0 || (slot.SourceCount <= 0 && !sourceless))
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
                case BasisConstraintKind.Blend:
                    SolveBlend(in slot, in local, in parent, weight, ref result);
                    break;
                case BasisConstraintKind.Override:
                    SolveOverride(in slot, in local, in parent, weight, ref result);
                    break;
                case BasisConstraintKind.Damped:
                    SolveDamped(slotIndex, in slot, in parent, in targetWorld, weight, ref result);
                    break;
                case BasisConstraintKind.TwistCorrection:
                    SolveTwistCorrection(in slot, weight, ref result);
                    break;
                case BasisConstraintKind.TwoBoneIK:
                    SolveTwoBoneIK(in slot, weight, ref result);
                    break;
                case BasisConstraintKind.ChainIK:
                    SolveChainIK(in slot, weight, chainBase, ref result);
                    break;
                case BasisConstraintKind.TwistChain:
                    SolveTwistChain(in slot, in local, in parent, weight, ref result);
                    break;
                case BasisConstraintKind.Referential:
                    SolveReferential(in slot, weight, ref result);
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
            // Locked keeps the axes this constraint does not drive at their live pose; unlocked
            // returns them to the captured rest pose instead. Either way the undriven fill lands on
            // both sides of the lerp, so the weight blend cannot drag those axes anywhere else.
            float3 undriven = slot.Locked != 0 ? current : slot.TranslationAtRest;
            float3 masked = BasisConstraintMath.MaskAxis(undriven, driven, slot.TranslationMask);
            float3 rest = BasisConstraintMath.MaskAxis(undriven, slot.TranslationAtRest, slot.TranslationMask);

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
            quaternion undriven = slot.Locked != 0 ? current : slot.RotationAtRest;
            quaternion masked = BasisConstraintMath.MaskEuler(undriven, driven, slot.RotationMask);
            quaternion rest = BasisConstraintMath.MaskEuler(undriven, slot.RotationAtRest, slot.RotationMask);

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
            float3 undriven = slot.Locked != 0 ? current : slot.ScaleAtRest;
            float3 masked = BasisConstraintMath.MaskAxis(undriven, driven, slot.ScaleMask);
            float3 rest = BasisConstraintMath.MaskAxis(undriven, slot.ScaleAtRest, slot.ScaleMask);

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
            quaternion undriven = slot.Locked != 0 ? current : slot.RotationAtRest;
            quaternion masked = BasisConstraintMath.MaskEuler(undriven, localDriven, slot.RotationMask);
            quaternion rest = BasisConstraintMath.MaskEuler(undriven, slot.RotationAtRest, slot.RotationMask);

            result.LocalRotation = math.slerp(rest, masked, weight);
            result.WriteRotation = 1;
        }

        /// <summary>
        /// Lerps between the first two sources rather than averaging every source, then lands the
        /// result through the same mask / rest / weight path the other kinds use.
        /// A source list shorter than two leaves the transform alone — there is nothing to blend.
        /// </summary>
        private void SolveBlend(
            in BasisConstraintSlot slot,
            in BasisConstraintTransform local,
            in BasisConstraintWorld parent,
            float weight,
            ref BasisConstraintResult result)
        {
            if (slot.SourceCount < 2)
            {
                return;
            }

            BasisConstraintSource a = Sources[slot.SourceStart];
            BasisConstraintSource b = Sources[slot.SourceStart + 1];
            if (a.TransformIndex < 0 || b.TransformIndex < 0)
            {
                return;
            }

            BasisConstraintWorld worldA = World[a.TransformIndex];
            BasisConstraintWorld worldB = World[b.TransformIndex];

            // The mask doubles as Unity's blendPosition / blendRotation toggles: a kind that should
            // not touch a channel arrives here with that channel's mask cleared, and the apply below
            // then resolves every axis to the undriven fill.
            if (slot.TranslationMask != 0)
            {
                float3 blended = math.lerp(
                    worldA.Position, worldB.Position, math.saturate(slot.PositionChannelWeight));
                ApplyPosition(in slot, in local, in parent, blended, weight, ref result);
            }

            if (slot.RotationMask != 0)
            {
                quaternion blended = math.slerp(
                    worldA.Rotation, worldB.Rotation, math.saturate(slot.RotationChannelWeight));
                ApplyRotation(in slot, in local, in parent, blended, weight, ref result);
            }
        }

        /// <summary>
        /// Analytic two-bone IK: the law of cosines gives the elbow angle that puts the tip on its
        /// target, then the root swings the whole limb to aim at it, then the hint rolls the limb
        /// about its own axis to decide which way the joint breaks.
        ///
        /// Animation Rigging leans on the animation stream to push each rotation down to the child
        /// bones between steps. Nothing propagates here — the solve reads a snapshot — so the chain's
        /// world poses are re-derived by hand after each rotation, which is what
        /// <see cref="RotateChainFrom"/> does.
        /// </summary>
        private void SolveTwoBoneIK(
            in BasisConstraintSlot slot,
            float weight,
            ref BasisConstraintResult result)
        {
            // root, mid, tip — anything else means the hierarchy was too shallow to be a limb.
            if (slot.ChainCount != 3 || slot.SourceCount < 1)
            {
                return;
            }

            int2 rootEntry = Chain[slot.ChainStart];
            int2 midEntry = Chain[slot.ChainStart + 1];
            int2 tipEntry = Chain[slot.ChainStart + 2];

            BasisConstraintSource targetSource = Sources[slot.SourceStart];
            if (targetSource.TransformIndex < 0)
            {
                return;
            }
            BasisConstraintWorld targetWorld = World[targetSource.TransformIndex];

            float3 rootPosition = World[rootEntry.x].Position;
            float3 midPosition = World[midEntry.x].Position;
            float3 tipPosition = World[tipEntry.x].Position;

            float positionWeight = math.saturate(slot.PositionChannelWeight) * weight;
            float rotationWeight = math.saturate(slot.RotationChannelWeight) * weight;

            float3 goalPosition = math.lerp(
                tipPosition, targetWorld.Position + slot.BindPosition, positionWeight);
            quaternion goalRotation = math.slerp(
                World[tipEntry.x].Rotation,
                math.mul(targetWorld.Rotation, slot.BindRotation),
                rotationWeight);

            // Hint is optional and rides in the second source slot.
            bool hasHint = slot.SourceCount > 1
                && Sources[slot.SourceStart + 1].TransformIndex >= 0
                && slot.HintWeight > 0f;
            float3 hintPosition = hasHint
                ? World[Sources[slot.SourceStart + 1].TransformIndex].Position
                : float3.zero;

            float3 upperArm = midPosition - rootPosition;
            float3 forearm = tipPosition - midPosition;
            float3 reach = tipPosition - rootPosition;
            float3 toGoal = goalPosition - rootPosition;

            float upperLength = math.length(upperArm);
            float forearmLength = math.length(forearm);
            float reachLength = math.length(reach);
            float goalLength = math.length(toGoal);

            float currentAngle = TriangleAngle(reachLength, upperLength, forearmLength);
            float wantedAngle = TriangleAngle(goalLength, upperLength, forearmLength);

            // Prefer the bend plane the pose already has; fall back to the hint, then to the goal
            // direction, then to world up, so a perfectly straight limb still picks a sane plane.
            float3 bendAxis = math.cross(upperArm, forearm);
            if (math.lengthsq(bendAxis) < BasisConstraintDefaults.WeightEpsilon)
            {
                bendAxis = hasHint
                    ? math.cross(hintPosition - rootPosition, forearm)
                    : float3.zero;
                if (math.lengthsq(bendAxis) < BasisConstraintDefaults.WeightEpsilon)
                {
                    bendAxis = math.cross(toGoal, forearm);
                }
                if (math.lengthsq(bendAxis) < BasisConstraintDefaults.WeightEpsilon)
                {
                    bendAxis = new float3(0f, 1f, 0f);
                }
            }
            bendAxis = math.normalizesafe(bendAxis, new float3(0f, 1f, 0f));

            // Bend the elbow, then re-derive the chain so the tip reflects it.
            // Full angle, not half: Animation Rigging composes its delta as (axis*sin(a), cos(a))
            // with a already halved, whereas AxisAngle halves the angle it is handed.
            quaternion bend = quaternion.AxisAngle(bendAxis, currentAngle - wantedAngle);
            RotateChainFrom(slot, 1, bend);

            // Swing the whole limb so the (now correctly bent) tip points at the goal.
            tipPosition = World[tipEntry.x].Position;
            quaternion swing = FromToRotation(tipPosition - rootPosition, toGoal);
            RotateChainFrom(slot, 0, swing);

            if (hasHint)
            {
                midPosition = World[midEntry.x].Position;
                tipPosition = World[tipEntry.x].Position;
                float3 limbAxis = tipPosition - rootPosition;
                float limbLengthSq = math.lengthsq(limbAxis);
                if (limbLengthSq > 0f)
                {
                    float3 limbDirection = limbAxis * math.rsqrt(limbLengthSq);
                    // Only the components across the limb axis matter — rolling about it is exactly
                    // the freedom the hint is there to pin down.
                    float3 toMid = midPosition - rootPosition;
                    float3 toHint = hintPosition - rootPosition;
                    float3 midAcross = toMid - limbDirection * math.dot(toMid, limbDirection);
                    float3 hintAcross = toHint - limbDirection * math.dot(toHint, limbDirection);

                    float maxReach = upperLength + forearmLength;
                    if (math.lengthsq(midAcross) > maxReach * maxReach * 0.001f
                        && math.lengthsq(hintAcross) > 0f)
                    {
                        quaternion roll = FromToRotation(midAcross, hintAcross);
                        roll = math.nlerp(quaternion.identity, roll, math.saturate(slot.HintWeight));
                        RotateChainFrom(slot, 0, roll);
                    }
                }
            }

            // The tip takes the target's rotation outright rather than inheriting the swing.
            WriteChainRotation(tipEntry, goalRotation);

            // The tip is this slot's own target row, already written above.
            result.WriteRotation = 0;
            result.WritePosition = 0;
        }

        /// <summary>
        /// Reaches a chain of any length at a target using FABRIK: pull the tip onto the goal and let
        /// the chain trail behind it, then pin the root back where it belongs and let the correction
        /// travel outward. Repeat until the tip is close enough or the iteration budget runs out.
        ///
        /// FABRIK works in positions; bones want rotations. The positions are solved first, then each
        /// bone is turned by whatever carries its old direction onto its new one.
        /// </summary>
        private void SolveChainIK(
            in BasisConstraintSlot slot,
            float weight,
            int chainBase,
            ref BasisConstraintResult result)
        {
            int count = slot.ChainCount;
            if (count < 2 || slot.SourceCount < 1)
            {
                return;
            }
            BasisConstraintSource targetSource = Sources[slot.SourceStart];
            if (targetSource.TransformIndex < 0)
            {
                return;
            }

            float reachWeight = math.saturate(slot.PositionChannelWeight) * weight;
            if (reachWeight <= BasisConstraintDefaults.WeightEpsilon)
            {
                return;
            }

            BasisConstraintWorld targetWorld = World[targetSource.TransformIndex];

            // This group's own window onto the shared scratch, so the reach below can index from zero
            // the way it reads regardless of which group is running it.
            NativeArray<float3> chainPositions = ChainPositions.GetSubArray(chainBase, ChainStride);
            NativeArray<float> chainLengths = ChainLengths.GetSubArray(chainBase, ChainStride);

            float maxReach = 0f;
            for (int Index = 0; Index < count; Index++)
            {
                float3 position = World[Chain[slot.ChainStart + Index].x].Position;
                chainPositions[Index] = position;
                if (Index > 0)
                {
                    float length = math.distance(position, chainPositions[Index - 1]);
                    chainLengths[Index - 1] = length;
                    maxReach += length;
                }
            }

            float3 rootPosition = chainPositions[0];
            int tip = count - 1;
            float3 goal = math.lerp(chainPositions[tip], targetWorld.Position, reachWeight);

            if (math.distancesq(goal, rootPosition) > maxReach * maxReach)
            {
                // Out of range: there is nothing to iterate toward, so lay the chain straight at it.
                float3 direction = math.normalizesafe(goal - rootPosition, new float3(0f, 0f, 1f));
                for (int Index = 1; Index < count; Index++)
                {
                    chainPositions[Index] = chainPositions[Index - 1] + direction * chainLengths[Index - 1];
                }
            }
            else
            {
                float toleranceSq = slot.Tolerance * slot.Tolerance;
                for (int Iteration = 0; Iteration < slot.MaxIterations; Iteration++)
                {
                    if (math.distancesq(chainPositions[tip], goal) <= toleranceSq)
                    {
                        break;
                    }

                    // Forward: tip onto the goal, everything else trailing behind it.
                    chainPositions[tip] = goal;
                    for (int Index = tip - 1; Index >= 0; Index--)
                    {
                        float3 toward = math.normalizesafe(
                            chainPositions[Index] - chainPositions[Index + 1], new float3(0f, 0f, 1f));
                        chainPositions[Index] = chainPositions[Index + 1] + toward * chainLengths[Index];
                    }

                    // Backward: root pinned, correction travelling back out to the tip.
                    chainPositions[0] = rootPosition;
                    for (int Index = 1; Index < count; Index++)
                    {
                        float3 toward = math.normalizesafe(
                            chainPositions[Index] - chainPositions[Index - 1], new float3(0f, 0f, 1f));
                        chainPositions[Index] = chainPositions[Index - 1] + toward * chainLengths[Index - 1];
                    }
                }
            }

            // Positions to rotations, root outward, so each bone turns in the frame its parent left.
            for (int Index = 0; Index < count - 1; Index++)
            {
                int2 entry = Chain[slot.ChainStart + Index];
                float3 was = World[Chain[slot.ChainStart + Index + 1].x].Position - World[entry.x].Position;
                float3 wants = chainPositions[Index + 1] - chainPositions[Index];
                quaternion turn = FromToRotation(was, wants);
                if (!turn.Equals(quaternion.identity))
                {
                    RotateChainFrom(slot, Index, turn);
                }
            }

            // The tip takes the target's rotation on its own weight, as in two-bone IK.
            float tipWeight = math.saturate(slot.RotationChannelWeight) * weight;
            if (tipWeight > BasisConstraintDefaults.WeightEpsilon)
            {
                int2 tipEntry = Chain[slot.ChainStart + tip];
                WriteChainRotation(tipEntry, math.slerp(
                    World[tipEntry.x].Rotation, targetWorld.Rotation, tipWeight));
            }

            result.WritePosition = 0;
            result.WriteRotation = 0;
        }

        /// <summary>
        /// Holds a set of transforms in the arrangement they were captured in, with one of them
        /// leading. Everything else is placed by carrying its captured offset from the leader onto
        /// wherever the leader is now.
        ///
        /// Which one leads is re-read every frame, so it can change at runtime. The offsets come from
        /// the captured poses rather than from live ones, so handing leadership over does not drift
        /// the arrangement — the same relationship is re-derived from the same source either way.
        /// </summary>
        private void SolveReferential(
            in BasisConstraintSlot slot,
            float weight,
            ref BasisConstraintResult result)
        {
            int count = slot.ChainCount;
            if (count < 2 || weight <= BasisConstraintDefaults.WeightEpsilon)
            {
                return;
            }

            int driver = math.clamp(slot.DriverIndex, 0, count - 1);
            int2 driverEntry = Chain[slot.ChainStart + driver];
            BasisConstraintWorld driverNow = World[driverEntry.x];
            BasisConstraintWorld driverBind = ChainBind[slot.ChainStart + driver];

            quaternion driverBindInverse = math.conjugate(driverBind.Rotation);

            for (int Index = 0; Index < count; Index++)
            {
                if (Index == driver)
                {
                    // The leader is what everything else is measured against; it stays put.
                    continue;
                }

                int2 entry = Chain[slot.ChainStart + Index];
                BasisConstraintWorld bind = ChainBind[slot.ChainStart + Index];

                // Where this member sat relative to the leader when the arrangement was captured.
                float3 offsetPosition = math.mul(driverBindInverse, bind.Position - driverBind.Position);
                quaternion offsetRotation = math.mul(driverBindInverse, bind.Rotation);

                float3 wantedPosition = driverNow.Position + math.mul(driverNow.Rotation, offsetPosition);
                quaternion wantedRotation = math.mul(driverNow.Rotation, offsetRotation);

                BasisConstraintWorld current = World[entry.x];
                WriteChainPose(entry,
                    math.lerp(current.Position, wantedPosition, weight),
                    math.slerp(current.Rotation, wantedRotation, weight));
            }

            result.WritePosition = 0;
            result.WriteRotation = 0;
        }

        /// <summary>
        /// Records a solved world pose as a local one on a chain member's own results row, and keeps
        /// the sampled world entry in step so anything solved later reads the placed pose.
        /// </summary>
        private void WriteChainPose(int2 entry, float3 worldPosition, quaternion worldRotation)
        {
            BasisConstraintTransform local = Local[entry.x];
            BasisConstraintWorld parent = local.ParentIndex >= 0
                ? World[local.ParentIndex]
                : IdentityWorld();

            BasisConstraintResult row = Results[entry.y];
            row.LocalPosition = BasisConstraintMath.WorldToParentPoint(parent, worldPosition);
            row.LocalRotation = BasisConstraintMath.WorldToParentRotation(parent, worldRotation);
            row.WritePosition = 1;
            row.WriteRotation = 1;
            Results[entry.y] = row;

            BasisConstraintWorld world = World[entry.x];
            world.Position = worldPosition;
            world.Rotation = worldRotation;
            World[entry.x] = world;
        }

        /// <summary>
        /// Turns one bone in the chain and carries the result down to everything below it, standing
        /// in for the propagation an animation stream would do for free.
        /// </summary>
        private void RotateChainFrom(in BasisConstraintSlot slot, int boneOffset, quaternion delta)
        {
            int2 entry = Chain[slot.ChainStart + boneOffset];
            BasisConstraintWorld bone = World[entry.x];
            float3 pivot = bone.Position;

            quaternion rotated = math.mul(delta, bone.Rotation);
            bone.Rotation = rotated;
            World[entry.x] = bone;
            WriteChainRotation(entry, rotated);

            for (int Index = boneOffset + 1; Index < slot.ChainCount; Index++)
            {
                int2 childEntry = Chain[slot.ChainStart + Index];
                BasisConstraintWorld child = World[childEntry.x];
                child.Position = pivot + math.mul(delta, child.Position - pivot);
                child.Rotation = math.mul(delta, child.Rotation);
                World[childEntry.x] = child;
                WriteChainRotation(childEntry, child.Rotation);
            }
        }

        /// <summary>
        /// Records a bone's solved world rotation as a local one on its own results row. Written
        /// straight through rather than merged, because within a chain this slot owns every bone.
        /// </summary>
        private void WriteChainRotation(int2 entry, quaternion worldRotation)
        {
            BasisConstraintTransform local = Local[entry.x];
            BasisConstraintWorld parent = local.ParentIndex >= 0
                ? World[local.ParentIndex]
                : IdentityWorld();

            BasisConstraintResult row = Results[entry.y];
            row.LocalRotation = BasisConstraintMath.WorldToParentRotation(parent, worldRotation);
            row.WriteRotation = 1;
            Results[entry.y] = row;
        }

        /// <summary>Interior angle opposite <paramref name="opposite"/>, clamped to a real triangle.</summary>
        private static float TriangleAngle(float opposite, float sideA, float sideB)
        {
            float divisor = 2f * sideA * sideB;
            if (divisor < BasisConstraintDefaults.WeightEpsilon)
            {
                return 0f;
            }
            float cosine = math.clamp((sideA * sideA + sideB * sideB - opposite * opposite) / divisor, -1f, 1f);
            return math.acos(cosine);
        }

        /// <summary>Shortest rotation carrying one direction onto another.</summary>
        private static float3 SafeDirection(float3 value)
            => math.normalizesafe(value, new float3(0f, 0f, 1f));

        private static quaternion FromToRotation(float3 from, float3 to)
        {
            float3 a = SafeDirection(from);
            float3 b = SafeDirection(to);
            float dot = math.clamp(math.dot(a, b), -1f, 1f);
            float3 axis = math.cross(a, b);
            if (math.lengthsq(axis) < BasisConstraintDefaults.WeightEpsilon)
            {
                // Parallel or antiparallel: same direction is a no-op, opposite needs any
                // perpendicular axis to spin a half turn about.
                if (dot > 0f)
                {
                    return quaternion.identity;
                }
                float3 fallback = math.abs(a.x) < 0.9f ? new float3(1f, 0f, 0f) : new float3(0f, 1f, 0f);
                return quaternion.AxisAngle(math.normalize(math.cross(a, fallback)), math.PI);
            }
            return quaternion.AxisAngle(math.normalize(axis), math.acos(dot));
        }

        /// <summary>
        /// Spreads the twist between a chain's two ends across the bones in between, so a wrist or
        /// waist turn distributes along the chain instead of shearing at one joint.
        ///
        /// Animation Rigging drives the whole chain from one component, sampling its curve at bind
        /// time into a per-bone weight. Basis drives one transform per constraint, so a converted
        /// chain gets one of these per bone, each already carrying its own sampled weight — which
        /// also means no managed AnimationCurve has to reach the job. Bones do not depend on each
        /// other here: every one is an absolute blend between the two ends, so no chain walk is
        /// needed either.
        /// </summary>
        private void SolveTwistChain(
            in BasisConstraintSlot slot,
            in BasisConstraintTransform local,
            in BasisConstraintWorld parent,
            float weight,
            ref BasisConstraintResult result)
        {
            if (slot.SourceCount < 2)
            {
                return;
            }
            BasisConstraintSource rootSource = Sources[slot.SourceStart];
            BasisConstraintSource tipSource = Sources[slot.SourceStart + 1];
            if (rootSource.TransformIndex < 0 || tipSource.TransformIndex < 0)
            {
                return;
            }

            quaternion blended = math.slerp(
                World[rootSource.TransformIndex].Rotation,
                World[tipSource.TransformIndex].Rotation,
                math.saturate(slot.PositionChannelWeight));

            // The bind offset is what this bone was holding relative to that blend when it was
            // captured, so an already-posed chain keeps its shape instead of collapsing onto the ends.
            ApplyRotation(in slot, in local, in parent, math.mul(slot.BindRotation, blended), weight, ref result);
        }

        /// <summary>
        /// Passes a fraction of the source's twist about one axis onto this transform — the forearm
        /// and upper-arm rolls that stop a wrist from shearing its mesh.
        ///
        /// Animation Rigging drives a whole list of twist nodes from one component. Basis drives one
        /// target per constraint, so a converted rig gets one of these per node, each carrying its own
        /// share. A negative share counters the twist instead of following it.
        /// </summary>
        private void SolveTwistCorrection(
            in BasisConstraintSlot slot,
            float weight,
            ref BasisConstraintResult result)
        {
            if (slot.SourceCount < 1)
            {
                return;
            }
            BasisConstraintSource source = Sources[slot.SourceStart];
            if (source.TransformIndex < 0)
            {
                return;
            }

            // How far the source has rolled since bind, reduced to just the component about the
            // chosen axis: zero the other two imaginary parts and renormalise.
            quaternion delta = math.mul(slot.BindRotation, Local[source.TransformIndex].LocalRotation);
            float3 axis = slot.AimVector;
            quaternion twist = math.normalizesafe(
                new quaternion(
                    axis.x * delta.value.x,
                    axis.y * delta.value.y,
                    axis.z * delta.value.z,
                    delta.value.w),
                quaternion.identity);

            float share = math.clamp(slot.PositionChannelWeight, -1f, 1f);
            quaternion directed = share < 0f ? math.conjugate(twist) : twist;
            quaternion applied = math.nlerp(quaternion.identity, directed, math.abs(share));

            result.LocalRotation = math.nlerp(slot.RotationAtRest, applied, weight);
            result.WriteRotation = 1;
        }

        /// <summary>60Hz integration step for the damped kind, matching Animation Rigging.</summary>
        private const float DampFixedStep = 0.01667f;
        private const float DampRate = 40f;

        /// <summary>
        /// Lets the target lag behind its source instead of following rigidly, for floppy secondary
        /// motion. Integrated in fixed 60Hz sub-steps so the lag looks the same at any framerate,
        /// which is why this is the one kind that needs a delta time and a memory of last frame.
        ///
        /// The damp values read as resistance: 0 snaps straight onto the source, 1 never moves.
        /// </summary>
        private void SolveDamped(
            int slotIndex,
            in BasisConstraintSlot slot,
            in BasisConstraintWorld parent,
            in BasisConstraintWorld targetWorld,
            float weight,
            ref BasisConstraintResult result)
        {
            if (slot.SourceCount < 1)
            {
                return;
            }
            BasisConstraintSource source = Sources[slot.SourceStart];
            if (source.TransformIndex < 0)
            {
                return;
            }

            BasisConstraintDampState state = DampState[slotIndex];
            float remaining = math.abs(DeltaTime);

            // At zero weight or on a frame with no time behind it there is nothing to integrate, so
            // park the memory on the live pose. That also keeps re-enabling from snapping: the lag
            // resumes from where the transform actually is rather than where it was long ago.
            if (weight <= BasisConstraintDefaults.WeightEpsilon || remaining <= 0f || state.Initialized == 0)
            {
                state.PreviousPosition = targetWorld.Position;
                state.PreviousRotation = targetWorld.Rotation;
                state.Initialized = 1;
                DampState[slotIndex] = state;
                if (weight <= BasisConstraintDefaults.WeightEpsilon || remaining <= 0f)
                {
                    return;
                }
            }

            BasisConstraintWorld sourceWorld = World[source.TransformIndex];

            // Where the target would sit if it followed rigidly: the source's pose carrying the
            // offset the two had when the bind was captured.
            float3 targetPosition = sourceWorld.Position + math.mul(sourceWorld.Rotation, slot.BindPosition);
            quaternion targetRotation = math.mul(sourceWorld.Rotation, slot.BindRotation);
            targetPosition = math.lerp(targetWorld.Position, targetPosition, weight);
            targetRotation = math.nlerp(targetWorld.Rotation, targetRotation, weight);

            float positionRate = 1f - math.saturate(slot.PositionChannelWeight);
            float rotationRate = 1f - math.saturate(slot.RotationChannelWeight);
            positionRate *= positionRate;
            rotationRate *= rotationRate;

            bool maintainAim = slot.MaintainAim != 0 && math.lengthsq(slot.AimVector) > 0f;

            while (remaining > 0f)
            {
                float step = DampRate * math.min(DampFixedStep, remaining);

                state.PreviousPosition += (targetPosition - state.PreviousPosition) * positionRate * step;
                state.PreviousRotation = math.mul(
                    state.PreviousRotation,
                    math.nlerp(
                        quaternion.identity,
                        math.mul(math.conjugate(state.PreviousRotation), targetRotation),
                        rotationRate * step));

                if (maintainAim)
                {
                    // Keep the captured axis pointed at the source even while the body of the motion
                    // lags, so a damped chain still looks like it is reaching toward its parent.
                    float3 from = math.mul(state.PreviousRotation, slot.AimVector);
                    float3 to = sourceWorld.Position - state.PreviousPosition;
                    float3 axis = math.cross(from, to);
                    float axisLength = math.length(axis);
                    if (axisLength > BasisConstraintDefaults.WeightEpsilon)
                    {
                        float angle = math.acos(math.clamp(
                            math.dot(math.normalizesafe(from), math.normalizesafe(to)), -1f, 1f));
                        state.PreviousRotation = math.mul(
                            quaternion.AxisAngle(axis / axisLength, angle), state.PreviousRotation);
                    }
                }

                remaining -= DampFixedStep;
            }

            state.Initialized = 1;
            DampState[slotIndex] = state;

            result.LocalPosition = BasisConstraintMath.WorldToParentPoint(parent, state.PreviousPosition);
            result.LocalRotation = BasisConstraintMath.WorldToParentRotation(parent, state.PreviousRotation);
            result.WritePosition = 1;
            result.WriteRotation = 1;
        }

        /// <summary>
        /// Replaces the target's pose with an override, either explicit values or a source's movement
        /// since the bind was captured. Unlike the other kinds this blends away from the target's
        /// <em>current</em> pose rather than its rest pose, matching Animation Rigging: an override is
        /// meant to layer on top of whatever posed the transform this frame, not fight it.
        /// </summary>
        private void SolveOverride(
            in BasisConstraintSlot slot,
            in BasisConstraintTransform local,
            in BasisConstraintWorld parent,
            float weight,
            ref BasisConstraintResult result)
        {
            float3 overridePosition = slot.OverridePosition;
            quaternion overrideRotation = slot.OverrideRotation;

            if (slot.UseOverrideSource != 0)
            {
                if (slot.SourceCount < 1)
                {
                    return;
                }
                BasisConstraintSource source = Sources[slot.SourceStart];
                if (source.TransformIndex < 0)
                {
                    return;
                }

                // The source contributes how far it has moved from its captured bind, not where it
                // sits: delta = inverse(bind) * currentLocal, then conjugated into the target's space.
                BasisConstraintTransform sourceLocal = Local[source.TransformIndex];
                quaternion deltaRotation = math.mul(slot.BindRotation, sourceLocal.LocalRotation);
                float3 deltaPosition = slot.BindPosition
                    + math.mul(slot.BindRotation, sourceLocal.LocalPosition);

                quaternion toSpace = slot.SourceToSpaceRotation;
                quaternion fromSpace = math.conjugate(toSpace);
                overrideRotation = math.mul(math.mul(fromSpace, deltaRotation), toSpace);
                overridePosition = math.mul(fromSpace, deltaPosition);
            }

            float positionAmount = math.saturate(slot.PositionChannelWeight) * weight;
            float rotationAmount = math.saturate(slot.RotationChannelWeight) * weight;

            float3 currentPosition = local.LocalPosition;
            quaternion currentRotation = local.LocalRotation;
            float3 drivenPosition;
            quaternion drivenRotation;

            switch (slot.OverrideSpace)
            {
                case BasisOverrideSpace.World:
                    drivenPosition = BasisConstraintMath.WorldToParentPoint(parent, overridePosition);
                    drivenRotation = BasisConstraintMath.WorldToParentRotation(parent, overrideRotation);
                    break;
                case BasisOverrideSpace.Pivot:
                    // Compose onto the pose the transform already has, in its own local frame.
                    drivenPosition = currentPosition + math.mul(currentRotation, overridePosition);
                    drivenRotation = math.mul(currentRotation, overrideRotation);
                    break;
                default:
                    drivenPosition = overridePosition;
                    drivenRotation = overrideRotation;
                    break;
            }

            if (slot.TranslationMask != 0)
            {
                float3 masked = BasisConstraintMath.MaskAxis(
                    currentPosition, drivenPosition, slot.TranslationMask);
                result.LocalPosition = math.lerp(currentPosition, masked, positionAmount);
                result.WritePosition = 1;
            }

            if (slot.RotationMask != 0)
            {
                quaternion masked = BasisConstraintMath.MaskEuler(
                    currentRotation, drivenRotation, slot.RotationMask);
                result.LocalRotation = math.slerp(currentRotation, masked, rotationAmount);
                result.WriteRotation = 1;
            }
        }

        /// <summary>
        /// Lands an already-resolved world position on the target: into parent space, through the
        /// axis mask against the locked-or-rest fill, then blended against the rest pose by weight.
        /// </summary>
        private static void ApplyPosition(
            in BasisConstraintSlot slot,
            in BasisConstraintTransform local,
            in BasisConstraintWorld parent,
            float3 drivenWorld,
            float weight,
            ref BasisConstraintResult result)
        {
            float3 driven = BasisConstraintMath.WorldToParentPoint(parent, drivenWorld) + slot.TranslationOffset;
            float3 current = local.LocalPosition;
            float3 undriven = slot.Locked != 0 ? current : slot.TranslationAtRest;
            float3 masked = BasisConstraintMath.MaskAxis(undriven, driven, slot.TranslationMask);
            float3 rest = BasisConstraintMath.MaskAxis(undriven, slot.TranslationAtRest, slot.TranslationMask);

            result.LocalPosition = math.lerp(rest, masked, weight);
            result.WritePosition = 1;
        }

        /// <summary>Rotation counterpart to <see cref="ApplyPosition"/>.</summary>
        private static void ApplyRotation(
            in BasisConstraintSlot slot,
            in BasisConstraintTransform local,
            in BasisConstraintWorld parent,
            quaternion drivenWorld,
            float weight,
            ref BasisConstraintResult result)
        {
            quaternion driven = math.mul(
                BasisConstraintMath.WorldToParentRotation(parent, drivenWorld), slot.RotationOffset);
            quaternion current = local.LocalRotation;
            quaternion undriven = slot.Locked != 0 ? current : slot.RotationAtRest;
            quaternion masked = BasisConstraintMath.MaskEuler(undriven, driven, slot.RotationMask);
            quaternion rest = BasisConstraintMath.MaskEuler(undriven, slot.RotationAtRest, slot.RotationMask);

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
