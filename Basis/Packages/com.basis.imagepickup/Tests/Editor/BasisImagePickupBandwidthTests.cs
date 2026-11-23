using NUnit.Framework;

namespace Basis.ImagePickup.Tests
{
    public sealed class BasisImagePickupBandwidthTests
    {
        private const int Chunk = BasisImagePickupSettings.ChunkPayloadBytes;

        [SetUp]
        public void SetUp()
        {
            BasisImagePickupLinkProbe.Reset();
            BasisImagePickupBandwidth.Reset();
        }

        [Test]
        public void TheUplinkGetsHalfOfTheMeasuredLineAndTheRelayFallsBackUntilAServerSpeaks()
        {
            Assert.That(
                BasisImagePickupBandwidth.UplinkBytesPerSecond,
                Is.EqualTo(BasisImagePickupSettings.StartingUplinkBudgetBytesPerSecond * 0.5)
                    .Within(0.001)
            );
            Assert.That(
                BasisImagePickupBandwidth.RelayBytesPerSecond,
                Is.EqualTo((double)BasisImagePickupSettings.RelayEgressBudgetBytesPerSecond)
                    .Within(0.001)
            );
        }

        [Test]
        public void AnAdvertisedBudgetReplacesTheFallbackAndIsSpentInFull()
        {
            // Taken at face value rather than halved: the operator who configured it has already decided
            // what their pipe is worth, and discounting it again would quietly hand them half.
            BasisImagePickupBandwidth.ServerRelayBudgetBytesPerSecond = 25_000_000L;

            Assert.That(
                BasisImagePickupBandwidth.RelayBytesPerSecond,
                Is.EqualTo(25_000_000d).Within(0.001)
            );
        }

        [Test]
        public void LeavingAnInstanceForgetsWhatTheLastServerAdvertised()
        {
            BasisImagePickupBandwidth.ServerRelayBudgetBytesPerSecond = 25_000_000L;

            BasisImagePickupBandwidth.Reset();

            Assert.That(
                BasisImagePickupBandwidth.RelayBytesPerSecond,
                Is.EqualTo((double)BasisImagePickupSettings.RelayEgressBudgetBytesPerSecond)
                    .Within(0.001)
            );
        }

        [Test]
        public void ASharerGetsTheAdvertisedBudgetDividedByTheFanOut()
        {
            RampProbePastTheRelayBudget();

            double solo = SustainedPayloadBytesPerSecond(1, 25_000_000L);
            double crowded = SustainedPayloadBytesPerSecond(20, 25_000_000L);

            Assert.That(solo, Is.EqualTo(25_000_000d).Within(25_000_000d * 0.05));
            Assert.That(crowded, Is.EqualTo(1_250_000d).Within(1_250_000d * 0.05));
        }

        [Test]
        public void AnAdvertisedBudgetMovesAnImageInSecondsWhereTheFallbackTookMinutes()
        {
            // The complaint this whole path exists to answer: a twenty-player instance on the built-in
            // guess crawled, and no amount of pipe at either end changed it, because the guess never
            // asked anyone. Four megabytes is an ordinary screenshot.
            const int Image = 4 * 1024 * 1024;
            const int FanOut = 20;

            RampProbePastTheRelayBudget();

            double fallbackSeconds = SecondsToSend(Image, FanOut, 0L);
            double advertisedSeconds = SecondsToSend(Image, FanOut, 25_000_000L);

            Assert.That(fallbackSeconds, Is.GreaterThan(120d));
            Assert.That(advertisedSeconds, Is.LessThan(5d));
        }

        /// <summary>
        /// Idles the probe up to its ceiling so the uplink bucket is not what these measurements land on.
        /// Live it gets there the same way, by ramping on a quiet link while transfers are bucket-limited.
        /// </summary>
        private static void RampProbePastTheRelayBudget()
        {
            float now = 0f;
            for (int step = 0; step < 200; step++)
            {
                now += BasisImagePickupSettings.LinkProbeIntervalSeconds;
                BasisImagePickupLinkProbe.Observe(now, 10f, 0);
            }
        }

        /// <summary>Rate a relayed transfer settles at, in payload bytes per second, over ten seconds.</summary>
        private static double SustainedPayloadBytesPerSecond(int relayRecipients, long advertisedBudget)
        {
            const float Step = 1f / 60f;
            const int Steps = 600;

            BasisImagePickupBandwidth.Reset();
            BasisImagePickupBandwidth.ServerRelayBudgetBytesPerSecond = advertisedBudget;
            double delivered = 0d;
            for (int step = 0; step < Steps; step++)
            {
                BasisImagePickupBandwidth.Refill(Step);
                while (BasisImagePickupBandwidth.TryConsume(Chunk, 0, relayRecipients))
                    delivered += Chunk;
            }
            return delivered / (Steps * Step);
        }

        /// <summary>How long one image of <paramref name="payloadBytes"/> takes at a given fan-out.</summary>
        private static double SecondsToSend(int payloadBytes, int relayRecipients, long advertisedBudget)
        {
            const float Step = 1f / 60f;
            const int MaxSteps = 60 * 60 * 30;

            BasisImagePickupBandwidth.Reset();
            BasisImagePickupBandwidth.ServerRelayBudgetBytesPerSecond = advertisedBudget;
            double sent = 0d;
            for (int step = 0; step < MaxSteps; step++)
            {
                BasisImagePickupBandwidth.Refill(Step);
                while (sent < payloadBytes && BasisImagePickupBandwidth.TryConsume(Chunk, 0, relayRecipients))
                    sent += Chunk;
                if (sent >= payloadBytes)
                    return (step + 1) * Step;
            }
            return double.PositiveInfinity;
        }

        [Test]
        public void RelayedSendCostsUplinkOnceAndRelayOncePerRecipient()
        {
            double uplinkBefore = BasisImagePickupBandwidth.UplinkTokens;
            double relayBefore = BasisImagePickupBandwidth.RelayTokens;

            Assert.That(BasisImagePickupBandwidth.TryConsume(1000, 0, 8), Is.True);

            Assert.That(
                uplinkBefore - BasisImagePickupBandwidth.UplinkTokens,
                Is.EqualTo(1000d).Within(0.001)
            );
            Assert.That(
                relayBefore - BasisImagePickupBandwidth.RelayTokens,
                Is.EqualTo(8000d).Within(0.001)
            );
        }

        [Test]
        public void DirectSendCostsUplinkPerPeerAndNoRelay()
        {
            double uplinkBefore = BasisImagePickupBandwidth.UplinkTokens;
            double relayBefore = BasisImagePickupBandwidth.RelayTokens;

            Assert.That(BasisImagePickupBandwidth.TryConsume(1000, 3, 0), Is.True);

            Assert.That(
                uplinkBefore - BasisImagePickupBandwidth.UplinkTokens,
                Is.EqualTo(3000d).Within(0.001)
            );
            Assert.That(BasisImagePickupBandwidth.RelayTokens, Is.EqualTo(relayBefore));
        }

        [Test]
        public void MixedSendChargesOneRelayCopyPlusEachDirectPeer()
        {
            double uplinkBefore = BasisImagePickupBandwidth.UplinkTokens;

            Assert.That(BasisImagePickupBandwidth.TryConsume(500, 2, 4), Is.True);

            Assert.That(
                uplinkBefore - BasisImagePickupBandwidth.UplinkTokens,
                Is.EqualTo(1500d).Within(0.001)
            );
            Assert.That(
                BasisImagePickupBandwidth.RelayCapacityBytes
                    - BasisImagePickupBandwidth.RelayTokens,
                Is.EqualTo(2000d).Within(0.001)
            );
        }

        [Test]
        public void AnExhaustedRelayBucketStillLetsDirectPeersTransfer()
        {
            // Wide but small sends, so the relay bucket empties while the uplink bucket — which a relayed
            // send only ever charges one copy against — still has room. That separation is the whole point:
            // being unable to afford the server's fan-out says nothing about a direct link.
            while (BasisImagePickupBandwidth.RelayTokens > 0d)
            {
                Assert.That(BasisImagePickupBandwidth.TryConsume(64, 0, 64), Is.True);
            }

            Assert.That(BasisImagePickupBandwidth.UplinkTokens, Is.GreaterThan(0d));
            Assert.That(BasisImagePickupBandwidth.TryConsume(64, 0, 1), Is.False);
            Assert.That(BasisImagePickupBandwidth.TryConsume(64, 1, 0), Is.True);
        }

        [Test]
        public void SendsStopWhenTheUplinkBucketIsSpentAndResumeAfterRefill()
        {
            while (BasisImagePickupBandwidth.UplinkTokens > 0d)
            {
                Assert.That(BasisImagePickupBandwidth.TryConsume(Chunk, 1, 0), Is.True);
            }

            Assert.That(BasisImagePickupBandwidth.TryConsume(Chunk, 1, 0), Is.False);

            BasisImagePickupBandwidth.Refill(1f);

            Assert.That(BasisImagePickupBandwidth.TryConsume(Chunk, 1, 0), Is.True);
        }

        [Test]
        public void RefillCreditsTheConfiguredRateAndClampsToTheBurstWindow()
        {
            BasisImagePickupBandwidth.TryConsume(Chunk, 1, 0);
            double spent = BasisImagePickupBandwidth.UplinkCapacityBytes
                - BasisImagePickupBandwidth.UplinkTokens;
            Assert.That(spent, Is.GreaterThan(0d));

            BasisImagePickupBandwidth.Refill(0.001f);
            Assert.That(
                BasisImagePickupBandwidth.UplinkTokens,
                Is.EqualTo(
                        BasisImagePickupBandwidth.UplinkCapacityBytes
                            - spent
                            + BasisImagePickupBandwidth.UplinkBytesPerSecond * 0.001d
                    )
                    .Within(0.01)
            );

            BasisImagePickupBandwidth.Refill(600f);
            Assert.That(
                BasisImagePickupBandwidth.UplinkTokens,
                Is.EqualTo(BasisImagePickupBandwidth.UplinkCapacityBytes).Within(0.001)
            );
            Assert.That(
                BasisImagePickupBandwidth.RelayTokens,
                Is.EqualTo(BasisImagePickupBandwidth.RelayCapacityBytes).Within(0.001)
            );
        }

        [Test]
        public void APacketLargerThanTheBucketStillSendsAndIsRepaidBeforeTheNextOne()
        {
            int oversized = (int)BasisImagePickupBandwidth.RelayCapacityBytes * 4;

            Assert.That(BasisImagePickupBandwidth.TryConsume(oversized, 0, 1), Is.True);
            Assert.That(BasisImagePickupBandwidth.RelayTokens, Is.LessThan(0d));
            Assert.That(BasisImagePickupBandwidth.TryConsume(Chunk, 0, 1), Is.False);

            BasisImagePickupBandwidth.Refill(3600f);

            Assert.That(BasisImagePickupBandwidth.TryConsume(Chunk, 0, 1), Is.True);
        }

        [Test]
        public void SendsWithNoRecipientsAreFree()
        {
            double uplinkBefore = BasisImagePickupBandwidth.UplinkTokens;

            Assert.That(BasisImagePickupBandwidth.TryConsume(Chunk, 0, 0), Is.True);

            Assert.That(BasisImagePickupBandwidth.UplinkTokens, Is.EqualTo(uplinkBefore));
        }
    }
}
