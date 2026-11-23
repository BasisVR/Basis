using NUnit.Framework;

namespace Basis.ImagePickup.Tests
{
    public sealed class BasisImagePickupLinkProbeTests
    {
        private const float Quiet = 20f;
        private const float Interval = BasisImagePickupSettings.LinkProbeIntervalSeconds;

        [SetUp]
        public void SetUp()
        {
            BasisImagePickupLinkProbe.Reset();
        }

        /// <summary>Leaves the probe with a quiet baseline and one full ramp step applied.</summary>
        private static void SettleQuietBaseline()
        {
            BasisImagePickupLinkProbe.Reset();
            BasisImagePickupLinkProbe.Observe(1f, Quiet, 0);
            BasisImagePickupLinkProbe.Observe(2f, Quiet, 0);
        }

        [Test]
        public void StartsAtTheAssumedBudgetUntilSomethingIsMeasured()
        {
            Assert.That(
                BasisImagePickupLinkProbe.DiscoveredUplinkBytesPerSecond,
                Is.EqualTo((float)BasisImagePickupSettings.StartingUplinkBudgetBytesPerSecond)
            );
        }

        [Test]
        public void TheFirstSampleOnlyEstablishesAStartingPoint()
        {
            BasisImagePickupLinkProbe.Observe(1f, Quiet, 0);

            Assert.That(
                BasisImagePickupLinkProbe.DiscoveredUplinkBytesPerSecond,
                Is.EqualTo((float)BasisImagePickupSettings.StartingUplinkBudgetBytesPerSecond)
            );
        }

        [Test]
        public void SamplesArrivingFasterThanTheControlIntervalAreIgnored()
        {
            BasisImagePickupLinkProbe.Observe(1f, Quiet, 0);
            float before = BasisImagePickupLinkProbe.DiscoveredUplinkBytesPerSecond;

            BasisImagePickupLinkProbe.Observe(1f + Interval * 0.5f, Quiet, 0);

            Assert.That(
                BasisImagePickupLinkProbe.DiscoveredUplinkBytesPerSecond,
                Is.EqualTo(before)
            );
        }

        [Test]
        public void AQuietLinkRampsTheRateUp()
        {
            BasisImagePickupLinkProbe.Observe(1f, Quiet, 0);
            BasisImagePickupLinkProbe.Observe(2f, Quiet, 0);

            Assert.That(BasisImagePickupLinkProbe.QueuingDelayMs, Is.EqualTo(0f));
            Assert.That(
                BasisImagePickupLinkProbe.DiscoveredUplinkBytesPerSecond,
                Is.EqualTo(
                        BasisImagePickupSettings.StartingUplinkBudgetBytesPerSecond
                            * (1f + BasisImagePickupSettings.LinkProbeRampFraction)
                    )
                    .Within(1f)
            );
        }

        [Test]
        public void AVeryFastLinkIsFoundInSecondsRatherThanMinutes()
        {
            BasisImagePickupLinkProbe.Observe(0f, Quiet, 0);
            for (float t = Interval; t <= 30f; t += Interval)
                BasisImagePickupLinkProbe.Observe(t, Quiet, 0);

            Assert.That(
                BasisImagePickupLinkProbe.DiscoveredUplinkBytesPerSecond,
                Is.EqualTo((float)BasisImagePickupSettings.MaxUplinkBudgetBytesPerSecond)
            );
        }

        [Test]
        public void TheRateClimbsByAFractionSoEveryScaleTakesTheSameNumberOfSteps()
        {
            SettleQuietBaseline();
            float low = BasisImagePickupLinkProbe.DiscoveredUplinkBytesPerSecond;
            BasisImagePickupLinkProbe.Observe(3f, Quiet, 0);
            float lowGrowth = BasisImagePickupLinkProbe.DiscoveredUplinkBytesPerSecond / low;

            for (float t = 4f; t <= 12f; t += 1f)
                BasisImagePickupLinkProbe.Observe(t, Quiet, 0);
            float high = BasisImagePickupLinkProbe.DiscoveredUplinkBytesPerSecond;
            Assume.That(high, Is.LessThan(BasisImagePickupSettings.MaxUplinkBudgetBytesPerSecond));
            BasisImagePickupLinkProbe.Observe(13f, Quiet, 0);
            float highGrowth = BasisImagePickupLinkProbe.DiscoveredUplinkBytesPerSecond / high;

            Assert.That(high, Is.GreaterThan(low * 10f));
            Assert.That(highGrowth, Is.EqualTo(lowGrowth).Within(0.001f));
        }

        [Test]
        public void TheBacklogThresholdScalesWithTheRateItIsJudging()
        {
            Assert.That(
                BasisImagePickupLinkProbe.BacklogLimit(1024f),
                Is.EqualTo(BasisImagePickupSettings.LinkProbeQueueBackoffPackets)
            );
            Assert.That(
                BasisImagePickupLinkProbe.BacklogLimit(64f * 1024f * 1024f),
                Is.GreaterThan(BasisImagePickupSettings.LinkProbeQueueBackoffPackets * 100)
            );
        }

        [Test]
        public void AHealthyFastTransferIsNotMistakenForABacklog()
        {
            // One interval of in-flight packets at a fast but healthy rate must not read as "behind".
            float fast = 32f * 1024f * 1024f;
            int inFlight = BasisImagePickupLinkProbe.BacklogLimit(fast) - 1;

            BasisImagePickupLinkProbe.Reset();
            BasisImagePickupLinkProbe.Observe(1f, Quiet, 0);
            for (float t = 2f; t <= 24f; t += 1f)
                BasisImagePickupLinkProbe.Observe(t, Quiet, 0);
            Assume.That(
                BasisImagePickupLinkProbe.DiscoveredUplinkBytesPerSecond,
                Is.GreaterThan(fast)
            );
            float before = BasisImagePickupLinkProbe.DiscoveredUplinkBytesPerSecond;

            BasisImagePickupLinkProbe.Observe(25f, Quiet, inFlight);

            Assert.That(
                BasisImagePickupLinkProbe.DiscoveredUplinkBytesPerSecond,
                Is.GreaterThanOrEqualTo(before)
            );
        }

        [Test]
        public void QueuingDelayPastTheTargetBacksTheRateOff()
        {
            BasisImagePickupLinkProbe.Observe(1f, Quiet, 0);
            BasisImagePickupLinkProbe.Observe(2f, Quiet, 0);
            float peak = BasisImagePickupLinkProbe.DiscoveredUplinkBytesPerSecond;

            BasisImagePickupLinkProbe.Observe(
                3f,
                Quiet + BasisImagePickupSettings.TargetQueuingDelayMs * 4f,
                0
            );

            Assert.That(
                BasisImagePickupLinkProbe.QueuingDelayMs,
                Is.EqualTo(BasisImagePickupSettings.TargetQueuingDelayMs * 4f).Within(0.001f)
            );
            Assert.That(
                BasisImagePickupLinkProbe.DiscoveredUplinkBytesPerSecond,
                Is.LessThan(peak)
            );
        }

        [Test]
        public void DelayUnderTheTargetStillRampsUpButMoreSlowly()
        {
            // The baseline is only established by the first effective sample, so both runs need a settled
            // quiet baseline before the step under test or they would both read zero queuing delay.
            SettleQuietBaseline();
            float start = BasisImagePickupLinkProbe.DiscoveredUplinkBytesPerSecond;
            BasisImagePickupLinkProbe.Observe(
                3f,
                Quiet + BasisImagePickupSettings.TargetQueuingDelayMs * 0.5f,
                0
            );
            float gentle = BasisImagePickupLinkProbe.DiscoveredUplinkBytesPerSecond - start;

            SettleQuietBaseline();
            BasisImagePickupLinkProbe.Observe(3f, Quiet, 0);
            float full = BasisImagePickupLinkProbe.DiscoveredUplinkBytesPerSecond - start;

            Assert.That(gentle, Is.GreaterThan(0f));
            Assert.That(gentle, Is.LessThan(full));
        }

        [Test]
        public void ABackedUpSendQueueHalvesTheRateInsteadOfRamping()
        {
            SettleQuietBaseline();
            float ramped = BasisImagePickupLinkProbe.DiscoveredUplinkBytesPerSecond;

            BasisImagePickupLinkProbe.Reset();
            BasisImagePickupLinkProbe.Observe(1f, Quiet, 0);
            BasisImagePickupLinkProbe.Observe(
                2f,
                Quiet,
                BasisImagePickupSettings.LinkProbeQueueBackoffPackets
            );

            Assert.That(
                BasisImagePickupLinkProbe.DiscoveredUplinkBytesPerSecond,
                Is.EqualTo(
                        BasisImagePickupSettings.StartingUplinkBudgetBytesPerSecond
                            * BasisImagePickupSettings.LinkProbeQueueBackoffFactor
                    )
                    .Within(1f)
            );
            Assert.That(
                BasisImagePickupLinkProbe.DiscoveredUplinkBytesPerSecond,
                Is.LessThan(ramped)
            );
        }

        [Test]
        public void SustainedQueuingDelayDrivesTheRateToItsFloor()
        {
            SettleQuietBaseline();

            for (int i = 3; i < 30; i++)
                BasisImagePickupLinkProbe.Observe(i, Quiet + 500f, 0);

            Assert.That(
                BasisImagePickupLinkProbe.DiscoveredUplinkBytesPerSecond,
                Is.EqualTo((float)BasisImagePickupSettings.MinUplinkBudgetBytesPerSecond)
            );
        }

        [Test]
        public void ATransfersOwnQueuingNeverBecomesTheBaselineItIsMeasuredAgainst()
        {
            SettleQuietBaseline();

            // Well past a single slot, and past the old single-expiry design's whole window: the quiet
            // minimum has to survive as long as any slot still remembers it, or the probe would decide its
            // own congestion was the new normal and ramp straight back into it.
            float slot = BasisImagePickupSettings.LinkProbeBaselineWindowSeconds / 4f;
            for (float t = 3f; t <= 2f + slot * 3f; t += 1f)
                BasisImagePickupLinkProbe.Observe(t, Quiet + 500f, 0);

            Assert.That(BasisImagePickupLinkProbe.BaseRoundTripMs, Is.EqualTo(Quiet));
            Assert.That(BasisImagePickupLinkProbe.QueuingDelayMs, Is.EqualTo(500f));
        }

        [Test]
        public void TheRateNeverLeavesItsFloorOrCeiling()
        {
            for (int i = 1; i < 200; i++)
            {
                BasisImagePickupLinkProbe.Observe(
                    i,
                    Quiet,
                    BasisImagePickupSettings.LinkProbeQueueBackoffPackets
                );
            }

            Assert.That(
                BasisImagePickupLinkProbe.DiscoveredUplinkBytesPerSecond,
                Is.EqualTo((float)BasisImagePickupSettings.MinUplinkBudgetBytesPerSecond)
            );

            BasisImagePickupLinkProbe.Reset();
            for (int i = 1; i < 200; i++)
                BasisImagePickupLinkProbe.Observe(i, Quiet, 0);

            Assert.That(
                BasisImagePickupLinkProbe.DiscoveredUplinkBytesPerSecond,
                Is.EqualTo((float)BasisImagePickupSettings.MaxUplinkBudgetBytesPerSecond)
            );
        }

        [Test]
        public void APermanentlySlowerPathReArmsTheBaselineInsteadOfBackingOffForever()
        {
            SettleQuietBaseline();

            float slot = BasisImagePickupSettings.LinkProbeBaselineWindowSeconds / 4f;
            for (float t = 3f; t <= 2f + slot * 5f; t += 1f)
                BasisImagePickupLinkProbe.Observe(t, 200f, 0);

            Assert.That(BasisImagePickupLinkProbe.BaseRoundTripMs, Is.EqualTo(200f));
            Assert.That(BasisImagePickupLinkProbe.QueuingDelayMs, Is.EqualTo(0f));
        }

        [Test]
        public void AQuieterRoundTripImmediatelyBecomesTheNewBaseline()
        {
            BasisImagePickupLinkProbe.Observe(1f, 80f, 0);
            BasisImagePickupLinkProbe.Observe(2f, 80f, 0);
            BasisImagePickupLinkProbe.Observe(3f, 30f, 0);

            Assert.That(BasisImagePickupLinkProbe.BaseRoundTripMs, Is.EqualTo(30f));
            Assert.That(BasisImagePickupLinkProbe.QueuingDelayMs, Is.EqualTo(0f));
        }

        [Test]
        public void AnIdleLinkHoldsItsRateInsteadOfRampingToTheCeiling()
        {
            BasisImagePickupLinkProbe.Observe(0f, Quiet, 0, false);
            for (float t = Interval; t <= 30f; t += Interval)
            {
                BasisImagePickupLinkProbe.Observe(t, Quiet, 0, false);
            }

            Assert.That(
                BasisImagePickupLinkProbe.DiscoveredUplinkBytesPerSecond,
                Is.EqualTo((float)BasisImagePickupSettings.StartingUplinkBudgetBytesPerSecond)
            );
        }

        [Test]
        public void AStandingBacklogHalvesTheRateOnceRatherThanEverySample()
        {
            BasisImagePickupLinkProbe.Observe(0f, Quiet, 0);
            for (float t = 1f; t <= 8f; t += 1f)
            {
                BasisImagePickupLinkProbe.Observe(t, Quiet, 0);
            }
            float before = BasisImagePickupLinkProbe.DiscoveredUplinkBytesPerSecond;
            int backlog = BasisImagePickupLinkProbe.BacklogLimit(before);

            for (float t = 9f; t <= 14f; t += 1f)
            {
                BasisImagePickupLinkProbe.Observe(t, Quiet, backlog);
            }

            Assert.That(
                BasisImagePickupLinkProbe.DiscoveredUplinkBytesPerSecond,
                Is.EqualTo(before * BasisImagePickupSettings.LinkProbeQueueBackoffFactor).Within(1f)
            );
        }

        [Test]
        public void AGrowingBacklogKeepsBackingOff()
        {
            BasisImagePickupLinkProbe.Observe(0f, Quiet, 0);
            for (float t = 1f; t <= 8f; t += 1f)
            {
                BasisImagePickupLinkProbe.Observe(t, Quiet, 0);
            }
            float before = BasisImagePickupLinkProbe.DiscoveredUplinkBytesPerSecond;
            int backlog = BasisImagePickupLinkProbe.BacklogLimit(before);

            BasisImagePickupLinkProbe.Observe(9f, Quiet, backlog);
            BasisImagePickupLinkProbe.Observe(10f, Quiet, backlog * 2);

            float factor = BasisImagePickupSettings.LinkProbeQueueBackoffFactor;
            Assert.That(
                BasisImagePickupLinkProbe.DiscoveredUplinkBytesPerSecond,
                Is.EqualTo(before * factor * factor).Within(1f)
            );
        }

        [Test]
        public void OneLateRoundTripBrakesLessThanAQuietOneRamps()
        {
            SettleQuietBaseline();
            float start = BasisImagePickupLinkProbe.DiscoveredUplinkBytesPerSecond;
            BasisImagePickupLinkProbe.Observe(3f, Quiet + 1500f, 0);
            float braked = BasisImagePickupLinkProbe.DiscoveredUplinkBytesPerSecond;

            SettleQuietBaseline();
            BasisImagePickupLinkProbe.Observe(3f, Quiet, 0);
            float ramped = BasisImagePickupLinkProbe.DiscoveredUplinkBytesPerSecond;

            Assert.That(start / braked, Is.LessThan(ramped / start));
            Assert.That(braked, Is.GreaterThan(start * 0.5f));
        }

        [Test]
        public void TheImageShareIsHalfOfWhateverTheProbeDiscovered()
        {
            BasisImagePickupLinkProbe.Observe(1f, Quiet, 0);
            BasisImagePickupLinkProbe.Observe(2f, Quiet, 0);

            Assert.That(
                BasisImagePickupBandwidth.UplinkBytesPerSecond,
                Is.EqualTo(BasisImagePickupLinkProbe.DiscoveredUplinkBytesPerSecond * 0.5)
                    .Within(0.001)
            );
        }
    }
}
