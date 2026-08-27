using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.GlobalIllumination
{
    public class BasisGlobalIlluminationSettingsTests
    {
        private BasisGlobalIlluminationSettings volume;

        [SetUp]
        public void SetUp()
        {
            volume = new BasisGlobalIlluminationSettings();
        }

        [Test]
        public void EffectIsOffByDefault()
        {
            Assert.IsFalse(volume.enable);
            Assert.IsFalse(volume.IsActive());
        }

        [Test]
        public void EnabledWithZeroIntensityIsNotActive()
        {
            volume.enable = true;
            volume.intensity = 0f;
            Assert.IsFalse(volume.IsActive());
        }

        [Test]
        public void EnabledWithIntensityIsActive()
        {
            volume.enable = true;
            volume.intensity = 1f;
            Assert.IsTrue(volume.IsActive());
        }

        [TestCase(BasisGlobalIlluminationQuality.Low, 1, 12)]
        [TestCase(BasisGlobalIlluminationQuality.Medium, 2, 20)]
        [TestCase(BasisGlobalIlluminationQuality.High, 4, 32)]
        [TestCase(BasisGlobalIlluminationQuality.Ultra, 8, 48)]
        public void QualityDrivesRayBudget(BasisGlobalIlluminationQuality quality, int expectedRays, int expectedSteps)
        {
            volume.quality = quality;
            Assert.AreEqual(expectedRays, volume.ResolvedRayCount());
            Assert.AreEqual(expectedSteps, volume.ResolvedRaySteps());
        }

        [Test]
        public void QualityBudgetIsMonotonic()
        {
            int previousRays = 0, previousSteps = 0, previousEmitters = 0;
            BasisGlobalIlluminationQuality[] ladder =
            {
                BasisGlobalIlluminationQuality.Low,
                BasisGlobalIlluminationQuality.Medium,
                BasisGlobalIlluminationQuality.High,
                BasisGlobalIlluminationQuality.Ultra
            };
            for (int index = 0; index < ladder.Length; index++)
            {
                volume.quality = ladder[index];
                Assert.Greater(volume.ResolvedRayCount(), previousRays);
                Assert.Greater(volume.ResolvedRaySteps(), previousSteps);
                Assert.Greater(volume.ResolvedMaxEmitters(), previousEmitters);
                previousRays = volume.ResolvedRayCount();
                previousSteps = volume.ResolvedRaySteps();
                previousEmitters = volume.ResolvedMaxEmitters();
            }
        }

        [Test]
        public void OverrideTakesPrecedenceOverQuality()
        {
            volume.quality = BasisGlobalIlluminationQuality.Low;
            volume.overrideQualityCounts = true;
            volume.rayCount = 7;
            volume.rayMaxSteps = 41;
            Assert.AreEqual(7, volume.ResolvedRayCount());
            Assert.AreEqual(41, volume.ResolvedRaySteps());
        }

        [Test]
        public void MaxEmittersNeverExceedsTheShaderArray()
        {
            BasisGlobalIlluminationQuality[] ladder =
            {
                BasisGlobalIlluminationQuality.Low,
                BasisGlobalIlluminationQuality.Medium,
                BasisGlobalIlluminationQuality.High,
                BasisGlobalIlluminationQuality.Ultra
            };
            for (int index = 0; index < ladder.Length; index++)
            {
                volume.quality = ladder[index];
                Assert.LessOrEqual(volume.ResolvedMaxEmitters(), BasisGlobalIlluminationPass.MaxEmitters);
            }
        }

        [TestCase(BasisGlobalIlluminationResolution.Full, 1)]
        [TestCase(BasisGlobalIlluminationResolution.Half, 2)]
        [TestCase(BasisGlobalIlluminationResolution.Quarter, 4)]
        public void ResolutionDivisorMatchesTheEnum(BasisGlobalIlluminationResolution resolution, int expected)
        {
            volume.resolution = resolution;
            Assert.AreEqual(expected, volume.ResolvedResolutionDivisor());
        }

        [Test]
        public void RayCountRangeCoversEveryQualityTier()
        {
            Assert.LessOrEqual(BasisGlobalIlluminationSettings.RayCountMin, 1);
            volume.quality = BasisGlobalIlluminationQuality.Ultra;
            Assert.LessOrEqual(volume.ResolvedRayCount(), BasisGlobalIlluminationSettings.RayCountMax);
            Assert.LessOrEqual(volume.ResolvedRaySteps(), BasisGlobalIlluminationSettings.RayStepsMax);
        }

        [Test]
        public void DefaultsSitInsideTheirOwnRanges()
        {
            Assert.GreaterOrEqual(volume.intensity, BasisGlobalIlluminationSettings.IntensityMin);
            Assert.LessOrEqual(volume.intensity, BasisGlobalIlluminationSettings.IntensityMax);
            Assert.GreaterOrEqual(volume.temporalResponse, BasisGlobalIlluminationSettings.TemporalResponseMin);
            Assert.LessOrEqual(volume.temporalResponse, BasisGlobalIlluminationSettings.TemporalResponseMax);
            Assert.GreaterOrEqual(volume.maxRayLength, BasisGlobalIlluminationSettings.RayLengthMin);
            Assert.LessOrEqual(volume.maxRayLength, BasisGlobalIlluminationSettings.RayLengthMax);
            Assert.GreaterOrEqual(volume.thickness, BasisGlobalIlluminationSettings.ThicknessMin);
            Assert.LessOrEqual(volume.thickness, BasisGlobalIlluminationSettings.ThicknessMax);
            Assert.GreaterOrEqual(volume.fireflyClamp, BasisGlobalIlluminationSettings.FireflyClampMin);
            Assert.LessOrEqual(volume.fireflyClamp, BasisGlobalIlluminationSettings.FireflyClampMax);
            Assert.GreaterOrEqual(volume.specularIntensity, BasisGlobalIlluminationSettings.IntensityMin);
            Assert.LessOrEqual(volume.specularIntensity, BasisGlobalIlluminationSettings.IntensityMax);
            Assert.GreaterOrEqual(volume.specularMaxRoughness, BasisGlobalIlluminationSettings.SpecularRoughnessMin);
            Assert.LessOrEqual(volume.specularMaxRoughness, BasisGlobalIlluminationSettings.SpecularRoughnessMax);
            Assert.GreaterOrEqual(volume.specularRayLength, BasisGlobalIlluminationSettings.RayLengthMin);
            Assert.LessOrEqual(volume.specularRayLength, BasisGlobalIlluminationSettings.SpecularRayLengthMax);
            Assert.GreaterOrEqual(volume.specularBounces, BasisGlobalIlluminationSettings.BouncesMin);
            Assert.LessOrEqual(volume.specularBounces, BasisGlobalIlluminationSettings.BouncesMax);
        }

        [Test]
        public void ReflectionsAreOffByDefault()
        {
            volume.enable = true;
            Assert.IsFalse(volume.specular);
            Assert.IsFalse(volume.SpecularActive());
        }

        /// <summary>
        /// The two gathers are independent switches, and each has to be able to run without the other:
        /// reflections are worth having over a screen space diffuse gather, and the diffuse gather is worth
        /// having without them. IsActive is the union, because it is what decides whether the feature
        /// enqueues anything at all.
        /// </summary>
        [Test]
        public void DiffuseAndReflectionsGateIndependently()
        {
            volume.enable = true;

            volume.intensity = 1f;
            volume.specular = false;
            Assert.IsTrue(volume.DiffuseActive());
            Assert.IsFalse(volume.SpecularActive());
            Assert.IsTrue(volume.IsActive());

            volume.intensity = 0f;
            volume.specular = true;
            volume.specularIntensity = 1f;
            Assert.IsFalse(volume.DiffuseActive(), "a zero diffuse intensity still has to mean the diffuse gather is off");
            Assert.IsTrue(volume.SpecularActive());
            Assert.IsTrue(volume.IsActive(), "reflections alone have to keep the feature enqueuing its passes");

            volume.specularIntensity = 0f;
            Assert.IsFalse(volume.SpecularActive());
            Assert.IsFalse(volume.IsActive());
        }

        /// <summary>The component's own switch still turns everything off, reflections included.</summary>
        [Test]
        public void DisablingTheComponentTurnsReflectionsOffToo()
        {
            volume.enable = false;
            volume.specular = true;
            volume.specularIntensity = 1f;
            Assert.IsFalse(volume.SpecularActive());
            Assert.IsFalse(volume.IsActive());
        }

        /// <summary>
        /// Reflections are independent of Mode. The trace needs the ray traced backend either way, but a
        /// world running the screen space diffuse gather must still be able to ask for them.
        /// </summary>
        [Test]
        public void ReflectionsDoNotDependOnTheDiffuseMode()
        {
            volume.enable = true;
            volume.specular = true;
            volume.specularIntensity = 1f;

            volume.mode = BasisGlobalIlluminationMode.ScreenSpace;
            Assert.IsTrue(volume.SpecularActive());
            Assert.IsFalse(volume.IsRayTraced());

            volume.mode = BasisGlobalIlluminationMode.RayTraced;
            Assert.IsTrue(volume.SpecularActive());
        }
    }
}
