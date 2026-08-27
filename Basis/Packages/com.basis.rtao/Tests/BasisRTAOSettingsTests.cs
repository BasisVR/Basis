using NUnit.Framework;
using UnityEngine;

namespace Basis.Rendering.RTAO.Tests
{
    public sealed class BasisRTAOSettingsTests
    {
        [Test]
        public void LayersMatchTheirNames()
        {
            // The default mask has to be a constant because Unity forbids NameToLayer from the field
            // initializer that consumes it. This is the guard on that constant: reorder the layer list and
            // this fails, rather than the tracer quietly following whatever now sits at bits 6 and 7.
            int local = LayerMask.NameToLayer(BasisRTAOSceneSettings.LocalAvatarLayer);
            int remote = LayerMask.NameToLayer(BasisRTAOSceneSettings.RemoteAvatarLayer);

            Assert.GreaterOrEqual(local, 0, $"This project is expected to have a {BasisRTAOSceneSettings.LocalAvatarLayer} layer.");
            Assert.GreaterOrEqual(remote, 0, $"This project is expected to have a {BasisRTAOSceneSettings.RemoteAvatarLayer} layer.");

            Assert.AreEqual((1 << local) | (1 << remote), BasisRTAOSceneSettings.AvatarLayerMask,
                $"{BasisRTAOSceneSettings.LocalAvatarLayer} is layer {local} and " +
                $"{BasisRTAOSceneSettings.RemoteAvatarLayer} is layer {remote}, which no longer match the " +
                "constant the default mask is built from.");

            Assert.AreEqual(BasisRTAOSceneSettings.AvatarLayerMask, BasisRTAOSceneSettings.AvatarLayers.value,
                "The name lookup and the constant have to agree.");
        }

        [Test]
        public void DefaultMatchesMediumPreset()
        {
            BasisRTAOSettings expected = BasisRTAOSettings.FromQuality(BasisRTAOQuality.Medium);
            BasisRTAOSettings actual = BasisRTAOSettings.Default;
            Assert.AreEqual(expected.raysPerPixel, actual.raysPerPixel);
            Assert.AreEqual(expected.resolutionDivider, actual.resolutionDivider);
            Assert.AreEqual(expected.radius, actual.radius);
            Assert.AreEqual(expected.temporalFrames, actual.temporalFrames);
        }

        [Test]
        public void RayCountRisesWithQuality()
        {
            int low = BasisRTAOSettings.FromQuality(BasisRTAOQuality.Low).raysPerPixel;
            int medium = BasisRTAOSettings.FromQuality(BasisRTAOQuality.Medium).raysPerPixel;
            int high = BasisRTAOSettings.FromQuality(BasisRTAOQuality.High).raysPerPixel;
            int ultra = BasisRTAOSettings.FromQuality(BasisRTAOQuality.Ultra).raysPerPixel;

            Assert.Less(low, medium);
            Assert.Less(medium, high);
            Assert.Less(high, ultra);
        }

        [Test]
        public void BlurRadiusFallsWithQuality()
        {
            int low = BasisRTAOSettings.FromQuality(BasisRTAOQuality.Low).blurMaxRadius;
            int ultra = BasisRTAOSettings.FromQuality(BasisRTAOQuality.Ultra).blurMaxRadius;
            Assert.Greater(low, ultra, "A noisier preset needs a wider spatial filter, not a narrower one.");
        }

        [Test]
        public void OnlyUltraTracesAtFullResolution()
        {
            Assert.AreEqual(1, BasisRTAOSettings.FromQuality(BasisRTAOQuality.Ultra).resolutionDivider);
            Assert.AreEqual(2, BasisRTAOSettings.FromQuality(BasisRTAOQuality.High).resolutionDivider);
            Assert.AreEqual(2, BasisRTAOSettings.FromQuality(BasisRTAOQuality.Medium).resolutionDivider);
            Assert.AreEqual(2, BasisRTAOSettings.FromQuality(BasisRTAOQuality.Low).resolutionDivider);
        }

        [Test]
        public void LowerQualityLeansHarderOnTemporalAccumulation()
        {
            int low = BasisRTAOSettings.FromQuality(BasisRTAOQuality.Low).temporalFrames;
            int ultra = BasisRTAOSettings.FromQuality(BasisRTAOQuality.Ultra).temporalFrames;
            Assert.Greater(low, ultra);
        }

        [Test]
        public void ValidatedClampsRayCount()
        {
            BasisRTAOSettings settings = BasisRTAOSettings.Default;
            settings.raysPerPixel = 0;
            Assert.AreEqual(1, settings.Validated().raysPerPixel);

            settings.raysPerPixel = 9999;
            Assert.AreEqual(16, settings.Validated().raysPerPixel);
        }

        [Test]
        public void ValidatedClampsResolutionDivider()
        {
            BasisRTAOSettings settings = BasisRTAOSettings.Default;
            settings.resolutionDivider = 0;
            Assert.AreEqual(1, settings.Validated().resolutionDivider);

            settings.resolutionDivider = 64;
            Assert.AreEqual(4, settings.Validated().resolutionDivider);
        }

        [Test]
        public void ValidatedKeepsRadiusPositive()
        {
            BasisRTAOSettings settings = BasisRTAOSettings.Default;
            settings.radius = -5f;
            Assert.Greater(settings.Validated().radius, 0f);
        }

        [Test]
        public void ValidatedKeepsFadeEndBeyondFadeStart()
        {
            BasisRTAOSettings settings = BasisRTAOSettings.Default;
            settings.fadeStart = 50f;
            settings.fadeEnd = 10f;
            BasisRTAOSettings validated = settings.Validated();
            Assert.Greater(validated.fadeEnd, validated.fadeStart);
        }

        [Test]
        public void ValidatedKeepsBlurRadiiOrdered()
        {
            BasisRTAOSettings settings = BasisRTAOSettings.Default;
            settings.blurMaxRadius = 2;
            settings.blurMinRadius = 7;
            BasisRTAOSettings validated = settings.Validated();
            Assert.LessOrEqual(validated.blurMinRadius, validated.blurMaxRadius);
        }

        [Test]
        public void ValidatedClampsNormalisedRanges()
        {
            BasisRTAOSettings settings = BasisRTAOSettings.Default;
            settings.directLightingStrength = 4f;
            settings.specularOcclusionRelief = -2f;
            settings.temporalMinAlpha = -1f;
            settings.temporalNormalTolerance = 3f;
            BasisRTAOSettings validated = settings.Validated();

            Assert.AreEqual(1f, validated.directLightingStrength);
            Assert.AreEqual(0f, validated.specularOcclusionRelief);
            Assert.AreEqual(0f, validated.temporalMinAlpha);
            Assert.AreEqual(1f, validated.temporalNormalTolerance);
        }

        /// <summary>
        /// The whole point of phrasing this as relief rather than strength: Unity deserialises a field an
        /// asset predates as zero, so zero has to be the physical answer. If this ever flips to a non-zero
        /// default, every renderer asset already saved silently loses specular occlusion - and it loses it
        /// invisibly, because the picture still looks plausible.
        /// </summary>
        [Test]
        public void SpecularOcclusionReliefDefaultsToZeroSoOldAssetsGetThePhysicalAnswer()
        {
            Assert.AreEqual(0f, BasisRTAOSettings.Default.specularOcclusionRelief, 1e-4f);
            Assert.AreEqual(0f, default(BasisRTAOSettings).specularOcclusionRelief, 1e-4f,
                "an asset saved before the field existed deserialises to default(struct)");

            foreach (BasisRTAOQuality quality in System.Enum.GetValues(typeof(BasisRTAOQuality)))
            {
                Assert.AreEqual(0f, BasisRTAOSettings.FromQuality(quality).specularOcclusionRelief, 1e-4f, $"quality {quality}");
            }
        }

        // A quality tier decides how many rays are cast, not how the shading looks, and specular occlusion
        // is a look. It has to survive the preset merge at every tier or the authored value is silently
        // replaced by whatever the preset happens to carry.
        [Test]
        public void SpecularOcclusionReliefSurvivesCostMerge()
        {
            BasisRTAOSettings authored = BasisRTAOSettings.Default;
            authored.specularOcclusionRelief = 0.25f;

            foreach (BasisRTAOQuality quality in System.Enum.GetValues(typeof(BasisRTAOQuality)))
            {
                BasisRTAOSettings merged = authored.WithCostFrom(BasisRTAOSettings.FromQuality(quality));
                Assert.AreEqual(0.25f, merged.specularOcclusionRelief, 1e-4f, $"quality {quality} overwrote the authored value");
            }
        }

        [Test]
        public void ValidatedKeepsBiasesNonNegative()
        {
            BasisRTAOSettings settings = BasisRTAOSettings.Default;
            settings.normalBias = -1f;
            settings.distanceBias = -1f;
            settings.noiseCellSize = -1f;
            BasisRTAOSettings validated = settings.Validated();

            Assert.GreaterOrEqual(validated.normalBias, 0f);
            Assert.GreaterOrEqual(validated.distanceBias, 0f);
            Assert.Greater(validated.noiseCellSize, 0f);
        }

        [Test]
        public void NoisierPresetsDenoiseHarder()
        {
            int low = BasisRTAOSettings.FromQuality(BasisRTAOQuality.Low).denoisePasses;
            int medium = BasisRTAOSettings.FromQuality(BasisRTAOQuality.Medium).denoisePasses;
            int ultra = BasisRTAOSettings.FromQuality(BasisRTAOQuality.Ultra).denoisePasses;

            Assert.Greater(low, ultra, "One ray per pixel needs more filtering than six.");
            Assert.GreaterOrEqual(low, medium);
            Assert.GreaterOrEqual(medium, ultra);
        }

        [Test]
        public void ValidatedClampsDenoisePasses()
        {
            BasisRTAOSettings settings = BasisRTAOSettings.Default;
            settings.denoisePasses = -4;
            Assert.AreEqual(0, settings.Validated().denoisePasses);

            settings.denoisePasses = 99;
            Assert.AreEqual(4, settings.Validated().denoisePasses, "Each pass doubles the reach, so four is already a 16 texel footprint.");
        }

        [Test]
        public void DenoiseCanBeTurnedOffEntirely()
        {
            BasisRTAOSettings settings = BasisRTAOSettings.Default;
            settings.denoisePasses = 0;
            Assert.AreEqual(0, settings.Validated().denoisePasses,
                "Zero passes has to survive validation, or the toggle cannot actually turn the filter off.");
        }

        [Test]
        public void ValidatedIsIdempotent()
        {
            BasisRTAOSettings once = BasisRTAOSettings.Default.Validated();
            BasisRTAOSettings twice = once.Validated();
            Assert.AreEqual(JsonUtility.ToJson(once), JsonUtility.ToJson(twice));
        }

        [Test]
        public void StereoCoherentNoiseIsOnByDefaultForEveryPreset()
        {
            foreach (BasisRTAOQuality quality in System.Enum.GetValues(typeof(BasisRTAOQuality)))
                Assert.IsTrue(BasisRTAOSettings.FromQuality(quality).stereoCoherentNoise, $"{quality} must default to stereo coherent noise so both eyes agree.");
        }

        [Test]
        public void BakeBudgetClimbsWithQuality()
        {
            Assert.AreEqual(1, BasisRTAOSceneSettings.BakeBudgetForQuality(BasisRTAOQuality.Low));
            Assert.AreEqual(4, BasisRTAOSceneSettings.BakeBudgetForQuality(BasisRTAOQuality.Medium));
            Assert.AreEqual(16, BasisRTAOSceneSettings.BakeBudgetForQuality(BasisRTAOQuality.High));
            Assert.AreEqual(100, BasisRTAOSceneSettings.BakeBudgetForQuality(BasisRTAOQuality.Ultra));
        }

        [Test]
        public void BakeIntervalTightensWithQuality()
        {
            int low = BasisRTAOSceneSettings.BakeIntervalForQuality(BasisRTAOQuality.Low);
            int ultra = BasisRTAOSceneSettings.BakeIntervalForQuality(BasisRTAOQuality.Ultra);

            Assert.Greater(low, ultra);
            Assert.AreEqual(1, ultra,
                "A budget of 100 buys nothing if every avatar is still rate limited, so Ultra has to allow a re-pose every frame.");
        }

        [Test]
        public void QualityDrivesBothHalvesOfTheBakeBudget()
        {
            foreach (BasisRTAOQuality quality in System.Enum.GetValues(typeof(BasisRTAOQuality)))
            {
                BasisRTAOSceneSettings settings = BasisRTAOSceneSettings.FromQuality(quality);
                Assert.AreEqual(BasisRTAOSceneSettings.BakeBudgetForQuality(quality), settings.skinnedBakesPerFrame);
                Assert.AreEqual(BasisRTAOSceneSettings.BakeIntervalForQuality(quality), settings.skinnedBakeInterval);
            }
        }

        [Test]
        public void SceneSettingsValidationClampsTheBudget()
        {
            BasisRTAOSceneSettings settings = BasisRTAOSceneSettings.Default;
            settings.skinnedBakesPerFrame = -5;
            Assert.AreEqual(0, settings.Validated().skinnedBakesPerFrame, "Zero is a valid budget: it means nobody is re-posed.");

            settings.skinnedBakesPerFrame = 9999;
            Assert.AreEqual(128, settings.Validated().skinnedBakesPerFrame);

            settings.skinnedBakeInterval = 0;
            Assert.AreEqual(1, settings.Validated().skinnedBakeInterval, "An interval of zero would divide the rate limit by nothing.");
        }

        [Test]
        public void SceneDefaultsBakeSkinnedMeshes()
        {
            Assert.AreEqual(BasisRTAOSceneSettings.AvatarLayerMask, BasisRTAOSceneSettings.Default.layerMask.value,
                "This system is only used on avatars, so the default must not be paying to trace the world.");

            Assert.AreEqual(BasisRTAOSkinnedMode.Dynamic, BasisRTAOSceneSettings.Default.skinnedMode,
                "Avatars are the thing people look at, so they cast occlusion by default; the per frame bake budget is what keeps it affordable.");
            Assert.Greater(BasisRTAOSceneSettings.Default.skinnedBakesPerFrame, 0, "Dynamic skinned mode needs a bake budget to do anything.");
            Assert.Greater(BasisRTAOSceneSettings.Default.skinnedBakeInterval, 1, "Re-baking every avatar every frame is not a budget.");
            Assert.GreaterOrEqual(BasisRTAOSceneSettings.Default.skinnedMaxDistance, 14f,
                "Basis stops an avatar casting shadows at mesh LOD 2, roughly 14 m, so the pose budget should reach at least that far or remotes stop being re-posed while they are still plainly visible.");
        }
    }
}
