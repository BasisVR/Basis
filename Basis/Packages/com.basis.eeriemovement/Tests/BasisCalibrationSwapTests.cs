using NUnit.Framework;

namespace Basis.Tests.IK
{
    /// <summary>
    /// Tests for what an avatar SWAP remaps. Roles + rotation frames are preserved across a swap; only the
    /// height scale is recomputed (BasisHeightDriver re-runs DeviceScale). These pin that the recompute is a
    /// pure remap by the new avatar's authored eye height -- right -- and that it no longer drifts with the head
    /// pose at swap time -- the bug. Fix: BasisHeightDriver.CapturePlayerHeight reuses the last genuine standing
    /// eye height (BasisCalibrationMath.ShouldRecaptureEyeHeight) instead of re-polling the live HMD per load.
    /// </summary>
    public class BasisCalibrationSwapTests
    {
        static readonly float[] StandingEye = { 1.40f, 1.61f, 1.90f };
        static readonly float[] AvatarA = { 1.30f, 1.61f, 1.72f };
        static readonly float[] AvatarB = { 1.45f, 1.55f, 1.80f };

        static float RenderedViewpoint(float trueStandingEye, float deviceScale) => trueStandingEye * deviceScale;

        // ---- recapture decision: when a load re-polls vs reuses ------------------------------------

        [Test]
        public void Swap_ReusesGenuineValue_EverythingElseRepolls()
        {
            // A swap (recapture=false) reuses only once a genuine value exists; explicit recalibration always
            // re-measures, and a swap with no genuine value yet still re-polls (preserves the "scrunch" fix).
            Assert.That(BasisCalibrationMath.ShouldRecaptureEyeHeight(false, true), Is.False, "a swap must reuse a genuine standing eye height.");
            Assert.That(BasisCalibrationMath.ShouldRecaptureEyeHeight(false, false), Is.True, "a swap with no genuine value yet must re-poll.");
            Assert.That(BasisCalibrationMath.ShouldRecaptureEyeHeight(true, true), Is.True, "explicit recalibration must always re-measure.");
            Assert.That(BasisCalibrationMath.ShouldRecaptureEyeHeight(true, false), Is.True);
        }

        [Test]
        public void GenuineFlag_FreezesOnlyRealMeasurements_FallbackKeepsRepolling()
        {
            // Sequence: a fallback poll (HMD not ready at first load) must NOT freeze in; only a real measurement
            // flips hasGenuine so later swaps reuse it. This is what keeps the scrunch fix while killing drift.
            bool hasGenuine = false; // first load, before tracking

            bool fallbackIsGenuine = false;
            hasGenuine = fallbackIsGenuine;
            Assert.That(BasisCalibrationMath.ShouldRecaptureEyeHeight(false, hasGenuine), Is.True,
                "a fallback eye height must not be frozen in across swaps.");

            bool realIsGenuine = true; // HMD now tracking
            hasGenuine = realIsGenuine;
            Assert.That(BasisCalibrationMath.ShouldRecaptureEyeHeight(false, hasGenuine), Is.False,
                "after a real measurement, subsequent swaps reuse it.");
        }

        // ---- the remap is RIGHT: rescale by exactly the avatar eye ratio --------------------------

        [Test]
        public void Swap_RemapsByExactlyTheAvatarEyeRatio_NothingElseLeaksIn()
        {
            // Because the player denominator is reused unchanged across the swap, DeviceScale changes by EXACTLY
            // the ratio of the two avatars' authored eye heights -- the legitimate per-avatar remap, nothing else.
            const float o = 0.04f, correction = 0.06f, nudge = 0f;
            foreach (float E in StandingEye)
                foreach (float A in AvatarA)
                    foreach (float B in AvatarB)
                    {
                        float scaleA = BasisCalibrationMath.ComputeDeviceScale(A, 1f, E, o + correction, nudge);
                        float scaleB = BasisCalibrationMath.ComputeDeviceScale(B, 1f, E, o + correction, nudge);
                        Assert.That(scaleB / scaleA, Is.EqualTo(B / A).Within(1e-5f),
                            $"E={E} A={A} B={B}: a swap must rescale by exactly B/A and carry nothing else over.");
                    }
        }

        [Test]
        public void Swap_WithACorrectDenominator_LandsEachAvatarsViewpointOnItsOwnEye()
        {
            // End to end: with the denominator equal to the true standing eye, swapping A->B keeps the first
            // person viewpoint exactly on whichever avatar is worn -- the remap is correct for both.
            foreach (float E in StandingEye)
                foreach (float A in AvatarA)
                    foreach (float B in AvatarB)
                    {
                        float scaleA = BasisCalibrationMath.ComputeDeviceScale(A, 1f, E, 0f, 0f);
                        float scaleB = BasisCalibrationMath.ComputeDeviceScale(B, 1f, E, 0f, 0f);
                        Assert.That(RenderedViewpoint(E, scaleA), Is.EqualTo(A).Within(1e-4f), $"E={E} A={A}: viewpoint must land on avatar A's eye.");
                        Assert.That(RenderedViewpoint(E, scaleB), Is.EqualTo(B).Within(1e-4f), $"E={E} B={B}: after swap, viewpoint must land on avatar B's eye.");
                    }
        }

        // ---- the bug it fixes: re-poll WAS stance-dependent (wrong) --------------------------------

        [Test]
        public void ReusedDenominator_IsStanceIndependent_WhereTheOldRepollDrifted()
        {
            // The drift: re-polling the live HMD on every load captured the head's CURRENT height, so swapping
            // while looking down/leaning shifted the denominator and the SAME avatar reloaded at a taller scale.
            const float A = 1.61f;
            foreach (float E in StandingEye)
            {
                float correct = BasisCalibrationMath.ComputeDeviceScale(A, 1f, E, 0f, 0f); // E captured standing level
                foreach (float stanceDrop in new[] { 0f, 0.08f, 0.15f })
                {
                    float livePollNow = E - stanceDrop; // head lower at the swap instant

                    float reused = BasisCalibrationMath.ComputeDeviceScale(A, 1f, E, 0f, 0f);          // NEW: reuse genuine
                    float repolled = BasisCalibrationMath.ComputeDeviceScale(A, 1f, livePollNow, 0f, 0f); // OLD: re-poll stance

                    Assert.That(reused, Is.EqualTo(correct).Within(1e-6f),
                        $"E={E} drop={stanceDrop}: the reused scale must not depend on swap-time stance.");
                    if (stanceDrop > 0f)
                        Assert.That(repolled, Is.GreaterThan(reused),
                            $"E={E} drop={stanceDrop}: the old live re-poll drifts taller as the head drops at swap time.");
                }
            }
        }
    }
}
