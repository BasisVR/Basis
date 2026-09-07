using NUnit.Framework;
using Unity.Collections;

namespace Basis.Tests.Performance
{
    /// <summary>
    /// Jiggle is the dominant per-frame cost in a crowded instance, and two independent LOD systems
    /// trim it by distance: the collider tier other chains bounce off, and whether a remote's rigs
    /// simulate at all. Both decisions were moved into a Burst job so they run alongside the
    /// transmit tick instead of inline, and the job's own summary says its math is kept in sync
    /// BY HAND with <see cref="BasisJiggleColliderLOD.ComputeTier"/> and
    /// <see cref="BasisJiggleSimulationLOD.ShouldSimulate"/>.
    ///
    /// Hand-synced duplicates drift. These tests sweep both implementations over the same distances
    /// and starting states and require identical answers, so a change to one that misses the other
    /// fails here rather than showing up as jiggle popping on and off in a busy instance.
    /// </summary>
    public class BasisJiggleLodJobTests
    {
        bool savedColliderEnabled, savedSimulationEnabled;
        float savedNear, savedMid, savedFar, savedCutoff;

        [SetUp]
        public void CaptureModuleState()
        {
            savedColliderEnabled = BasisJiggleColliderLOD.Enabled;
            savedNear = BasisJiggleColliderLOD._nearSqr;
            savedMid = BasisJiggleColliderLOD._midSqr;
            savedFar = BasisJiggleColliderLOD._farSqr;
            savedSimulationEnabled = BasisJiggleSimulationLOD.Enabled;
            savedCutoff = BasisJiggleSimulationLOD._cutoffSqr;

            BasisJiggleColliderLOD.Enabled = true;
            BasisJiggleColliderLOD._nearSqr = 25f * 25f;
            BasisJiggleColliderLOD._midSqr = 50f * 50f;
            BasisJiggleColliderLOD._farSqr = 100f * 100f;
            BasisJiggleSimulationLOD.Enabled = true;
            BasisJiggleSimulationLOD._cutoffSqr = 120f * 120f;
        }

        [TearDown]
        public void RestoreModuleState()
        {
            BasisJiggleColliderLOD.Enabled = savedColliderEnabled;
            BasisJiggleColliderLOD._nearSqr = savedNear;
            BasisJiggleColliderLOD._midSqr = savedMid;
            BasisJiggleColliderLOD._farSqr = savedFar;
            BasisJiggleSimulationLOD.Enabled = savedSimulationEnabled;
            BasisJiggleSimulationLOD._cutoffSqr = savedCutoff;
        }

        /// <summary>Runs the job over one receiver, wired exactly the way ScheduleTick wires it.</summary>
        static (BasisJiggleColliderTier Tier, bool Simulate) RunJob(
            float distanceSq,
            BasisJiggleColliderTier currentTier,
            bool currentlySimulating,
            bool hasColliders = true,
            bool hasRigs = true)
        {
            NativeArray<float> distances = new NativeArray<float>(1, Allocator.Temp);
            NativeArray<bool> colliders = new NativeArray<bool>(1, Allocator.Temp);
            NativeArray<BasisJiggleColliderTier> currentTiers = new NativeArray<BasisJiggleColliderTier>(1, Allocator.Temp);
            NativeArray<bool> rigs = new NativeArray<bool>(1, Allocator.Temp);
            NativeArray<bool> simulating = new NativeArray<bool>(1, Allocator.Temp);
            NativeArray<BasisJiggleColliderTier> targetTiers = new NativeArray<BasisJiggleColliderTier>(1, Allocator.Temp);
            NativeArray<bool> targetSimulate = new NativeArray<bool>(1, Allocator.Temp);
            try
            {
                distances[0] = distanceSq;
                colliders[0] = hasColliders;
                currentTiers[0] = currentTier;
                rigs[0] = hasRigs;
                simulating[0] = currentlySimulating;

                new BasisJiggleLodJob
                {
                    ColliderLodEnabled = BasisJiggleColliderLOD.Enabled,
                    NearSqr = BasisJiggleColliderLOD._nearSqr,
                    MidSqr = BasisJiggleColliderLOD._midSqr,
                    FarSqr = BasisJiggleColliderLOD._farSqr,
                    ColliderHysteresisSqr = BasisJiggleColliderLOD.HysteresisSqr,
                    SimulationLodEnabled = BasisJiggleSimulationLOD.Enabled,
                    SimCutoffSqr = BasisJiggleSimulationLOD._cutoffSqr,
                    SimHysteresisSqr = BasisJiggleSimulationLOD.HysteresisSqr,
                    distanceSq = distances,
                    HasJiggleColliders = colliders,
                    CurrentColliderTier = currentTiers,
                    HasJiggleRigs = rigs,
                    CurrentlySimulating = simulating,
                    TargetColliderTier = targetTiers,
                    TargetShouldSimulate = targetSimulate,
                }.Execute(0);

                return (targetTiers[0], targetSimulate[0]);
            }
            finally
            {
                distances.Dispose();
                colliders.Dispose();
                currentTiers.Dispose();
                rigs.Dispose();
                simulating.Dispose();
                targetTiers.Dispose();
                targetSimulate.Dispose();
            }
        }

        static readonly float[] SweepDistances =
        {
            0f, 1f, 100f, 500f, 625f, 700f, 756f, 800f, 1500f, 2500f, 3000f, 3025f, 3100f,
            5000f, 9000f, 10_000f, 12_100f, 13_000f, 14_400f, 17_424f, 20_000f, 1_000_000f,
        };

        static readonly BasisJiggleColliderTier[] AllTiers =
        {
            BasisJiggleColliderTier.Full,
            BasisJiggleColliderTier.NoFingers,
            BasisJiggleColliderTier.HandsOnly,
            BasisJiggleColliderTier.None,
        };

        // ── parity ────────────────────────────────────────────────────────────

        [Test]
        public void ColliderTierMatchesTheManagedModuleEverywhere()
        {
            foreach (BasisJiggleColliderTier current in AllTiers)
            {
                foreach (float distanceSq in SweepDistances)
                {
                    BasisJiggleColliderTier expected = BasisJiggleColliderLOD.ComputeTier(distanceSq, current);
                    BasisJiggleColliderTier actual = RunJob(distanceSq, current, true).Tier;
                    Assert.That(actual, Is.EqualTo(expected),
                        $"squared distance {distanceSq} starting from {current}");
                }
            }
        }

        [Test]
        public void ShouldSimulateMatchesTheManagedModuleEverywhere()
        {
            foreach (bool simulating in new[] { false, true })
            {
                foreach (float distanceSq in SweepDistances)
                {
                    bool expected = BasisJiggleSimulationLOD.ShouldSimulate(distanceSq, simulating);
                    bool actual = RunJob(distanceSq, BasisJiggleColliderTier.Full, simulating).Simulate;
                    Assert.That(actual, Is.EqualTo(expected),
                        $"squared distance {distanceSq} while simulating={simulating}");
                }
            }
        }

        [Test]
        public void ParityHoldsAfterTheSlidersMove()
        {
            // The thresholds are copied into the job every tick, so a user dragging the
            // Performance Limits sliders must not desynchronise the two implementations.
            BasisJiggleColliderLOD._nearSqr = 3f * 3f;
            BasisJiggleColliderLOD._midSqr = 7f * 7f;
            BasisJiggleColliderLOD._farSqr = 11f * 11f;
            BasisJiggleSimulationLOD._cutoffSqr = 15f * 15f;

            for (float distance = 0f; distance <= 20f; distance += 0.25f)
            {
                float distanceSq = distance * distance;
                foreach (BasisJiggleColliderTier current in AllTiers)
                {
                    Assert.That(RunJob(distanceSq, current, true).Tier,
                        Is.EqualTo(BasisJiggleColliderLOD.ComputeTier(distanceSq, current)),
                        $"tier at {distance} m from {current}");
                }
                Assert.That(RunJob(distanceSq, BasisJiggleColliderTier.Full, false).Simulate,
                    Is.EqualTo(BasisJiggleSimulationLOD.ShouldSimulate(distanceSq, false)),
                    $"simulate at {distance} m");
            }
        }

        // ── banding ───────────────────────────────────────────────────────────

        [Test]
        public void CloseAvatarsKeepEveryCollider()
        {
            Assert.That(RunJob(1f, BasisJiggleColliderTier.Full, true).Tier,
                Is.EqualTo(BasisJiggleColliderTier.Full));
        }

        [Test]
        public void TheTiersStepDownWithDistance()
        {
            // Comfortably inside each band so the hysteresis margin is not what is being read.
            Assert.That(RunJob(10f * 10f, BasisJiggleColliderTier.Full, true).Tier,
                Is.EqualTo(BasisJiggleColliderTier.Full));
            Assert.That(RunJob(35f * 35f, BasisJiggleColliderTier.Full, true).Tier,
                Is.EqualTo(BasisJiggleColliderTier.NoFingers), "finger colliders go first: nobody can see them at 35 m.");
            Assert.That(RunJob(70f * 70f, BasisJiggleColliderTier.Full, true).Tier,
                Is.EqualTo(BasisJiggleColliderTier.HandsOnly));
            Assert.That(RunJob(200f * 200f, BasisJiggleColliderTier.Full, true).Tier,
                Is.EqualTo(BasisJiggleColliderTier.None));
        }

        [Test]
        public void ABoundaryHoverDoesNotChurnTheColliderSet()
        {
            // 25 m is the near boundary; a player oscillating around it must not have its
            // finger colliders added and removed every tick, which costs more than keeping them.
            float justOver = 26f * 26f;

            Assert.That(RunJob(justOver, BasisJiggleColliderTier.Full, true).Tier,
                Is.EqualTo(BasisJiggleColliderTier.Full),
                "crossing out needs to clear the boundary by the hysteresis margin.");
            Assert.That(RunJob(justOver, BasisJiggleColliderTier.NoFingers, true).Tier,
                Is.EqualTo(BasisJiggleColliderTier.NoFingers),
                "and coming back needs to clear it the other way.");
        }

        [Test]
        public void SimulationPausesPastTheCutoff()
        {
            Assert.That(RunJob(50f * 50f, BasisJiggleColliderTier.Full, true).Simulate, Is.True);
            Assert.That(RunJob(300f * 300f, BasisJiggleColliderTier.Full, true).Simulate, Is.False);
        }

        [Test]
        public void SimulationCutoffHasItsOwnHysteresis()
        {
            // Re-enabling a rig reseeds it from the current pose, which is visible; the
            // hysteresis is what stops that happening every tick on the boundary.
            float justOver = 125f * 125f;
            Assert.That(RunJob(justOver, BasisJiggleColliderTier.Full, currentlySimulating: true).Simulate, Is.True);
            Assert.That(RunJob(justOver, BasisJiggleColliderTier.Full, currentlySimulating: false).Simulate, Is.False);
        }

        // ── disabled and non-applicable receivers ─────────────────────────────

        [Test]
        public void ColliderLodDisabled_ReportsTheFullSet()
        {
            BasisJiggleColliderLOD.Enabled = false;
            Assert.That(RunJob(10_000f, BasisJiggleColliderTier.None, true).Tier,
                Is.EqualTo(BasisJiggleColliderTier.Full));
            Assert.That(BasisJiggleColliderLOD.ComputeTier(10_000f, BasisJiggleColliderTier.None),
                Is.EqualTo(BasisJiggleColliderTier.Full));
        }

        [Test]
        public void SimulationLodDisabled_ReportsSimulating()
        {
            BasisJiggleSimulationLOD.Enabled = false;
            Assert.That(RunJob(1_000_000f, BasisJiggleColliderTier.Full, currentlySimulating: false).Simulate, Is.True);
            Assert.That(BasisJiggleSimulationLOD.ShouldSimulate(1_000_000f, false), Is.True);
        }

        [Test]
        public void ReceiversWithNothingToTrim_KeepTheirCurrentValues()
        {
            // The job is scheduled unconditionally, so a receiver with no jiggle at all has to
            // come back as a copy-through rather than as a decision the caller might act on.
            (BasisJiggleColliderTier Tier, bool Simulate) result =
                RunJob(1_000_000f, BasisJiggleColliderTier.HandsOnly, true, hasColliders: false, hasRigs: false);

            Assert.That(result.Tier, Is.EqualTo(BasisJiggleColliderTier.HandsOnly));
            Assert.That(result.Simulate, Is.True);
        }

        [Test]
        public void ACollidersOnlyReceiver_StillGetsASimulationDecision()
        {
            (BasisJiggleColliderTier Tier, bool Simulate) result =
                RunJob(300f * 300f, BasisJiggleColliderTier.Full, true, hasColliders: true, hasRigs: false);

            Assert.That(result.Tier, Is.EqualTo(BasisJiggleColliderTier.None));
            Assert.That(result.Simulate, Is.True, "no rigs means nothing to pause, so the current value comes back.");
        }

        [Test]
        public void ActiveCategoriesNarrowMonotonically()
        {
            // The tiers are only worth anything if each one is a strict subset of the last.
            bool[] full = BasisJiggleColliderLOD.ActiveCategories(BasisJiggleColliderTier.Full);
            bool[] noFingers = BasisJiggleColliderLOD.ActiveCategories(BasisJiggleColliderTier.NoFingers);
            bool[] handsOnly = BasisJiggleColliderLOD.ActiveCategories(BasisJiggleColliderTier.HandsOnly);
            bool[] none = BasisJiggleColliderLOD.ActiveCategories(BasisJiggleColliderTier.None);

            for (int index = 0; index < 4; index++)
            {
                Assert.That(!noFingers[index] || full[index], Is.True, $"category {index}");
                Assert.That(!handsOnly[index] || noFingers[index], Is.True, $"category {index}");
                Assert.That(!none[index] || handsOnly[index], Is.True, $"category {index}");
            }
            Assert.That(none, Is.EqualTo(new[] { false, false, false, false }));
            Assert.That(full, Is.EqualTo(new[] { true, true, true, true }));
        }
    }
}
