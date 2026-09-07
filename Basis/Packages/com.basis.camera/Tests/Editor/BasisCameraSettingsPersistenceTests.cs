using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Basis.Cinematics;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Camera
{
    /// <summary>
    /// Settings are written by one method and read back by another, and nothing makes the two agree.
    /// <c>ApplySettings</c> pushes a saved file into the live camera; <c>CreateCurrentCameraSettings</c>
    /// harvests the live camera back into a file. A field handled by only one of them is the quietest
    /// bug in the system — the control works perfectly all session and the value is gone next launch.
    ///
    /// <para>
    /// So the central test here is not a list of fields but a property: apply then capture is the
    /// identity. It walks <c>CameraSettings</c> by reflection, so a field added later is covered the
    /// day it is added rather than the day somebody remembers to extend a test.
    /// </para>
    /// </summary>
    public class BasisCameraSettingsPersistenceTests
    {
        private BasisCameraSettingsRig _rig;

        [SetUp]
        public void SetUp() => _rig = new BasisCameraSettingsRig();

        [TearDown]
        public void TearDown() => _rig?.Dispose();

        /// <summary>
        /// Fields the round trip deliberately does not hold, each with the reason. Anything not
        /// listed here has to survive, so the list is the documentation of what does not persist.
        /// </summary>
        private static readonly Dictionary<string, string> RoundTripExclusions = new Dictionary<string, string>
        {
            // Stamped to the current version by the migration on every load, by design.
            { "settingsVersion", "always re-stamped to CurrentVersion" },

            // Loading an empty shot list seeds the three default shots, so an empty rig cannot come
            // back empty. Covered on its own by CinematicShots_SurviveTheRoundTrip.
            { "cinematicShots", "an empty list is seeded with default shots on load" },

            // A label derived from the settings around it rather than a setting in its own right:
            // a file claiming a mode its values do not match is re-derived to Custom on load, which
            // is the whole point. Covered on its own by CameraMode_SurvivesTheRoundTrip.
            { "cameraMode", "re-derived from the settings it describes, not stored independently" },
        };

        [Test]
        public void EverySettingSurvivesApplyThenCapture()
        {
            BasisHandHeldCameraUI.CameraSettings original = BasisCameraSettingsRig.DistinctiveSettings();

            _rig.UI.ApplySettingsForTest(original);
            BasisHandHeldCameraUI.CameraSettings captured = _rig.UI.CreateCurrentCameraSettingsForTest();

            StringBuilder lost = new StringBuilder();
            foreach (FieldInfo field in SettingsFields())
            {
                if (RoundTripExclusions.ContainsKey(field.Name)) continue;

                object before = field.GetValue(original);
                object after = field.GetValue(captured);
                if (ValuesMatch(before, after)) continue;

                lost.AppendLine($"  {field.Name}: set {Describe(before)}, came back {Describe(after)}");
            }

            Assert.That(lost.Length, Is.Zero,
                "These settings are applied to the camera but never harvested back out of it, so they " +
                "are lost the moment the camera is rebuilt — a world change is enough:\n" + lost);
        }

        [Test]
        public void ApplyingACapturedFileTwiceIsStable()
        {
            // Loading is not a one-shot: the camera reloads its settings on respawn, and any value
            // that drifts on each pass walks away over a session rather than failing outright.
            _rig.UI.ApplySettingsForTest(BasisCameraSettingsRig.DistinctiveSettings());
            BasisHandHeldCameraUI.CameraSettings first = _rig.UI.CreateCurrentCameraSettingsForTest();

            _rig.UI.ApplySettingsForTest(first);
            BasisHandHeldCameraUI.CameraSettings second = _rig.UI.CreateCurrentCameraSettingsForTest();

            StringBuilder drifted = new StringBuilder();
            foreach (FieldInfo field in SettingsFields())
            {
                if (field.Name == "cinematicShots") continue;

                object before = field.GetValue(first);
                object after = field.GetValue(second);
                if (ValuesMatch(before, after)) continue;

                drifted.AppendLine($"  {field.Name}: {Describe(before)} -> {Describe(after)}");
            }

            Assert.That(drifted.Length, Is.Zero, "Reloading a settings file changed it:\n" + drifted);
        }

        [Test]
        public void EverySettingSurvivesTheJsonItIsStoredAs()
        {
            // JsonUtility zero-fills anything it cannot map rather than erroring, so a renamed or
            // unserializable field comes back as 0/false with no complaint anywhere.
            BasisHandHeldCameraUI.CameraSettings original = BasisCameraSettingsRig.DistinctiveSettings();

            string json = JsonUtility.ToJson(original);
            var restored = JsonUtility.FromJson<BasisHandHeldCameraUI.CameraSettings>(json);

            StringBuilder lost = new StringBuilder();
            foreach (FieldInfo field in SettingsFields())
            {
                if (field.Name == "cinematicShots") continue;

                object before = field.GetValue(original);
                object after = field.GetValue(restored);
                if (ValuesMatch(before, after)) continue;

                lost.AppendLine($"  {field.Name}: wrote {Describe(before)}, read back {Describe(after)}");
            }

            Assert.That(lost.Length, Is.Zero,
                "These fields do not survive being written to disk and read back:\n" + lost);
        }

        [Test]
        public void ThePanelSettingsWithNoPlaceToBeSavedAreTheOnesListedHere()
        {
            // These are live controls on the panel that CameraSettings has no field for, so they
            // return to their defaults on the next camera. That is a gap rather than a bug — the
            // wiring was never there to break — but it is worth being an explicit, visible list
            // rather than something a user rediscovers each time they respawn the camera.
            //
            // Adding a field for any of them means a version bump plus a migration entry, because
            // JsonUtility zero-fills what an older file lacks. Delete the line here when that
            // happens; the round-trip test above then covers it automatically.
            // Lateral Tracking and Detached Marker used to head this list. They were the only two
            // controls in a Follow section that otherwise persists in full, so they are fields now
            // (v8) and the round-trip test above covers them.
            string[] fields =
            {
                "CloseHidesCamera",          // Output > Close Hides Instead, back to off.
            };

            System.Reflection.FieldInfo[] settingsFields =
                typeof(BasisHandHeldCameraUI.CameraSettings).GetFields(BindingFlags.Public | BindingFlags.Instance);

            foreach (string field in fields)
            {
                bool persisted = System.Array.Exists(settingsFields, f =>
                    string.Equals(f.Name, field, System.StringComparison.OrdinalIgnoreCase));

                Assert.That(persisted, Is.False,
                    $"{field} is persisted now — take it off this list so the round-trip test covers it.");
            }

            // The capture camera's per-layer visibility is the fourth: the Render Layers section
            // edits the culling mask directly and nothing carries it across a respawn.
            Assert.That(settingsFields, Has.None.Matches<FieldInfo>(f => f.Name.ToLowerInvariant().Contains("cullingmask")),
                "The render-layer choices are persisted now — extend the round trip to cover them.");
        }

        [Test]
        public void FocusDistanceField_IsLegacyAndCarriesNothing()
        {
            // Pinning the dead field so nobody wires a control to it expecting it to work. If this
            // ever starts failing, focusDistance has been given a meaning and belongs in the round
            // trip above rather than on the exclusion list.
            BasisHandHeldCameraUI.CameraSettings original = BasisCameraSettingsRig.DistinctiveSettings();
            original.focusDistance = 37f;

            _rig.UI.ApplySettingsForTest(original);

            Assert.That(_rig.DepthOfField.focusDistance.value, Is.Not.EqualTo(37f).Within(1e-3f),
                "focusDistance is not the depth-of-field focus; depthFocusDistance is.");
            Assert.That(_rig.CaptureCamera.focalLength, Is.Not.EqualTo(37f).Within(1e-3f),
                "It is certainly not a lens focal length — writing it there drove the FOV to ~100 degrees.");
        }

        [Test]
        public void TheModifierStack_SurvivesTheRoundTrip()
        {
            BasisHandHeldCameraUI.CameraSettings original = BasisCameraSettingsRig.DistinctiveSettings();

            _rig.UI.ApplySettingsForTest(original);
            BasisHandHeldCameraUI.CameraSettings captured = _rig.UI.CreateCurrentCameraSettingsForTest();

            Assert.That(captured.modifiers, Is.Not.Null);
            Assert.That(BasisCameraModifierStack.Matches(original.modifiers, captured.modifiers), Is.True,
                "A configured stack must come back exactly as it went in.");
            Assert.That(captured.modifiers, Is.Not.SameAs(_rig.Camera.Modifiers),
                "The harvest must take a copy, or the file and the live camera share one object.");
        }

        [Test]
        public void CameraMode_SurvivesTheRoundTrip()
        {
            // A file saved in a mode has to come back in it. The mode is re-derived on load rather
            // than trusted, so this only holds when the file's values genuinely still match the
            // mode it names — which is exactly what a file saved from that mode contains.
            _rig.Camera.ApplyCameraMode(BasisCameraMode.FollowMe);
            BasisHandHeldCameraUI.CameraSettings inMode = _rig.UI.CreateCurrentCameraSettingsForTest();

            Assert.That(inMode.cameraMode, Is.EqualTo((int)BasisCameraMode.FollowMe),
                "The mode was not captured into the file.");

            _rig.UI.ApplySettingsForTest(inMode);
            BasisHandHeldCameraUI.CameraSettings captured = _rig.UI.CreateCurrentCameraSettingsForTest();

            Assert.That(captured.cameraMode, Is.EqualTo((int)BasisCameraMode.FollowMe),
                "Loading a file saved in Follow Me must come back in Follow Me.");
            Assert.That(_rig.Camera.Modifiers.DrivesPosition, Is.True,
                "Follow is not persisted, so the restore has to re-arm it — otherwise the camera " +
                "comes back labelled Follow Me while sitting inert in the player's hand.");
        }

        [Test]
        public void ALoadedFileWhoseValuesNoLongerMatchItsMode_ComesBackAsCustom()
        {
            // Hand-edited files exist, and so do settings written by an older build. The label has
            // to follow the values rather than the other way round.
            BasisHandHeldCameraUI.CameraSettings original = BasisCameraSettingsRig.DistinctiveSettings();
            original.cameraMode = (int)BasisCameraMode.FollowMe;

            _rig.UI.ApplySettingsForTest(original);
            BasisHandHeldCameraUI.CameraSettings captured = _rig.UI.CreateCurrentCameraSettingsForTest();

            Assert.That(captured.cameraMode, Is.EqualTo((int)BasisCameraMode.Custom));
        }

        [Test]
        public void ANullStackInAFile_LoadsAsTheShippedDefaults()
        {
            BasisHandHeldCameraUI.CameraSettings original = BasisCameraSettingsRig.DistinctiveSettings();
            original.modifiers = null;

            _rig.UI.ApplySettingsForTest(original);

            Assert.That(_rig.Camera.Modifiers, Is.Not.Null);
            Assert.That(_rig.Camera.Modifiers.DrivesAnything, Is.False,
                "A file with no stack must leave the camera in the operator's hands, not part-configured.");
        }

        [Test]
        public void VolumetricFogDensity_FallsBackToSomethingUsableWithNoFogComponent()
        {
            // The density is harvested from the live fog component, which is platform-gated and
            // absent here. What matters is that its absence saves a sane value rather than a
            // zero-filled one — a saved 0 would open every later session with the fog switched off.
            _rig.UI.ApplySettingsForTest(BasisCameraSettingsRig.DistinctiveSettings());
            BasisHandHeldCameraUI.CameraSettings captured = _rig.UI.CreateCurrentCameraSettingsForTest();

            Assert.That(captured.VolumetricFogVolumedensity, Is.GreaterThan(0f));
            Assert.That(captured.VolumetricFogVolumedensity, Is.LessThanOrEqualTo(1f));
        }

        // ---------- Defaults a fresh install lands on ----------

        [Test]
        public void AFreshCameraAppliesItsDefaultsWithoutError()
        {
            Assert.DoesNotThrow(() => _rig.UI.ApplySettingsForTest(new BasisHandHeldCameraUI.CameraSettings()));

            Assert.That(_rig.DepthOfField.active, Is.False, "Depth of field is off until asked for.");
            Assert.That(_rig.Vignette.active, Is.False);
            Assert.That(_rig.ChromaticAberration.active, Is.False);
            Assert.That(_rig.FilmGrain.active, Is.False);
            Assert.That(_rig.WhiteBalance.active, Is.False);
            Assert.That(_rig.LensDistortion.active, Is.False);
            Assert.That(_rig.MotionBlur.active, Is.False);
            Assert.That(_rig.Camera.backgroundMode, Is.EqualTo(BasisCameraBackgroundMode.World),
                "A fresh camera photographs the world, not a green screen.");
        }

        [Test]
        public void ResetSettings_ReturnsEveryEffectToNeutral()
        {
            _rig.UI.ChangeVignette(0.9f);
            _rig.UI.ChangeFilmGrain(0.9f);
            _rig.UI.ChangeLensDistortion(0.9f);
            _rig.UI.ChangeChromaticAberration(0.9f);
            _rig.UI.ChangeWhiteBalanceTemperature(80f);
            _rig.UI.ChangeMotionBlur(0.9f);
            _rig.Camera.SetBackgroundMode(BasisCameraBackgroundMode.Magenta);

            _rig.UI.ResetSettings();

            Assert.That(_rig.Vignette.active, Is.False);
            Assert.That(_rig.FilmGrain.active, Is.False);
            Assert.That(_rig.LensDistortion.active, Is.False);
            Assert.That(_rig.ChromaticAberration.active, Is.False);
            Assert.That(_rig.WhiteBalance.active, Is.False);
            Assert.That(_rig.MotionBlur.active, Is.False);
            Assert.That(_rig.Camera.backgroundMode, Is.EqualTo(BasisCameraBackgroundMode.World),
                "Reset has to reach the whole camera, not only the sliders on the page it was pressed from.");
        }

        // ---------- Migration ----------

        [Test]
        public void MissingFieldsLoadTheirConstructorDefault()
        {
            // Load-bearing, and the opposite of what several comments in the settings code claim.
            // JsonUtility does NOT zero-fill a field the JSON omits when the class has a
            // constructor: it constructs the object, then writes over only the fields present.
            //
            // What follows from that: adding a setting whose sane default is non-zero needs it set
            // in the constructor, and then no migration — old files pick the default up for free.
            // A migration is for the other case, where the old file states a value outright and
            // that value has since become wrong. The unusable f/1 aperture is the real example.
            var defaults = new BasisHandHeldCameraUI.CameraSettings();
            var sparse = JsonUtility.FromJson<BasisHandHeldCameraUI.CameraSettings>("{\"fov\":50}");

            Assert.That(sparse.fov, Is.EqualTo(50f).Within(1e-4f), "what the file does say wins");
            Assert.That(sparse.modifiers.subject.anchorToBody, Is.EqualTo(defaults.modifiers.subject.anchorToBody));
            Assert.That(sparse.modifiers.follow.positionOffset, Is.EqualTo(defaults.modifiers.follow.positionOffset));
            Assert.That(sparse.depthAperture, Is.EqualTo(defaults.depthAperture).Within(1e-4f));
            Assert.That(sparse.modifiers.subject.framingRadius, Is.EqualTo(defaults.modifiers.subject.framingRadius).Within(1e-4f));
            Assert.That(sparse.backgroundCustomColor, Is.EqualTo(defaults.backgroundCustomColor));

            // A value the file does state still overrides the constructor, including a falsy one —
            // otherwise "off" could never be saved.
            var explicitlyOff = JsonUtility.FromJson<BasisHandHeldCameraUI.CameraSettings>(
                "{\"modifiers\":{\"subject\":{\"anchorToBody\":false}}}");
            Assert.That(explicitlyOff.modifiers.subject.anchorToBody, Is.False);
        }

        [Test]
        public void MigrationRunsOnceAndStampsTheCurrentVersion()
        {
            var legacy = JsonUtility.FromJson<BasisHandHeldCameraUI.CameraSettings>("{\"settingsVersion\":1}");

            BasisHandHeldCameraUI.MigrateSettingsForTest(legacy);

            Assert.That(legacy.settingsVersion, Is.EqualTo(BasisHandHeldCameraUI.CameraSettings.CurrentVersion));
        }

        [Test]
        public void TheOldestPossibleFileComesForwardEntirelyUsable()
        {
            // The worst case a migration has to survive: a file that claims the first version and
            // carries nothing else, so every field arrives zero-filled and every migration step
            // runs. Each assertion below is a zero-fill whose meaning is not merely wrong but
            // harmful — which is the whole reason the version gate exists.
            var defaults = new BasisHandHeldCameraUI.CameraSettings();
            var loaded = JsonUtility.FromJson<BasisHandHeldCameraUI.CameraSettings>("{\"settingsVersion\":1}");

            BasisHandHeldCameraUI.MigrateSettingsForTest(loaded);

            Assert.That(loaded.settingsVersion, Is.EqualTo(BasisHandHeldCameraUI.CameraSettings.CurrentVersion));
            Assert.That(loaded.modifiers.follow.positionOffset, Is.Not.EqualTo(Vector3.zero),
                "A zero follow offset parks the camera inside the player.");
            Assert.That(loaded.msaaSamples, Is.EqualTo(defaults.msaaSamples));
            Assert.That(loaded.depthAperture,
                Is.InRange(BasisHandHeldCameraUI.MinAperture, BasisHandHeldCameraUI.MaxAperture),
                "An aperture outside URP's range is dead slider travel.");
            Assert.That(loaded.dofFocalLength, Is.GreaterThan(0f), "A zero lens divides through zero.");
            Assert.That(loaded.modifiers.subject.framingRadius, Is.GreaterThan(0f),
                "A zero framing radius dollies the camera into the subject's face.");
            Assert.That(loaded.backgroundCustomColor.a, Is.GreaterThan(0f),
                "A transparent custom background is not a background.");
        }

        [Test]
        public void MigratingIsIdempotentFromEveryShippedVersion()
        {
            // Settings are loaded on every spawn, and a migration that keeps changing the file is
            // one that walks a value further each session rather than failing once.
            for (int version = 1; version <= BasisHandHeldCameraUI.CameraSettings.CurrentVersion; version++)
            {
                var loaded = JsonUtility.FromJson<BasisHandHeldCameraUI.CameraSettings>(
                    "{\"settingsVersion\":" + version + "}");

                BasisHandHeldCameraUI.MigrateSettingsForTest(loaded);
                string once = JsonUtility.ToJson(loaded);

                BasisHandHeldCameraUI.MigrateSettingsForTest(loaded);
                string twice = JsonUtility.ToJson(loaded);

                Assert.That(twice, Is.EqualTo(once), $"Migrating a v{version} file twice changed it again.");
                Assert.That(loaded.settingsVersion, Is.EqualTo(BasisHandHeldCameraUI.CameraSettings.CurrentVersion),
                    $"v{version} did not come forward.");
            }
        }

        [Test]
        public void AMigratedFileThenAppliesCleanly()
        {
            // Migration and apply are separate steps and only ever run together in production, so
            // the pair is what actually has to work.
            var legacy = JsonUtility.FromJson<BasisHandHeldCameraUI.CameraSettings>(
                "{\"settingsVersion\":1,\"resolutionIndex\":1,\"fov\":50}");

            BasisHandHeldCameraUI.MigrateSettingsForTest(legacy);

            Assert.DoesNotThrow(() => _rig.UI.ApplySettingsForTest(legacy));
            Assert.That(_rig.CaptureCamera.fieldOfView, Is.EqualTo(50f).Within(1e-3f));
            Assert.That(_rig.DepthOfField.aperture.value,
                Is.InRange(BasisHandHeldCameraUI.MinAperture, BasisHandHeldCameraUI.MaxAperture));
        }

        // ---------- controls whose state is owned in two places ----------

        [Test]
        public void FocusMode_IsWrittenAsAWholeSoTheReadoutCannotContradictTheCamera()
        {
            // Two pieces of state say "Auto": currentDepthMode, which the prop's sprites and its
            // focus slider visibility read, and autoFocusFollowSubject, which is what actually
            // gates UpdateAutoFocus. The prop's own Auto/Manual buttons used to write only the
            // first, so Auto lit up over a camera that never focused, and Manual showed the focus
            // slider while auto-focus went on overwriting it every frame.
            _rig.UI.SetFocusFollowsSubject(true);
            Assert.That(_rig.Camera.autoFocusFollowSubject, Is.True);
            Assert.That(_rig.UI.currentDepthMode, Is.EqualTo(BasisHandHeldCameraUI.DepthMode.Auto));

            _rig.UI.SetFocusFollowsSubject(false);
            Assert.That(_rig.Camera.autoFocusFollowSubject, Is.False,
                "Manual has to stop auto-focus driving, not just relabel the readout.");
            Assert.That(_rig.UI.currentDepthMode, Is.EqualTo(BasisHandHeldCameraUI.DepthMode.Manual));
        }

        [Test]
        public void FocusMode_SurvivesTheRoundTripAsOneAnswer()
        {
            // useManualFocus and autoFocusFollowSubject are stored separately, so a file can only
            // describe one focus mode if whatever wrote it kept them in step.
            _rig.UI.SetFocusFollowsSubject(true);
            BasisHandHeldCameraUI.CameraSettings captured = _rig.UI.CreateCurrentCameraSettingsForTest();

            Assert.That(captured.autoFocusFollowSubject, Is.True);
            Assert.That(captured.useManualFocus, Is.False, "Both halves must agree in the file too.");

            _rig.UI.ApplySettingsForTest(captured);
            Assert.That(_rig.Camera.autoFocusFollowSubject, Is.True);
            Assert.That(_rig.UI.currentDepthMode, Is.EqualTo(BasisHandHeldCameraUI.DepthMode.Auto));
        }

        [Test]
        public void SetResolutionIndex_MovesThePresetAndTheCameraTogether()
        {
            // ChangeResolution alone only writes the pixel dimensions. The index is separate state
            // and is what a save records, what the prop's sprite strip lights, and where its cycle
            // button counts on from — the panel dropdown went straight to the camera and left all
            // three behind, so the choice vanished on the next load.
            var resolutions = _rig.Camera.MetaData.resolutions;
            Assert.That(resolutions.Length, Is.GreaterThan(2), "sanity: the rig has presets to pick between");

            _rig.UI.SetResolutionIndex(2);

            Assert.That(_rig.Camera.captureWidth, Is.EqualTo(resolutions[2].width));
            Assert.That(_rig.Camera.captureHeight, Is.EqualTo(resolutions[2].height));
            Assert.That(_rig.UI.CreateCurrentCameraSettingsForTest().resolutionIndex, Is.EqualTo(2),
                "The index the save reads has to follow the dimensions the camera was given.");
        }

        [Test]
        public void SetResolutionIndex_IgnoresAnIndexWithNoPreset()
        {
            _rig.UI.SetResolutionIndex(1);
            int width = _rig.Camera.captureWidth;

            _rig.UI.SetResolutionIndex(_rig.Camera.MetaData.resolutions.Length);
            _rig.UI.SetResolutionIndex(-1);

            Assert.That(_rig.Camera.captureWidth, Is.EqualTo(width),
                "An out-of-range preset must leave the camera on the one it had.");
            Assert.That(_rig.UI.CreateCurrentCameraSettingsForTest().resolutionIndex, Is.EqualTo(1));
        }

        [Test]
        public void DetachedMarkerAndLateralTracking_ComeBackAfterAReload()
        {
            // The two Follow controls that had nowhere to be saved until v8.
            _rig.Camera.Modifiers.follow.lateralTracking = 0.25f;
            _rig.Camera.SetDetachedMarker(BasisCameraDetachedMarker.Off);

            BasisHandHeldCameraUI.CameraSettings captured = _rig.UI.CreateCurrentCameraSettingsForTest();
            _rig.UI.ApplySettingsForTest(new BasisHandHeldCameraUI.CameraSettings());
            _rig.UI.ApplySettingsForTest(captured);

            Assert.That(_rig.Camera.Modifiers.follow.lateralTracking, Is.EqualTo(0.25f).Within(1e-3f));
            Assert.That(_rig.Camera.detachedMarker, Is.EqualTo(BasisCameraDetachedMarker.Off));
        }

        [Test]
        public void TheMarkerSize_ComesBackAfterAReload()
        {
            // The resize is done out in the world with both hands on the puck, nowhere near this
            // panel, so a size that did not persist would be undone by every respawn.
            _rig.Camera.SetDetachedMarkerScale(2.5f);

            BasisHandHeldCameraUI.CameraSettings captured = _rig.UI.CreateCurrentCameraSettingsForTest();
            _rig.UI.ApplySettingsForTest(new BasisHandHeldCameraUI.CameraSettings());
            Assert.That(_rig.Camera.DetachedMarkerScale, Is.EqualTo(1f).Within(1e-3f),
                "A fresh file is the natural size, so the reset has to be visible before the reload proves anything.");

            _rig.UI.ApplySettingsForTest(captured);

            Assert.That(captured.detachedMarkerScale, Is.EqualTo(2.5f).Within(1e-3f));
            Assert.That(_rig.Camera.DetachedMarkerScale, Is.EqualTo(2.5f).Within(1e-3f));
        }

        [Test]
        public void AFileFromBeforeTheMarkerCouldBeResized_LoadsAtTheNaturalSize()
        {
            // The field zero-fills to 0 — a marker with no size is no marker — so the size is
            // defaulted in the constructor rather than migrated, and an older file has to pick it up
            // from there. That it does is what this pins.
            var legacy = JsonUtility.FromJson<BasisHandHeldCameraUI.CameraSettings>(
                "{\"settingsVersion\":11,\"detachedMarker\":1}");

            BasisHandHeldCameraUI.MigrateSettingsForTest(legacy);

            Assert.That(legacy.detachedMarkerScale, Is.EqualTo(1f).Within(1e-4f));

            // And a file that does state a nonsense size is read as the natural one rather than
            // clamped to the smallest marker the panel can ask for.
            _rig.UI.ApplySettingsForTest(
                JsonUtility.FromJson<BasisHandHeldCameraUI.CameraSettings>(
                    "{\"settingsVersion\":11,\"detachedMarkerScale\":0}"));

            Assert.That(_rig.Camera.DetachedMarkerScale, Is.EqualTo(1f).Within(1e-4f));
        }

        [Test]
        public void UpgradingAPreV8File_KeepsTheMarkerOn()
        {
            // The marker zero-fills to Off, leaving nothing on screen to show where a detached
            // camera went - and nothing to grab it back by.
            var legacy = JsonUtility.FromJson<BasisHandHeldCameraUI.CameraSettings>(
                "{\"settingsVersion\":7,\"detachedMarker\":0}");

            BasisHandHeldCameraUI.MigrateSettingsForTest(legacy);

            var defaults = new BasisHandHeldCameraUI.CameraSettings();
            Assert.That(legacy.detachedMarker, Is.EqualTo(defaults.detachedMarker));
            Assert.That(defaults.detachedMarker, Is.EqualTo((int)BasisCameraDetachedMarker.Puck));
        }

        [Test]
        public void UpgradingAPreV9File_CarriesTheAutoFollowBlockOntoTheStack()
        {
            // Everything the auto-follow block held now lives on a modifier or on the subject
            // settings. The values are read straight off the old text, because CameraSettings no
            // longer carries the fields they were stored in.
            const string legacyJson =
                "{\"settingsVersion\":8,\"autoFollowPositionOffset\":{\"x\":2.5,\"y\":1.5,\"z\":3.5}," +
                "\"autoFollowRotationOffset\":{\"x\":7,\"y\":-9,\"z\":0},\"autoFollowPlayspace\":false," +
                "\"autoFollowLookAtHeightOffset\":-0.42,\"autoFollowLateralTracking\":0.9," +
                "\"subjectFramingRadius\":0.75}";

            var loaded = JsonUtility.FromJson<BasisHandHeldCameraUI.CameraSettings>(legacyJson);
            BasisHandHeldCameraUI.UpgradeLegacyFollowForTest(loaded, legacyJson);
            BasisHandHeldCameraUI.MigrateSettingsForTest(loaded);

            Assert.That(loaded.modifiers.follow.positionOffset, Is.EqualTo(new Vector3(2.5f, 1.5f, 3.5f)));
            Assert.That(loaded.modifiers.follow.lateralTracking, Is.EqualTo(0.9f).Within(1e-4f));
            Assert.That(loaded.modifiers.lookAt.rotationOffset, Is.EqualTo(new Vector3(7f, -9f, 0f)));
            Assert.That(loaded.modifiers.compose.rotationOffset, Is.EqualTo(new Vector3(7f, -9f, 0f)));
            Assert.That(loaded.modifiers.subject.anchorToBody, Is.False);
            Assert.That(loaded.modifiers.subject.aimHeightOffset, Is.EqualTo(-0.42f).Within(1e-4f));
            Assert.That(loaded.modifiers.subject.framingRadius, Is.EqualTo(0.75f).Within(1e-4f));
            Assert.That(loaded.settingsVersion, Is.EqualTo(BasisHandHeldCameraUI.CameraSettings.CurrentVersion));
        }

        [Test]
        public void UpgradingAPreV9File_LeavesBothSlotsEmpty()
        {
            // Whether follow was armed was never saved, so an upgraded file keeps its numbers and
            // stays put. A camera that restored armed would fly out of your hand on spawn.
            const string legacyJson = "{\"settingsVersion\":8,\"autoFollowLateralTracking\":0.9}";

            var loaded = JsonUtility.FromJson<BasisHandHeldCameraUI.CameraSettings>(legacyJson);
            BasisHandHeldCameraUI.UpgradeLegacyFollowForTest(loaded, legacyJson);

            Assert.That(loaded.modifiers.DrivesAnything, Is.False);
        }

        [Test]
        public void TheGlideSwitch_ReachesTheCameraBothWays()
        {
            BasisHandHeldCameraUI.CameraSettings settings = BasisCameraSettingsRig.DistinctiveSettings();

            settings.flyMomentum = false;
            _rig.UI.ApplySettingsForTest(settings);
            Assert.That(_rig.Camera.useMomentum, Is.False,
                "Instant Stop has to cut the fly velocity dead, on desktop and in VR alike.");

            settings.flyMomentum = true;
            _rig.UI.ApplySettingsForTest(settings);
            Assert.That(_rig.Camera.useMomentum, Is.True);
        }

        [Test]
        public void AFileWrittenBeforeTheGlideSwitch_StillCoastsToAStop()
        {
            // Defaulted in the constructor rather than migrated, so a file that has never heard of
            // the switch loads as the coast it was flown with instead of zero-filling to a hard stop.
            var loaded = JsonUtility.FromJson<BasisHandHeldCameraUI.CameraSettings>(
                "{\"settingsVersion\":11,\"flySpeed\":3.0}");

            Assert.That(loaded.flySpeed, Is.EqualTo(3f).Within(1e-4f),
                "the file this is standing in for has to actually have been read");
            Assert.That(loaded.flyMomentum, Is.True);
        }

        [Test]
        public void TheRollSwitch_ReachesTheCameraBothWays()
        {
            BasisHandHeldCameraUI.CameraSettings settings = BasisCameraSettingsRig.DistinctiveSettings();

            settings.cameraRoll = true;
            _rig.UI.ApplySettingsForTest(settings);
            Assert.That(_rig.Camera.cameraRollEnabled, Is.True,
                "Camera Roll has to reach the camera, or the grip's roll is never let through to the shot.");

            settings.cameraRoll = false;
            _rig.UI.ApplySettingsForTest(settings);
            Assert.That(_rig.Camera.cameraRollEnabled, Is.False);
        }

        [Test]
        public void AFileWrittenBeforeRollExisted_LoadsWithTheCameraLevel()
        {
            // Off is the zero fill, so a file that has never heard of the switch needs no migration
            // to load as the level camera it was saved as.
            var loaded = JsonUtility.FromJson<BasisHandHeldCameraUI.CameraSettings>(
                "{\"settingsVersion\":12,\"flySpeed\":3.0}");

            Assert.That(loaded.flySpeed, Is.EqualTo(3f).Within(1e-4f),
                "the file this is standing in for has to actually have been read");
            Assert.That(loaded.cameraRoll, Is.False, "Off is the zero fill, so the shot loads level.");
        }

        // ---------- helpers ----------

        private static IEnumerable<FieldInfo> SettingsFields() =>
            typeof(BasisHandHeldCameraUI.CameraSettings)
                .GetFields(BindingFlags.Public | BindingFlags.Instance);

        private static bool ValuesMatch(object a, object b)
        {
            switch (a)
            {
                case float f: return Mathf.Abs(f - (float)b) < 1e-3f;
                case Vector3 v: return Vector3.Distance(v, (Vector3)b) < 1e-3f;
                case Color c:
                    Color other = (Color)b;
                    return Mathf.Abs(c.r - other.r) < 1e-3f && Mathf.Abs(c.g - other.g) < 1e-3f &&
                           Mathf.Abs(c.b - other.b) < 1e-3f && Mathf.Abs(c.a - other.a) < 1e-3f;
                case null: return b == null;
                case Basis.Cinematics.BasisCameraModifierStack stack:
                    return Basis.Cinematics.BasisCameraModifierStack.Matches(
                        stack, b as Basis.Cinematics.BasisCameraModifierStack);
                default: return a.Equals(b);
            }
        }

        private static string Describe(object value) => value == null ? "null" : value.ToString();
    }
}
