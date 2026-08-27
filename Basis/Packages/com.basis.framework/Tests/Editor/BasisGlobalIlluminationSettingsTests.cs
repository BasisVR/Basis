// Global illumination is optional: the define comes from the com.basis.globalillumination package
// being present (asmdef versionDefines), and the effect is not viable on mobile GPUs, so the whole
// integration compiles out on Android.
#if BASIS_HAS_GI && !UNITY_ANDROID
using System;
using System.Globalization;
using Basis.BasisUI;
using Basis.Scripts.Drivers;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Basis.Tests.Graphics
{
    public class BasisGlobalIlluminationSettingsTests
    {
        private GameObject host;
        private Func<UnityEngine.Camera, bool> previousFilter;
        private UnityEngine.Camera previousCameraInstance;

        [SetUp]
        public void SetUp()
        {
            previousFilter = BasisGlobalIlluminationFeature.CameraFilter;
            previousCameraInstance = BasisLocalCameraDriver.CameraInstance;
        }

        [TearDown]
        public void TearDown()
        {
            BasisGlobalIlluminationFeature.CameraFilter = previousFilter;
            BasisLocalCameraDriver.CameraInstance = previousCameraInstance;
            if (host != null)
            {
                SMModuleGlobalIlluminationURP module = host.GetComponent<SMModuleGlobalIlluminationURP>();
                if (module != null && module.Volume != null && module.Volume.sharedProfile != null)
                {
                    UnityEngine.Object.DestroyImmediate(module.Volume.sharedProfile);
                }
                UnityEngine.Object.DestroyImmediate(host);
                host = null;
            }
        }

        private SMModuleGlobalIlluminationURP NewModule()
        {
            host = new GameObject("gi-settings-module");
            return host.AddComponent<SMModuleGlobalIlluminationURP>();
        }

        private static BasisGlobalIlluminationVolume NewVolume()
        {
            return ScriptableObject.CreateInstance<BasisGlobalIlluminationVolume>();
        }

        private static string Invariant(float value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        // ----- defaults -----

        [Test]
        public void GlobalIlluminationIsOffByDefault()
        {
            Assert.IsFalse(BasisSettingsDefaults.UseGlobalIllumination.DefaultValue.GetDefault());
            Assert.AreEqual("Medium", BasisSettingsDefaults.GlobalIlluminationQuality.DefaultValue.GetDefault());
            Assert.AreEqual("Half", BasisSettingsDefaults.GlobalIlluminationResolution.DefaultValue.GetDefault());
            Assert.AreEqual("Reflection Probe", BasisSettingsDefaults.GlobalIlluminationFallback.DefaultValue.GetDefault());
            Assert.AreEqual(1f, BasisSettingsDefaults.GlobalIlluminationIntensity.DefaultValue.GetDefault());
        }

        [Test]
        public void NoSliderUsesZeroAsItsDefaultOrItsMinimum()
        {
            // A slider whose bottom is 0 makes "off" a magic value on a continuous control; Basis uses a
            // toggle plus a slider for that instead.
            AssertSliderRange(BasisSettingsDefaults.GI_INTENSITY_MIN, BasisSettingsDefaults.GI_INTENSITY_MAX, BasisSettingsDefaults.GlobalIlluminationIntensity.DefaultValue.GetDefault());
            AssertSliderRange(BasisSettingsDefaults.GI_SATURATION_MIN, BasisSettingsDefaults.GI_SATURATION_MAX, BasisSettingsDefaults.GlobalIlluminationSaturation.DefaultValue.GetDefault());
            AssertSliderRange(BasisSettingsDefaults.GI_OBSCURANCE_MIN, BasisSettingsDefaults.GI_OBSCURANCE_MAX, BasisSettingsDefaults.GlobalIlluminationObscurance.DefaultValue.GetDefault());
            AssertSliderRange(BasisSettingsDefaults.GI_RAY_LENGTH_MIN, BasisSettingsDefaults.GI_RAY_LENGTH_MAX, BasisSettingsDefaults.GlobalIlluminationRayLength.DefaultValue.GetDefault());
            AssertSliderRange(BasisSettingsDefaults.GI_SMOOTHING_MIN, BasisSettingsDefaults.GI_SMOOTHING_MAX, BasisSettingsDefaults.GlobalIlluminationSmoothing.DefaultValue.GetDefault());
            AssertSliderRange(BasisSettingsDefaults.GI_TEMPORAL_RESPONSE_MIN, BasisSettingsDefaults.GI_TEMPORAL_RESPONSE_MAX, BasisSettingsDefaults.GlobalIlluminationTemporalResponse.DefaultValue.GetDefault());
            AssertSliderRange(BasisSettingsDefaults.GI_EMITTER_INTENSITY_MIN, BasisSettingsDefaults.GI_EMITTER_INTENSITY_MAX, BasisSettingsDefaults.GlobalIlluminationEmitterIntensity.DefaultValue.GetDefault());
        }

        private static void AssertSliderRange(float min, float max, float value)
        {
            Assert.Greater(min, 0f, "slider minimum is zero");
            Assert.Greater(max, min);
            Assert.GreaterOrEqual(value, min);
            Assert.LessOrEqual(value, max);
            Assert.Greater(value, 0f, "slider default is zero");
        }

        [Test]
        public void EverySettingsRangeSitsInsideTheVolumeParameterRangeItDrives()
        {
            // A binding range wider than the volume's own would silently clip in the inspector and read
            // back a different value than the player chose.
            Assert.GreaterOrEqual(BasisSettingsDefaults.GI_INTENSITY_MIN, BasisGlobalIlluminationVolume.IntensityMin);
            Assert.LessOrEqual(BasisSettingsDefaults.GI_INTENSITY_MAX, BasisGlobalIlluminationVolume.IntensityMax);
            Assert.GreaterOrEqual(BasisSettingsDefaults.GI_SATURATION_MIN, BasisGlobalIlluminationVolume.SaturationMin);
            Assert.LessOrEqual(BasisSettingsDefaults.GI_SATURATION_MAX, BasisGlobalIlluminationVolume.SaturationMax);
            Assert.GreaterOrEqual(BasisSettingsDefaults.GI_OBSCURANCE_MIN, BasisGlobalIlluminationVolume.ObscuranceMin);
            Assert.LessOrEqual(BasisSettingsDefaults.GI_OBSCURANCE_MAX, BasisGlobalIlluminationVolume.ObscuranceMax);
            Assert.GreaterOrEqual(BasisSettingsDefaults.GI_RAY_LENGTH_MIN, BasisGlobalIlluminationVolume.RayLengthMin);
            Assert.LessOrEqual(BasisSettingsDefaults.GI_RAY_LENGTH_MAX, BasisGlobalIlluminationVolume.RayLengthMax);
            Assert.GreaterOrEqual(BasisSettingsDefaults.GI_SMOOTHING_MIN, BasisGlobalIlluminationVolume.SmoothingMin);
            Assert.LessOrEqual(BasisSettingsDefaults.GI_SMOOTHING_MAX, BasisGlobalIlluminationVolume.SmoothingMax);
            Assert.GreaterOrEqual(BasisSettingsDefaults.GI_TEMPORAL_RESPONSE_MIN, BasisGlobalIlluminationVolume.TemporalResponseMin);
            Assert.LessOrEqual(BasisSettingsDefaults.GI_TEMPORAL_RESPONSE_MAX, BasisGlobalIlluminationVolume.TemporalResponseMax);
            Assert.GreaterOrEqual(BasisSettingsDefaults.GI_EMITTER_INTENSITY_MIN, BasisGlobalIlluminationVolume.EmitterIntensityMin);
            Assert.LessOrEqual(BasisSettingsDefaults.GI_EMITTER_INTENSITY_MAX, BasisGlobalIlluminationVolume.EmitterIntensityMax);
        }

        // ----- dropdown parsing -----

        [TestCase("low", BasisGlobalIlluminationQuality.Low)]
        [TestCase("Medium", BasisGlobalIlluminationQuality.Medium)]
        [TestCase("HIGH", BasisGlobalIlluminationQuality.High)]
        [TestCase("UlTrA", BasisGlobalIlluminationQuality.Ultra)]
        [TestCase("garbage", BasisGlobalIlluminationQuality.Medium)]
        [TestCase("", BasisGlobalIlluminationQuality.Medium)]
        [TestCase(null, BasisGlobalIlluminationQuality.Medium)]
        public void QualityDropdownValuesParseCaseInsensitively(string option, BasisGlobalIlluminationQuality expected)
        {
            Assert.AreEqual(expected, SMModuleGlobalIlluminationURP.ReadQuality(option));
        }

        [TestCase("Screen Space", BasisGlobalIlluminationMode.ScreenSpace)]
        [TestCase("ray traced", BasisGlobalIlluminationMode.RayTraced)]
        [TestCase("RayTraced", BasisGlobalIlluminationMode.RayTraced)]
        [TestCase("garbage", BasisGlobalIlluminationMode.ScreenSpace)]
        [TestCase("", BasisGlobalIlluminationMode.ScreenSpace)]
        [TestCase(null, BasisGlobalIlluminationMode.ScreenSpace)]
        public void ModeDropdownValuesParseCaseInsensitively(string option, BasisGlobalIlluminationMode expected)
        {
            Assert.AreEqual(expected, SMModuleGlobalIlluminationURP.ReadMode(option));
        }

        [TestCase("Off", BasisGlobalIlluminationRaySkinnedMode.Off)]
        [TestCase("static", BasisGlobalIlluminationRaySkinnedMode.Static)]
        [TestCase("DYNAMIC", BasisGlobalIlluminationRaySkinnedMode.Dynamic)]
        [TestCase("garbage", BasisGlobalIlluminationRaySkinnedMode.Dynamic)]
        [TestCase(null, BasisGlobalIlluminationRaySkinnedMode.Dynamic)]
        public void SkinnedMeshDropdownValuesParseCaseInsensitively(string option, BasisGlobalIlluminationRaySkinnedMode expected)
        {
            Assert.AreEqual(expected, SMModuleGlobalIlluminationURP.ReadSkinnedMode(option));
        }

        [TestCase("Full", BasisGlobalIlluminationResolution.Full)]
        [TestCase("half", BasisGlobalIlluminationResolution.Half)]
        [TestCase("QUARTER", BasisGlobalIlluminationResolution.Quarter)]
        [TestCase("garbage", BasisGlobalIlluminationResolution.Half)]
        [TestCase(null, BasisGlobalIlluminationResolution.Half)]
        public void ResolutionDropdownValuesParseCaseInsensitively(string option, BasisGlobalIlluminationResolution expected)
        {
            Assert.AreEqual(expected, SMModuleGlobalIlluminationURP.ReadResolution(option));
        }

        [TestCase("None", BasisGlobalIlluminationFallback.None)]
        [TestCase("sky", BasisGlobalIlluminationFallback.Sky)]
        [TestCase("Reflection Probe", BasisGlobalIlluminationFallback.ReflectionProbe)]
        [TestCase("garbage", BasisGlobalIlluminationFallback.ReflectionProbe)]
        [TestCase(null, BasisGlobalIlluminationFallback.ReflectionProbe)]
        public void FallbackDropdownValuesParseCaseInsensitively(string option, BasisGlobalIlluminationFallback expected)
        {
            Assert.AreEqual(expected, SMModuleGlobalIlluminationURP.ReadFallback(option));
        }

        [TestCase("Off", BasisGlobalIlluminationDebugView.None)]
        [TestCase("indirect", BasisGlobalIlluminationDebugView.Indirect)]
        [TestCase("Obscurance", BasisGlobalIlluminationDebugView.Obscurance)]
        [TestCase("NORMALS", BasisGlobalIlluminationDebugView.Normals)]
        [TestCase("ray hits", BasisGlobalIlluminationDebugView.RayHits)]
        [TestCase("Indirect Only", BasisGlobalIlluminationDebugView.IndirectOnly)]
        [TestCase("garbage", BasisGlobalIlluminationDebugView.None)]
        [TestCase(null, BasisGlobalIlluminationDebugView.None)]
        public void DebugViewDropdownValuesParse(string option, BasisGlobalIlluminationDebugView expected)
        {
            Assert.AreEqual(expected, SMModuleGlobalIlluminationURP.ReadDebugView(option));
        }

        [Test]
        public void EveryDropdownDefaultParsesBackToItsOwnValue()
        {
            // A default string that does not round-trip silently lands on the parser's fallback, so the
            // menu would show one option while the effect ran another.
            Assert.AreEqual(BasisGlobalIlluminationQuality.Medium, SMModuleGlobalIlluminationURP.ReadQuality(BasisSettingsDefaults.GlobalIlluminationQuality.DefaultValue.GetDefault()));
            Assert.AreEqual(BasisGlobalIlluminationResolution.Half, SMModuleGlobalIlluminationURP.ReadResolution(BasisSettingsDefaults.GlobalIlluminationResolution.DefaultValue.GetDefault()));
            Assert.AreEqual(BasisGlobalIlluminationFallback.ReflectionProbe, SMModuleGlobalIlluminationURP.ReadFallback(BasisSettingsDefaults.GlobalIlluminationFallback.DefaultValue.GetDefault()));
            Assert.AreEqual(BasisGlobalIlluminationDebugView.None, SMModuleGlobalIlluminationURP.ReadDebugView(BasisSettingsDefaults.DevGiDebugView.DefaultValue.GetDefault()));
        }

        [Test]
        public void DebugViewIsOffByDefault()
        {
            Assert.AreEqual("Off", BasisSettingsDefaults.DevGiDebugView.DefaultValue.GetDefault());
        }

        // ----- state -----

        [Test]
        public void StateFromDefaultsMatchesTheBindings()
        {
            BasisGlobalIlluminationState state = BasisGlobalIlluminationState.FromDefaults();
            Assert.AreEqual(BasisSettingsDefaults.UseGlobalIllumination.DefaultValue.GetDefault(), state.Enabled);
            Assert.AreEqual(BasisGlobalIlluminationQuality.Medium, state.Quality);
            Assert.AreEqual(BasisGlobalIlluminationResolution.Half, state.Resolution);
            Assert.AreEqual(BasisGlobalIlluminationFallback.ReflectionProbe, state.Fallback);
            Assert.AreEqual(BasisSettingsDefaults.GlobalIlluminationIntensity.DefaultValue.GetDefault(), state.Intensity);
            Assert.AreEqual(BasisSettingsDefaults.GlobalIlluminationTemporalResponse.DefaultValue.GetDefault(), state.TemporalResponse);
            Assert.IsTrue(state.TemporalFilter);
            Assert.IsTrue(state.Emitters);
            Assert.IsFalse(state.ReflectionProbes);
            Assert.IsFalse(state.Capture);
        }

        // ----- apply to the volume -----

        [Test]
        public void ApplyDrivesEveryPlayerFacingParameterAndOverridesIt()
        {
            BasisGlobalIlluminationVolume gi = NewVolume();
            try
            {
                BasisGlobalIlluminationState state = BasisGlobalIlluminationState.FromDefaults();
                state.Enabled = true;
                state.Quality = BasisGlobalIlluminationQuality.High;
                state.Resolution = BasisGlobalIlluminationResolution.Quarter;
                state.Fallback = BasisGlobalIlluminationFallback.Sky;
                state.Intensity = 1.5f;
                state.Saturation = 0.5f;
                state.Obscurance = 0.75f;
                state.RayLength = 32f;
                state.Smoothing = 1.5f;
                state.TemporalResponse = 0.4f;
                state.EmitterIntensity = 2f;
                SMModuleGlobalIlluminationURP.Apply(gi, state);

                Assert.IsTrue(gi.enable.overrideState);
                Assert.IsTrue(gi.enable.value);
                Assert.AreEqual(BasisGlobalIlluminationQuality.High, gi.quality.value);
                Assert.AreEqual(BasisGlobalIlluminationResolution.Quarter, gi.resolution.value);
                Assert.AreEqual(BasisGlobalIlluminationFallback.Sky, gi.fallback.value);
                Assert.AreEqual(1.5f, gi.intensity.value, 0.0001f);
                Assert.AreEqual(0.5f, gi.saturation.value, 0.0001f);
                Assert.AreEqual(0.75f, gi.obscuranceIntensity.value, 0.0001f);
                Assert.AreEqual(32f, gi.maxRayLength.value, 0.0001f);
                Assert.AreEqual(1.5f, gi.smoothing.value, 0.0001f);
                Assert.AreEqual(0.4f, gi.temporalResponse.value, 0.0001f);
                Assert.AreEqual(2f, gi.emitterIntensity.value, 0.0001f);

                Assert.IsTrue(gi.quality.overrideState);
                Assert.IsTrue(gi.resolution.overrideState);
                Assert.IsTrue(gi.fallback.overrideState);
                Assert.IsTrue(gi.intensity.overrideState);
                Assert.IsTrue(gi.saturation.overrideState);
                Assert.IsTrue(gi.obscuranceIntensity.overrideState);
                Assert.IsTrue(gi.maxRayLength.overrideState);
                Assert.IsTrue(gi.smoothing.overrideState);
                Assert.IsTrue(gi.temporalResponse.overrideState);
                Assert.IsTrue(gi.temporalFilter.overrideState);
                Assert.IsTrue(gi.emitterIntensity.overrideState);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gi);
            }
        }

        [Test]
        public void ApplyLeavesTheArtisticParametersToTheWorld()
        {
            // Thickness, jitter, the firefly clamp, the depth rejection threshold and the tint are
            // authoring decisions, so a world's own volume still wins on them.
            BasisGlobalIlluminationVolume gi = NewVolume();
            try
            {
                BasisGlobalIlluminationState state = BasisGlobalIlluminationState.FromDefaults();
                state.Enabled = true;
                SMModuleGlobalIlluminationURP.Apply(gi, state);

                Assert.IsFalse(gi.thickness.overrideState);
                Assert.IsFalse(gi.jitter.overrideState);
                Assert.IsFalse(gi.fireflyClamp.overrideState);
                Assert.IsFalse(gi.depthRejection.overrideState);
                Assert.IsFalse(gi.tint.overrideState);
                Assert.IsFalse(gi.fadeDistance.overrideState);
                Assert.IsFalse(gi.obscuranceRadius.overrideState);
                Assert.IsFalse(gi.normalSource.overrideState);
                Assert.IsFalse(gi.bilateralUpsample.overrideState);
                Assert.IsFalse(gi.neighbourhoodClamp.overrideState);
                Assert.IsFalse(gi.fallbackIntensity.overrideState);
                Assert.IsFalse(gi.emitterOcclusion.overrideState);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gi);
            }
        }

        [Test]
        public void EverySliderIsClampedToItsSettingsRange()
        {
            BasisGlobalIlluminationVolume gi = NewVolume();
            try
            {
                BasisGlobalIlluminationState high = BasisGlobalIlluminationState.FromDefaults();
                high.Enabled = true;
                high.Intensity = 999f;
                high.Saturation = 999f;
                high.Obscurance = 999f;
                high.RayLength = 999f;
                high.Smoothing = 999f;
                high.TemporalResponse = 999f;
                high.EmitterIntensity = 999f;
                SMModuleGlobalIlluminationURP.Apply(gi, high);

                Assert.AreEqual(BasisSettingsDefaults.GI_INTENSITY_MAX, gi.intensity.value, 0.0001f);
                Assert.AreEqual(BasisSettingsDefaults.GI_SATURATION_MAX, gi.saturation.value, 0.0001f);
                Assert.AreEqual(BasisSettingsDefaults.GI_OBSCURANCE_MAX, gi.obscuranceIntensity.value, 0.0001f);
                Assert.AreEqual(BasisSettingsDefaults.GI_RAY_LENGTH_MAX, gi.maxRayLength.value, 0.0001f);
                Assert.AreEqual(BasisSettingsDefaults.GI_SMOOTHING_MAX, gi.smoothing.value, 0.0001f);
                Assert.AreEqual(BasisSettingsDefaults.GI_TEMPORAL_RESPONSE_MAX, gi.temporalResponse.value, 0.0001f);
                Assert.AreEqual(BasisSettingsDefaults.GI_EMITTER_INTENSITY_MAX, gi.emitterIntensity.value, 0.0001f);

                BasisGlobalIlluminationState low = BasisGlobalIlluminationState.FromDefaults();
                low.Enabled = true;
                low.Intensity = -5f;
                low.Saturation = -5f;
                low.Obscurance = -5f;
                low.RayLength = -5f;
                low.Smoothing = -5f;
                low.TemporalResponse = -5f;
                low.EmitterIntensity = -5f;
                SMModuleGlobalIlluminationURP.Apply(gi, low);

                Assert.AreEqual(BasisSettingsDefaults.GI_INTENSITY_MIN, gi.intensity.value, 0.0001f);
                Assert.AreEqual(BasisSettingsDefaults.GI_SATURATION_MIN, gi.saturation.value, 0.0001f);
                Assert.AreEqual(BasisSettingsDefaults.GI_OBSCURANCE_MIN, gi.obscuranceIntensity.value, 0.0001f);
                Assert.AreEqual(BasisSettingsDefaults.GI_RAY_LENGTH_MIN, gi.maxRayLength.value, 0.0001f);
                Assert.AreEqual(BasisSettingsDefaults.GI_SMOOTHING_MIN, gi.smoothing.value, 0.0001f);
                Assert.AreEqual(BasisSettingsDefaults.GI_TEMPORAL_RESPONSE_MIN, gi.temporalResponse.value, 0.0001f);
                Assert.AreEqual(BasisSettingsDefaults.GI_EMITTER_INTENSITY_MIN, gi.emitterIntensity.value, 0.0001f);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gi);
            }
        }

        [Test]
        public void DisablingKeepsTheOverrideSoAWorldCannotForceItBackOn()
        {
            BasisGlobalIlluminationVolume gi = NewVolume();
            try
            {
                BasisGlobalIlluminationState state = BasisGlobalIlluminationState.FromDefaults();
                state.Enabled = false;
                SMModuleGlobalIlluminationURP.Apply(gi, state);

                Assert.IsTrue(gi.active);
                Assert.IsTrue(gi.enable.overrideState);
                Assert.IsFalse(gi.enable.value);
                Assert.IsFalse(gi.IsActive());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gi);
            }
        }

        [Test]
        public void ACaptureRaisesResolutionAndRayBudgetOnlyWhileItLasts()
        {
            BasisGlobalIlluminationVolume gi = NewVolume();
            try
            {
                BasisGlobalIlluminationState state = BasisGlobalIlluminationState.FromDefaults();
                state.Enabled = true;
                state.Quality = BasisGlobalIlluminationQuality.Low;
                state.Resolution = BasisGlobalIlluminationResolution.Quarter;
                state.Capture = true;
                SMModuleGlobalIlluminationURP.Apply(gi, state);

                Assert.AreEqual(BasisGlobalIlluminationResolution.Full, gi.resolution.value);
                Assert.IsTrue(gi.overrideQualityCounts.value);
                Assert.AreEqual(SMModuleGlobalIlluminationURP.CaptureRayCount, gi.ResolvedRayCount());
                Assert.AreEqual(SMModuleGlobalIlluminationURP.CaptureRaySteps, gi.ResolvedRaySteps());
                Assert.AreEqual(BasisGlobalIlluminationQuality.Low, gi.quality.value);

                state.Capture = false;
                SMModuleGlobalIlluminationURP.Apply(gi, state);
                Assert.AreEqual(BasisGlobalIlluminationResolution.Quarter, gi.resolution.value);
                Assert.IsFalse(gi.overrideQualityCounts.value);
                Assert.AreEqual(1, gi.ResolvedRayCount());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gi);
            }
        }

        [Test]
        public void ACaptureTurnsTheTemporalFilterOffBecauseAPhotoHasNoHistory()
        {
            BasisGlobalIlluminationVolume gi = NewVolume();
            try
            {
                BasisGlobalIlluminationState state = BasisGlobalIlluminationState.FromDefaults();
                state.Enabled = true;
                state.TemporalFilter = true;
                state.Capture = true;
                SMModuleGlobalIlluminationURP.Apply(gi, state);
                Assert.IsFalse(gi.temporalFilter.value);

                state.Capture = false;
                SMModuleGlobalIlluminationURP.Apply(gi, state);
                Assert.IsTrue(gi.temporalFilter.value);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gi);
            }
        }

        [Test]
        public void CaptureRayBudgetIsAtLeastTheHighestQualityTier()
        {
            // A photo that traced fewer rays than the player's own quality tier would be noisier than
            // what they were looking at when they pressed the shutter.
            BasisGlobalIlluminationVolume gi = NewVolume();
            try
            {
                gi.quality.value = BasisGlobalIlluminationQuality.Ultra;
                Assert.GreaterOrEqual(SMModuleGlobalIlluminationURP.CaptureRayCount, gi.ResolvedRayCount());
                Assert.GreaterOrEqual(SMModuleGlobalIlluminationURP.CaptureRaySteps, gi.ResolvedRaySteps());
                Assert.LessOrEqual(SMModuleGlobalIlluminationURP.CaptureRayCount, BasisGlobalIlluminationVolume.RayCountMax);
                Assert.LessOrEqual(SMModuleGlobalIlluminationURP.CaptureRaySteps, BasisGlobalIlluminationVolume.RayStepsMax);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gi);
            }
        }

        [Test]
        public void ApplyingToANullVolumeIsHarmless()
        {
            Assert.DoesNotThrow(() => SMModuleGlobalIlluminationURP.Apply((BasisGlobalIlluminationVolume)null, BasisGlobalIlluminationState.FromDefaults()));
        }

        // ----- the feature -----

        [Test]
        public void TurningTheEffectOffDeactivatesTheRendererFeature()
        {
            // The master switch cannot be a volume value alone: a world volume or a pipeline default
            // profile the player's own volume never outranks would keep the effect rendering. URP skips
            // AddRenderPasses entirely for an inactive feature, so that is the switch that holds.
            BasisGlobalIlluminationFeature feature = ScriptableObject.CreateInstance<BasisGlobalIlluminationFeature>();
            try
            {
                SMModuleGlobalIlluminationURP.Apply(feature, false, false, true);
                Assert.IsFalse(feature.isActive);

                SMModuleGlobalIlluminationURP.Apply(feature, true, false, true);
                Assert.IsTrue(feature.isActive);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(feature);
            }
        }

        [Test]
        public void RendererOptionsReachTheFeature()
        {
            BasisGlobalIlluminationFeature feature = ScriptableObject.CreateInstance<BasisGlobalIlluminationFeature>();
            try
            {
                SMModuleGlobalIlluminationURP.Apply(feature, true, true, true);
                Assert.IsTrue(feature.ReflectionProbes);
                Assert.IsTrue(feature.Mirrors);

                SMModuleGlobalIlluminationURP.Apply(feature, true, false, false);
                Assert.IsFalse(feature.ReflectionProbes);
                Assert.IsFalse(feature.Mirrors, "switching mirrors off did not reach the feature, so the setting cannot turn a mirror's bounce back off");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(feature);
            }
        }

        [Test]
        public void ApplyingRendererOptionsWithoutAFeatureIsHarmless()
        {
            // Android ships a renderer without the feature, so the lookup returns null every frame there.
            Assert.DoesNotThrow(() => SMModuleGlobalIlluminationURP.Apply((BasisGlobalIlluminationFeature)null, true, true, true));
            Assert.IsNull(SMModuleGlobalIlluminationURP.FindFeature(null));
        }

        [Test]
        public void TheShippedRendererAndPipelineProfilesCarryTheEffectSwitchedOff()
        {
            // The setting defaults to off, so everything the project ships has to agree with it: a
            // renderer feature left active or a pipeline default profile left enabled renders bounce
            // light before the settings module has run at all.
            BasisGlobalIlluminationFeature feature = SMModuleGlobalIlluminationURP.FindFeature();
            if (feature == null)
            {
                Assert.Ignore("The active render pipeline carries no global illumination feature.");
            }
            Assert.IsFalse(feature.isActive, "The renderer ships with the global illumination feature active.");
            AssertProfileCarriesTheEffectOff(VolumeManager.instance.globalDefaultProfile);
            AssertProfileCarriesTheEffectOff(VolumeManager.instance.qualityDefaultProfile);
        }

        private static void AssertProfileCarriesTheEffectOff(VolumeProfile profile)
        {
            if (profile == null || !profile.TryGet(out BasisGlobalIlluminationVolume gi))
            {
                return;
            }
            Assert.IsFalse(gi.enable.value, profile.name + " ships with global illumination enabled.");
        }

        [Test]
        public void TheModuleDrivesTheFeatureActiveStateFromTheSetting()
        {
            SMModuleGlobalIlluminationURP module = NewModule();
            BasisGlobalIlluminationFeature feature = SMModuleGlobalIlluminationURP.FindFeature();
            if (feature == null)
            {
                Assert.Ignore("The active render pipeline carries no global illumination feature.");
            }
            bool authored = feature.isActive;
            try
            {
                module.ValidSettingsChange(BasisSettingsDefaults.UseGlobalIllumination.BindingKey, "true");
                Assert.IsTrue(feature.isActive);

                module.ValidSettingsChange(BasisSettingsDefaults.UseGlobalIllumination.BindingKey, "false");
                Assert.IsFalse(feature.isActive);
            }
            finally
            {
                feature.SetActive(authored);
            }
        }

        // ----- settings plumbing -----

        [Test]
        public void EverySliderReachesTheOwnedVolume()
        {
            // Regression shape: a slider persisted and threaded through the state but never read at the
            // point of application looks wired up and changes nothing on screen.
            SMModuleGlobalIlluminationURP module = NewModule();
            module.ApplyOverride();

            module.ValidSettingsChange(BasisSettingsDefaults.GlobalIlluminationIntensity.BindingKey, Invariant(BasisSettingsDefaults.GI_INTENSITY_MAX));
            Assert.AreEqual(BasisSettingsDefaults.GI_INTENSITY_MAX, module.GlobalIllumination.intensity.value, 0.0001f);

            module.ValidSettingsChange(BasisSettingsDefaults.GlobalIlluminationSaturation.BindingKey, Invariant(BasisSettingsDefaults.GI_SATURATION_MIN));
            Assert.AreEqual(BasisSettingsDefaults.GI_SATURATION_MIN, module.GlobalIllumination.saturation.value, 0.0001f);

            module.ValidSettingsChange(BasisSettingsDefaults.GlobalIlluminationObscurance.BindingKey, Invariant(BasisSettingsDefaults.GI_OBSCURANCE_MAX));
            Assert.AreEqual(BasisSettingsDefaults.GI_OBSCURANCE_MAX, module.GlobalIllumination.obscuranceIntensity.value, 0.0001f);

            module.ValidSettingsChange(BasisSettingsDefaults.GlobalIlluminationRayLength.BindingKey, Invariant(BasisSettingsDefaults.GI_RAY_LENGTH_MAX));
            Assert.AreEqual(BasisSettingsDefaults.GI_RAY_LENGTH_MAX, module.GlobalIllumination.maxRayLength.value, 0.0001f);

            module.ValidSettingsChange(BasisSettingsDefaults.GlobalIlluminationSmoothing.BindingKey, Invariant(BasisSettingsDefaults.GI_SMOOTHING_MIN));
            Assert.AreEqual(BasisSettingsDefaults.GI_SMOOTHING_MIN, module.GlobalIllumination.smoothing.value, 0.0001f);

            module.ValidSettingsChange(BasisSettingsDefaults.GlobalIlluminationTemporalResponse.BindingKey, Invariant(BasisSettingsDefaults.GI_TEMPORAL_RESPONSE_MAX));
            Assert.AreEqual(BasisSettingsDefaults.GI_TEMPORAL_RESPONSE_MAX, module.GlobalIllumination.temporalResponse.value, 0.0001f);

            module.ValidSettingsChange(BasisSettingsDefaults.GlobalIlluminationEmitterIntensity.BindingKey, Invariant(BasisSettingsDefaults.GI_EMITTER_INTENSITY_MAX));
            Assert.AreEqual(BasisSettingsDefaults.GI_EMITTER_INTENSITY_MAX, module.GlobalIllumination.emitterIntensity.value, 0.0001f);
        }

        [Test]
        public void EveryToggleAndDropdownReachesTheOwnedVolume()
        {
            SMModuleGlobalIlluminationURP module = NewModule();
            module.ApplyOverride();

            module.ValidSettingsChange(BasisSettingsDefaults.UseGlobalIllumination.BindingKey, "true");
            Assert.IsTrue(module.GlobalIllumination.enable.value);

            module.ValidSettingsChange(BasisSettingsDefaults.GlobalIlluminationQuality.BindingKey, "ultra");
            Assert.AreEqual(BasisGlobalIlluminationQuality.Ultra, module.GlobalIllumination.quality.value);

            module.ValidSettingsChange(BasisSettingsDefaults.GlobalIlluminationResolution.BindingKey, "quarter");
            Assert.AreEqual(BasisGlobalIlluminationResolution.Quarter, module.GlobalIllumination.resolution.value);

            module.ValidSettingsChange(BasisSettingsDefaults.GlobalIlluminationFallback.BindingKey, "sky");
            Assert.AreEqual(BasisGlobalIlluminationFallback.Sky, module.GlobalIllumination.fallback.value);

            module.ValidSettingsChange(BasisSettingsDefaults.GlobalIlluminationTemporalFilter.BindingKey, "false");
            Assert.IsFalse(module.GlobalIllumination.temporalFilter.value);

            module.ValidSettingsChange(BasisSettingsDefaults.GlobalIlluminationWideBlur.BindingKey, "false");
            Assert.IsFalse(module.GlobalIllumination.wideBlur.value);

            module.ValidSettingsChange(BasisSettingsDefaults.GlobalIlluminationRayReuse.BindingKey, "false");
            Assert.IsFalse(module.GlobalIllumination.rayReuse.value);

            module.ValidSettingsChange(BasisSettingsDefaults.GlobalIlluminationEmitters.BindingKey, "false");
            Assert.IsFalse(module.GlobalIllumination.emitters.value);
        }

        [Test]
        public void AnUnparseableSliderValueLeavesTheVolumeAlone()
        {
            SMModuleGlobalIlluminationURP module = NewModule();
            module.ApplyOverride();
            float before = module.GlobalIllumination.intensity.value;

            Assert.DoesNotThrow(() => module.ValidSettingsChange(BasisSettingsDefaults.GlobalIlluminationIntensity.BindingKey, "not a number"));
            Assert.AreEqual(before, module.GlobalIllumination.intensity.value, 0.0001f);
        }

        [Test]
        public void AnUnrelatedSettingKeyIsIgnored()
        {
            SMModuleGlobalIlluminationURP module = NewModule();
            module.ApplyOverride();
            Assert.DoesNotThrow(() => module.ValidSettingsChange("somethingelse", "true"));
            Assert.IsFalse(module.GlobalIllumination.enable.value);
        }

        [Test]
        public void ChangingTheDebugViewWithoutAFeatureIsQuiet()
        {
            SMModuleGlobalIlluminationURP module = NewModule();
            module.ApplyOverride();
            Assert.DoesNotThrow(() => module.ValidSettingsChange(BasisSettingsDefaults.DevGiDebugView.BindingKey, "obscurance"));
        }

        [Test]
        public void TheTogglePropagatesToThePipelinesOwnDefaultVolumeProfile()
        {
            // URP seeds every camera's stack from the default profile before any scene volume, so unless
            // the toggle reaches it the effect is pinned to whatever that asset ships with.
            SMModuleGlobalIlluminationURP module = NewModule();
            module.ApplyOverride();

            VolumeProfile profile = ScriptableObject.CreateInstance<VolumeProfile>();
            try
            {
                BasisGlobalIlluminationVolume seeded = profile.Add<BasisGlobalIlluminationVolume>(false);
                seeded.enable.overrideState = true;
                seeded.enable.value = true;

                module.ValidSettingsChange(BasisSettingsDefaults.UseGlobalIllumination.BindingKey, "false");
                module.ApplyToProfile(profile);

                Assert.IsTrue(seeded.enable.overrideState);
                Assert.IsFalse(seeded.enable.value, "Turning the setting off must also clear the pipeline default profile.");

                module.ValidSettingsChange(BasisSettingsDefaults.UseGlobalIllumination.BindingKey, "true");
                module.ApplyToProfile(profile);
                Assert.IsTrue(seeded.enable.value);

                module.RestoreAuthoredProfiles();
                Assert.IsTrue(seeded.enable.value, "Teardown restores what the asset was authored with.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void ApplyingToAProfileWithoutTheComponentIsHarmless()
        {
            SMModuleGlobalIlluminationURP module = NewModule();
            module.ApplyOverride();
            VolumeProfile profile = ScriptableObject.CreateInstance<VolumeProfile>();
            try
            {
                Assert.DoesNotThrow(() => module.ApplyToProfile(profile));
                Assert.DoesNotThrow(() => module.ApplyToProfile(null));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        // ----- the module's own volumes -----

        [Test]
        public void ModuleOwnsOneGlobalVolumeOnTheDefaultLayerThatStartsDisabled()
        {
            SMModuleGlobalIlluminationURP module = NewModule();
            module.ApplyOverride();

            Volume volume = module.Volume;
            Assert.IsNotNull(volume);
            Assert.IsTrue(volume.isGlobal);
            Assert.AreEqual(SMModuleGlobalIlluminationURP.OverridePriority, volume.priority);
            Assert.AreEqual(0, volume.gameObject.layer);
            Assert.AreEqual(host.transform, volume.transform.parent);
            Assert.IsTrue(volume.gameObject.activeSelf);
            Assert.IsTrue(volume.sharedProfile.TryGet(out BasisGlobalIlluminationVolume gi));
            Assert.AreSame(module.GlobalIllumination, gi);
            Assert.IsTrue(gi.enable.overrideState);
            Assert.IsFalse(gi.enable.value);
            Assert.IsFalse(module.Capturing);

            module.ApplyOverride();
            Assert.AreSame(volume, module.Volume);
            Assert.AreEqual(1, host.transform.childCount);
            Assert.AreEqual(0, module.CameraVolumes.Count);
        }

        [Test]
        public void ModuleCoversARegisteredCamerasVolumeLayerWithTheSameProfile()
        {
            SMModuleGlobalIlluminationURP module = NewModule();
            GameObject captureObject = new GameObject("capture-camera");
            UnityEngine.Camera capture = captureObject.AddComponent<UnityEngine.Camera>();
            try
            {
                captureObject.AddComponent<UniversalAdditionalCameraData>().volumeLayerMask = 1 << 11;
                SMModuleGlobalIlluminationURP.RegisterCamera(capture);

                module.ApplyOverride();

                Assert.IsTrue(module.CameraVolumes.TryGetValue(11, out Volume layerVolume));
                Assert.AreEqual(11, layerVolume.gameObject.layer);
                Assert.IsTrue(layerVolume.isGlobal);
                Assert.AreEqual(SMModuleGlobalIlluminationURP.OverridePriority, layerVolume.priority);
                Assert.AreEqual(host.transform, layerVolume.transform.parent);
                Assert.AreSame(module.Volume.sharedProfile, layerVolume.sharedProfile);
                Assert.AreEqual(2, host.transform.childCount);

                module.ApplyOverride();
                Assert.AreEqual(1, module.CameraVolumes.Count);
                Assert.AreEqual(2, host.transform.childCount);
            }
            finally
            {
                SMModuleGlobalIlluminationURP.UnregisterCamera(capture);
                UnityEngine.Object.DestroyImmediate(captureObject);
            }
        }

        // ----- the camera gate -----

        [Test]
        public void CameraGateAllowsEverythingWithoutALocalPlayerAndOnlyTheMainCameraWithOne()
        {
            GameObject mainObject = new GameObject("main-camera");
            GameObject otherObject = new GameObject("mirror-camera");
            try
            {
                UnityEngine.Camera main = mainObject.AddComponent<UnityEngine.Camera>();
                UnityEngine.Camera other = otherObject.AddComponent<UnityEngine.Camera>();

                BasisLocalCameraDriver.CameraInstance = null;
                Assert.IsTrue(SMModuleGlobalIlluminationURP.AcceptsCamera(main));
                Assert.IsTrue(SMModuleGlobalIlluminationURP.AcceptsCamera(other));

                BasisLocalCameraDriver.CameraInstance = main;
                Assert.IsTrue(SMModuleGlobalIlluminationURP.AcceptsCamera(main));
                Assert.IsFalse(SMModuleGlobalIlluminationURP.AcceptsCamera(other));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mainObject);
                UnityEngine.Object.DestroyImmediate(otherObject);
            }
        }

        [Test]
        public void RegisteredCaptureCamerasPassTheGateUntilSuspendedOrUnregistered()
        {
            GameObject mainObject = new GameObject("main-camera");
            GameObject captureObject = new GameObject("capture-camera");
            UnityEngine.Camera capture = captureObject.AddComponent<UnityEngine.Camera>();
            try
            {
                BasisLocalCameraDriver.CameraInstance = mainObject.AddComponent<UnityEngine.Camera>();
                Assert.IsFalse(SMModuleGlobalIlluminationURP.IsCameraRegistered(capture));
                Assert.IsFalse(SMModuleGlobalIlluminationURP.AcceptsCamera(capture));

                SMModuleGlobalIlluminationURP.RegisterCamera(capture);
                Assert.IsTrue(SMModuleGlobalIlluminationURP.IsCameraRegistered(capture));
                Assert.IsTrue(SMModuleGlobalIlluminationURP.AcceptsCamera(capture));

                SMModuleGlobalIlluminationURP.SuspendCamera(capture, true);
                Assert.IsTrue(SMModuleGlobalIlluminationURP.IsCameraRegistered(capture));
                Assert.IsFalse(SMModuleGlobalIlluminationURP.AcceptsCamera(capture));

                SMModuleGlobalIlluminationURP.SuspendCamera(capture, false);
                Assert.IsTrue(SMModuleGlobalIlluminationURP.AcceptsCamera(capture));

                SMModuleGlobalIlluminationURP.UnregisterCamera(capture);
                Assert.IsFalse(SMModuleGlobalIlluminationURP.IsCameraRegistered(capture));
                Assert.IsFalse(SMModuleGlobalIlluminationURP.AcceptsCamera(capture));

                SMModuleGlobalIlluminationURP.RegisterCamera(null);
                SMModuleGlobalIlluminationURP.SuspendCamera(null, true);
                SMModuleGlobalIlluminationURP.UnregisterCamera(null);
                Assert.IsFalse(SMModuleGlobalIlluminationURP.IsCameraRegistered(null));
                Assert.IsFalse(SMModuleGlobalIlluminationURP.AcceptsCamera(null));
            }
            finally
            {
                SMModuleGlobalIlluminationURP.UnregisterCamera(capture);
                UnityEngine.Object.DestroyImmediate(captureObject);
                UnityEngine.Object.DestroyImmediate(mainObject);
            }
        }

        [Test]
        public void UncoveredVolumeLayerIsTheLowestLayerOutsideTheDefaultVolumeMask()
        {
            GameObject captureObject = new GameObject("capture-camera");
            try
            {
                UnityEngine.Camera capture = captureObject.AddComponent<UnityEngine.Camera>();
                Assert.AreEqual(-1, SMModuleGlobalIlluminationURP.UncoveredVolumeLayer(null));
                Assert.AreEqual(-1, SMModuleGlobalIlluminationURP.UncoveredVolumeLayer(capture));

                UniversalAdditionalCameraData cameraData = captureObject.AddComponent<UniversalAdditionalCameraData>();
                cameraData.volumeLayerMask = 1 << 11;
                Assert.AreEqual(11, SMModuleGlobalIlluminationURP.UncoveredVolumeLayer(capture));
                cameraData.volumeLayerMask = (1 << 11) | (1 << 5);
                Assert.AreEqual(5, SMModuleGlobalIlluminationURP.UncoveredVolumeLayer(capture));
                cameraData.volumeLayerMask = 1;
                Assert.AreEqual(-1, SMModuleGlobalIlluminationURP.UncoveredVolumeLayer(capture));
                cameraData.volumeLayerMask = ~0;
                Assert.AreEqual(-1, SMModuleGlobalIlluminationURP.UncoveredVolumeLayer(capture));
                cameraData.volumeLayerMask = 0;
                Assert.AreEqual(-1, SMModuleGlobalIlluminationURP.UncoveredVolumeLayer(capture));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(captureObject);
            }
        }

        // ----- capture lifecycle -----

        [Test]
        public void CaptureOnlyEngagesForARegisteredCameraWhileTheEffectIsOn()
        {
            SMModuleGlobalIlluminationURP module = NewModule();
            module.Awake();
            GameObject captureObject = new GameObject("capture-camera");
            UnityEngine.Camera capture = captureObject.AddComponent<UnityEngine.Camera>();
            try
            {
                module.ValidSettingsChange(BasisSettingsDefaults.UseGlobalIllumination.BindingKey, "false");
                SMModuleGlobalIlluminationURP.RegisterCamera(capture);
                SMModuleGlobalIlluminationURP.BeginCapture(capture);
                Assert.IsFalse(module.Capturing, "A capture must not engage while the effect is switched off.");

                module.ValidSettingsChange(BasisSettingsDefaults.UseGlobalIllumination.BindingKey, "true");
                SMModuleGlobalIlluminationURP.UnregisterCamera(capture);
                SMModuleGlobalIlluminationURP.BeginCapture(capture);
                Assert.IsFalse(module.Capturing, "A capture must not engage for an unregistered camera.");

                SMModuleGlobalIlluminationURP.RegisterCamera(capture);
                SMModuleGlobalIlluminationURP.BeginCapture(capture);
                Assert.IsTrue(module.Capturing);
                Assert.AreEqual(BasisGlobalIlluminationResolution.Full, module.GlobalIllumination.resolution.value);

                SMModuleGlobalIlluminationURP.EndCapture();
                Assert.IsFalse(module.Capturing);
                Assert.AreEqual(BasisGlobalIlluminationResolution.Half, module.GlobalIllumination.resolution.value);

                Assert.DoesNotThrow(SMModuleGlobalIlluminationURP.EndCapture);
            }
            finally
            {
                SMModuleGlobalIlluminationURP.UnregisterCamera(capture);
                UnityEngine.Object.DestroyImmediate(captureObject);
                module.OnDestroy();
            }
        }

        [Test]
        public void AwakeInstallsTheCameraGateAndTeardownRemovesIt()
        {
            BasisGlobalIlluminationFeature.CameraFilter = null;
            SMModuleGlobalIlluminationURP module = NewModule();
            module.Awake();
            Assert.IsNotNull(BasisGlobalIlluminationFeature.CameraFilter);

            module.OnDestroy();
            Assert.IsNull(BasisGlobalIlluminationFeature.CameraFilter);
        }
    }
}
#endif
