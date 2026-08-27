using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.GlobalIllumination
{
    public class BasisGlobalIlluminationVolumeTests
    {
        private BasisGlobalIlluminationVolume volume;

        [SetUp]
        public void SetUp()
        {
            volume = ScriptableObject.CreateInstance<BasisGlobalIlluminationVolume>();
        }

        [TearDown]
        public void TearDown()
        {
            if (volume != null) { Object.DestroyImmediate(volume); }
        }

        [Test]
        public void EffectIsOffByDefault()
        {
            Assert.IsFalse(volume.enable.value);
            Assert.IsFalse(volume.IsActive());
        }

        [Test]
        public void EnabledWithZeroIntensityIsNotActive()
        {
            volume.enable.value = true;
            volume.intensity.value = 0f;
            Assert.IsFalse(volume.IsActive());
        }

        [Test]
        public void EnabledWithIntensityIsActive()
        {
            volume.enable.value = true;
            volume.intensity.value = 1f;
            Assert.IsTrue(volume.IsActive());
        }

        [TestCase(BasisGlobalIlluminationQuality.Low, 1, 12)]
        [TestCase(BasisGlobalIlluminationQuality.Medium, 2, 20)]
        [TestCase(BasisGlobalIlluminationQuality.High, 4, 32)]
        [TestCase(BasisGlobalIlluminationQuality.Ultra, 8, 48)]
        public void QualityDrivesRayBudget(BasisGlobalIlluminationQuality quality, int expectedRays, int expectedSteps)
        {
            volume.quality.value = quality;
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
                volume.quality.value = ladder[index];
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
            volume.quality.value = BasisGlobalIlluminationQuality.Low;
            volume.overrideQualityCounts.value = true;
            volume.rayCount.value = 7;
            volume.rayMaxSteps.value = 41;
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
                volume.quality.value = ladder[index];
                Assert.LessOrEqual(volume.ResolvedMaxEmitters(), BasisGlobalIlluminationPass.MaxEmitters);
            }
        }

        [TestCase(BasisGlobalIlluminationResolution.Full, 1)]
        [TestCase(BasisGlobalIlluminationResolution.Half, 2)]
        [TestCase(BasisGlobalIlluminationResolution.Quarter, 4)]
        public void ResolutionDivisorMatchesTheEnum(BasisGlobalIlluminationResolution resolution, int expected)
        {
            volume.resolution.value = resolution;
            Assert.AreEqual(expected, volume.ResolvedResolutionDivisor());
        }

        [Test]
        public void RayCountRangeCoversEveryQualityTier()
        {
            Assert.LessOrEqual(BasisGlobalIlluminationVolume.RayCountMin, 1);
            volume.quality.value = BasisGlobalIlluminationQuality.Ultra;
            Assert.LessOrEqual(volume.ResolvedRayCount(), BasisGlobalIlluminationVolume.RayCountMax);
            Assert.LessOrEqual(volume.ResolvedRaySteps(), BasisGlobalIlluminationVolume.RayStepsMax);
        }

        [Test]
        public void DefaultsSitInsideTheirOwnRanges()
        {
            Assert.GreaterOrEqual(volume.intensity.value, BasisGlobalIlluminationVolume.IntensityMin);
            Assert.LessOrEqual(volume.intensity.value, BasisGlobalIlluminationVolume.IntensityMax);
            Assert.GreaterOrEqual(volume.temporalResponse.value, BasisGlobalIlluminationVolume.TemporalResponseMin);
            Assert.LessOrEqual(volume.temporalResponse.value, BasisGlobalIlluminationVolume.TemporalResponseMax);
            Assert.GreaterOrEqual(volume.maxRayLength.value, BasisGlobalIlluminationVolume.RayLengthMin);
            Assert.LessOrEqual(volume.maxRayLength.value, BasisGlobalIlluminationVolume.RayLengthMax);
            Assert.GreaterOrEqual(volume.thickness.value, BasisGlobalIlluminationVolume.ThicknessMin);
            Assert.LessOrEqual(volume.thickness.value, BasisGlobalIlluminationVolume.ThicknessMax);
            Assert.GreaterOrEqual(volume.fireflyClamp.value, BasisGlobalIlluminationVolume.FireflyClampMin);
            Assert.LessOrEqual(volume.fireflyClamp.value, BasisGlobalIlluminationVolume.FireflyClampMax);
        }
    }
}
