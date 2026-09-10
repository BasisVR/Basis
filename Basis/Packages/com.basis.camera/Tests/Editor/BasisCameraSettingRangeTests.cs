using System;
using System.Globalization;
using Basis.BasisUI;
using Basis.BasisUI.HandHeldCamera;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Basis.Tests.Camera
{
    /// <summary>
    /// Whether the settings read the way they behave. A control can be perfectly wired and still be
    /// wrong to use: a slider whose travel runs past the clamp underneath it has a stretch that does
    /// nothing, a preset whose label the parser cannot read applies nothing at all, and a dropdown
    /// whose labels have drifted from the enum they stand in for picks the wrong entry silently.
    ///
    /// <para>
    /// The depth-of-field limits are asserted against URP's own <c>ClampedFloatParameter</c> bounds
    /// rather than against copies of the numbers, so the tests keep meaning something if URP moves
    /// them.
    /// </para>
    /// </summary>
    public class BasisCameraSettingRangeTests
    {
        // ---------- No dead travel: slider ranges against the clamp underneath ----------

        [Test]
        public void ApertureRange_IsExactlyWhatUrpAccepts()
        {
            DepthOfField dof = ScriptableObject.CreateInstance<DepthOfField>();
            try
            {
                Assert.That(BasisHandHeldCameraUI.MinAperture, Is.EqualTo(dof.aperture.min).Within(1e-4f),
                    "Travel below the clamp is dead — and below f/1 the circle of confusion inverts.");
                Assert.That(BasisHandHeldCameraUI.MaxAperture, Is.EqualTo(dof.aperture.max).Within(1e-4f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(dof);
            }
        }

        [Test]
        public void FocalLengthRange_IsExactlyWhatUrpAccepts()
        {
            DepthOfField dof = ScriptableObject.CreateInstance<DepthOfField>();
            try
            {
                Assert.That(BasisHandHeldCameraUI.MinFocalLength, Is.EqualTo(dof.focalLength.min).Within(1e-4f));
                Assert.That(BasisHandHeldCameraUI.MaxFocalLength, Is.EqualTo(dof.focalLength.max).Within(1e-4f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(dof);
            }
        }

        [Test]
        public void BladeCountRange_IsExactlyWhatUrpAccepts()
        {
            DepthOfField dof = ScriptableObject.CreateInstance<DepthOfField>();
            try
            {
                Assert.That(BasisHandHeldCameraUI.MinBladeCount, Is.EqualTo(dof.bladeCount.min));
                Assert.That(BasisHandHeldCameraUI.MaxBladeCount, Is.EqualTo(dof.bladeCount.max));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(dof);
            }
        }

        [Test]
        public void PercentageEffectSliders_MapOntoTheZeroToOneParametersBeneathThem()
        {
            // The panel offers these as percentages and divides by 100 on the way in. If the
            // parameter's range were not 0..1 the top of the slider would clip or fall short.
            AssertNormalised<Vignette>(v => v.intensity);
            AssertNormalised<ChromaticAberration>(c => c.intensity);
            AssertNormalised<FilmGrain>(f => f.intensity);
        }

        [Test]
        public void MotionBlurRanges_AreExactlyWhatUrpAccepts()
        {
            MotionBlur blur = ScriptableObject.CreateInstance<MotionBlur>();
            try
            {
                // The panel offers the strength as a percentage of the 0..1 parameter and the
                // length limit as a percentage of the frame, which is what URP's clamp already is.
                Assert.That(BasisHandHeldCameraUI.MinMotionBlur, Is.EqualTo(blur.intensity.min).Within(1e-4f));
                Assert.That(BasisHandHeldCameraUI.MaxMotionBlur, Is.EqualTo(blur.intensity.max).Within(1e-4f));
                Assert.That(BasisHandHeldCameraUI.MinMotionBlurClamp, Is.EqualTo(blur.clamp.min).Within(1e-4f));
                Assert.That(BasisHandHeldCameraUI.MaxMotionBlurClamp, Is.EqualTo(blur.clamp.max).Within(1e-4f),
                    "Travel past the clamp is dead: the parameter's setter clamps before anything renders.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(blur);
            }
        }

        [Test]
        public void MotionBlurDropdownIndices_StillLineUpWithUrpsEnums()
        {
            // Both dropdowns store the picked index and cast it to the enum, so a member added or
            // reordered upstream would silently select a different entry.
            Assert.That((int)MotionBlurQuality.Low, Is.EqualTo(0));
            Assert.That((int)MotionBlurQuality.Medium, Is.EqualTo(1));
            Assert.That((int)MotionBlurQuality.High, Is.EqualTo(2));
            Assert.That(Enum.GetValues(typeof(MotionBlurQuality)).Length, Is.EqualTo(3),
                "The quality dropdown offers exactly three entries.");

            Assert.That((int)MotionBlurMode.CameraOnly, Is.EqualTo(0));
            Assert.That((int)MotionBlurMode.CameraAndObjects, Is.EqualTo(1));
            Assert.That(Enum.GetValues(typeof(MotionBlurMode)).Length, Is.EqualTo(2),
                "The mode dropdown offers exactly two entries.");
        }

        [Test]
        public void MotionBlurDefault_LeavesTheShotAloneButIsReadyToUse()
        {
            var defaults = new BasisHandHeldCameraUI.CameraSettings();

            Assert.That(defaults.motionBlurIntensity, Is.Zero,
                "A fresh camera adds nothing to the shot; motion blur is opted into.");
            Assert.That(defaults.motionBlurClamp, Is.GreaterThan(0f),
                "A zero length limit would leave the effect switched on and rendering nothing.");
            Assert.That(defaults.motionBlurClamp, Is.LessThanOrEqualTo(BasisHandHeldCameraUI.MaxMotionBlurClamp));
            Assert.That(defaults.motionBlurMode, Is.EqualTo((int)MotionBlurMode.CameraOnly),
                "Camera And Objects costs a motion vector pass, so it is asked for rather than assumed.");
        }

        [Test]
        public void LensDistortionSlider_CoversBarrelAndPincushionAlike()
        {
            LensDistortion distortion = ScriptableObject.CreateInstance<LensDistortion>();
            try
            {
                // The panel runs -100..100 and divides by 100, so the parameter has to be symmetric
                // about zero or one direction is cut short and reads as a lopsided control.
                Assert.That(distortion.intensity.min, Is.EqualTo(-1f).Within(1e-4f));
                Assert.That(distortion.intensity.max, Is.EqualTo(1f).Within(1e-4f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(distortion);
            }
        }

        [Test]
        public void GradingSliders_MatchTheRangesTheirParametersClampTo()
        {
            ColorAdjustments grading = ScriptableObject.CreateInstance<ColorAdjustments>();
            WhiteBalance balance = ScriptableObject.CreateInstance<WhiteBalance>();
            try
            {
                // The panel offers all four at -100..100 and hue at -180..180.
                Assert.That(grading.contrast.min, Is.EqualTo(-100f).Within(1e-4f));
                Assert.That(grading.contrast.max, Is.EqualTo(100f).Within(1e-4f));
                Assert.That(grading.saturation.min, Is.EqualTo(-100f).Within(1e-4f));
                Assert.That(grading.saturation.max, Is.EqualTo(100f).Within(1e-4f));
                Assert.That(grading.hueShift.min, Is.EqualTo(-180f).Within(1e-4f));
                Assert.That(grading.hueShift.max, Is.EqualTo(180f).Within(1e-4f));
                Assert.That(balance.temperature.min, Is.EqualTo(-100f).Within(1e-4f));
                Assert.That(balance.tint.max, Is.EqualTo(100f).Within(1e-4f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(grading);
                UnityEngine.Object.DestroyImmediate(balance);
            }
        }

        [Test]
        public void FovRange_IsWideEnoughToBeWorthASliderAndNarrowEnoughToStaySane()
        {
            Assert.That(BasisHandHeldCameraUI.MinFov, Is.GreaterThan(0f));
            Assert.That(BasisHandHeldCameraUI.MaxFov, Is.LessThanOrEqualTo(179f),
                "A field of view at or past 180 degrees has no valid projection.");
            Assert.That(BasisHandHeldCameraUI.MaxFov - BasisHandHeldCameraUI.MinFov, Is.GreaterThan(30f));
        }

        [Test]
        public void FocusRange_StartsCloseEnoughForAPortraitAndReachesPastAnyRoom()
        {
            Assert.That(BasisHandHeldCameraUI.MinFocusDistance, Is.LessThanOrEqualTo(0.5f));
            Assert.That(BasisHandHeldCameraUI.MaxFocusDistance, Is.GreaterThanOrEqualTo(20f));
        }

        // ---------- Defaults land somewhere the controls can reach ----------

        [Test]
        public void EveryShippedDefaultSitsInsideTheRangeItsControlOffers()
        {
            // A default outside its slider is snapped on the first drag, so the camera silently
            // changes the moment the control is touched — and never changes back.
            var defaults = new BasisHandHeldCameraUI.CameraSettings();

            Assert.That(defaults.fov, Is.InRange(BasisHandHeldCameraUI.MinFov, BasisHandHeldCameraUI.MaxFov));
            Assert.That(defaults.depthAperture, Is.InRange(BasisHandHeldCameraUI.MinAperture, BasisHandHeldCameraUI.MaxAperture));
            Assert.That(defaults.depthFocusDistance, Is.InRange(BasisHandHeldCameraUI.MinFocusDistance, BasisHandHeldCameraUI.MaxFocusDistance));
            Assert.That(defaults.dofFocalLength, Is.InRange(BasisHandHeldCameraUI.MinFocalLength, BasisHandHeldCameraUI.MaxFocalLength));
            Assert.That(defaults.dofBladeCount, Is.InRange(BasisHandHeldCameraUI.MinBladeCount, BasisHandHeldCameraUI.MaxBladeCount));
            Assert.That(defaults.exposureIndex, Is.InRange(0, BasisHandHeldCameraUI.ExposureStopCount - 1));
            Assert.That(defaults.resolutionIndex, Is.InRange(0, new BasisHandHeldCameraMetaData().resolutions.Length - 1));
            Assert.That(defaults.formatIndex, Is.InRange(0, new BasisHandHeldCameraMetaData().formats.Length - 1));
            Assert.That(defaults.backgroundMode, Is.InRange(0, Enum.GetValues(typeof(BasisCameraBackgroundMode)).Length - 1));

            // The film grading. Each of these has a neutral its control has to be able to return
            // to, and a default outside the slider would be a look nobody could undo.
            Assert.That(defaults.filmLift, Is.InRange(BasisHandHeldCameraUI.MinFilmLift, BasisHandHeldCameraUI.MaxFilmLift));
            Assert.That(defaults.splitToningBalance,
                Is.InRange(BasisHandHeldCameraUI.MinSplitToningBalance, BasisHandHeldCameraUI.MaxSplitToningBalance));
            Assert.That(defaults.filmGrainResponse, Is.InRange(0f, 1f));
            Assert.That(defaults.filmGrainType,
                Is.InRange(0, (int)UnityEngine.Rendering.Universal.FilmGrainLookup.Large02));
        }

        [Test]
        public void ThePhysicalCameraPresetIndicesAllPointAtAPreset()
        {
            var defaults = new BasisHandHeldCameraUI.CameraSettings();
            var metaData = new BasisHandHeldCameraMetaData();

            Assert.That(defaults.apertureIndex, Is.InRange(0, metaData.apertures.Length - 1));
            Assert.That(defaults.shutterSpeedIndex, Is.InRange(0, metaData.shutterSpeeds.Length - 1));
            Assert.That(defaults.isoIndex, Is.InRange(0, metaData.isoValues.Length - 1));
            Assert.That(metaData.MSAALevels, Contains.Item(defaults.msaaSamples));
        }

        // ---------- Presets that are parsed out of their own labels ----------

        [Test]
        public void EveryAperturePresetLabelParsesBackToAnFStop()
        {
            // ApplySettings reads the number straight out of the label. A label the parser trips on
            // throws inside a try/catch and abandons the rest of the load.
            var metaData = new BasisHandHeldCameraMetaData();

            for (int Index = 0; Index < metaData.apertures.Length; Index++)
            {
                string label = metaData.apertures[Index];
                Assert.That(float.TryParse(label.TrimStart('f', '/'), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out float stop), Is.True,
                    $"Aperture preset \"{label}\" cannot be parsed.");
                Assert.That(stop, Is.GreaterThan(0f), $"Aperture preset \"{label}\" is not a usable f-stop.");
            }
        }

        [Test]
        public void ApertureLabelsParseTheSameWayInEveryLocale()
        {
            // Three of the presets carry a decimal point. Parsed under the running machine's own
            // locale, "f/1.4" comes back as 14 anywhere the decimal separator is a comma — an
            // aperture no lens has, applied to every capture, on a whole set of machines that
            // will never see it fail on ours.
            System.Globalization.CultureInfo previous = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    System.Globalization.CultureInfo.GetCultureInfo("de-DE");

                var metaData = new BasisHandHeldCameraMetaData();
                for (int Index = 0; Index < metaData.apertures.Length; Index++)
                {
                    float stop = BasisHandHeldCameraUI.ParseAperture(metaData.apertures[Index]);

                    Assert.That(stop, Is.InRange(BasisHandHeldCameraUI.MinAperture, BasisHandHeldCameraUI.MaxAperture),
                        $"\"{metaData.apertures[Index]}\" parsed to {stop} under a comma-decimal locale.");
                }
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = previous;
            }
        }

        [Test]
        public void EveryShutterSpeedPresetLabelParsesBackToAFraction()
        {
            var metaData = new BasisHandHeldCameraMetaData();

            for (int Index = 0; Index < metaData.shutterSpeeds.Length; Index++)
            {
                string label = metaData.shutterSpeeds[Index];
                string[] parts = label.Split('/');

                Assert.That(parts.Length, Is.EqualTo(2), $"Shutter preset \"{label}\" is not a fraction.");
                Assert.That(float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture,
                        out float denominator), Is.True, $"Shutter preset \"{label}\" cannot be parsed.");
                Assert.That(denominator, Is.Not.Zero, $"Shutter preset \"{label}\" divides by zero.");
            }
        }

        [Test]
        public void EveryIsoPresetLabelParsesBackToAWholeNumber()
        {
            var metaData = new BasisHandHeldCameraMetaData();

            for (int Index = 0; Index < metaData.isoValues.Length; Index++)
            {
                string label = metaData.isoValues[Index];
                Assert.That(int.TryParse(label, NumberStyles.Integer, CultureInfo.InvariantCulture, out int iso),
                    Is.True, $"ISO preset \"{label}\" cannot be parsed.");
                Assert.That(iso, Is.GreaterThan(0));
            }
        }

        [Test]
        public void PresetTablesReadInAConsistentDirection()
        {
            // These are picked from a list by eye. Out of order they still work, but the control
            // stops behaving like a dial — one step right can darken or brighten unpredictably.
            var metaData = new BasisHandHeldCameraMetaData();

            AssertAscending(metaData.isoValues, label => float.Parse(label, CultureInfo.InvariantCulture), "ISO");
            AssertAscending(metaData.apertures,
                label => float.Parse(label.TrimStart('f', '/'), CultureInfo.InvariantCulture), "aperture");
            // Shutter speeds are listed fastest first, so their denominators descend.
            AssertDescending(metaData.shutterSpeeds,
                label => float.Parse(label.Split('/')[1], CultureInfo.InvariantCulture), "shutter speed");
        }

        [Test]
        public void ResolutionPresetsAscendAndShareOneAspect()
        {
            // The preview, the render target and the composition guides are all sized from the
            // chosen preset, so a preset with an odd aspect reframes the shot rather than resizing it.
            var metaData = new BasisHandHeldCameraMetaData();
            float aspect = (float)metaData.resolutions[0].width / metaData.resolutions[0].height;

            for (int Index = 0; Index < metaData.resolutions.Length; Index++)
            {
                (int width, int height) = metaData.resolutions[Index];

                Assert.That(width, Is.GreaterThan(0));
                Assert.That(height, Is.GreaterThan(0));
                Assert.That((float)width / height, Is.EqualTo(aspect).Within(0.01f),
                    $"Preset {width}x{height} does not share the aspect of the others.");

                if (Index == 0) continue;
                Assert.That(width, Is.GreaterThan(metaData.resolutions[Index - 1].width),
                    "Resolution presets read as a quality ladder and have to climb.");
            }
        }

        [Test]
        public void MsaaLevelsAreThePowersOfTwoTheGpuAccepts()
        {
            var metaData = new BasisHandHeldCameraMetaData();

            Assert.That(metaData.MSAALevels, Is.EqualTo(new[] { 1, 2, 4, 8 }));
        }

        // ---------- Tables that stand in for an enum ----------

        [Test]
        public void TheMsaaDropdownOffersExactlyTheLevelsTheCameraSupports()
        {
            // The dropdown resolves its selection by index into this table, so a table that has
            // drifted from the camera's own list applies the wrong sample count.
            Assert.That(BasisHandHeldCameraPanelProvider.MsaaSampleCountsForTest,
                Is.EqualTo(new BasisHandHeldCameraMetaData().MSAALevels));
        }

        [Test]
        public void TheDetachedMarkerDropdownHasOneLabelPerMode()
        {
            Assert.That(BasisHandHeldCameraPanelProvider.DetachedMarkerKeysForTest.Length,
                Is.EqualTo(Enum.GetValues(typeof(BasisCameraDetachedMarker)).Length),
                "The dropdown casts its index straight to the enum, so a missing label makes a mode unreachable " +
                "and a spare one selects a mode that does not exist.");
        }

        [Test]
        public void TheEaseDropdownsHaveOneLabelPerCurve()
        {
            // Both ease dropdowns cast their index straight to the enum, so a missing label makes a
            // curve unreachable and a spare one selects a curve that does not exist.
            Assert.That(BasisHandHeldCameraPanelProvider.DollyEaseKeysForTest.Length,
                Is.EqualTo(Enum.GetValues(typeof(Basis.Cinematics.BasisCameraEase)).Length));
        }

        [Test]
        public void TheAimPointDropdownHasOneLabelPerPoint()
        {
            Assert.That(BasisHandHeldCameraPanelProvider.AimPointKeysForTest.Length,
                Is.EqualTo(Enum.GetValues(typeof(Basis.Cinematics.BasisCameraAimPoint)).Length),
                "The dropdown indexes the aim-point catalogue, so a missing label makes a point unreachable.");
        }

        [Test]
        public void EveryDropdownOptionKeyHasATranslationAndATooltip()
        {
            // The options are keys rather than text, so a key with nothing behind it shows as the
            // key itself in the list. The tooltip is what tells you which option to pick at all.
            foreach (string key in BasisHandHeldCameraPanelProvider.OptionKeysForTest)
            {
                Assert.That(BasisLocalization.TryGet(key, out string label), Is.True,
                    $"{key} has no translation, so the dropdown would show the key.");
                Assert.That(label, Is.Not.Empty);

                Assert.That(BasisLocalization.TryGet(key + ".tooltip", out string tooltip) ||
                            BasisLocalization.TryGet(key + ".description", out tooltip), Is.True,
                    $"{key} has no tooltip or description, so hovering it explains nothing.");
                Assert.That(tooltip, Is.Not.Empty);
            }
        }

        [Test]
        public void TheFocusModeDropdownHasALabelForEachMode()
        {
            // Index 0 means follow the subject and index 1 means manual; the handler is written
            // against exactly that.
            Assert.That(BasisHandHeldCameraPanelProvider.FocusModeLabelsForTest.Length, Is.EqualTo(2));
        }

        [Test]
        public void TheStreamResolutionTableIsPairedUp()
        {
            int[] widths = BasisHandHeldCameraPanelProvider.VideoResolutionWidthsForTest;
            int[] heights = BasisHandHeldCameraPanelProvider.VideoResolutionHeightsForTest;

            Assert.That(widths.Length, Is.EqualTo(heights.Length),
                "The dropdown reads both tables at one index; a short one throws on the last entry.");

            for (int Index = 0; Index < widths.Length; Index++)
            {
                Assert.That(widths[Index], Is.GreaterThan(0));
                Assert.That(heights[Index], Is.GreaterThan(0));
                if (Index > 0) Assert.That(widths[Index], Is.GreaterThan(widths[Index - 1]));
            }
        }

        [Test]
        public void TheStreamPortRangeIsOneAUserCanActuallyBindTo()
        {
            Assert.That(BasisHandHeldCameraPanelProvider.WebPortMinForTest, Is.GreaterThanOrEqualTo(1024),
                "Ports below 1024 need privileges the game does not have.");
            Assert.That(BasisHandHeldCameraPanelProvider.WebPortMaxForTest, Is.LessThanOrEqualTo(65535));
            Assert.That(BasisHandHeldCameraPanelProvider.WebPortMaxForTest,
                Is.GreaterThan(BasisHandHeldCameraPanelProvider.WebPortMinForTest));
        }

        [Test]
        public void EveryBackgroundModeResolvesAndOnlyTransparentClearsWithZeroAlpha()
        {
            // The dropdown casts its index to this enum, and every entry has to resolve to
            // something the camera can clear to.
            foreach (BasisCameraBackgroundMode mode in Enum.GetValues(typeof(BasisCameraBackgroundMode)))
            {
                Color custom = new Color(0.2f, 0.4f, 0.6f, 1f);
                Color resolved = BasisCameraBackgrounds.ColorFor(mode, custom);

                if (mode == BasisCameraBackgroundMode.Transparent)
                    Assert.That(resolved.a, Is.Zero, "Transparent must produce a zero-alpha clear for Spout compositing.");
                else
                    Assert.That(resolved.a, Is.GreaterThan(0f), $"{mode} unexpectedly resolves to a transparent clear colour.");
            }

            Assert.That(BasisCameraBackgrounds.ColorFor(BasisCameraBackgroundMode.Custom, Color.red),
                Is.EqualTo(Color.red), "Custom is the only mode that has to follow the colour picker.");
            Assert.That((int)BasisCameraBackgroundMode.World, Is.Zero,
                "World has to be the zero value so an old settings file zero-fills to the world, not a green screen.");
            Assert.That((int)BasisCameraBackgroundMode.Custom, Is.EqualTo(6),
                "Existing persisted background values must not move when Transparent is added.");
            Assert.That((int)BasisCameraBackgroundMode.Transparent, Is.EqualTo(7));
        }

        // ---------- Exposure ----------

        [Test]
        public void TheExposureStopTableClimbsEvenlyThroughZero()
        {
            // The slider is an index into this table, so uneven spacing makes the same drag worth
            // a different amount of light depending on where you started.
            var rig = new BasisCameraSettingsRig();
            try
            {
                float previous = float.NegativeInfinity;
                float firstStep = float.NaN;
                bool crossesZero = false;

                for (int Index = 0; Index < BasisHandHeldCameraUI.ExposureStopCount; Index++)
                {
                    rig.UI.ChangeExposureCompensation(Index);
                    float stop = rig.ColorAdjustments.postExposure.value;

                    Assert.That(stop, Is.GreaterThan(previous), "The stop table has to climb.");
                    if (Index == 1) firstStep = stop - previous;
                    if (Index > 1)
                    {
                        Assert.That(stop - previous, Is.EqualTo(firstStep).Within(1e-3f),
                            "Every step has to be worth the same amount of light.");
                    }
                    if (Mathf.Abs(stop) < 1e-4f) crossesZero = true;
                    previous = stop;
                }

                Assert.That(crossesZero, Is.True, "There has to be a neutral notch to return to.");
            }
            finally
            {
                rig.Dispose();
            }
        }

        [Test]
        public void TheDefaultExposureIsTheNeutralNotch()
        {
            var rig = new BasisCameraSettingsRig();
            try
            {
                var defaults = new BasisHandHeldCameraUI.CameraSettings();
                rig.UI.ChangeExposureCompensation(defaults.exposureIndex);

                Assert.That(rig.ColorAdjustments.postExposure.value, Is.EqualTo(0f).Within(1e-4f),
                    "A fresh camera must not be pre-graded.");
            }
            finally
            {
                rig.Dispose();
            }
        }

        // ---------- helpers ----------

        private static void AssertNormalised<T>(Func<T, UnityEngine.Rendering.ClampedFloatParameter> pick)
            where T : UnityEngine.Rendering.VolumeComponent
        {
            T component = ScriptableObject.CreateInstance<T>();
            try
            {
                UnityEngine.Rendering.ClampedFloatParameter parameter = pick(component);
                Assert.That(parameter.min, Is.EqualTo(0f).Within(1e-4f), $"{typeof(T).Name} does not start at 0.");
                Assert.That(parameter.max, Is.EqualTo(1f).Within(1e-4f), $"{typeof(T).Name} does not end at 1.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(component);
            }
        }

        private static void AssertAscending(string[] labels, Func<string, float> parse, string what)
        {
            for (int Index = 1; Index < labels.Length; Index++)
            {
                Assert.That(parse(labels[Index]), Is.GreaterThan(parse(labels[Index - 1])),
                    $"The {what} presets are not in order at \"{labels[Index]}\".");
            }
        }

        private static void AssertDescending(string[] labels, Func<string, float> parse, string what)
        {
            for (int Index = 1; Index < labels.Length; Index++)
            {
                Assert.That(parse(labels[Index]), Is.LessThan(parse(labels[Index - 1])),
                    $"The {what} presets are not in order at \"{labels[Index]}\".");
            }
        }
    }
}
