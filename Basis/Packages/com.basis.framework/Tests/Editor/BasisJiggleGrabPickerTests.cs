using Basis.Scripts.BasisSdk.Interactions;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Interactions
{
    /// <summary>
    /// Pins how a grab press chooses its jiggle point. Every reported "it grabbed the wrong one"
    /// has reduced to this geometry, so the rules are stated as cases rather than left implicit in
    /// the driver: a hand takes what lies across its fingers, and a point takes the nearest thing
    /// actually aimed at.
    ///
    /// Distances are in metres and sized like a real hand — the pick radius in the driver is
    /// ~0.09 m, a palm-to-fingertip span is ~0.09 m.
    /// </summary>
    public class BasisJiggleGrabPickerTests
    {
        private const float Radius = 0.09f;
        private const float Tolerance = 1e-4f;

        private static readonly Vector3 Palm = new Vector3(0f, 1.2f, 0.3f);
        private static readonly Vector3 FingerTip = new Vector3(0f, 1.2f, 0.39f);

        // ── distance to the grip span ───────────────────────────────────────

        [Test]
        public void DistanceToSegment_AtAnEndpoint_IsZero()
        {
            Assert.AreEqual(0f, BasisJiggleGrabPicker.DistanceToSegment(Palm, Palm, FingerTip), Tolerance);
            Assert.AreEqual(0f, BasisJiggleGrabPicker.DistanceToSegment(FingerTip, Palm, FingerTip), Tolerance);
        }

        [Test]
        public void DistanceToSegment_BesideTheMiddle_IsThePerpendicular()
        {
            Vector3 beside = (Palm + FingerTip) * 0.5f + Vector3.up * 0.04f;

            Assert.AreEqual(0.04f, BasisJiggleGrabPicker.DistanceToSegment(beside, Palm, FingerTip), Tolerance);
        }

        [Test]
        public void DistanceToSegment_PastAnEnd_MeasuresFromThatEnd_NotTheInfiniteLine()
        {
            // Straight out beyond the fingertips: a strand there is out of the hand, even though an
            // infinite line through the fingers would pass right through it.
            Vector3 beyond = FingerTip + (FingerTip - Palm).normalized * 0.5f;

            Assert.AreEqual(0.5f, BasisJiggleGrabPicker.DistanceToSegment(beyond, Palm, FingerTip), Tolerance);
        }

        [Test]
        public void DistanceToSegment_WithADegenerateSpan_FallsBackToPointDistance()
        {
            Assert.AreEqual(0.25f, BasisJiggleGrabPicker.DistanceToSegment(Palm + Vector3.up * 0.25f, Palm, Palm), Tolerance);
        }

        // ── grasping ────────────────────────────────────────────────────────

        [Test]
        public void Grasp_TakesAStrandLyingAcrossTheFingers()
        {
            Vector3 acrossFingers = Vector3.Lerp(Palm, FingerTip, 0.75f) + Vector3.up * 0.01f;

            Assert.IsTrue(BasisJiggleGrabPicker.TryScoreGrasp(acrossFingers, Palm, FingerTip, Radius, out float score));
            Assert.Less(score, Radius);
        }

        [Test]
        public void Grasp_RefusesAStrandBehindTheKnuckles()
        {
            // The whole reason the volume is a span and not a sphere on the palm: this sits well
            // within a palm-centred sphere of the same radius, but it is behind the hand.
            Vector3 behindHand = Palm - (FingerTip - Palm).normalized * 0.12f;

            Assert.IsFalse(BasisJiggleGrabPicker.TryScoreGrasp(behindHand, Palm, FingerTip, Radius, out _));
        }

        [Test]
        public void Grasp_ReachesFurtherAlongTheFingersThanAPalmSphereWould()
        {
            // Just past the fingertips, further from the palm than the radius — a palm-centred
            // sphere misses it, the hand does not.
            Vector3 pastTheTips = FingerTip + (FingerTip - Palm).normalized * 0.02f;

            Assert.Greater(Vector3.Distance(pastTheTips, Palm), Radius, "test setup: should be outside a palm sphere");
            Assert.IsTrue(BasisJiggleGrabPicker.TryScoreGrasp(pastTheTips, Palm, FingerTip, Radius, out _));
        }

        [Test]
        public void Grasp_PrefersTheStrandNearestTheHand()
        {
            Vector3 near = Vector3.Lerp(Palm, FingerTip, 0.5f) + Vector3.up * 0.01f;
            Vector3 far = Vector3.Lerp(Palm, FingerTip, 0.5f) + Vector3.up * 0.06f;

            BasisJiggleGrabPicker.TryScoreGrasp(near, Palm, FingerTip, Radius, out float nearScore);
            BasisJiggleGrabPicker.TryScoreGrasp(far, Palm, FingerTip, Radius, out float farScore);

            Assert.Less(nearScore, farScore);
        }

        [Test]
        public void Grasp_RefusesAnythingBeyondTheRadius()
        {
            Vector3 outOfReach = Vector3.Lerp(Palm, FingerTip, 0.5f) + Vector3.up * (Radius + 0.001f);

            Assert.IsFalse(BasisJiggleGrabPicker.TryScoreGrasp(outOfReach, Palm, FingerTip, Radius, out _));
        }

        // ── pointing ────────────────────────────────────────────────────────

        private static readonly Vector3 RayOrigin = Vector3.zero;
        private static readonly Vector3 RayDirection = Vector3.forward;

        [Test]
        public void Pointing_TakesAPointOnTheAxis()
        {
            Assert.IsTrue(BasisJiggleGrabPicker.TryScorePointing(new Vector3(0f, 0f, 2f), RayOrigin, RayDirection, 3f, Radius, out float score));
            Assert.AreEqual(2f, score, Tolerance, "on-axis score is its distance along the ray");
        }

        [Test]
        public void Pointing_RefusesAPointBehindTheHand()
        {
            Assert.IsFalse(BasisJiggleGrabPicker.TryScorePointing(new Vector3(0f, 0f, -1f), RayOrigin, RayDirection, 3f, Radius, out _));
        }

        [Test]
        public void Pointing_RefusesAPointBeyondTheReach()
        {
            Assert.IsFalse(BasisJiggleGrabPicker.TryScorePointing(new Vector3(0f, 0f, 3.5f), RayOrigin, RayDirection, 3f, Radius, out _));
        }

        [Test]
        public void Pointing_RefusesAPointOffTheAxis()
        {
            Assert.IsFalse(BasisJiggleGrabPicker.TryScorePointing(new Vector3(Radius + 0.01f, 0f, 2f), RayOrigin, RayDirection, 3f, Radius, out _));
        }

        [Test]
        public void Pointing_TakesTheNearestOfTwoOnTheAxis()
        {
            BasisJiggleGrabPicker.TryScorePointing(new Vector3(0f, 0f, 1f), RayOrigin, RayDirection, 3f, Radius, out float near);
            BasisJiggleGrabPicker.TryScorePointing(new Vector3(0f, 0f, 2.5f), RayOrigin, RayDirection, 3f, Radius, out float far);

            Assert.Less(near, far);
        }

        [Test]
        public void Pointing_BreaksATieOnHowCentredTheCandidateIs()
        {
            BasisJiggleGrabPicker.TryScorePointing(new Vector3(0f, 0f, 2f), RayOrigin, RayDirection, 3f, Radius, out float centred);
            BasisJiggleGrabPicker.TryScorePointing(new Vector3(0.05f, 0f, 2f), RayOrigin, RayDirection, 3f, Radius, out float offset);

            Assert.Less(centred, offset);
        }

        [Test]
        public void Pointing_StillPrefersACloserCandidateOverAMoreCentredFarOne()
        {
            // Distance along the ray leads; being on-axis only settles ties. A strand at arm's reach
            // must not lose to one three times further away just for being better centred.
            BasisJiggleGrabPicker.TryScorePointing(new Vector3(0.06f, 0f, 0.6f), RayOrigin, RayDirection, 3f, Radius, out float closeButOffset);
            BasisJiggleGrabPicker.TryScorePointing(new Vector3(0f, 0f, 2.5f), RayOrigin, RayDirection, 3f, Radius, out float farButCentred);

            Assert.Less(closeButOffset, farButCentred);
        }

        [Test]
        public void Pointing_NormalisesTheDirection()
        {
            Assert.IsTrue(BasisJiggleGrabPicker.TryScorePointing(new Vector3(0f, 0f, 2f), RayOrigin, Vector3.forward * 17f, 3f, Radius, out float scaled));
            BasisJiggleGrabPicker.TryScorePointing(new Vector3(0f, 0f, 2f), RayOrigin, Vector3.forward, 3f, Radius, out float unit);

            Assert.AreEqual(unit, scaled, Tolerance);
        }

        [Test]
        public void Pointing_WithADegenerateDirection_DoesNotThrowOrMatchEverything()
        {
            Assert.DoesNotThrow(() =>
                BasisJiggleGrabPicker.TryScorePointing(new Vector3(3f, 2f, 1f), RayOrigin, Vector3.zero, 3f, Radius, out _));
            Assert.IsFalse(BasisJiggleGrabPicker.TryScorePointing(new Vector3(3f, 2f, 1f), RayOrigin, Vector3.zero, 3f, Radius, out _));
        }

        // ── touch begin/end latching ────────────────────────────────────────

        private const float Dwell = 0.15f;

        [Test]
        public void Touch_FirstContact_Begins()
        {
            BasisJiggleTouchLatch latch = BasisJiggleTouchLatch.Fresh;

            Assert.AreEqual(BasisJiggleTouchEdge.Began, latch.Update(true, 10f, Dwell));
            Assert.IsTrue(latch.Touching);
        }

        [Test]
        public void Touch_HoldingStill_ReportsNothingFurther()
        {
            BasisJiggleTouchLatch latch = BasisJiggleTouchLatch.Fresh;
            latch.Update(true, 10f, Dwell);

            Assert.AreEqual(BasisJiggleTouchEdge.None, latch.Update(true, 10.02f, Dwell));
            Assert.AreEqual(BasisJiggleTouchEdge.None, latch.Update(true, 20f, Dwell));
        }

        [Test]
        public void Touch_ReleasedAfterTheDwell_Ends()
        {
            BasisJiggleTouchLatch latch = BasisJiggleTouchLatch.Fresh;
            latch.Update(true, 10f, Dwell);

            // Polled per frame, as the driver does: the first frame without contact starts the
            // release, and a later one past the dwell commits it.
            Assert.AreEqual(BasisJiggleTouchEdge.None, latch.Update(false, 10.01f, Dwell));
            Assert.IsTrue(latch.Touching, "still held while the release is pending");
            Assert.AreEqual(BasisJiggleTouchEdge.Ended, latch.Update(false, 10.01f + Dwell + 0.01f, Dwell));
            Assert.IsFalse(latch.Touching);
        }

        [Test]
        public void Touch_BeginsImmediately_WithNoDwellDelay()
        {
            // The begin edge is deliberately not dwelled: a reaction that waits is a reaction that
            // reads as broken.
            BasisJiggleTouchLatch latch = BasisJiggleTouchLatch.Fresh;

            Assert.AreEqual(BasisJiggleTouchEdge.Began, latch.Update(true, 10f, Dwell));
        }

        [Test]
        public void Touch_RegainedDuringAPendingRelease_DoesNotEndOrRebegin()
        {
            BasisJiggleTouchLatch latch = BasisJiggleTouchLatch.Fresh;
            latch.Update(true, 10f, Dwell);
            latch.Update(false, 10.01f, Dwell);

            Assert.AreEqual(BasisJiggleTouchEdge.None, latch.Update(true, 10.02f, Dwell), "no second begin");
            // The pending release was cancelled, so a long hold from here does not suddenly end.
            Assert.AreEqual(BasisJiggleTouchEdge.None, latch.Update(true, 20f, Dwell));
            Assert.IsTrue(latch.Touching);
        }

        [Test]
        public void Touch_LostForLessThanTheDwell_StaysHeld()
        {
            // A hand resting on a chain sits right at the edge of the grip volume and drops contact
            // for a frame or two; that must not read as letting go.
            BasisJiggleTouchLatch latch = BasisJiggleTouchLatch.Fresh;
            latch.Update(true, 10f, Dwell);

            Assert.AreEqual(BasisJiggleTouchEdge.None, latch.Update(false, 10.05f, Dwell));
            Assert.IsTrue(latch.Touching, "still counts as touching");
            Assert.AreEqual(BasisJiggleTouchEdge.None, latch.Update(true, 10.06f, Dwell), "regained, no second begin");
        }

        [Test]
        public void Touch_Flicker_ProducesOneBeginAndOneEnd()
        {
            BasisJiggleTouchLatch latch = BasisJiggleTouchLatch.Fresh;
            int begins = 0;
            int ends = 0;

            // 60 frames of contact that drops out every other frame, then a real release.
            for (int frame = 0; frame < 60; frame++)
            {
                BasisJiggleTouchEdge edge = latch.Update(frame % 2 == 0, 10f + frame * 0.011f, Dwell);
                if (edge == BasisJiggleTouchEdge.Began) begins++;
                if (edge == BasisJiggleTouchEdge.Ended) ends++;
            }
            for (int frame = 0; frame < 60; frame++)
            {
                BasisJiggleTouchEdge edge = latch.Update(false, 11f + frame * 0.011f, Dwell);
                if (edge == BasisJiggleTouchEdge.Began) begins++;
                if (edge == BasisJiggleTouchEdge.Ended) ends++;
            }

            Assert.AreEqual(1, begins, "one begin for one touch");
            Assert.AreEqual(1, ends, "one end for one release");
        }

        [Test]
        public void Touch_AfterEnding_CanBeginAgain()
        {
            BasisJiggleTouchLatch latch = BasisJiggleTouchLatch.Fresh;
            latch.Update(true, 10f, Dwell);
            latch.Update(false, 10.01f, Dwell);
            latch.Update(false, 10.01f + Dwell + 0.01f, Dwell);

            Assert.AreEqual(BasisJiggleTouchEdge.Began, latch.Update(true, 11f, Dwell));
        }

        [Test]
        public void Touch_NoContactOnAFreshLatch_ReportsNothing()
        {
            BasisJiggleTouchLatch latch = BasisJiggleTouchLatch.Fresh;

            Assert.AreEqual(BasisJiggleTouchEdge.None, latch.Update(false, 10f, Dwell));
            Assert.IsFalse(latch.Touching);
        }

        // ── the reported failure, as a case ─────────────────────────────────

        [Test]
        public void AChainAgainstTheHandIsFoundByTheWidenedPass_NotLeftToPointing()
        {
            // The search runs the grip volume, then the same volume widened, and only then the aim
            // ray. A chain the hand is in contact with but slightly outside the tight volume must be
            // caught by that middle pass — otherwise the press either does nothing or, worse, the ray
            // takes something across the room.
            float widened = Radius * BasisJiggleGrabDriver.ReachIntentRadiusMultiplier;
            Vector3 justOutsideTheGrip = Vector3.Lerp(Palm, FingerTip, 0.5f) + Vector3.up * (Radius * 1.5f);

            Assert.IsFalse(BasisJiggleGrabPicker.TryScoreGrasp(justOutsideTheGrip, Palm, FingerTip, Radius, out _),
                "test setup: outside the tight grip volume");
            Assert.IsTrue(BasisJiggleGrabPicker.TryScoreGrasp(justOutsideTheGrip, Palm, FingerTip, widened, out _),
                "but still against the hand, so the widened pass takes it");
        }

        [Test]
        public void AChainInTheHandIsNotBeatenByOneAcrossTheRoom()
        {
            // What "sometimes it grabs one far away" looked like: the hand is holding one strand
            // while another sits metres ahead. Grasping must not even consider the far one.
            Vector3 inHand = Vector3.Lerp(Palm, FingerTip, 0.6f);
            Vector3 acrossTheRoom = Palm + Vector3.forward * 3f;

            Assert.IsTrue(BasisJiggleGrabPicker.TryScoreGrasp(inHand, Palm, FingerTip, Radius, out _));
            Assert.IsFalse(BasisJiggleGrabPicker.TryScoreGrasp(acrossTheRoom, Palm, FingerTip, Radius, out _));
        }
    }

    public class BasisJiggleGrabTargetPolicyTests
    {
        [Test]
        public void DesktopPointer_DoesNotTargetTheLocalAvatar()
        {
            Assert.IsFalse(BasisJiggleGrabDriver.CanGrabLocalAvatar(desktopInput: true));
        }

        [Test]
        public void VrHand_CanStillTargetTheLocalAvatar()
        {
            Assert.IsTrue(BasisJiggleGrabDriver.CanGrabLocalAvatar(desktopInput: false));
        }
    }
}
