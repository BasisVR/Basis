using NUnit.Framework;
using Basis.Cinematics;
using UnityEngine;

namespace Basis.Tests.Camera
{
    /// <summary>
    /// Covers the per-camera options that persist: capture format, resolution defaults and the
    /// settings round-trip. Format is the interesting one — its control was removed from the prop
    /// HUD, so anything still keyed off that Toggle silently stops working rather than failing to
    /// compile. These assert the state is owned by the class, not by a prefab reference.
    /// </summary>
    public class BasisHandHeldCameraOptionsTests
    {
        private BasisHandHeldCameraUI NewUI() => new BasisHandHeldCameraUI();

        [Test]
        public void Format_DefaultsToPng()
        {
            BasisHandHeldCameraUI ui = NewUI();

            Assert.That(ui.FormatIndex, Is.EqualTo(BasisHandHeldCameraUI.FORMAT_PNG));
            Assert.That(ui.useEXR, Is.False);
        }

        [Test]
        public void SetFormat_RoundTripsWithoutAToggle()
        {
            // No prefab Toggle is wired here at all, which is exactly the state the prop HUD is
            // in now that PNG/EXR moved to the menu panel.
            BasisHandHeldCameraUI ui = NewUI();

            ui.SetFormat(BasisHandHeldCameraUI.FORMAT_EXR);
            Assert.That(ui.FormatIndex, Is.EqualTo(BasisHandHeldCameraUI.FORMAT_EXR));
            Assert.That(ui.useEXR, Is.True);
            Assert.That(ui.GetFormatIndex(), Is.EqualTo(BasisHandHeldCameraUI.FORMAT_EXR));

            ui.SetFormat(BasisHandHeldCameraUI.FORMAT_PNG);
            Assert.That(ui.FormatIndex, Is.EqualTo(BasisHandHeldCameraUI.FORMAT_PNG));
            Assert.That(ui.useEXR, Is.False);
            Assert.That(ui.GetFormatIndex(), Is.EqualTo(BasisHandHeldCameraUI.FORMAT_PNG));
        }

        [Test]
        public void SetFormat_ClampsUnknownIndexToPng()
        {
            BasisHandHeldCameraUI ui = NewUI();

            ui.SetFormat(99);

            Assert.That(ui.FormatIndex, Is.EqualTo(BasisHandHeldCameraUI.FORMAT_PNG),
                "An out-of-range index must not leave the format in an undefined state.");
        }

        [Test]
        public void SetFormat_SwapsRepeatedly()
        {
            BasisHandHeldCameraUI ui = NewUI();

            for (int Index = 0; Index < 6; Index++)
            {
                ui.SetFormat(BasisHandHeldCameraUI.FORMAT_EXR);
                Assert.That(ui.useEXR, Is.True);

                ui.SetFormat(BasisHandHeldCameraUI.FORMAT_PNG);
                Assert.That(ui.useEXR, Is.False);
            }
        }

        [Test]
        public void ExposureStops_ExposeAUsableSliderRange()
        {
            // The panel builds its exposure slider from this count; a zero or one-entry array
            // would collapse the slider to a single unusable notch.
            Assert.That(BasisHandHeldCameraUI.ExposureStopCount, Is.GreaterThan(1));
            Assert.That(NewUI().ExposureIndex, Is.InRange(0, BasisHandHeldCameraUI.ExposureStopCount - 1));
        }

        [Test]
        public void CameraSettings_DefaultResolutionIsTenEighty()
        {
            // Maintainer set 1080 as the default capture resolution. Index 1 of MetaData
            // resolutions is 1920x1080; index 0 is 720p, which is what this previously was.
            BasisHandHeldCameraUI.CameraSettings settings = new BasisHandHeldCameraUI.CameraSettings();

            Assert.That(settings.resolutionIndex, Is.EqualTo(1));

            BasisHandHeldCameraMetaData metaData = new BasisHandHeldCameraMetaData();
            Assert.That(settings.resolutionIndex, Is.LessThan(metaData.resolutions.Length));
            Assert.That(metaData.resolutions[settings.resolutionIndex].width, Is.EqualTo(1920));
            Assert.That(metaData.resolutions[settings.resolutionIndex].height, Is.EqualTo(1080));
        }

        [Test]
        public void CameraSettings_UsesWorldFogByDefault()
        {
            Assert.That(new BasisHandHeldCameraUI.CameraSettings().overrideVolumetricFog, Is.False);
        }

        [Test]
        public void CameraSettings_UsesLiveGlobalIlluminationByDefault()
        {
            var defaults = new BasisHandHeldCameraUI.CameraSettings();

            Assert.That(defaults.overrideGlobalIllumination, Is.False);
            // The value fields still carry the live BasisSettingsDefaults GI defaults even while the
            // override itself is off, so turning it on for the first time does not jar against what
            // the live preview already looks like.
            Assert.That(defaults.giQuality, Is.EqualTo(1), "Medium");
            Assert.That(defaults.giFallback, Is.EqualTo(2), "Reflection Probe");
            Assert.That(defaults.giIntensity, Is.EqualTo(1f).Within(1e-4f));
            Assert.That(defaults.giWideBlur, Is.True);
            Assert.That(defaults.giRayReuse, Is.True);
            Assert.That(defaults.giEmitters, Is.True);
            Assert.That(defaults.giMirrors, Is.True);
            Assert.That(defaults.giReflectionProbes, Is.False);
            Assert.That(defaults.giSpecular, Is.False);
        }

        [Test]
        public void CameraSettings_UsesLiveRTAOByDefault()
        {
            var defaults = new BasisHandHeldCameraUI.CameraSettings();

            Assert.That(defaults.overrideRTAO, Is.False);
            Assert.That(defaults.rtaoMode, Is.EqualTo(0), "Screen Space");
            Assert.That(defaults.rtaoIntensity, Is.EqualTo(1f).Within(1e-4f));
            Assert.That(defaults.rtaoDenoisePasses, Is.EqualTo(2), "High");
            Assert.That(defaults.rtaoSkinnedMeshes, Is.EqualTo(1), "Proxy");
        }

        [Test]
        public void AFileFromBeforeRTAOOverridesExisted_LoadsTheLiveDefaultsNotZeroFill()
        {
            var loaded = JsonUtility.FromJson<BasisHandHeldCameraUI.CameraSettings>(
                "{\"settingsVersion\":12,\"fov\":50}");
            var defaults = new BasisHandHeldCameraUI.CameraSettings();

            Assert.That(loaded.overrideRTAO, Is.False);
            Assert.That(loaded.rtaoIntensity, Is.EqualTo(defaults.rtaoIntensity).Within(1e-4f));
            Assert.That(loaded.rtaoDenoisePasses, Is.EqualTo(defaults.rtaoDenoisePasses));
            Assert.That(loaded.rtaoSkinnedMeshes, Is.EqualTo(defaults.rtaoSkinnedMeshes));
        }

        [Test]
        public void AFileFromBeforeGlobalIlluminationOverridesExisted_LoadsTheLiveDefaultsNotZeroFill()
        {
            // JsonUtility does not zero-fill a field absent from the JSON when the class has a
            // constructor — it constructs the object first and only writes over what the file
            // actually states (see MissingFieldsLoadTheirConstructorDefault). So a pre-existing save
            // that has never heard of these 24 fields has to come back holding the constructor's
            // live-matching defaults, not 0/false/"None" — a zero-filled Fallback would silently
            // read as "None" instead of "Reflection Probe", and zero-filled WideBlur/RayReuse/
            // Emitters/Intensity would silently read as off/off/off/black.
            var loaded = JsonUtility.FromJson<BasisHandHeldCameraUI.CameraSettings>(
                "{\"settingsVersion\":12,\"fov\":50}");
            var defaults = new BasisHandHeldCameraUI.CameraSettings();

            Assert.That(loaded.overrideGlobalIllumination, Is.False);
            Assert.That(loaded.giQuality, Is.EqualTo(defaults.giQuality));
            Assert.That(loaded.giFallback, Is.EqualTo(defaults.giFallback));
            Assert.That(loaded.giIntensity, Is.EqualTo(defaults.giIntensity).Within(1e-4f));
            Assert.That(loaded.giWideBlur, Is.EqualTo(defaults.giWideBlur));
            Assert.That(loaded.giRayReuse, Is.EqualTo(defaults.giRayReuse));
            Assert.That(loaded.giEmitters, Is.EqualTo(defaults.giEmitters));
            Assert.That(loaded.giMirrors, Is.EqualTo(defaults.giMirrors));
        }

        [Test]
        public void CameraSettings_SurviveAJsonRoundTrip()
        {
            // Settings are persisted with JsonUtility, which zero-fills anything it cannot map —
            // a renamed field would come back as 0 rather than erroring.
            BasisHandHeldCameraUI.CameraSettings original = new BasisHandHeldCameraUI.CameraSettings
            {
                resolutionIndex = 2,
                formatIndex = BasisHandHeldCameraUI.FORMAT_EXR,
                msaaSamples = 8,
                fov = 55f,
                bloomIntensity = 1.25f,
                bloomThreshold = 0.75f,
                contrast = -20f,
                saturation = 30f,
                depthAperture = 4f,
                depthFocusDistance = 3.5f,
                exposureIndex = 9,
                depthIsActive = true,
                useManualFocus = false,
                overrideVolumetricFog = true,
                VolumetricFogVolumedensity = 0.4f,
                overrideGlobalIllumination = true,
                giMode = 1,
                giFallback = 0,
                giIntensity = 2.4f,
                giWideBlur = false,
                giMirrors = false,
                overrideRTAO = true,
                rtaoMode = 1,
                rtaoIntensity = 0.72f,
                rtaoDenoisePasses = 3,
                hueShift = 45f,
                vignette = 0.3f,
                chromaticAberration = 0.2f,
                filmGrain = 0.15f,
                whiteBalanceTemperature = 20f,
                whiteBalanceTint = -10f,
                lensDistortion = 0.25f,
                autoFocusFollowSubject = true,
                modifiers = StackWithFollowOffset(new Vector3(1f, 0.5f, 2f)),
                capture360 = true,
                useAutoLeveling = true,
                useVRHandheldSmoothing = true,
                printPhoto = true,
            };

            string json = JsonUtility.ToJson(original);
            BasisHandHeldCameraUI.CameraSettings restored =
                JsonUtility.FromJson<BasisHandHeldCameraUI.CameraSettings>(json);

            Assert.That(restored.resolutionIndex, Is.EqualTo(original.resolutionIndex));
            Assert.That(restored.formatIndex, Is.EqualTo(original.formatIndex));
            Assert.That(restored.msaaSamples, Is.EqualTo(original.msaaSamples));
            Assert.That(restored.fov, Is.EqualTo(original.fov).Within(1e-4f));
            Assert.That(restored.bloomIntensity, Is.EqualTo(original.bloomIntensity).Within(1e-4f));
            Assert.That(restored.bloomThreshold, Is.EqualTo(original.bloomThreshold).Within(1e-4f));
            Assert.That(restored.contrast, Is.EqualTo(original.contrast).Within(1e-4f));
            Assert.That(restored.saturation, Is.EqualTo(original.saturation).Within(1e-4f));
            Assert.That(restored.depthAperture, Is.EqualTo(original.depthAperture).Within(1e-4f));
            Assert.That(restored.depthFocusDistance, Is.EqualTo(original.depthFocusDistance).Within(1e-4f));
            Assert.That(restored.exposureIndex, Is.EqualTo(original.exposureIndex));
            Assert.That(restored.depthIsActive, Is.True);
            Assert.That(restored.useManualFocus, Is.False);
            Assert.That(restored.overrideVolumetricFog, Is.True);
            Assert.That(restored.VolumetricFogVolumedensity, Is.EqualTo(original.VolumetricFogVolumedensity).Within(1e-4f));
            Assert.That(restored.overrideGlobalIllumination, Is.True);
            Assert.That(restored.giMode, Is.EqualTo(original.giMode));
            Assert.That(restored.giFallback, Is.EqualTo(original.giFallback));
            Assert.That(restored.giIntensity, Is.EqualTo(original.giIntensity).Within(1e-4f));
            Assert.That(restored.giWideBlur, Is.False);
            Assert.That(restored.giMirrors, Is.False);
            Assert.That(restored.overrideRTAO, Is.True);
            Assert.That(restored.rtaoMode, Is.EqualTo(original.rtaoMode));
            Assert.That(restored.rtaoIntensity, Is.EqualTo(original.rtaoIntensity).Within(1e-4f));
            Assert.That(restored.rtaoDenoisePasses, Is.EqualTo(original.rtaoDenoisePasses));
            Assert.That(restored.hueShift, Is.EqualTo(original.hueShift).Within(1e-4f));
            Assert.That(restored.vignette, Is.EqualTo(original.vignette).Within(1e-4f));
            Assert.That(restored.chromaticAberration, Is.EqualTo(original.chromaticAberration).Within(1e-4f));
            Assert.That(restored.filmGrain, Is.EqualTo(original.filmGrain).Within(1e-4f));
            Assert.That(restored.whiteBalanceTemperature, Is.EqualTo(original.whiteBalanceTemperature).Within(1e-4f));
            Assert.That(restored.whiteBalanceTint, Is.EqualTo(original.whiteBalanceTint).Within(1e-4f));
            Assert.That(restored.lensDistortion, Is.EqualTo(original.lensDistortion).Within(1e-4f));
            Assert.That(restored.autoFocusFollowSubject, Is.True);
            Assert.That(restored.modifiers.follow.positionOffset, Is.EqualTo(original.modifiers.follow.positionOffset));
            Assert.That(restored.modifiers.subject.anchorToBody, Is.False);
            Assert.That(restored.modifiers.subject.modifier, Is.EqualTo(original.modifiers.subject.modifier));
            Assert.That(restored.modifiers.subject.framingRadius,
                Is.EqualTo(original.modifiers.subject.framingRadius).Within(1e-4f));
            Assert.That(restored.modifiers.positionModifier, Is.EqualTo(original.modifiers.positionModifier));
            Assert.That(restored.modifiers.subject.aimHeightOffset, Is.EqualTo(original.modifiers.subject.aimHeightOffset).Within(1e-4f));
            Assert.That(restored.capture360, Is.True);
            Assert.That(restored.useAutoLeveling, Is.True);
            Assert.That(restored.useVRHandheldSmoothing, Is.True);
            Assert.That(restored.printPhoto, Is.True);
        }

        [Test]
        public void UpgradingAPreV2File_RestoresFollowDefaultsRatherThanZeroFill()
        {
            // A save written before the auto-follow fields existed lacks them entirely. Simulate
            // that old file: a v1 JSON with none of the new fields.
            string legacyJson =
                "{\"settingsVersion\":1,\"resolutionIndex\":1,\"formatIndex\":0,\"fov\":50}";

            var loaded = JsonUtility.FromJson<BasisHandHeldCameraUI.CameraSettings>(legacyJson);

            // This originally asserted the opposite, on the belief that JsonUtility zero-fills a
            // field the JSON does not carry. It does not, for a class with a constructor: the
            // object is constructed first and only the fields actually present are written over,
            // so an absent field arrives holding its constructor default. That makes the migration
            // below belt-and-braces for these particular fields — and it is the migrations that
            // correct a value the old file states outright, like the unusable f/1 aperture, that
            // are doing real work. See MissingFieldsLoadTheirConstructorDefault.
            Assert.That(loaded.modifiers.subject.anchorToBody, Is.True, "sanity: the constructor default survives");
            Assert.That(loaded.modifiers.follow.positionOffset, Is.Not.EqualTo(Vector3.zero),
                "sanity: the constructor default survives");

            BasisHandHeldCameraUI.MigrateSettingsForTest(loaded);

            var defaults = new BasisHandHeldCameraUI.CameraSettings();
            Assert.That(loaded.modifiers.subject.anchorToBody, Is.True);
            Assert.That(loaded.modifiers.follow.positionOffset, Is.EqualTo(defaults.modifiers.follow.positionOffset));
            Assert.That(loaded.msaaSamples, Is.EqualTo(defaults.msaaSamples));
            Assert.That(loaded.settingsVersion, Is.EqualTo(BasisHandHeldCameraUI.CameraSettings.CurrentVersion));
        }

        [Test]
        public void UpgradingAPreV4File_RecoversDepthOfFieldStateFromMode()
        {
            // depthIsActive was never written before v4, so every old file has it false while the
            // real on/off lived in dofMode. Migration must recover it, or DoF silently switches off
            // for anyone who had it on.
            string legacyOn =
                "{\"settingsVersion\":3,\"dofMode\":2,\"depthIsActive\":false}";
            var loadedOn = JsonUtility.FromJson<BasisHandHeldCameraUI.CameraSettings>(legacyOn);
            Assert.That(loadedOn.depthIsActive, Is.False, "sanity: the old file really does say false");

            BasisHandHeldCameraUI.MigrateSettingsForTest(loadedOn);
            Assert.That(loadedOn.depthIsActive, Is.True, "Bokeh in the old file meant DoF was on.");

            // ...and a file whose mode was Off must stay off.
            string legacyOff =
                "{\"settingsVersion\":3,\"dofMode\":0,\"depthIsActive\":false}";
            var loadedOff = JsonUtility.FromJson<BasisHandHeldCameraUI.CameraSettings>(legacyOff);
            BasisHandHeldCameraUI.MigrateSettingsForTest(loadedOff);
            Assert.That(loadedOff.depthIsActive, Is.False, "Off in the old file meant DoF was off.");
        }

        [Test]
        public void UpgradingAPreV5File_ReplacesTheUnusableOptics()
        {
            // f/1 on a 125mm lens is a depth of field about 3cm deep at portrait range, so nobody
            // could ever be usefully in focus. Old files have to be taken off it, not just new ones.
            string legacy =
                "{\"settingsVersion\":4,\"depthAperture\":1,\"dofFocalLength\":125}";
            var loaded = JsonUtility.FromJson<BasisHandHeldCameraUI.CameraSettings>(legacy);

            BasisHandHeldCameraUI.MigrateSettingsForTest(loaded);

            var defaults = new BasisHandHeldCameraUI.CameraSettings();
            Assert.That(loaded.depthAperture, Is.EqualTo(defaults.depthAperture).Within(1e-4f));
            Assert.That(loaded.dofFocalLength, Is.EqualTo(defaults.dofFocalLength).Within(1e-4f));
            Assert.That(loaded.settingsVersion, Is.EqualTo(BasisHandHeldCameraUI.CameraSettings.CurrentVersion));
        }

        [Test]
        public void ShippedApertureDefault_IsInsideTheRangeURPWillAccept()
        {
            // URP clamps DepthOfField.aperture to [1, 32]; a default outside it is silently ignored
            // and the slider below it is dead travel.
            var defaults = new BasisHandHeldCameraUI.CameraSettings();

            Assert.That(defaults.depthAperture, Is.InRange(BasisHandHeldCameraUI.MinAperture, BasisHandHeldCameraUI.MaxAperture));
        }

        [Test]
        public void DepthOfFieldStyleAndEnabledState_AreIndependent()
        {
            // dofMode is the blur style, depthIsActive is the on/off. A default camera keeps a
            // usable style while staying disabled — if applying the style could also enable DoF,
            // a fresh camera would come up blurred.
            var defaults = new BasisHandHeldCameraUI.CameraSettings();

            Assert.That(defaults.depthIsActive, Is.False, "Depth of field is off until asked for.");
            Assert.That(defaults.dofMode, Is.Not.EqualTo(0),
                "The stored style stays a real mode so enabling DoF does not land on Off.");
        }

        [Test]
        public void CurrentVersionFile_IsLeftUnchangedByMigration()
        {
            var current = new BasisHandHeldCameraUI.CameraSettings
            {
                modifiers = StackWithFollowOffset(new Vector3(5f, 5f, 5f)),
            };
            current.settingsVersion = BasisHandHeldCameraUI.CameraSettings.CurrentVersion;

            BasisHandHeldCameraUI.MigrateSettingsForTest(current);

            Assert.That(current.modifiers.subject.anchorToBody, Is.False, "A current-version file must not be re-defaulted.");
            Assert.That(current.modifiers.follow.positionOffset, Is.EqualTo(new Vector3(5f, 5f, 5f)));
        }

        [Test]
        public void ExtraPostProcessing_DefaultsToNeutral()
        {
            // Every new effect must default to 0 so a fresh camera looks exactly as it did before
            // these options existed — nothing is applied until the user dials it in.
            BasisHandHeldCameraUI.CameraSettings settings = new BasisHandHeldCameraUI.CameraSettings();

            Assert.That(settings.vignette, Is.Zero);
            Assert.That(settings.chromaticAberration, Is.Zero);
            Assert.That(settings.filmGrain, Is.Zero);
            Assert.That(settings.whiteBalanceTemperature, Is.Zero);
            Assert.That(settings.whiteBalanceTint, Is.Zero);
            Assert.That(settings.lensDistortion, Is.Zero);
            Assert.That(settings.hueShift, Is.Zero);
            Assert.That(settings.overrideVolumetricFog, Is.False);
            Assert.That(settings.autoFocusFollowSubject, Is.False);
        }

        [Test]
        public void CameraSettings_DefaultMsaaMatchesTheRtDefault()
        {
            // The panel offers 1/2/4/8; the persisted default must be one of them, and match the
            // RT's hardcoded historical default of 2 so existing behaviour is unchanged.
            BasisHandHeldCameraUI.CameraSettings settings = new BasisHandHeldCameraUI.CameraSettings();

            Assert.That(settings.msaaSamples, Is.EqualTo(2));
            Assert.That(new[] { 1, 2, 4, 8 }, Contains.Item(settings.msaaSamples));
        }

        [Test]
        public void MetaData_PresetsAreNonEmpty()
        {
            // The panel builds dropdowns straight from these; an empty array is an empty dropdown.
            BasisHandHeldCameraMetaData metaData = new BasisHandHeldCameraMetaData();

            Assert.That(metaData.resolutions, Is.Not.Empty);
            Assert.That(metaData.formats, Is.Not.Empty);
            Assert.That(metaData.apertures, Is.Not.Empty);
            Assert.That(metaData.shutterSpeeds, Is.Not.Empty);
            Assert.That(metaData.isoValues, Is.Not.Empty);
        }

        private static BasisCameraModifierStack StackWithFollowOffset(Vector3 offset)
        {
            BasisCameraModifierStack stack = new BasisCameraModifierStack();
            stack.follow.positionOffset = offset;
            stack.subject.anchorToBody = false;
            stack.subject.aimHeightOffset = -0.3f;
            stack.subject.framingRadius = 0.7f;
            return stack;
        }
    }
}
