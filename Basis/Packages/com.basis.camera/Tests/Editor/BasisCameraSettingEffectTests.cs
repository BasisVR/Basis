using NUnit.Framework;
using Basis.Cinematics;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Basis.Tests.Camera
{
    /// <summary>
    /// One test per user-facing camera setting, each asserting the control actually reaches
    /// something. A slider that writes a value nobody reads looks identical to a working one from
    /// the panel, so the failure mode these guard against is silent: the handle moves, the number
    /// updates, and the shot never changes.
    ///
    /// <para>
    /// Two things are checked for every post-processing setting, because either alone is not
    /// enough. The value must land on the override — and <c>overrideState</c> must be set, since
    /// the volume system blends a parameter into the frame only when it is overridden and
    /// otherwise keeps the profile's own value. That is the exact shape of the hue-shift bug
    /// already recorded in the source: right value, no override, dead control.
    /// </para>
    ///
    /// <para>
    /// Each test also moves the setting somewhere it was not already, so none of them can pass by
    /// asserting a default against itself.
    /// </para>
    /// </summary>
    public class BasisCameraSettingEffectTests
    {
        private BasisCameraSettingsRig _rig;

        [SetUp]
        public void SetUp() => _rig = new BasisCameraSettingsRig();

        [TearDown]
        public void TearDown() => _rig?.Dispose();

        // ---------- Lens ----------

        [Test]
        public void FieldOfView_ReachesTheCaptureCamera()
        {
            _rig.CaptureCamera.fieldOfView = 60f;

            _rig.UI.ChangeFOV(95f);

            Assert.That(_rig.CaptureCamera.fieldOfView, Is.EqualTo(95f).Within(1e-3f),
                "The field of view slider is the framing control; if it does not reach the camera nothing else on the lens page means anything.");
        }

        [Test]
        public void FieldOfView_DoesNotDisturbTheSensorOrFocalLength()
        {
            // On a physical camera the sensor size, focal length and FOV are one value seen three
            // ways, so writing the wrong one silently overwrites the FOV. That is what drove every
            // camera to ~100 degrees regardless of the slider.
            _rig.CaptureCamera.usePhysicalProperties = true;
            _rig.CaptureCamera.sensorSize = new Vector2(36f, 24f);

            _rig.UI.ChangeFOV(50f);

            Assert.That(_rig.CaptureCamera.sensorSize.x, Is.EqualTo(36f).Within(1e-3f));
            Assert.That(_rig.CaptureCamera.sensorSize.y, Is.EqualTo(24f).Within(1e-3f));
        }

        [Test]
        public void PhysicalAperture_ReachesTheCaptureCamera()
        {
            _rig.CaptureCamera.aperture = 1f;
            int index = System.Array.IndexOf(_rig.Camera.MetaData.apertures, "f/8");

            _rig.UI.ChangeAperture(index);

            Assert.That(_rig.CaptureCamera.aperture, Is.EqualTo(8f).Within(1e-3f),
                "The f-stop presets are parsed out of their labels; a label the parser cannot read leaves the exposure untouched.");
        }

        [Test]
        public void ShutterSpeed_ReachesTheCaptureCameraAsSeconds()
        {
            int index = System.Array.IndexOf(_rig.Camera.MetaData.shutterSpeeds, "1/250");

            _rig.UI.ChangeShutterSpeed(index);

            Assert.That(_rig.CaptureCamera.shutterSpeed, Is.EqualTo(1f / 250f).Within(1e-6f),
                "The preset reads as a fraction of a second; the camera wants the seconds it denotes.");
        }

        [Test]
        public void Iso_ReachesTheCaptureCamera()
        {
            int index = System.Array.IndexOf(_rig.Camera.MetaData.isoValues, "1600");

            _rig.UI.ChangeISO(index);

            Assert.That(_rig.CaptureCamera.iso, Is.EqualTo(1600));
        }

        [Test]
        public void Msaa_IsSanitisedToASampleCountTheGpuAccepts()
        {
            // The dropdown only offers 1/2/4/8, but the value also arrives from a saved file that
            // may predate the dropdown. A count the GPU rejects fails the render target silently.
            _rig.Camera.SetMsaaSamples(3);
            Assert.That(_rig.Camera.msaaSamples, Is.EqualTo(2));

            _rig.Camera.SetMsaaSamples(64);
            Assert.That(_rig.Camera.msaaSamples, Is.EqualTo(8));

            _rig.Camera.SetMsaaSamples(0);
            Assert.That(_rig.Camera.msaaSamples, Is.EqualTo(1));
        }

        // ---------- Depth of field ----------

        [Test]
        public void DepthOfFieldMode_SetsBothTheStyleAndWhetherItRuns()
        {
            _rig.UI.SetDoFMode(2);
            Assert.That(_rig.DepthOfField.mode.value, Is.EqualTo(DepthOfFieldMode.Bokeh));
            Assert.That(_rig.DepthOfField.mode.overrideState, Is.True,
                "Without the override the profile's own mode keeps winning and the dropdown does nothing.");
            Assert.That(_rig.DepthOfField.active, Is.True);

            _rig.UI.SetDoFMode(0);
            Assert.That(_rig.DepthOfField.active, Is.False,
                "Off is the first entry of the dropdown, so it owns the on/off as well as the style.");
        }

        [Test]
        public void DepthOfFieldMode_ClampsOutOfRangeInsteadOfLandingOnAnUndefinedStyle()
        {
            _rig.UI.SetDoFMode(99);

            Assert.That((int)_rig.DepthOfField.mode.value, Is.InRange(0, 2));
        }

        [Test]
        public void Aperture_ReachesTheDepthOfFieldOverride()
        {
            _rig.DepthOfField.aperture.value = 16f;

            _rig.UI.ChangeAperture(4f);

            Assert.That(_rig.DepthOfField.aperture.value, Is.EqualTo(4f).Within(1e-3f));
        }

        [Test]
        public void Aperture_IsHeldInsideTheRangeUrpWillAccept()
        {
            // URP clamps aperture to [1, 32]. A slider that travels outside it is dead at both ends,
            // and below f/1 the circle of confusion inverts and swaps near for far blur.
            _rig.UI.ChangeAperture(0.05f);
            Assert.That(_rig.DepthOfField.aperture.value, Is.GreaterThanOrEqualTo(BasisHandHeldCameraUI.MinAperture));

            _rig.UI.ChangeAperture(500f);
            Assert.That(_rig.DepthOfField.aperture.value, Is.LessThanOrEqualTo(BasisHandHeldCameraUI.MaxAperture));
        }

        [Test]
        public void FocalLength_ReachesTheOverrideAndMovesTheMinimumFocusDistance()
        {
            float shortLensMinimum = _rig.Camera.MinimumFocusDistance;

            _rig.UI.ChangeDoFFocalLength(300f);

            Assert.That(_rig.DepthOfField.focalLength.value, Is.EqualTo(300f).Within(1e-3f));
            Assert.That(_rig.DepthOfField.focalLength.overrideState, Is.True);
            Assert.That(_rig.Camera.MinimumFocusDistance, Is.GreaterThan(shortLensMinimum),
                "A longer lens has to hold focus further out, or the blur solver divides through zero.");
        }

        [Test]
        public void BladeCount_ReachesTheOverrideAsAWholeNumber()
        {
            _rig.UI.ChangeDoFBladeCount(6.6f);

            Assert.That(_rig.DepthOfField.bladeCount.value, Is.EqualTo(7),
                "Blades come off a slider carrying floats but describe a physical iris.");
            Assert.That(_rig.DepthOfField.bladeCount.overrideState, Is.True);
        }

        [Test]
        public void FocusDistance_ReachesTheOverride()
        {
            _rig.UI.DepthChangeFocusDistance(7.5f);

            Assert.That(_rig.DepthOfField.focusDistance.value, Is.EqualTo(7.5f).Within(1e-3f));
            Assert.That(_rig.DepthOfField.focusDistance.overrideState, Is.True);
        }

        [Test]
        public void FocusDistance_FromTheLegacyEntryPoint_GoesToDepthOfFieldNotTheLens()
        {
            // ChangeFocusDistance used to write captureCamera.focalLength — millimetres of lens
            // from a value in metres of focus — which drove the FOV to about 100 degrees.
            _rig.CaptureCamera.usePhysicalProperties = true;
            _rig.CaptureCamera.fieldOfView = 50f;

            _rig.UI.ChangeFocusDistance(9f);

            Assert.That(_rig.DepthOfField.focusDistance.value, Is.EqualTo(9f).Within(1e-3f));
            Assert.That(_rig.CaptureCamera.fieldOfView, Is.EqualTo(50f).Within(1e-3f),
                "Focus is a depth-of-field concept; it must never reach the lens focal length.");
        }

        // ---------- Exposure and colour ----------

        [Test]
        public void Exposure_ReachesPostExposureAndCoversBothDirections()
        {
            _rig.UI.ChangeExposureCompensation(0);
            float darkest = _rig.ColorAdjustments.postExposure.value;

            _rig.UI.ChangeExposureCompensation(BasisHandHeldCameraUI.ExposureStopCount - 1);
            float brightest = _rig.ColorAdjustments.postExposure.value;

            Assert.That(darkest, Is.LessThan(0f));
            Assert.That(brightest, Is.GreaterThan(0f),
                "The stop table has to straddle zero or the slider can only ever darken the shot.");
        }

        [Test]
        public void Exposure_ClampsToTheStopTableRatherThanIndexingPastIt()
        {
            _rig.UI.ChangeExposureCompensation(9999);

            Assert.That(_rig.UI.ExposureIndex, Is.EqualTo(BasisHandHeldCameraUI.ExposureStopCount - 1));
        }

        [Test]
        public void Contrast_ReachesColourAdjustments()
        {
            _rig.UI.ChangeContrast(35f);

            Assert.That(_rig.ColorAdjustments.contrast.value, Is.EqualTo(35f).Within(1e-3f));
        }

        [Test]
        public void Saturation_ReachesColourAdjustments()
        {
            _rig.UI.ChangeSaturation(-40f);

            Assert.That(_rig.ColorAdjustments.saturation.value, Is.EqualTo(-40f).Within(1e-3f));
        }

        [Test]
        public void HueShift_ReachesColourAdjustmentsAndTurnsOnItsOverride()
        {
            // Hue shift ships with overrideState off in the profile, unlike contrast and
            // saturation. Writing the value alone leaves the volume on the profile's own hue.
            _rig.ColorAdjustments.hueShift.overrideState = false;

            _rig.UI.ChangeHueShift(60f);

            Assert.That(_rig.ColorAdjustments.hueShift.value, Is.EqualTo(60f).Within(1e-3f));
            Assert.That(_rig.ColorAdjustments.hueShift.overrideState, Is.True);
        }

        [Test]
        public void WhiteBalance_TemperatureAndTintBothReachTheOverride()
        {
            _rig.UI.ChangeWhiteBalanceTemperature(25f);
            _rig.UI.ChangeWhiteBalanceTint(-15f);

            Assert.That(_rig.WhiteBalance.temperature.value, Is.EqualTo(25f).Within(1e-3f));
            Assert.That(_rig.WhiteBalance.tint.value, Is.EqualTo(-15f).Within(1e-3f));
            Assert.That(_rig.WhiteBalance.temperature.overrideState, Is.True);
            Assert.That(_rig.WhiteBalance.tint.overrideState, Is.True);
        }

        [Test]
        public void WhiteBalance_StaysOnWhileEitherAxisIsDialledIn()
        {
            // Both axes share one effect, so zeroing one must not switch off the other.
            _rig.UI.ChangeWhiteBalanceTemperature(25f);
            _rig.UI.ChangeWhiteBalanceTint(-15f);

            _rig.UI.ChangeWhiteBalanceTemperature(0f);

            Assert.That(_rig.WhiteBalance.active, Is.True, "Tint is still dialled in.");

            _rig.UI.ChangeWhiteBalanceTint(0f);

            Assert.That(_rig.WhiteBalance.active, Is.False, "Neutral on both axes means the effect adds nothing.");
        }

        // ---------- Added effects ----------

        [Test]
        public void Vignette_ReachesTheOverrideAndSwitchesItselfOnAndOff()
        {
            _rig.UI.ChangeVignette(0.4f);

            Assert.That(_rig.Vignette.intensity.value, Is.EqualTo(0.4f).Within(1e-3f));
            Assert.That(_rig.Vignette.intensity.overrideState, Is.True);
            Assert.That(_rig.Vignette.active, Is.True);

            _rig.UI.ChangeVignette(0f);

            Assert.That(_rig.Vignette.active, Is.False,
                "Zero has to leave the shot exactly as it was, not merely add nothing visible.");
        }

        [Test]
        public void ChromaticAberration_ReachesTheOverrideAndSwitchesItselfOnAndOff()
        {
            _rig.UI.ChangeChromaticAberration(0.3f);

            Assert.That(_rig.ChromaticAberration.intensity.value, Is.EqualTo(0.3f).Within(1e-3f));
            Assert.That(_rig.ChromaticAberration.intensity.overrideState, Is.True);
            Assert.That(_rig.ChromaticAberration.active, Is.True);

            _rig.UI.ChangeChromaticAberration(0f);
            Assert.That(_rig.ChromaticAberration.active, Is.False);
        }

        [Test]
        public void FilmGrain_ReachesTheOverrideAndPicksALookupItCanActuallyDraw()
        {
            // Custom is the one lookup that needs a texture supplied. Left there with no texture
            // the effect renders nothing however high the intensity goes.
            _rig.FilmGrain.type.value = FilmGrainLookup.Custom;

            _rig.UI.ChangeFilmGrain(0.25f);

            Assert.That(_rig.FilmGrain.intensity.value, Is.EqualTo(0.25f).Within(1e-3f));
            Assert.That(_rig.FilmGrain.type.value, Is.Not.EqualTo(FilmGrainLookup.Custom));
            Assert.That(_rig.FilmGrain.type.overrideState, Is.True);
            Assert.That(_rig.FilmGrain.active, Is.True);

            _rig.UI.ChangeFilmGrain(0f);
            Assert.That(_rig.FilmGrain.active, Is.False);
        }

        [Test]
        public void LensDistortion_ReachesTheOverrideAndWorksInBothDirections()
        {
            _rig.UI.ChangeLensDistortion(0.5f);
            Assert.That(_rig.LensDistortion.intensity.value, Is.EqualTo(0.5f).Within(1e-3f));
            Assert.That(_rig.LensDistortion.active, Is.True);

            _rig.UI.ChangeLensDistortion(-0.5f);
            Assert.That(_rig.LensDistortion.intensity.value, Is.EqualTo(-0.5f).Within(1e-3f));
            Assert.That(_rig.LensDistortion.active, Is.True,
                "Barrel and pincushion are opposite signs of one control; only zero is neutral.");

            _rig.UI.ChangeLensDistortion(0f);
            Assert.That(_rig.LensDistortion.active, Is.False);
        }

        [Test]
        public void BloomScatter_ReachesTheOverrideWithoutSwitchingBloomOn()
        {
            _rig.Bloom.active = false;

            _rig.UI.ChangeBloomScatter(0.35f);

            Assert.That(_rig.Bloom.scatter.value, Is.EqualTo(0.35f).Within(1e-3f));
            Assert.That(_rig.Bloom.scatter.overrideState, Is.True);
            Assert.That(_rig.Bloom.active, Is.False,
                "Scatter shapes the glow; the intensity is what decides whether there is one.");
        }

        [Test]
        public void VignetteSmoothness_ReachesTheOverrideAndStaysInsideURPsRange()
        {
            _rig.UI.ChangeVignetteSmoothness(0.7f);
            Assert.That(_rig.Vignette.smoothness.value, Is.EqualTo(0.7f).Within(1e-3f));
            Assert.That(_rig.Vignette.smoothness.overrideState, Is.True);

            _rig.UI.ChangeVignetteSmoothness(0f);
            Assert.That(_rig.Vignette.smoothness.value,
                Is.EqualTo(BasisHandHeldCameraUI.MinVignetteSmoothness).Within(1e-4f),
                "URP clamps smoothness above zero, so the slider must not be able to ask for zero.");
        }

        [Test]
        public void VignetteSmoothness_DoesNotSwitchTheVignetteOn()
        {
            _rig.Vignette.active = false;

            _rig.UI.ChangeVignetteSmoothness(0.9f);

            Assert.That(_rig.Vignette.active, Is.False);
        }

        [Test]
        public void LensDistortionScale_ReachesTheOverrideWithoutSwitchingDistortionOn()
        {
            _rig.LensDistortion.active = false;

            _rig.UI.ChangeLensDistortionScale(1.4f);

            Assert.That(_rig.LensDistortion.scale.value, Is.EqualTo(1.4f).Within(1e-3f));
            Assert.That(_rig.LensDistortion.scale.overrideState, Is.True);
            Assert.That(_rig.LensDistortion.active, Is.False);
        }

        [Test]
        public void PaniniProjection_ReachesTheOverrideAndSwitchesItselfOnAndOff()
        {
            _rig.UI.ChangePaniniDistance(0.6f);

            Assert.That(_rig.PaniniProjection.distance.value, Is.EqualTo(0.6f).Within(1e-3f));
            Assert.That(_rig.PaniniProjection.distance.overrideState, Is.True);
            Assert.That(_rig.PaniniProjection.active, Is.True);

            _rig.UI.ChangePaniniDistance(0f);
            Assert.That(_rig.PaniniProjection.active, Is.False);
        }

        [Test]
        public void PaniniCrop_ReachesTheOverrideWithoutSwitchingTheProjectionOn()
        {
            _rig.PaniniProjection.active = false;

            _rig.UI.ChangePaniniCropToFit(0.25f);

            Assert.That(_rig.PaniniProjection.cropToFit.value, Is.EqualTo(0.25f).Within(1e-3f));
            Assert.That(_rig.PaniniProjection.cropToFit.overrideState, Is.True);
            Assert.That(_rig.PaniniProjection.active, Is.False);
        }

        [Test]
        public void CaptureTonemapping_IsTheStillsGradeAndDefaultsToWhatTheCaptureAlwaysUsed()
        {
            Assert.That(_rig.Camera.CaptureTonemapping, Is.EqualTo(TonemappingMode.ACES),
                "The capture path hard-coded ACES before this was a choice, so that is the default.");

            _rig.Camera.SetCaptureTonemapping((int)TonemappingMode.Neutral);
            Assert.That(_rig.Camera.CaptureTonemapping, Is.EqualTo(TonemappingMode.Neutral));

            _rig.Camera.SetCaptureTonemapping(99);
            Assert.That(_rig.Camera.CaptureTonemapping, Is.EqualTo(TonemappingMode.ACES),
                "A hand-edited file must not be able to name a mode URP has no entry for.");
        }

        [Test]
        public void TheViewfinderGradeIsFixedAndSeparateFromTheStills()
        {
            Assert.That(BasisHandHeldCamera.PreviewTonemapping, Is.EqualTo(TonemappingMode.Neutral));
            Assert.That(BasisHandHeldCamera.PreviewTonemapping, Is.Not.EqualTo(_rig.Camera.CaptureTonemapping),
                "The preview and the still are graded separately, which is what the setting exists to say.");
        }

        [Test]
        public void TonemappingSurvivesAProfileThatCarriesNoTonemapper()
        {
            // The rig supplies no Tonemapping override, exactly as a profile missing one would.
            Assert.That(_rig.Camera.MetaData.tonemapping, Is.Null);
            Assert.DoesNotThrow(() => _rig.Camera.ToggleToneMapping(TonemappingMode.ACES));
        }

        [Test]
        public void MotionBlur_ReachesTheOverrideAndSwitchesItselfOnAndOff()
        {
            _rig.UI.ChangeMotionBlur(0.6f);

            Assert.That(_rig.MotionBlur.intensity.value, Is.EqualTo(0.6f).Within(1e-3f));
            Assert.That(_rig.MotionBlur.intensity.overrideState, Is.True);
            Assert.That(_rig.MotionBlur.active, Is.True);

            _rig.UI.ChangeMotionBlur(0f);

            Assert.That(_rig.MotionBlur.active, Is.False,
                "URP skips the pass at zero strength, and an inactive override still asks for the depth texture.");
        }

        [Test]
        public void MotionBlurClamp_ReachesTheOverrideWithoutTouchingTheStrength()
        {
            _rig.UI.ChangeMotionBlur(0.5f);
            _rig.UI.ChangeMotionBlurClamp(0.15f);

            Assert.That(_rig.MotionBlur.clamp.value, Is.EqualTo(0.15f).Within(1e-4f));
            Assert.That(_rig.MotionBlur.clamp.overrideState, Is.True);
            Assert.That(_rig.MotionBlur.intensity.value, Is.EqualTo(0.5f).Within(1e-3f),
                "The length limit shapes the streak; it is not a second strength control.");
        }

        [Test]
        public void MotionBlurQualityAndMode_ReachTheOverrideAsTheirEnums()
        {
            _rig.UI.SetMotionBlurQuality(2);
            _rig.UI.SetMotionBlurMode(1);

            Assert.That(_rig.MotionBlur.quality.value, Is.EqualTo(MotionBlurQuality.High));
            Assert.That(_rig.MotionBlur.quality.overrideState, Is.True);
            Assert.That(_rig.MotionBlur.mode.value, Is.EqualTo(MotionBlurMode.CameraAndObjects));
            Assert.That(_rig.MotionBlur.mode.overrideState, Is.True,
                "Camera And Objects is what makes URP render the motion vector pass — unoverridden it never reaches the stack.");

            Assert.That(_rig.UI.MotionBlurQuality, Is.EqualTo(2));
            Assert.That(_rig.UI.MotionBlurMode, Is.EqualTo(1));
        }

        [Test]
        public void MotionBlurQualityAndMode_ClampToTheEnumsTheyStandFor()
        {
            // Both arrive as an index from a dropdown, and a dropdown that gains or loses an entry
            // would otherwise cast a number the enum has no member for.
            _rig.UI.SetMotionBlurQuality(99);
            _rig.UI.SetMotionBlurMode(99);

            Assert.That(_rig.MotionBlur.quality.value, Is.EqualTo(MotionBlurQuality.High));
            Assert.That(_rig.MotionBlur.mode.value, Is.EqualTo(MotionBlurMode.CameraAndObjects));

            _rig.UI.SetMotionBlurQuality(-3);
            _rig.UI.SetMotionBlurMode(-3);

            Assert.That(_rig.MotionBlur.quality.value, Is.EqualTo(MotionBlurQuality.Low));
            Assert.That(_rig.MotionBlur.mode.value, Is.EqualTo(MotionBlurMode.CameraOnly));
        }

        [Test]
        public void Bloom_IntensityAndThresholdBothReachTheOverride()
        {
            _rig.UI.ChangeBloomIntensity(2.5f);
            _rig.UI.ChangeBloomThreshold(1.4f);

            Assert.That(_rig.Bloom.intensity.value, Is.EqualTo(2.5f).Within(1e-3f));
            Assert.That(_rig.Bloom.threshold.value, Is.EqualTo(1.4f).Within(1e-3f));
        }

        // ---------- Capture ----------

        [Test]
        public void PhotoResolution_ReachesTheCaptureSize()
        {
            _rig.Camera.ChangeResolution(2);

            Assert.That(_rig.Camera.captureWidth, Is.EqualTo(3840));
            Assert.That(_rig.Camera.captureHeight, Is.EqualTo(2160));
        }

        [Test]
        public void PhotoResolution_IgnoresAnIndexThatIsNotAPreset()
        {
            _rig.Camera.ChangeResolution(1);
            int width = _rig.Camera.captureWidth;

            _rig.Camera.ChangeResolution(99);

            Assert.That(_rig.Camera.captureWidth, Is.EqualTo(width),
                "An index past the preset table must leave the capture size alone, not zero it.");
        }

        [Test]
        public void PhotoFormat_ReachesTheCameraThatWritesTheFile()
        {
            _rig.UI.SetFormat(BasisHandHeldCameraUI.FORMAT_EXR);
            Assert.That(_rig.Camera.captureFormat, Is.EqualTo("EXR"));

            _rig.UI.SetFormat(BasisHandHeldCameraUI.FORMAT_PNG);
            Assert.That(_rig.Camera.captureFormat, Is.EqualTo("PNG"),
                "The format control moved off the prop to the panel; the camera still has to hear about it.");
        }

        [Test]
        public void Capture360_ReachesTheCamera()
        {
            _rig.UI.SetCapture360State(true);
            Assert.That(_rig.Camera.capture360Enabled, Is.True);

            _rig.UI.SetCapture360State(false);
            Assert.That(_rig.Camera.capture360Enabled, Is.False);
        }

        [Test]
        public void Selfie_FlipsTheCameraAndTheMirroredPreviewTogether()
        {
            GameObject flip = new GameObject("PreviewFlip");
            try
            {
                flip.transform.localScale = new Vector3(1f, 1f, 1f);
                _rig.UI.imagePreviewFlip = flip.transform;

                _rig.UI.ToggleSelfie();

                Assert.That(_rig.UI.IsSelfieMode, Is.True);
                Assert.That(flip.transform.localScale.x, Is.LessThan(0f),
                    "Selfie mirrors the preview; without the flip the picture reads back to front.");

                _rig.UI.ToggleSelfie();

                Assert.That(_rig.UI.IsSelfieMode, Is.False);
                Assert.That(flip.transform.localScale.x, Is.GreaterThan(0f));
            }
            finally
            {
                Object.DestroyImmediate(flip);
            }
        }

        [Test]
        public void ExposureOnCamera_ShowsAndHidesTheSliderOnTheProp()
        {
            _rig.UI.SetExposureOnCameraVisible(true);
            Assert.That(_rig.UI.ShowExposureOnCamera, Is.True);
            Assert.That(_rig.ExposureSlider.gameObject.activeSelf, Is.True);

            _rig.UI.SetExposureOnCameraVisible(false);
            Assert.That(_rig.UI.ShowExposureOnCamera, Is.False);
            Assert.That(_rig.ExposureSlider.gameObject.activeSelf, Is.False,
                "The toggle's whole job is whether the prop carries the exposure slider.");
        }

        // ---------- Follow ----------

        [Test]
        public void FollowOffsets_AreStoredPerAxisSoOneSliderCannotClearAnother()
        {
            // The panel drives these one axis at a time out of a shared vector.
            _rig.Camera.Modifiers.follow.positionOffset = new Vector3(0.5f, 0f, 1.4f);

            Vector3 offset = _rig.Camera.Modifiers.follow.positionOffset;
            offset[1] = 0.9f;
            _rig.Camera.Modifiers.follow.positionOffset = offset;

            Assert.That(_rig.Camera.Modifiers.follow.positionOffset, Is.EqualTo(new Vector3(0.5f, 0.9f, 1.4f)));
        }

        [Test]
        public void AutoFollow_TakesTheCameraOutOfTheHandAndGivesItBack()
        {
            _rig.Camera.SetPositionModifier(BasisCameraPositionModifier.FollowSubject);
            Assert.That(_rig.Camera.Modifiers.DrivesPosition, Is.True);
            Assert.That(_rig.Camera.PinSpace, Is.EqualTo(BasisHandHeldCameraInteractable.CameraPinSpace.WorldSpace),
                "A followed camera cannot stay pinned to the hand it flew away from.");

            _rig.Camera.SetPositionModifier(BasisCameraPositionModifier.FreeFly);
            Assert.That(_rig.Camera.Modifiers.DrivesPosition, Is.False);
            Assert.That(_rig.Camera.PinSpace, Is.EqualTo(BasisHandHeldCameraInteractable.CameraPinSpace.HandHeld));
        }

        // ---------- Background ----------

        [Test]
        public void BackgroundMode_ClearsToTheColourItNames()
        {
            _rig.CaptureCamera.clearFlags = CameraClearFlags.Skybox;

            _rig.Camera.SetBackgroundMode(BasisCameraBackgroundMode.GreenScreen);

            Assert.That(_rig.CaptureCamera.clearFlags, Is.EqualTo(CameraClearFlags.SolidColor));
            Assert.That(_rig.CaptureCamera.backgroundColor, Is.EqualTo(BasisCameraBackgrounds.ChromaGreen));
        }

        [Test]
        public void TransparentBackground_ClearsWithZeroAlpha()
        {
            _rig.Camera.SetBackgroundMode(BasisCameraBackgroundMode.Transparent);

            Assert.That(_rig.CaptureCamera.clearFlags, Is.EqualTo(CameraClearFlags.SolidColor));
            Assert.That(_rig.CaptureCamera.backgroundColor, Is.EqualTo(Color.clear));
            Assert.That(_rig.Camera.IsBackgroundKeyable, Is.True);
        }

        [Test]
        public void BackgroundMode_ReturningToWorldRestoresWhatTheCameraHadBefore()
        {
            _rig.CaptureCamera.clearFlags = CameraClearFlags.Skybox;
            int worldMask = _rig.CaptureCamera.cullingMask;

            _rig.Camera.SetBackgroundMode(BasisCameraBackgroundMode.Black);
            _rig.Camera.SetBackgroundMode(BasisCameraBackgroundMode.World);

            Assert.That(_rig.CaptureCamera.clearFlags, Is.EqualTo(CameraClearFlags.Skybox),
                "World means what the camera actually had, not a guess at what it should have.");
            Assert.That(_rig.CaptureCamera.cullingMask, Is.EqualTo(worldMask));
        }

        [Test]
        public void CustomBackgroundColour_AppliesImmediatelyOnlyWhileCustomIsSelected()
        {
            Color orange = new Color(1f, 0.5f, 0f, 1f);

            _rig.Camera.SetBackgroundMode(BasisCameraBackgroundMode.Black);
            _rig.Camera.SetBackgroundCustomColor(orange);

            Assert.That(_rig.CaptureCamera.backgroundColor, Is.EqualTo(Color.black),
                "Editing the custom colour must not hijack a different mode.");

            _rig.Camera.SetBackgroundMode(BasisCameraBackgroundMode.Custom);

            Assert.That(_rig.CaptureCamera.backgroundColor, Is.EqualTo(orange));
        }

        [Test]
        public void KeepWorld_DecidesWhetherAColourBackgroundIsAKeyableMatte()
        {
            _rig.Camera.SetBackgroundMode(BasisCameraBackgroundMode.GreenScreen);
            _rig.Camera.SetBackgroundKeepsWorld(false);
            int matteMask = _rig.CaptureCamera.cullingMask;

            _rig.Camera.SetBackgroundKeepsWorld(true);

            Assert.That(_rig.Camera.IsBackgroundKeyable, Is.False);
            Assert.That(_rig.CaptureCamera.cullingMask, Is.Not.EqualTo(matteMask),
                "Keeping the world in shot is the difference between a green wall and a green key.");
        }

        // ---------- Debug gizmos ----------

        [Test]
        public void GizmoLayers_ToggleOneAtATime()
        {
            _rig.Camera.DebugGizmos.SetLayerEnabled(BasisCameraGizmoLayers.Frustum, true);
            _rig.Camera.DebugGizmos.SetLayerEnabled(BasisCameraGizmoLayers.Follow, true);

            _rig.Camera.DebugGizmos.SetLayerEnabled(BasisCameraGizmoLayers.Frustum, false);

            Assert.That(_rig.Camera.DebugGizmos.IsLayerEnabled(BasisCameraGizmoLayers.Frustum), Is.False);
            Assert.That(_rig.Camera.DebugGizmos.IsLayerEnabled(BasisCameraGizmoLayers.Follow), Is.True,
                "The layers are a flags field; clearing one must not clear its neighbours.");
        }

        // ---------- Robustness ----------

        [Test]
        public void EverySettingIsSafeToDriveBeforeAProfileIsCached()
        {
            // Awake caches the post-processing references. A camera whose profile has not resolved
            // yet — a world change mid-load, a prefab missing an override — still receives these
            // calls from the panel tick, and an exception there takes the whole panel down.
            BasisHandHeldCameraMetaData metaData = _rig.Camera.MetaData;
            metaData.depthOfField = null;
            metaData.bloom = null;
            metaData.colorAdjustments = null;
            metaData.vignette = null;
            metaData.chromaticAberration = null;
            metaData.filmGrain = null;
            metaData.whiteBalance = null;
            metaData.lensDistortion = null;
            metaData.motionBlur = null;

            Assert.DoesNotThrow(() =>
            {
                _rig.UI.ChangeAperture(4f);
                _rig.UI.ChangeDoFFocalLength(50f);
                _rig.UI.ChangeDoFBladeCount(5f);
                _rig.UI.DepthChangeFocusDistance(3f);
                _rig.UI.ChangeFocusDistance(3f);
                _rig.UI.ChangeBloomIntensity(1f);
                _rig.UI.ChangeBloomThreshold(1f);
                _rig.UI.ChangeContrast(10f);
                _rig.UI.ChangeSaturation(10f);
                _rig.UI.ChangeHueShift(10f);
                _rig.UI.ChangeVignette(0.2f);
                _rig.UI.ChangeChromaticAberration(0.2f);
                _rig.UI.ChangeFilmGrain(0.2f);
                _rig.UI.ChangeWhiteBalanceTemperature(10f);
                _rig.UI.ChangeWhiteBalanceTint(10f);
                _rig.UI.ChangeLensDistortion(0.2f);
                _rig.UI.ChangeMotionBlur(0.2f);
                _rig.UI.ChangeMotionBlurClamp(0.1f);
                _rig.UI.SetMotionBlurQuality(2);
                _rig.UI.SetMotionBlurMode(1);
                _rig.UI.ChangeExposureCompensation(4f);
                _rig.UI.ChangeVolumetricDensity(0.1f);
                _rig.UI.SetDoFMode(2);
                _rig.UI.SetDepthMode(BasisHandHeldCameraUI.DepthMode.Manual);
            });
        }
    }
}
