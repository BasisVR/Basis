using System.Collections.Generic;
using Basis.Scripts.UI.NamePlate;
using NUnit.Framework;

namespace Basis.Tests.IK
{
    /// <summary>
    /// Pins the nameplate overlay limiter policy (BasisNamePlateOverlayCore): the nearest-K
    /// selection that bounds how many chat bubbles / avatar-loading displays render at once,
    /// the loading-text quantization that stops far-too-frequent TMP re-tessellation during
    /// avatar downloads, and the idle-release rule that stops "every player who ever chatted
    /// keeps a TMP forever" accumulation. These rules are what keeps the overlay cost constant
    /// at 1000 players — a regression here silently reverts the nameplates to unbounded cost.
    /// </summary>
    public class BasisNamePlateOverlayTests
    {
        // ── SelectNearest ───────────────────────────────────────────────────

        private static List<bool> Select(List<float> distances, int cap, out int visibleCount)
        {
            var scratch = new List<int>();
            var visible = new List<bool>();
            visibleCount = BasisNamePlateOverlayCore.SelectNearest(distances, cap, scratch, visible);
            return visible;
        }

        [Test]
        public void SelectNearest_EmptyInput_SelectsNothing()
        {
            var visible = Select(new List<float>(), 8, out int count);
            Assert.AreEqual(0, count);
            Assert.AreEqual(0, visible.Count);
        }

        [Test]
        public void SelectNearest_UnderCap_SelectsAll()
        {
            var visible = Select(new List<float> { 9f, 1f, 4f }, 8, out int count);
            Assert.AreEqual(3, count);
            Assert.AreEqual(new List<bool> { true, true, true }, visible);
        }

        [Test]
        public void SelectNearest_ExactlyAtCap_SelectsAll()
        {
            var visible = Select(new List<float> { 9f, 1f, 4f }, 3, out int count);
            Assert.AreEqual(3, count);
            Assert.AreEqual(new List<bool> { true, true, true }, visible);
        }

        [Test]
        public void SelectNearest_OverCap_SelectsTheNearestAndKeepsInputOrder()
        {
            // Distances deliberately unsorted: visibility flags must stay parallel to the input.
            var visible = Select(new List<float> { 25f, 1f, 16f, 4f, 9f }, 2, out int count);
            Assert.AreEqual(2, count);
            Assert.AreEqual(new List<bool> { false, true, false, true, false }, visible);
        }

        [Test]
        public void SelectNearest_ZeroOrNegativeCap_SelectsNothing()
        {
            var distances = new List<float> { 1f, 2f };

            var visible = Select(distances, 0, out int count);
            Assert.AreEqual(0, count);
            Assert.AreEqual(new List<bool> { false, false }, visible);

            visible = Select(distances, -3, out count);
            Assert.AreEqual(0, count);
            Assert.AreEqual(new List<bool> { false, false }, visible);
        }

        [Test]
        public void SelectNearest_TiesAtTheBoundary_StillSelectExactlyCap()
        {
            var distances = new List<float> { 5f, 5f, 5f, 5f };
            var visible = Select(distances, 2, out int count);

            Assert.AreEqual(2, count);
            int selected = 0;
            for (int i = 0; i < visible.Count; i++)
            {
                if (visible[i]) selected++;
            }
            Assert.AreEqual(2, selected);
        }

        [Test]
        public void SelectNearest_EverySelectedIsNoFartherThanEveryRejected()
        {
            var distances = new List<float> { 7f, 3f, 3f, 9f, 1f, 8f, 2f };
            var visible = Select(distances, 3, out _);

            float farthestSelected = float.MinValue;
            float nearestRejected = float.MaxValue;
            for (int i = 0; i < distances.Count; i++)
            {
                if (visible[i] && distances[i] > farthestSelected) farthestSelected = distances[i];
                if (!visible[i] && distances[i] < nearestRejected) nearestRejected = distances[i];
            }
            Assert.LessOrEqual(farthestSelected, nearestRejected);
        }

        [Test]
        public void SelectNearest_ReusedBuffers_ProduceCorrectResultsAcrossCalls()
        {
            var scratch = new List<int>();
            var visible = new List<bool>();

            BasisNamePlateOverlayCore.SelectNearest(new List<float> { 3f, 1f, 2f, 4f }, 2, scratch, visible);
            Assert.AreEqual(new List<bool> { false, true, true, false }, visible);

            // Smaller second call must fully reset the outputs, not inherit stale entries.
            BasisNamePlateOverlayCore.SelectNearest(new List<float> { 5f }, 2, scratch, visible);
            Assert.AreEqual(new List<bool> { true }, visible);

            // Larger third call after the small one.
            BasisNamePlateOverlayCore.SelectNearest(new List<float> { 9f, 8f, 7f, 6f, 5f }, 1, scratch, visible);
            Assert.AreEqual(new List<bool> { false, false, false, false, true }, visible);
        }

        // ── ProgressBucket ──────────────────────────────────────────────────

        [Test]
        public void ProgressBucket_QuantizesIntoSteps()
        {
            Assert.AreEqual(0, BasisNamePlateOverlayCore.ProgressBucket(0f, 5f));
            Assert.AreEqual(0, BasisNamePlateOverlayCore.ProgressBucket(4.99f, 5f));
            Assert.AreEqual(1, BasisNamePlateOverlayCore.ProgressBucket(5f, 5f));
            Assert.AreEqual(13, BasisNamePlateOverlayCore.ProgressBucket(67.2f, 5f));
            Assert.AreEqual(19, BasisNamePlateOverlayCore.ProgressBucket(99f, 5f));
        }

        [Test]
        public void ProgressBucket_SameBucketMeansNoTextRewrite()
        {
            // The consumer only rewrites the label when the bucket changes — values inside
            // one step must map to one bucket.
            int a = BasisNamePlateOverlayCore.ProgressBucket(41.1f, 5f);
            int b = BasisNamePlateOverlayCore.ProgressBucket(44.9f, 5f);
            Assert.AreEqual(a, b);

            int c = BasisNamePlateOverlayCore.ProgressBucket(45.0f, 5f);
            Assert.AreNotEqual(b, c);
        }

        [Test]
        public void ProgressBucket_NonPositiveStep_FallsBackToWholePercents()
        {
            Assert.AreEqual(4, BasisNamePlateOverlayCore.ProgressBucket(4.2f, 0f));
            Assert.AreEqual(4, BasisNamePlateOverlayCore.ProgressBucket(4.9f, -1f));
            Assert.AreEqual(5, BasisNamePlateOverlayCore.ProgressBucket(5.0f, 0f));
        }

        [Test]
        public void ProgressBucket_NegativeProgress_ClampsToFirstBucket()
        {
            Assert.AreEqual(0, BasisNamePlateOverlayCore.ProgressBucket(-25f, 5f));
        }

        // ── ShouldReleaseChatDisplay ────────────────────────────────────────

        [Test]
        public void ShouldReleaseChatDisplay_NoDisplay_NeverReleases()
        {
            Assert.IsFalse(BasisNamePlateOverlayCore.ShouldReleaseChatDisplay(
                displayExists: false, hasAnyContent: false, now: 1000.0, lastActiveTime: 0.0, idleSeconds: 30.0));
        }

        [Test]
        public void ShouldReleaseChatDisplay_LiveContent_NeverReleases()
        {
            Assert.IsFalse(BasisNamePlateOverlayCore.ShouldReleaseChatDisplay(
                displayExists: true, hasAnyContent: true, now: 1000.0, lastActiveTime: 0.0, idleSeconds: 30.0));
        }

        [Test]
        public void ShouldReleaseChatDisplay_IdleShorterThanThreshold_Keeps()
        {
            Assert.IsFalse(BasisNamePlateOverlayCore.ShouldReleaseChatDisplay(
                displayExists: true, hasAnyContent: false, now: 129.9, lastActiveTime: 100.0, idleSeconds: 30.0));
        }

        [Test]
        public void ShouldReleaseChatDisplay_IdleAtOrPastThreshold_Releases()
        {
            Assert.IsTrue(BasisNamePlateOverlayCore.ShouldReleaseChatDisplay(
                displayExists: true, hasAnyContent: false, now: 130.0, lastActiveTime: 100.0, idleSeconds: 30.0));
            Assert.IsTrue(BasisNamePlateOverlayCore.ShouldReleaseChatDisplay(
                displayExists: true, hasAnyContent: false, now: 500.0, lastActiveTime: 100.0, idleSeconds: 30.0));
        }

        // ── IsLoadingComplete ───────────────────────────────────────────────

        [Test]
        public void IsLoadingComplete_MidLoad_IsFalse()
        {
            Assert.IsFalse(BasisNamePlateOverlayCore.IsLoadingComplete(0f));
            Assert.IsFalse(BasisNamePlateOverlayCore.IsLoadingComplete(50f));
            Assert.IsFalse(BasisNamePlateOverlayCore.IsLoadingComplete(99.9f));
        }

        [Test]
        public void IsLoadingComplete_AtOrPastHundred_IsTrue()
        {
            // The old check was `progress == 100` — an exact float compare that missed
            // 99.999… accumulation and >100 reports, leaving the bar stuck on screen.
            Assert.IsTrue(BasisNamePlateOverlayCore.IsLoadingComplete(99.999f));
            Assert.IsTrue(BasisNamePlateOverlayCore.IsLoadingComplete(100f));
            Assert.IsTrue(BasisNamePlateOverlayCore.IsLoadingComplete(100.5f));
        }
    }
}
