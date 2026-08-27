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
                UnityEngine.Object.DestroyImmediate(host);
                host = null;
            }
        }

        private SMModuleGlobalIlluminationURP NewModule()
        {
            host = new GameObject("gi-settings-module");
            return host.AddComponent<SMModuleGlobalIlluminationURP>();
        }

        private static BasisGlobalIlluminationSettings NewVolume()
        {
            return new BasisGlobalIlluminationSettings();
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
        public void EverySettingsRangeSitsInsideTheRangeItDrives()
        {
            // A binding range wider than the volume's own would silently clip in the inspector and read
            // back a different value than the player chose.
            Assert.GreaterOrEqual(BasisSettingsDefaults.GI_INTENSITY_MIN, BasisGlobalIlluminationSettings.IntensityMin);
            Assert.LessOrEqual(BasisSettingsDefaults.GI_INTENSITY_MAX, BasisGlobalIlluminationSettings.IntensityMax);
            Assert.GreaterOrEqual(BasisSettingsDefaults.GI_SATURATION_MIN, BasisGlobalIlluminationSettings.SaturationMin);
            Assert.LessOrEqual(BasisSettingsDefaults.GI_SATURATION_MAX, BasisGlobalIlluminationSettings.SaturationMax);
            Assert.GreaterOrEqual(BasisSettingsDefaults.GI_OBSCURANCE_MIN, BasisGlobalIlluminationSettings.ObscuranceMin);
            Assert.LessOrEqual(BasisSettingsDefaults.GI_OBSCURANCE_MAX, BasisGlobalIlluminationSettings.ObscuranceMax);
            Assert.GreaterOrEqual(BasisSettingsDefaults.GI_RAY_LENGTH_MIN, BasisGlobalIlluminationSettings.RayLengthMin);
            Assert.LessOrEqual(BasisSettingsDefaults.GI_RAY_LENGTH_MAX, BasisGlobalIlluminationSettings.RayLengthMax);
            Assert.GreaterOrEqual(BasisSettingsDefaults.GI_SMOOTHING_MIN, BasisGlobalIlluminationSettings.SmoothingMin);
            Assert.LessOrEqual(BasisSettingsDefaults.GI_SMOOTHING_MAX, BasisGlobalIlluminationSettings.SmoothingMax);
            Assert.GreaterOrEqual(BasisSettingsDefaults.GI_TEMPORAL_RESPONSE_MIN, BasisGlobalIlluminationSettings.TemporalResponseMin);
            Assert.LessOrEqual(BasisSettingsDefaults.GI_TEMPORAL_RESPONSE_MAX, BasisGlobalIlluminationSettings.TemporalResponseMax);
            Assert.GreaterOrEqual(BasisSettingsDefaults.GI_EMITTER_INTENSITY_MIN, BasisGlobalIlluminationSettings.EmitterIntensityMin);
            Assert.LessOrEqual(BasisSettingsDefaults.GI_EMITTER_INTENSITY_MAX, BasisGlobalIlluminationSettings.EmitterIntensityMax);
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
        public void ApplyDrivesEveryPlayerFacingParameter()
        {
            BasisGlobalIlluminationSettings gi = NewVolume();
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

                Assert.IsTrue(gi.enable);
                Assert.AreEqual(BasisGlobalIlluminationQuality.High, gi.quality);
                Assert.AreEqual(BasisGlobalIlluminationResolution.Quarter, gi.resolution);
                Assert.AreEqual(BasisGlobalIlluminationFallback.Sky, gi.fallback);
                Assert.AreEqual(1.5f, gi.intensity, 0.0001f);
                Assert.AreEqual(0.5f, gi.saturation, 0.0001f);
                Assert.AreEqual(0.75f, gi.obscuranceIntensity, 0.0001f);
                Assert.AreEqual(32f, gi.maxRayLength, 0.0001f);
                Assert.AreEqual(1.5f, gi.smoothing, 0.0001f);
                Assert.AreEqual(0.4f, gi.temporalResponse, 0.0001f);
                Assert.AreEqual(2f, gi.emitterIntensity, 0.0001f);

            }
            finally
            {
            }
        }


        [Test]
        public void EverySliderIsClampedToItsSettingsRange()
        {
            BasisGlobalIlluminationSettings gi = NewVolume();
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

                Assert.AreEqual(BasisSettingsDefaults.GI_INTENSITY_MAX, gi.intensity, 0.0001f);
                Assert.AreEqual(BasisSettingsDefaults.GI_SATURATION_MAX, gi.saturation, 0.0001f);
                Assert.AreEqual(BasisSettingsDefaults.GI_OBSCURANCE_MAX, gi.obscuranceIntensity, 0.0001f);
                Assert.AreEqual(BasisSettingsDefaults.GI_RAY_LENGTH_MAX, gi.maxRayLength, 0.0001f);
                Assert.AreEqual(BasisSettingsDefaults.GI_SMOOTHING_MAX, gi.smoothing, 0.0001f);
                Assert.AreEqual(BasisSettingsDefaults.GI_TEMPORAL_RESPONSE_MAX, gi.temporalResponse, 0.0001f);
                Assert.AreEqual(BasisSettingsDefaults.GI_EMITTER_INTENSITY_MAX, gi.emitterIntensity, 0.0001f);

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

                Assert.AreEqual(BasisSettingsDefaults.GI_INTENSITY_MIN, gi.intensity, 0.0001f);
                Assert.AreEqual(BasisSettingsDefaults.GI_SATURATION_MIN, gi.saturation, 0.0001f);
                Assert.AreEqual(BasisSettingsDefaults.GI_OBSCURANCE_MIN, gi.obscuranceIntensity, 0.0001f);
                Assert.AreEqual(BasisSettingsDefaults.GI_RAY_LENGTH_MIN, gi.maxRayLength, 0.0001f);
                Assert.AreEqual(BasisSettingsDefaults.GI_SMOOTHING_MIN, gi.smoothing, 0.0001f);
                Assert.AreEqual(BasisSettingsDefaults.GI_TEMPORAL_RESPONSE_MIN, gi.temporalResponse, 0.0001f);
                Assert.AreEqual(BasisSettingsDefaults.GI_EMITTER_INTENSITY_MIN, gi.emitterIntensity, 0.0001f);
            }
            finally
            {
            }
        }

        [Test]
        public void DisablingActuallyDisables()
        {
            BasisGlobalIlluminationSettings gi = NewVolume();
            try
            {
                BasisGlobalIlluminationState state = BasisGlobalIlluminationState.FromDefaults();
                state.Enabled = false;
                SMModuleGlobalIlluminationURP.Apply(gi, state);

                Assert.IsFalse(gi.enable);
                Assert.IsFalse(gi.IsActive());
            }
            finally
            {
            }
        }

        [Test]
        public void ACaptureRaisesResolutionAndRayBudgetOnlyWhileItLasts()
        {
            BasisGlobalIlluminationSettings gi = NewVolume();
            try
            {
                BasisGlobalIlluminationState state = BasisGlobalIlluminationState.FromDefaults();
                state.Enabled = true;
                state.Quality = BasisGlobalIlluminationQuality.Low;
                state.Resolution = BasisGlobalIlluminationResolution.Quarter;
                state.Capture = true;
                SMModuleGlobalIlluminationURP.Apply(gi, state);

                Assert.AreEqual(BasisGlobalIlluminationResolution.Full, gi.resolution);
                Assert.IsTrue(gi.overrideQualityCounts);
                Assert.AreEqual(SMModuleGlobalIlluminationURP.CaptureRayCount, gi.ResolvedRayCount());
                Assert.AreEqual(SMModuleGlobalIlluminationURP.CaptureRaySteps, gi.ResolvedRaySteps());
                Assert.AreEqual(BasisGlobalIlluminationQuality.Low, gi.quality);

                state.Capture = false;
                SMModuleGlobalIlluminationURP.Apply(gi, state);
                Assert.AreEqual(BasisGlobalIlluminationResolution.Quarter, gi.resolution);
                Assert.IsFalse(gi.overrideQualityCounts);
                Assert.AreEqual(1, gi.ResolvedRayCount());
            }
            finally
            {
            }
        }

        [Test]
        public void ACaptureTurnsTheTemporalFilterOffBecauseAPhotoHasNoHistory()
        {
            BasisGlobalIlluminationSettings gi = NewVolume();
            try
            {
                BasisGlobalIlluminationState state = BasisGlobalIlluminationState.FromDefaults();
                state.Enabled = true;
                state.TemporalFilter = true;
                state.Capture = true;
                SMModuleGlobalIlluminationURP.Apply(gi, state);
                Assert.IsFalse(gi.temporalFilter);

                state.Capture = false;
                SMModuleGlobalIlluminationURP.Apply(gi, state);
                Assert.IsTrue(gi.temporalFilter);
            }
            finally
            {
            }
        }

        [Test]
        public void CaptureRayBudgetIsAtLeastTheHighestQualityTier()
        {
            // A photo that traced fewer rays than the player's own quality tier would be noisier than
            // what they were looking at when they pressed the shutter.
            BasisGlobalIlluminationSettings gi = NewVolume();
            try
            {
                gi.quality = BasisGlobalIlluminationQuality.Ultra;
                Assert.GreaterOrEqual(SMModuleGlobalIlluminationURP.CaptureRayCount, gi.ResolvedRayCount());
                Assert.GreaterOrEqual(SMModuleGlobalIlluminationURP.CaptureRaySteps, gi.ResolvedRaySteps());
                Assert.LessOrEqual(SMModuleGlobalIlluminationURP.CaptureRayCount, BasisGlobalIlluminationSettings.RayCountMax);
                Assert.LessOrEqual(SMModuleGlobalIlluminationURP.CaptureRaySteps, BasisGlobalIlluminationSettings.RayStepsMax);
            }
            finally
            {
            }
        }

        [Test]
        public void ApplyingToNullSettingsIsHarmless()
        {
            Assert.DoesNotThrow(() => SMModuleGlobalIlluminationURP.Apply((BasisGlobalIlluminationSettings)null, BasisGlobalIlluminationState.FromDefaults()));
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
        public void EverySliderReachesTheSettings()
        {
            // Regression shape: a slider persisted and threaded through the state but never read at the
            // point of application looks wired up and changes nothing on screen.
            SMModuleGlobalIlluminationURP module = NewModule();
            module.ApplyOverride();

            module.ValidSettingsChange(BasisSettingsDefaults.GlobalIlluminationIntensity.BindingKey, Invariant(BasisSettingsDefaults.GI_INTENSITY_MAX));
            Assert.AreEqual(BasisSettingsDefaults.GI_INTENSITY_MAX, module.GlobalIllumination.intensity, 0.0001f);

            module.ValidSettingsChange(BasisSettingsDefaults.GlobalIlluminationSaturation.BindingKey, Invariant(BasisSettingsDefaults.GI_SATURATION_MIN));
            Assert.AreEqual(BasisSettingsDefaults.GI_SATURATION_MIN, module.GlobalIllumination.saturation, 0.0001f);

            module.ValidSettingsChange(BasisSettingsDefaults.GlobalIlluminationObscurance.BindingKey, Invariant(BasisSettingsDefaults.GI_OBSCURANCE_MAX));
            Assert.AreEqual(BasisSettingsDefaults.GI_OBSCURANCE_MAX, module.GlobalIllumination.obscuranceIntensity, 0.0001f);

            module.ValidSettingsChange(BasisSettingsDefaults.GlobalIlluminationRayLength.BindingKey, Invariant(BasisSettingsDefaults.GI_RAY_LENGTH_MAX));
            Assert.AreEqual(BasisSettingsDefaults.GI_RAY_LENGTH_MAX, module.GlobalIllumination.maxRayLength, 0.0001f);

            module.ValidSettingsChange(BasisSettingsDefaults.GlobalIlluminationSmoothing.BindingKey, Invariant(BasisSettingsDefaults.GI_SMOOTHING_MIN));
            Assert.AreEqual(BasisSettingsDefaults.GI_SMOOTHING_MIN, module.GlobalIllumination.smoothing, 0.0001f);

            module.ValidSettingsChange(BasisSettingsDefaults.GlobalIlluminationTemporalResponse.BindingKey, Invariant(BasisSettingsDefaults.GI_TEMPORAL_RESPONSE_MAX));
            Assert.AreEqual(BasisSettingsDefaults.GI_TEMPORAL_RESPONSE_MAX, module.GlobalIllumination.temporalResponse, 0.0001f);

            module.ValidSettingsChange(BasisSettingsDefaults.GlobalIlluminationEmitterIntensity.BindingKey, Invariant(BasisSettingsDefaults.GI_EMITTER_INTENSITY_MAX));
            Assert.AreEqual(BasisSettingsDefaults.GI_EMITTER_INTENSITY_MAX, module.GlobalIllumination.emitterIntensity, 0.0001f);
        }

        [Test]
        public void EveryToggleAndDropdownReachesTheSettings()
        {
            SMModuleGlobalIlluminationURP module = NewModule();
            module.ApplyOverride();

            module.ValidSettingsChange(BasisSettingsDefaults.UseGlobalIllumination.BindingKey, "true");
            Assert.IsTrue(module.GlobalIllumination.enable);

            module.ValidSettingsChange(BasisSettingsDefaults.GlobalIlluminationQuality.BindingKey, "ultra");
            Assert.AreEqual(BasisGlobalIlluminationQuality.Ultra, module.GlobalIllumination.quality);

            module.ValidSettingsChange(BasisSettingsDefaults.GlobalIlluminationResolution.BindingKey, "quarter");
            Assert.AreEqual(BasisGlobalIlluminationResolution.Quarter, module.GlobalIllumination.resolution);

            module.ValidSettingsChange(BasisSettingsDefaults.GlobalIlluminationFallback.BindingKey, "sky");
            Assert.AreEqual(BasisGlobalIlluminationFallback.Sky, module.GlobalIllumination.fallback);

            module.ValidSettingsChange(BasisSettingsDefaults.GlobalIlluminationTemporalFilter.BindingKey, "false");
            Assert.IsFalse(module.GlobalIllumination.temporalFilter);

            module.ValidSettingsChange(BasisSettingsDefaults.GlobalIlluminationWideBlur.BindingKey, "false");
            Assert.IsFalse(module.GlobalIllumination.wideBlur);

            module.ValidSettingsChange(BasisSettingsDefaults.GlobalIlluminationRayReuse.BindingKey, "false");
            Assert.IsFalse(module.GlobalIllumination.rayReuse);

            module.ValidSettingsChange(BasisSettingsDefaults.GlobalIlluminationEmitters.BindingKey, "false");
            Assert.IsFalse(module.GlobalIllumination.emitters);
        }

        [Test]
        public void AnUnparseableSliderValueLeavesTheSettingsAlone()
        {
            SMModuleGlobalIlluminationURP module = NewModule();
            module.ApplyOverride();
            float before = module.GlobalIllumination.intensity;

            Assert.DoesNotThrow(() => module.ValidSettingsChange(BasisSettingsDefaults.GlobalIlluminationIntensity.BindingKey, "not a number"));
            Assert.AreEqual(before, module.GlobalIllumination.intensity, 0.0001f);
        }

        [Test]
        public void AnUnrelatedSettingKeyIsIgnored()
        {
            SMModuleGlobalIlluminationURP module = NewModule();
            module.ApplyOverride();
            Assert.DoesNotThrow(() => module.ValidSettingsChange("somethingelse", "true"));
            Assert.IsFalse(module.GlobalIllumination.enable);
        }

        [Test]
        public void ChangingTheDebugViewWithoutAFeatureIsQuiet()
        {
            SMModuleGlobalIlluminationURP module = NewModule();
            module.ApplyOverride();
            Assert.DoesNotThrow(() => module.ValidSettingsChange(BasisSettingsDefaults.DevGiDebugView.BindingKey, "obscurance"));
        }



        // ----- the module's own volumes -----



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
                Assert.AreEqual(BasisGlobalIlluminationResolution.Full, module.GlobalIllumination.resolution);

                SMModuleGlobalIlluminationURP.EndCapture();
                Assert.IsFalse(module.Capturing);
                Assert.AreEqual(BasisGlobalIlluminationResolution.Half, module.GlobalIllumination.resolution);

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
