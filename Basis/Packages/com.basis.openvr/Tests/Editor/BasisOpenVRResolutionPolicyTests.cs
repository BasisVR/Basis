using Basis.Scripts.Device_Management.Devices.OpenVR;
using NUnit.Framework;

namespace Basis.Tests.OpenVR
{
    public sealed class BasisOpenVRResolutionPolicyTests
    {
        const float Deadband = BasisOpenVRResolutionPolicy.DefaultDeadband;

        const float BoundsLeftMin = 0.0f;
        const float BoundsLeftMax = 0.93f;
        const float BoundsRightMin = 0.07f;
        const float BoundsRightMax = 1.0f;

        static float GrownSpan()
        {
            return BasisOpenVRResolutionPolicy.LargestSpan(BoundsLeftMin, BoundsLeftMax, BoundsRightMin, BoundsRightMax);
        }

        [Test]
        public void GrowForLensOverlap_MatchesSteamVRFormula()
        {
            float span = GrownSpan();
            float expected = 2160f / span;

            float actual = BasisOpenVRResolutionPolicy.GrowForLensOverlap(2160f, span);

            Assert.AreEqual(expected, actual, 0.0001f, "Growth must match SteamVR.cs sceneWidth/sceneHeight derivation exactly");
        }

        [Test]
        public void GrowForLensOverlap_GrowsAboveRawRecommendation()
        {
            float grown = BasisOpenVRResolutionPolicy.GrowForLensOverlap(2160f, GrownSpan());

            Assert.Greater(grown, 2160f, "Overlapping fov must grow the recommended size, never shrink it");
        }

        [Test]
        public void FirstPollAfterBoot_IsNoOp()
        {
            float target = BasisOpenVRResolutionPolicy.GrowForLensOverlap(2160f, GrownSpan());
            float bootScale = target / 1600f;
            float allocated = 1600f * bootScale;

            bool changed = BasisOpenVRResolutionPolicy.TryComputeEyeTextureScale(target, allocated, bootScale, Deadband, out float newScale);

            Assert.IsFalse(changed, "Enabling polling must not itself move the resolution that StartSDK already applied");
            Assert.AreEqual(bootScale, newScale, 0.0001f, "Scale must be left untouched when already on target");
        }

        [Test]
        public void BootFromUnityDefault_ReachesRecommended()
        {
            bool changed = BasisOpenVRResolutionPolicy.TryComputeEyeTextureScale(2400f, 1600f, 1f, Deadband, out float newScale);

            Assert.IsTrue(changed, "A cold start well away from the recommendation must apply");
            Assert.AreEqual(1.5f, newScale, 0.0001f, "Scale must be recommended/base");
        }

        [Test]
        public void WithinDeadband_DoesNotReapply()
        {
            bool changed = BasisOpenVRResolutionPolicy.TryComputeEyeTextureScale(2000f, 2020f, 1f, Deadband, out _);

            Assert.IsFalse(changed, "A 1% drift must not trigger an eye texture reallocation");
        }

        [Test]
        public void OutsideDeadband_Reapplies()
        {
            bool changed = BasisOpenVRResolutionPolicy.TryComputeEyeTextureScale(2000f, 2500f, 1f, Deadband, out _);

            Assert.IsTrue(changed, "A 25% drift is a real compositor change and must be applied");
        }

        [Test]
        public void SupersampleIncrease_RaisesScale()
        {
            float allocated = 2000f;
            float currentScale = 1f;
            float raisedTarget = 3000f;

            bool changed = BasisOpenVRResolutionPolicy.TryComputeEyeTextureScale(raisedTarget, allocated, currentScale, Deadband, out float newScale);

            Assert.IsTrue(changed, "Raising SteamVR supersampling must be picked up");
            Assert.Greater(newScale, currentScale, "Scale must rise with the compositor recommendation");
            Assert.AreEqual(1.5f, newScale, 0.0001f, "1.5x recommendation must land on 1.5x scale from a 1.0 base");
        }

        [Test]
        public void SupersampleDecrease_LowersScale()
        {
            float allocated = 3000f;
            float currentScale = 1.5f;
            float loweredTarget = 2000f;

            bool changed = BasisOpenVRResolutionPolicy.TryComputeEyeTextureScale(loweredTarget, allocated, currentScale, Deadband, out float newScale);

            Assert.IsTrue(changed, "Lowering SteamVR supersampling must be picked up");
            Assert.Less(newScale, currentScale, "Scale must fall with the compositor recommendation");
            Assert.AreEqual(1f, newScale, 0.0001f, "Returning to the native recommendation must return to 1.0 scale");
        }

        [Test]
        public void ConvergesWhenEngineCachesBase()
        {
            const float engineBase = 1600f;
            float scale = 1f;
            float allocated = engineBase * scale;
            const float target = 2400f;

            int applications = 0;
            for (int iteration = 0; iteration < 8; iteration++)
            {
                if (!BasisOpenVRResolutionPolicy.TryComputeEyeTextureScale(target, allocated, scale, Deadband, out float next))
                {
                    break;
                }
                applications++;
                scale = next;
                allocated = engineBase * scale;
            }

            Assert.AreEqual(1, applications, "With a cached engine base the loop must settle in a single reallocation");
            Assert.AreEqual(target, allocated, target * Deadband, "Allocation must land on the compositor recommendation");
        }

        [Test]
        public void ConvergesWhenEngineRequeriesBase()
        {
            const float runtimeRecommendation = 2400f;
            float scale = 1f;
            float allocated = 1600f;

            int applications = 0;
            for (int iteration = 0; iteration < 16; iteration++)
            {
                if (!BasisOpenVRResolutionPolicy.TryComputeEyeTextureScale(runtimeRecommendation, allocated, scale, Deadband, out float next))
                {
                    break;
                }
                applications++;
                scale = next;
                allocated = runtimeRecommendation * scale;
            }

            Assert.Less(applications, 16, "The loop must terminate rather than oscillate when the engine re-queries its base");
            Assert.AreEqual(runtimeRecommendation, allocated, runtimeRecommendation * Deadband, "Allocation must converge on the compositor recommendation either way");
        }

        [Test]
        public void RepeatedPollsAtTarget_NeverReallocate()
        {
            const float target = 2400f;
            float scale = 1.5f;
            float allocated = 2400f;

            for (int iteration = 0; iteration < 32; iteration++)
            {
                bool changed = BasisOpenVRResolutionPolicy.TryComputeEyeTextureScale(target, allocated, scale, Deadband, out float next);
                Assert.IsFalse(changed, "A steady compositor recommendation must never cause a reallocation");
                scale = next;
            }
        }

        [Test]
        public void ZeroRecommendation_IsIgnored()
        {
            bool changed = BasisOpenVRResolutionPolicy.TryComputeEyeTextureScale(0f, 2000f, 1f, Deadband, out float newScale);

            Assert.IsFalse(changed, "A runtime that reports no recommendation must not move the resolution");
            Assert.AreEqual(1f, newScale, 0.0001f, "Scale must be left untouched");
        }

        [Test]
        public void ZeroAllocation_IsIgnored()
        {
            bool changed = BasisOpenVRResolutionPolicy.TryComputeEyeTextureScale(2000f, 0f, 1f, Deadband, out _);

            Assert.IsFalse(changed, "Polling before the display subsystem has allocated must be a no-op");
        }

        [Test]
        public void NonPositiveCurrentScale_RecoversToOne()
        {
            bool changed = BasisOpenVRResolutionPolicy.TryComputeEyeTextureScale(2400f, 1600f, 0f, Deadband, out float newScale);

            Assert.IsTrue(changed, "A degenerate current scale must still resolve to a usable scale");
            Assert.AreEqual(1.5f, newScale, 0.0001f, "A zero current scale must be treated as 1.0 when back-solving the base");
        }

        [Test]
        public void DegenerateBounds_FallsBackToRawRecommendation()
        {
            float grown = BasisOpenVRResolutionPolicy.GrowForLensOverlap(2160f, 0f);

            Assert.AreEqual(2160f, grown, 0.0001f, "Unreadable texture bounds must fall back to the raw recommendation, not divide by zero");
        }

        [Test]
        public void AllocationMultiplierHeadroom_TargetsMaximumScale()
        {
            float recommended = BasisOpenVRResolutionPolicy.GrowForLensOverlap(2160f, GrownSpan());
            float target = recommended * 1.25f;

            bool changed = BasisOpenVRResolutionPolicy.TryComputeEyeTextureScale(target, recommended, 1f, Deadband, out float newScale);

            Assert.IsTrue(changed, "Allocating headroom for dynamic resolution must apply");
            Assert.AreEqual(1.25f, newScale, 0.0001f, "Eye textures must be allocated at the maximum dynamic scale");
        }
    }
}
