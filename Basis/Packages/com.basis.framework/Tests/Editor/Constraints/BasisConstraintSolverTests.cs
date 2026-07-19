using Basis.Scripts.Constraints;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

namespace Basis.Tests.Constraints
{
    /// <summary>
    /// Coverage for the six constraint kinds and the blending contract they share: inactive/zero-weight
    /// slots are exact no-ops, sources blend by normalised weight, axis masks fall back to the live pose
    /// when locked and the rest pose when unlocked, and Aim/LookAt actually point the aim axis at the
    /// blended source.
    /// </summary>
    public class BasisConstraintSolverTests
    {
        private const float Tolerance = 1e-4f;

        // ---------- no-op contract ----------

        [Test]
        public void InactiveSlot_LeavesPoseUntouched()
        {
            BasisConstraintSlot slot = Slot(BasisConstraintKind.Position, sourceCount: 1);
            slot.Active = 0;

            BasisConstraintWorld current = World(new float3(1f, 2f, 3f));
            BasisConstraintWorld result = Solve(slot, current, new[] { Source(0, 1f) }, new[] { World(new float3(50f, 50f, 50f)) });

            AssertPosition(current.Position, result.Position, "an inactive slot must not move the target at all");
        }

        [Test]
        public void ZeroWeight_IsExactNoOp()
        {
            BasisConstraintSlot slot = Slot(BasisConstraintKind.Position, sourceCount: 1);
            slot.Weight = 0f;

            BasisConstraintWorld current = World(new float3(1f, 2f, 3f));
            BasisConstraintWorld result = Solve(slot, current, new[] { Source(0, 1f) }, new[] { World(new float3(50f, 50f, 50f)) });

            AssertPosition(current.Position, result.Position, "weight 0 must be a no-op, never a snap to rest");
        }

        [Test]
        public void ZeroWeight_IsNoOpEvenWhenUnlocked()
        {
            // Regression guard: an unlocked slot must not fall back to its rest pose just because the
            // weight reached zero — that would make the blend discontinuous at 0.
            BasisConstraintSlot slot = Slot(BasisConstraintKind.Position, sourceCount: 1);
            slot.Weight = 0f;
            slot.Locked = 0;
            slot.TranslationAtRest = new float3(-99f, -99f, -99f);

            BasisConstraintWorld current = World(new float3(1f, 2f, 3f));
            BasisConstraintWorld result = Solve(slot, current, new[] { Source(0, 1f) }, new[] { World(new float3(50f, 50f, 50f)) });

            AssertPosition(current.Position, result.Position, "weight 0 must win over the unlocked rest fallback");
        }

        [Test]
        public void NoSources_LeavesPoseUntouched()
        {
            BasisConstraintSlot slot = Slot(BasisConstraintKind.Position, sourceCount: 0);

            BasisConstraintWorld current = World(new float3(1f, 2f, 3f));
            BasisConstraintWorld result = Solve(slot, current, new BasisConstraintSource[0], new[] { World(float3.zero) });

            AssertPosition(current.Position, result.Position, "a slot with no sources has nothing to follow");
        }

        [Test]
        public void AllSourceWeightsZero_LeavesPoseUntouched()
        {
            BasisConstraintSlot slot = Slot(BasisConstraintKind.Position, sourceCount: 2);

            BasisConstraintWorld current = World(new float3(1f, 2f, 3f));
            BasisConstraintWorld result = Solve(
                slot, current,
                new[] { Source(0, 0f), Source(1, 0f) },
                new[] { World(new float3(10f, 0f, 0f)), World(new float3(0f, 10f, 0f)) });

            AssertPosition(current.Position, result.Position, "sources that all sit at zero weight contribute nothing");
        }

        // ---------- position ----------

        [Test]
        public void Position_SingleSource_SnapsToSource()
        {
            BasisConstraintSlot slot = Slot(BasisConstraintKind.Position, sourceCount: 1);

            BasisConstraintWorld result = Solve(
                slot, World(float3.zero),
                new[] { Source(0, 1f) },
                new[] { World(new float3(4f, 5f, 6f)) });

            AssertPosition(new float3(4f, 5f, 6f), result.Position, "one full-weight source drives the target onto it");
        }

        [Test]
        public void Position_TwoEqualSources_LandsOnMidpoint()
        {
            BasisConstraintSlot slot = Slot(BasisConstraintKind.Position, sourceCount: 2);

            BasisConstraintWorld result = Solve(
                slot, World(float3.zero),
                new[] { Source(0, 1f), Source(1, 1f) },
                new[] { World(new float3(0f, 0f, 0f)), World(new float3(10f, 0f, 0f)) });

            AssertPosition(new float3(5f, 0f, 0f), result.Position, "equal weights blend to the midpoint");
        }

        [Test]
        public void Position_WeightsNormalise_SoASingleQuarterWeightSourceStillFullyDrives()
        {
            // The thing that most often surprises people: weights are relative to each other, so one
            // source at 0.25 is still the only contributor and wins outright.
            BasisConstraintSlot slot = Slot(BasisConstraintKind.Position, sourceCount: 1);

            BasisConstraintWorld result = Solve(
                slot, World(float3.zero),
                new[] { Source(0, 0.25f) },
                new[] { World(new float3(8f, 0f, 0f)) });

            AssertPosition(new float3(8f, 0f, 0f), result.Position, "a lone source drives fully regardless of its own weight");
        }

        [Test]
        public void Position_UnevenSourceWeights_BlendProportionally()
        {
            BasisConstraintSlot slot = Slot(BasisConstraintKind.Position, sourceCount: 2);

            BasisConstraintWorld result = Solve(
                slot, World(float3.zero),
                new[] { Source(0, 3f), Source(1, 1f) },
                new[] { World(new float3(0f, 0f, 0f)), World(new float3(8f, 0f, 0f)) });

            AssertPosition(new float3(2f, 0f, 0f), result.Position, "3:1 weighting lands a quarter of the way toward the second source");
        }

        [Test]
        public void Position_SlotWeight_BlendsFromCurrentPose()
        {
            BasisConstraintSlot slot = Slot(BasisConstraintKind.Position, sourceCount: 1);
            slot.Weight = 0.5f;

            BasisConstraintWorld result = Solve(
                slot, World(new float3(0f, 0f, 0f)),
                new[] { Source(0, 1f) },
                new[] { World(new float3(10f, 0f, 0f)) });

            AssertPosition(new float3(5f, 0f, 0f), result.Position, "slot weight lerps between the live pose and the constrained pose");
        }

        [Test]
        public void Position_TranslationOffset_AppliedAfterBlending()
        {
            BasisConstraintSlot slot = Slot(BasisConstraintKind.Position, sourceCount: 1);
            slot.TranslationOffset = new float3(0f, 2f, 0f);

            BasisConstraintWorld result = Solve(
                slot, World(float3.zero),
                new[] { Source(0, 1f) },
                new[] { World(new float3(4f, 0f, 0f)) });

            AssertPosition(new float3(4f, 2f, 0f), result.Position, "the slot offset shifts the blended result");
        }

        [Test]
        public void Position_SourcePositionOffset_IsAppliedInSourceSpace()
        {
            // Source is yawed 90 degrees, so its local +Z offset points along world +X.
            BasisConstraintSlot slot = Slot(BasisConstraintKind.Position, sourceCount: 1);
            BasisConstraintSource source = Source(0, 1f);
            source.PositionOffset = new float3(0f, 0f, 1f);

            quaternion yaw90 = quaternion.EulerZXY(0f, math.radians(90f), 0f);
            BasisConstraintWorld result = Solve(
                slot, World(float3.zero),
                new[] { source },
                new[] { World(float3.zero, yaw90) });

            AssertPosition(new float3(1f, 0f, 0f), result.Position, "a source offset rotates with the source, it is not a world-space nudge");
        }

        // ---------- axis masks ----------

        [Test]
        public void Position_MaskedAxis_KeepsLivePoseWhenLocked()
        {
            BasisConstraintSlot slot = Slot(BasisConstraintKind.Position, sourceCount: 1);
            slot.TranslationMask = (byte)(BasisConstraintAxis.X | BasisConstraintAxis.Z);
            slot.Locked = 1;

            BasisConstraintWorld result = Solve(
                slot, World(new float3(0f, 7f, 0f)),
                new[] { Source(0, 1f) },
                new[] { World(new float3(4f, 99f, 6f)) });

            AssertPosition(new float3(4f, 7f, 6f), result.Position, "the masked Y axis holds the live pose while X and Z follow the source");
        }

        [Test]
        public void Position_MaskedAxis_FallsBackToRestWhenUnlocked()
        {
            BasisConstraintSlot slot = Slot(BasisConstraintKind.Position, sourceCount: 1);
            slot.TranslationMask = (byte)(BasisConstraintAxis.X | BasisConstraintAxis.Z);
            slot.Locked = 0;
            slot.TranslationAtRest = new float3(0f, -3f, 0f);

            BasisConstraintWorld result = Solve(
                slot, World(new float3(0f, 7f, 0f)),
                new[] { Source(0, 1f) },
                new[] { World(new float3(4f, 99f, 6f)) });

            AssertPosition(new float3(4f, -3f, 6f), result.Position, "unlocking sends the masked Y axis to the rest pose instead of the live one");
        }

        [Test]
        public void Position_NoAxesEnabled_LeavesPositionUntouched()
        {
            BasisConstraintSlot slot = Slot(BasisConstraintKind.Position, sourceCount: 1);
            slot.TranslationMask = (byte)BasisConstraintAxis.None;

            BasisConstraintWorld current = World(new float3(1f, 2f, 3f));
            BasisConstraintWorld result = Solve(slot, current, new[] { Source(0, 1f) }, new[] { World(new float3(9f, 9f, 9f)) });

            AssertPosition(current.Position, result.Position, "masking every axis off makes the constraint inert");
        }

        [Test]
        public void Position_DoesNotDisturbRotationOrScale()
        {
            BasisConstraintSlot slot = Slot(BasisConstraintKind.Position, sourceCount: 1);

            quaternion spin = quaternion.EulerZXY(math.radians(20f), math.radians(30f), 0f);
            var current = new BasisConstraintWorld { Position = float3.zero, Rotation = spin, Scale = new float3(2f, 3f, 4f) };
            BasisConstraintWorld result = Solve(slot, current, new[] { Source(0, 1f) }, new[] { World(new float3(5f, 0f, 0f), quaternion.identity) });

            Assert.Less(AngleDegrees(spin, result.Rotation), 0.01f, "a position constraint must leave rotation alone");
            AssertPosition(new float3(2f, 3f, 4f), result.Scale, "a position constraint must leave scale alone");
        }

        // ---------- rotation ----------

        [Test]
        public void Rotation_SingleSource_MatchesSourceRotation()
        {
            BasisConstraintSlot slot = Slot(BasisConstraintKind.Rotation, sourceCount: 1);
            quaternion target = quaternion.EulerZXY(math.radians(15f), math.radians(40f), math.radians(5f));

            BasisConstraintWorld result = Solve(
                slot, World(float3.zero),
                new[] { Source(0, 1f) },
                new[] { World(float3.zero, target) });

            Assert.Less(AngleDegrees(target, result.Rotation), 0.05f, "one full-weight source hands its rotation to the target");
        }

        [Test]
        public void Rotation_TwoSources_BlendBetweenThem()
        {
            BasisConstraintSlot slot = Slot(BasisConstraintKind.Rotation, sourceCount: 2);
            quaternion none = quaternion.identity;
            quaternion yaw90 = quaternion.EulerZXY(0f, math.radians(90f), 0f);

            BasisConstraintWorld result = Solve(
                slot, World(float3.zero),
                new[] { Source(0, 1f), Source(1, 1f) },
                new[] { World(float3.zero, none), World(float3.zero, yaw90) });

            float toStart = AngleDegrees(none, result.Rotation);
            float toEnd = AngleDegrees(yaw90, result.Rotation);
            Assert.Less(math.abs(toStart - toEnd), 1f, $"an even blend must sit between both sources (got {toStart:0.0} vs {toEnd:0.0} degrees)");
            Assert.Less(toStart, 60f, "the blend must stay between the two sources, not swing outside them");
        }

        [Test]
        public void Rotation_BlendsShortWayWhenSourceQuaternionsAreOppositelySigned()
        {
            // q and -q are the same orientation; blending naively would drag the result toward identity.
            BasisConstraintSlot slot = Slot(BasisConstraintKind.Rotation, sourceCount: 2);
            quaternion yaw150 = quaternion.EulerZXY(0f, math.radians(150f), 0f);
            var negated = new quaternion(-yaw150.value);

            BasisConstraintWorld result = Solve(
                slot, World(float3.zero),
                new[] { Source(0, 1f), Source(1, 1f) },
                new[] { World(float3.zero, yaw150), World(float3.zero, negated) });

            Assert.Less(AngleDegrees(yaw150, result.Rotation), 0.5f, "blending a rotation with its own negation must return that same rotation");
        }

        [Test]
        public void Rotation_MaskedToSingleAxis_OnlyThatAxisFollows()
        {
            BasisConstraintSlot slot = Slot(BasisConstraintKind.Rotation, sourceCount: 1);
            slot.RotationMask = (byte)BasisConstraintAxis.Y;

            quaternion source = quaternion.EulerZXY(0f, math.radians(50f), 0f);
            BasisConstraintWorld result = Solve(
                slot, World(float3.zero, quaternion.identity),
                new[] { Source(0, 1f) },
                new[] { World(float3.zero, source) });

            float3 euler = BasisConstraintMath.ToEulerZXY(result.Rotation);
            Assert.AreEqual(50f, math.degrees(euler.y), 0.5f, "the enabled Y axis follows the source");
            Assert.AreEqual(0f, math.degrees(euler.x), 0.5f, "the masked X axis stays put");
            Assert.AreEqual(0f, math.degrees(euler.z), 0.5f, "the masked Z axis stays put");
        }

        // ---------- scale ----------

        [Test]
        public void Scale_SingleSource_TakesSourceScale()
        {
            BasisConstraintSlot slot = Slot(BasisConstraintKind.Scale, sourceCount: 1);

            BasisConstraintWorld result = Solve(
                slot, World(float3.zero),
                new[] { Source(0, 1f) },
                new[] { WorldScaled(new float3(2f, 3f, 4f)) });

            AssertPosition(new float3(2f, 3f, 4f), result.Scale, "one full-weight source hands its scale to the target");
        }

        [Test]
        public void Scale_Offset_MultipliesTheBlendedScale()
        {
            BasisConstraintSlot slot = Slot(BasisConstraintKind.Scale, sourceCount: 1);
            slot.ScaleOffset = new float3(2f, 2f, 2f);

            BasisConstraintWorld result = Solve(
                slot, World(float3.zero),
                new[] { Source(0, 1f) },
                new[] { WorldScaled(new float3(3f, 3f, 3f)) });

            AssertPosition(new float3(6f, 6f, 6f), result.Scale, "the scale offset is a multiplier, not an addition");
        }

        // ---------- parent ----------

        [Test]
        public void Parent_DrivesPositionAndRotationTogether()
        {
            BasisConstraintSlot slot = Slot(BasisConstraintKind.Parent, sourceCount: 1);
            quaternion spin = quaternion.EulerZXY(0f, math.radians(70f), 0f);

            BasisConstraintWorld result = Solve(
                slot, World(float3.zero, quaternion.identity),
                new[] { Source(0, 1f) },
                new[] { World(new float3(1f, 2f, 3f), spin) });

            AssertPosition(new float3(1f, 2f, 3f), result.Position, "parent drives position");
            Assert.Less(AngleDegrees(spin, result.Rotation), 0.05f, "parent drives rotation in the same pass");
        }

        [Test]
        public void Parent_LeavesScaleAlone()
        {
            BasisConstraintSlot slot = Slot(BasisConstraintKind.Parent, sourceCount: 1);

            var current = new BasisConstraintWorld { Position = float3.zero, Rotation = quaternion.identity, Scale = new float3(5f, 5f, 5f) };
            BasisConstraintWorld result = Solve(slot, current, new[] { Source(0, 1f) }, new[] { WorldScaled(new float3(1f, 1f, 1f)) });

            AssertPosition(new float3(5f, 5f, 5f), result.Scale, "parent constraints do not touch scale");
        }

        // ---------- aim / look at ----------

        [Test]
        public void Aim_PointsTheAimAxisAtTheSource()
        {
            BasisConstraintSlot slot = Slot(BasisConstraintKind.Aim, sourceCount: 1);
            slot.AimVector = new float3(0f, 0f, 1f);

            var target = new float3(5f, 0f, 0f);
            BasisConstraintWorld result = Solve(
                slot, World(float3.zero),
                new[] { Source(0, 1f) },
                new[] { World(target) });

            float3 aimed = math.mul(result.Rotation, new float3(0f, 0f, 1f));
            AssertDirection(math.normalize(target), aimed, "local forward must end up pointing at the source");
        }

        [Test]
        public void Aim_HonoursANonForwardAimVector()
        {
            BasisConstraintSlot slot = Slot(BasisConstraintKind.Aim, sourceCount: 1);
            slot.AimVector = new float3(1f, 0f, 0f);

            var target = new float3(0f, 0f, 9f);
            BasisConstraintWorld result = Solve(
                slot, World(float3.zero),
                new[] { Source(0, 1f) },
                new[] { World(target) });

            float3 aimed = math.mul(result.Rotation, new float3(1f, 0f, 0f));
            AssertDirection(math.normalize(target), aimed, "the chosen local axis, not forward, is what points at the source");
        }

        [Test]
        public void Aim_TracksTheBlendedSourcePointNotASingleSource()
        {
            BasisConstraintSlot slot = Slot(BasisConstraintKind.Aim, sourceCount: 2);
            slot.AimVector = new float3(0f, 0f, 1f);

            BasisConstraintWorld result = Solve(
                slot, World(float3.zero),
                new[] { Source(0, 1f), Source(1, 1f) },
                new[] { World(new float3(10f, 0f, 10f)), World(new float3(-10f, 0f, 10f)) });

            float3 aimed = math.mul(result.Rotation, new float3(0f, 0f, 1f));
            AssertDirection(new float3(0f, 0f, 1f), aimed, "two mirrored sources average to a point straight ahead");
        }

        [Test]
        public void Aim_SourceOnTopOfTarget_LeavesRotationUntouched()
        {
            BasisConstraintSlot slot = Slot(BasisConstraintKind.Aim, sourceCount: 1);
            quaternion spin = quaternion.EulerZXY(math.radians(10f), math.radians(20f), 0f);

            BasisConstraintWorld result = Solve(
                slot, World(float3.zero, spin),
                new[] { Source(0, 1f) },
                new[] { World(float3.zero) });

            Assert.Less(AngleDegrees(spin, result.Rotation), 0.01f, "a zero-length aim direction must not produce a garbage rotation");
        }

        [Test]
        public void LookAt_IgnoresTheAimVectorAndAlwaysUsesForward()
        {
            BasisConstraintSlot slot = Slot(BasisConstraintKind.LookAt, sourceCount: 1);
            slot.AimVector = new float3(1f, 0f, 0f); // deliberately not forward; LookAt must ignore it

            var target = new float3(0f, 0f, 6f);
            BasisConstraintWorld result = Solve(
                slot, World(float3.zero),
                new[] { Source(0, 1f) },
                new[] { World(target) });

            float3 aimed = math.mul(result.Rotation, new float3(0f, 0f, 1f));
            AssertDirection(math.normalize(target), aimed, "LookAt always aims local forward, whatever AimVector says");
        }

        [Test]
        public void LookAt_RollSpinsAboutTheAimAxisWithoutBreakingTheAim()
        {
            BasisConstraintSlot rolled = Slot(BasisConstraintKind.LookAt, sourceCount: 1);
            rolled.Roll = 45f;

            var target = new float3(0f, 0f, 6f);
            BasisConstraintWorld result = Solve(
                rolled, World(float3.zero),
                new[] { Source(0, 1f) },
                new[] { World(target) });

            float3 aimed = math.mul(result.Rotation, new float3(0f, 0f, 1f));
            AssertDirection(math.normalize(target), aimed, "roll must not knock the aim axis off the source");

            float3 up = math.mul(result.Rotation, new float3(0f, 1f, 0f));
            float rollAngle = math.degrees(math.acos(math.clamp(math.dot(up, new float3(0f, 1f, 0f)), -1f, 1f)));
            Assert.AreEqual(45f, rollAngle, 1f, "up must be twisted 45 degrees about the aim axis");
        }

        [Test]
        public void Aim_WorldUpVectorMode_UsesTheSlotVector()
        {
            BasisConstraintSlot slot = Slot(BasisConstraintKind.Aim, sourceCount: 1);
            slot.AimVector = new float3(0f, 0f, 1f);
            slot.UpVector = new float3(0f, 1f, 0f);
            slot.WorldUpKind = BasisWorldUpKind.Vector;
            slot.WorldUpVector = new float3(1f, 0f, 0f);

            BasisConstraintWorld result = Solve(
                slot, World(float3.zero),
                new[] { Source(0, 1f) },
                new[] { World(new float3(0f, 0f, 5f)) });

            float3 up = math.mul(result.Rotation, new float3(0f, 1f, 0f));
            AssertDirection(new float3(1f, 0f, 0f), up, "with world up set to +X the local up must resolve onto +X");
        }

        // ---------- batch ordering ----------

        [Test]
        public void SolveAll_AppliesEverySlotToItsOwnTarget()
        {
            var slots = new BasisConstraintSlot[2];
            slots[0] = Slot(BasisConstraintKind.Position, sourceCount: 1);
            slots[0].TargetIndex = 0;
            slots[0].SourceStart = 0;
            slots[1] = Slot(BasisConstraintKind.Position, sourceCount: 1);
            slots[1].TargetIndex = 1;
            slots[1].SourceStart = 1;

            var sources = new[] { Source(2, 1f), Source(3, 1f) };
            var world = new[]
            {
                World(float3.zero),
                World(float3.zero),
                World(new float3(1f, 0f, 0f)),
                World(new float3(0f, 2f, 0f)),
            };

            BasisConstraintWorld[] solved = SolveAll(slots, sources, world);

            AssertPosition(new float3(1f, 0f, 0f), solved[0].Position, "the first slot follows its own source");
            AssertPosition(new float3(0f, 2f, 0f), solved[1].Position, "the second slot follows its own source");
        }

        [Test]
        public void SolveAll_ChainedConstraintSeesTheAlreadySolvedParent()
        {
            // Slot 0 drives transform 0 onto (10,0,0); slot 1 follows transform 0, so ordering the
            // shallower slot first means the child picks up the solved value, not the stale one.
            var slots = new BasisConstraintSlot[2];
            slots[0] = Slot(BasisConstraintKind.Position, sourceCount: 1);
            slots[0].TargetIndex = 0;
            slots[0].SourceStart = 0;
            slots[0].Depth = 0;
            slots[1] = Slot(BasisConstraintKind.Position, sourceCount: 1);
            slots[1].TargetIndex = 1;
            slots[1].SourceStart = 1;
            slots[1].Depth = 1;

            var sources = new[] { Source(2, 1f), Source(0, 1f) };
            var world = new[]
            {
                World(float3.zero),
                World(float3.zero),
                World(new float3(10f, 0f, 0f)),
            };

            BasisConstraintWorld[] solved = SolveAll(slots, sources, world);

            AssertPosition(new float3(10f, 0f, 0f), solved[1].Position, "the chained slot must read its parent's solved pose");
        }

        [Test]
        public void SolveAll_SkipsSlotsWithAnOutOfRangeTarget()
        {
            var slots = new BasisConstraintSlot[1];
            slots[0] = Slot(BasisConstraintKind.Position, sourceCount: 1);
            slots[0].TargetIndex = 99;

            Assert.DoesNotThrow(
                () => SolveAll(slots, new[] { Source(0, 1f) }, new[] { World(float3.zero) }),
                "a dangling target index must be skipped, not read out of bounds");
        }

        // ---------- helpers ----------

        private static BasisConstraintSlot Slot(BasisConstraintKind kind, int sourceCount)
        {
            BasisConstraintSlot slot = BasisConstraintDefaults.Identity(kind);
            slot.TargetIndex = 0;
            slot.SourceStart = 0;
            slot.SourceCount = sourceCount;
            return slot;
        }

        private static BasisConstraintSource Source(int transformIndex, float weight) => new BasisConstraintSource
        {
            TransformIndex = transformIndex,
            Weight = weight,
            PositionOffset = float3.zero,
            RotationOffset = quaternion.identity,
        };

        private static BasisConstraintWorld World(float3 position) => World(position, quaternion.identity);

        private static BasisConstraintWorld World(float3 position, quaternion rotation) => new BasisConstraintWorld
        {
            Position = position,
            Rotation = rotation,
            Scale = new float3(1f, 1f, 1f),
        };

        private static BasisConstraintWorld WorldScaled(float3 scale) => new BasisConstraintWorld
        {
            Position = float3.zero,
            Rotation = quaternion.identity,
            Scale = scale,
        };

        private static BasisConstraintWorld Solve(
            BasisConstraintSlot slot,
            BasisConstraintWorld current,
            BasisConstraintSource[] sources,
            BasisConstraintWorld[] world)
        {
            using var sourceArray = new NativeArray<BasisConstraintSource>(sources, Allocator.Temp);
            using var worldArray = new NativeArray<BasisConstraintWorld>(world, Allocator.Temp);
            return BasisConstraintSolver.SolveSlot(slot, current, sourceArray, worldArray);
        }

        /// <summary>Runs a whole batch and hands back the solved world poses, releasing the native arrays even when an assert fails.</summary>
        private static BasisConstraintWorld[] SolveAll(
            BasisConstraintSlot[] slots,
            BasisConstraintSource[] sources,
            BasisConstraintWorld[] world)
        {
            var slotArray = new NativeArray<BasisConstraintSlot>(slots, Allocator.Temp);
            var sourceArray = new NativeArray<BasisConstraintSource>(sources, Allocator.Temp);
            var worldArray = new NativeArray<BasisConstraintWorld>(world, Allocator.Temp);
            try
            {
                BasisConstraintSolver.SolveAll(ref worldArray, slotArray, sourceArray);
                return worldArray.ToArray();
            }
            finally
            {
                slotArray.Dispose();
                sourceArray.Dispose();
                worldArray.Dispose();
            }
        }

        private static void AssertPosition(float3 expected, float3 actual, string because)
        {
            Assert.AreEqual(expected.x, actual.x, Tolerance, $"x: {because}");
            Assert.AreEqual(expected.y, actual.y, Tolerance, $"y: {because}");
            Assert.AreEqual(expected.z, actual.z, Tolerance, $"z: {because}");
        }

        private static void AssertDirection(float3 expected, float3 actual, string because)
        {
            float dot = math.dot(math.normalizesafe(expected), math.normalizesafe(actual));
            Assert.AreEqual(1f, dot, 1e-3f, $"{because} (expected {expected}, got {actual})");
        }

        private static float AngleDegrees(quaternion a, quaternion b)
        {
            // abs() folds q and -q together, since they name the same orientation.
            float dot = math.abs(math.dot(math.normalizesafe(a.value), math.normalizesafe(b.value)));
            return math.degrees(2f * math.acos(math.clamp(dot, -1f, 1f)));
        }
    }
}
