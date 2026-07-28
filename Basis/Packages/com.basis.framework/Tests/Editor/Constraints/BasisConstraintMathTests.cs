using Basis.Scripts.Constraints;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Basis.Tests.Constraints
{
    /// <summary>
    /// Direct cover for the shared constraint math. The end-to-end solve tests exercise this too, but
    /// only through whatever path a given constraint kind happens to take — enough to catch a grossly
    /// wrong blend, not enough to catch a sign error that shows up at one orientation. These go at the
    /// helpers straight on, where an edge case can be aimed at deliberately.
    /// </summary>
    public sealed class BasisConstraintMathTests
    {
        const float Tolerance = 1e-4f;

        NativeArray<BasisConstraintSource> sources;
        NativeArray<BasisConstraintWorld> world;

        [TearDown]
        public void TearDown()
        {
            if (sources.IsCreated) sources.Dispose();
            if (world.IsCreated) world.Dispose();
        }

        /// <summary>Builds a source range and the transform table it indexes into.</summary>
        void Given(params (float3 position, quaternion rotation, float3 scale, float weight)[] entries)
        {
            sources = new NativeArray<BasisConstraintSource>(entries.Length, Allocator.Temp);
            world = new NativeArray<BasisConstraintWorld>(entries.Length, Allocator.Temp);
            for (int Index = 0; Index < entries.Length; Index++)
            {
                sources[Index] = new BasisConstraintSource
                {
                    TransformIndex = Index,
                    Weight = entries[Index].weight,
                    PositionOffset = float3.zero,
                    RotationOffset = quaternion.identity,
                };
                world[Index] = new BasisConstraintWorld
                {
                    Position = entries[Index].position,
                    Rotation = entries[Index].rotation,
                    Scale = entries[Index].scale,
                };
            }
        }

        static void AssertFloat3(float3 expected, float3 actual, string what)
        {
            Assert.AreEqual(expected.x, actual.x, Tolerance, $"{what}.x");
            Assert.AreEqual(expected.y, actual.y, Tolerance, $"{what}.y");
            Assert.AreEqual(expected.z, actual.z, Tolerance, $"{what}.z");
        }

        // ── Blending positions ────────────────────────────────────────────────────────────────────

        [Test]
        public void BlendPositions_WeightsAreRelativeNotAbsolute()
        {
            // A lone source at a quarter weight still drives fully: weights are normalised against
            // their own total, so one source is always the whole answer no matter its weight.
            Given((new float3(4f, 0f, 0f), quaternion.identity, 1f, 0.25f));

            float3 blended = BasisConstraintMath.BlendPositions(
                sources, world, 0, 1, false, out float total);

            AssertFloat3(new float3(4f, 0f, 0f), blended, "position");
            Assert.AreEqual(0.25f, total, Tolerance, "the raw total is still reported");
        }

        [Test]
        public void BlendPositions_UnevenWeightsBlendProportionally()
        {
            Given(
                (new float3(0f, 0f, 0f), quaternion.identity, 1f, 3f),
                (new float3(4f, 0f, 0f), quaternion.identity, 1f, 1f));

            float3 blended = BasisConstraintMath.BlendPositions(sources, world, 0, 2, false, out _);

            AssertFloat3(new float3(1f, 0f, 0f), blended, "three-to-one lands a quarter of the way");
        }

        [Test]
        public void BlendPositions_ZeroWeightSourcesAreSkippedEntirely()
        {
            Given(
                (new float3(100f, 0f, 0f), quaternion.identity, 1f, 0f),
                (new float3(2f, 0f, 0f), quaternion.identity, 1f, 1f));

            float3 blended = BasisConstraintMath.BlendPositions(sources, world, 0, 2, false, out float total);

            AssertFloat3(new float3(2f, 0f, 0f), blended, "a zero-weight source contributes nothing");
            Assert.AreEqual(1f, total, Tolerance, "and is not counted toward the total");
        }

        [Test]
        public void BlendPositions_AllWeightsZeroReportsNothingToDrive()
        {
            Given((new float3(5f, 5f, 5f), quaternion.identity, 1f, 0f));

            BasisConstraintMath.BlendPositions(sources, world, 0, 1, false, out float total);

            Assert.AreEqual(0f, total, Tolerance,
                "callers key off the total to leave the transform alone rather than snapping it");
        }

        [Test]
        public void BlendPositions_SourceOffsetIsAppliedInTheSourcesOwnSpace()
        {
            // The offset rides the source's rotation, so a source turned 90 degrees about Y sends an
            // offset that pointed along +X out along -Z instead.
            Given((float3.zero, quaternion.AxisAngle(new float3(0f, 1f, 0f), math.PI * 0.5f), 1f, 1f));
            BasisConstraintSource entry = sources[0];
            entry.PositionOffset = new float3(2f, 0f, 0f);
            sources[0] = entry;

            float3 blended = BasisConstraintMath.BlendPositions(sources, world, 0, 1, true, out _);

            AssertFloat3(new float3(0f, 0f, -2f), blended, "offset rotated into the source's space");
        }

        [Test]
        public void BlendPositions_OffsetIsIgnoredWhenNotRequested()
        {
            Given((float3.zero, quaternion.identity, 1f, 1f));
            BasisConstraintSource entry = sources[0];
            entry.PositionOffset = new float3(9f, 9f, 9f);
            sources[0] = entry;

            float3 blended = BasisConstraintMath.BlendPositions(sources, world, 0, 1, false, out _);

            AssertFloat3(float3.zero, blended, "only the parent kind applies per-source offsets");
        }

        // ── Blending rotations ────────────────────────────────────────────────────────────────────

        [Test]
        public void BlendRotations_TakesTheShortWayWhenQuaternionsAreOppositelySigned()
        {
            // The same orientation has two quaternion representations, q and -q. Averaging them
            // naively cancels to nothing; the blend has to flip one hemisphere first. This is the
            // failure that looks like a random 180 degree snap in a rig.
            quaternion turned = quaternion.AxisAngle(new float3(0f, 1f, 0f), math.PI * 0.5f);
            quaternion negated = new quaternion(-turned.value);

            Given(
                (float3.zero, turned, 1f, 1f),
                (float3.zero, negated, 1f, 1f));

            quaternion blended = BasisConstraintMath.BlendRotations(sources, world, 0, 2, false, out _);

            Assert.Less(math.degrees(math.angle(blended, turned)), 0.5f,
                "two spellings of one orientation must average back to that orientation");
        }

        [Test]
        public void BlendRotations_HalfwayBetweenTwoEqualSources()
        {
            quaternion none = quaternion.identity;
            quaternion ninety = quaternion.AxisAngle(new float3(0f, 1f, 0f), math.PI * 0.5f);
            Given(
                (float3.zero, none, 1f, 1f),
                (float3.zero, ninety, 1f, 1f));

            quaternion blended = BasisConstraintMath.BlendRotations(sources, world, 0, 2, false, out _);

            Assert.AreEqual(45f, math.degrees(math.angle(blended, none)), 1f, "lands midway");
        }

        [Test]
        public void BlendRotations_IsNormalised()
        {
            Given(
                (float3.zero, quaternion.AxisAngle(new float3(1f, 0f, 0f), 0.7f), 1f, 2f),
                (float3.zero, quaternion.AxisAngle(new float3(0f, 0f, 1f), 1.3f), 1f, 5f));

            quaternion blended = BasisConstraintMath.BlendRotations(sources, world, 0, 2, false, out _);

            Assert.AreEqual(1f, math.length(blended.value), 1e-3f,
                "an unnormalised quaternion would scale everything it is applied to");
        }

        // ── Blending scales ───────────────────────────────────────────────────────────────────────

        [Test]
        public void BlendScales_BlendsProportionally()
        {
            Given(
                (float3.zero, quaternion.identity, new float3(2f, 2f, 2f), 1f),
                (float3.zero, quaternion.identity, new float3(4f, 4f, 4f), 1f));

            float3 blended = BasisConstraintMath.BlendScales(sources, world, 0, 2, out _);

            AssertFloat3(new float3(3f, 3f, 3f), blended, "scale");
        }

        // ── Masking ───────────────────────────────────────────────────────────────────────────────

        [Test]
        public void MaskAxis_KeepsCurrentOnExcludedAxes()
        {
            float3 masked = BasisConstraintMath.MaskAxis(
                new float3(1f, 2f, 3f), new float3(9f, 9f, 9f), (byte)(BasisConstraintAxis.X | BasisConstraintAxis.Z));

            AssertFloat3(new float3(9f, 2f, 9f), masked, "only X and Z take the driven value");
        }

        [Test]
        public void MaskAxis_NoneKeepsEverythingAndAllTakesEverything()
        {
            float3 current = new float3(1f, 2f, 3f);
            float3 driven = new float3(7f, 8f, 9f);

            AssertFloat3(current,
                BasisConstraintMath.MaskAxis(current, driven, (byte)BasisConstraintAxis.None), "none");
            AssertFloat3(driven,
                BasisConstraintMath.MaskAxis(current, driven, (byte)BasisConstraintAxis.All), "all");
        }

        [Test]
        public void MaskEuler_OnlyTheMaskedAxisFollows()
        {
            quaternion current = quaternion.identity;
            quaternion driven = quaternion.Euler(math.radians(new float3(30f, 40f, 50f)));

            quaternion masked = BasisConstraintMath.MaskEuler(
                current, driven, (byte)BasisConstraintAxis.Y);
            float3 euler = math.degrees(BasisConstraintMath.ToEulerZXY(masked));

            Assert.AreEqual(0f, euler.x, 0.5f, "X stays put");
            Assert.AreEqual(40f, euler.y, 0.5f, "Y follows");
            Assert.AreEqual(0f, euler.z, 0.5f, "Z stays put");
        }

        // ── Euler conversion ──────────────────────────────────────────────────────────────────────

        [Test]
        public void EulerZXY_RoundTrips()
        {
            // ZXY specifically, because that is the order Unity's constraints mask in — a different
            // order would give subtly different results per axis rather than obviously wrong ones.
            float3 original = math.radians(new float3(25f, -40f, 15f));

            float3 round = BasisConstraintMath.ToEulerZXY(BasisConstraintMath.FromEulerZXY(original));

            AssertFloat3(original, round, "euler round trip");
        }

        // ── Parent space ──────────────────────────────────────────────────────────────────────────

        [Test]
        public void WorldToParent_UndoesTheParentTransform()
        {
            BasisConstraintWorld parent = new BasisConstraintWorld
            {
                Position = new float3(5f, 0f, 0f),
                Rotation = quaternion.AxisAngle(new float3(0f, 1f, 0f), math.PI * 0.5f),
                Scale = new float3(1f, 1f, 1f),
            };

            float3 local = BasisConstraintMath.WorldToParentPoint(parent, new float3(5f, 0f, -2f));

            AssertFloat3(new float3(2f, 0f, 0f), local,
                "a point two ahead of a parent turned 90 degrees reads as +2 on its own X");
        }

        [Test]
        public void WorldToParentRotation_UndoesTheParentRotation()
        {
            quaternion turn = quaternion.AxisAngle(new float3(0f, 1f, 0f), math.PI * 0.5f);
            BasisConstraintWorld parent = new BasisConstraintWorld
            {
                Position = float3.zero,
                Rotation = turn,
                Scale = new float3(1f, 1f, 1f),
            };

            quaternion local = BasisConstraintMath.WorldToParentRotation(parent, turn);

            Assert.Less(math.degrees(math.angle(local, quaternion.identity)), 0.5f,
                "matching the parent exactly reads as no local rotation at all");
        }

        // ── Guards ────────────────────────────────────────────────────────────────────────────────

        [Test]
        public void SafeScale_NeverReturnsZero()
        {
            float3 safe = BasisConstraintMath.SafeScale(new float3(0f, 2f, 0f));

            Assert.AreNotEqual(0f, safe.x, "a zero scale would divide the solve into infinities");
            Assert.AreNotEqual(0f, safe.z);
            Assert.AreEqual(2f, safe.y, Tolerance, "a usable component is left alone");
        }

        [Test]
        public void AimRotation_PointsTheLocalAimAlongTheDirection()
        {
            quaternion aim = BasisConstraintMath.AimRotation(
                new float3(1f, 0f, 0f), new float3(0f, 1f, 0f),
                new float3(0f, 0f, 1f), new float3(0f, 1f, 0f));

            float3 pointed = math.mul(aim, new float3(0f, 0f, 1f));

            AssertFloat3(new float3(1f, 0f, 0f), pointed, "local forward ends up along the aim direction");
        }

        [Test]
        public void AimRotation_SurvivesADegenerateDirection()
        {
            // A source sitting exactly on the target gives a zero-length direction; the result has to
            // stay a usable rotation rather than becoming NaN and poisoning everything downstream.
            quaternion aim = BasisConstraintMath.AimRotation(
                float3.zero, new float3(0f, 1f, 0f), new float3(0f, 0f, 1f), new float3(0f, 1f, 0f));

            Assert.IsFalse(float.IsNaN(aim.value.x), "no NaN escapes a degenerate aim");
            Assert.AreEqual(1f, math.length(aim.value), 1e-3f, "and it is still a unit quaternion");
        }
    }
}
