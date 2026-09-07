using NUnit.Framework;
using Basis.Cinematics;
using UnityEngine;
using CameraPinSpace = BasisHandHeldCameraInteractable.CameraPinSpace;

namespace Basis.Tests.Camera
{
    /// <summary>
    /// Pins camera modes: that applying one leaves the camera in it, that editing a setting the
    /// mode chose drops to Custom while editing anything else does not, and that swapping between
    /// two modes that both claim world space hands over cleanly.
    ///
    /// Awake never runs outside play mode, so these cameras have field initializers and no scene:
    /// no capture camera and no volume profile. That confines the assertions to the behaviour half
    /// of each preset — which is the half with a state machine in it, and so the half that can
    /// actually break. The lens and post-processing halves are skipped by the same null guards in
    /// both the apply and the match, which is why a profile-less camera still round-trips.
    /// </summary>
    public class BasisCameraModePresetTests
    {
        private GameObject _go;
        private BasisHandHeldCamera _camera;

        // Every mode that can be applied, which is the whole table bar Custom. The four camera
        // kinds are in here for the same reason the four placement modes are: they go through the
        // same apply and the same match, and on this fixture — no capture camera, no volume profile
        // — the only thing that tells one kind from another is the body each hands out. That is
        // what makes them worth having here rather than only in the body's own tests.
        private static readonly BasisCameraMode[] Presets =
        {
            BasisCameraMode.Photo,
            BasisCameraMode.FlyingPuck,
            BasisCameraMode.FollowMe,
            BasisCameraMode.Cinematic,
            BasisCameraMode.Disposable,
            BasisCameraMode.Instant,
            BasisCameraMode.Camcorder,
            BasisCameraMode.Security,
        };

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("ModePresetCameraUnderTest");
            _camera = _go.AddComponent<BasisHandHeldCamera>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null) Object.DestroyImmediate(_go);
        }

        // ---- The contract that keeps apply and match from drifting apart --------------------

        [Test]
        public void ApplyingAnyMode_LeavesTheCameraMatchingIt()
        {
            // The single most load-bearing test here. Apply writes a preset and Match decides
            // whether the camera is still in it; if a value is ever added to one and not the
            // other, the mode would flip to Custom the instant it was selected.
            foreach (BasisCameraMode mode in Presets)
            {
                _camera.ApplyCameraMode(mode);

                Assert.That(_camera.CameraMode, Is.EqualTo(mode), $"{mode} did not take.");
                Assert.That(_camera.MatchesCameraMode(mode), Is.True,
                    $"Applying {mode} left the camera not matching {mode} — apply and match disagree.");
                Assert.That(_camera.RefreshCameraMode(), Is.False,
                    $"A freshly applied {mode} must not immediately re-derive to something else.");
            }
        }

        [Test]
        public void EveryPresetIsDistinguishableFromEveryOther()
        {
            // Two modes that match the same camera state would make the label a coin flip.
            foreach (BasisCameraMode applied in Presets)
            {
                _camera.ApplyCameraMode(applied);

                foreach (BasisCameraMode other in Presets)
                {
                    if (other == applied) continue;
                    Assert.That(_camera.MatchesCameraMode(other), Is.False,
                        $"A camera in {applied} also reports as {other}.");
                }
            }
        }

        [Test]
        public void DefaultsToPhoto()
        {
            Assert.That(_camera.CameraMode, Is.EqualTo(BasisCameraMode.Photo));
        }

        // ---- Drifting off a mode ------------------------------------------------------------

        [Test]
        public void ChangingASettingTheModeChose_DropsToCustom()
        {
            _camera.ApplyCameraMode(BasisCameraMode.FollowMe);

            _camera.subjectSettings.anchorToBody = !_camera.subjectSettings.anchorToBody;

            Assert.That(_camera.RefreshCameraMode(), Is.True, "The drift should have been noticed.");
            Assert.That(_camera.CameraMode, Is.EqualTo(BasisCameraMode.Custom));
        }

        [Test]
        public void DisarmingFollow_LeavesFollowMe()
        {
            _camera.ApplyCameraMode(BasisCameraMode.FollowMe);

            _camera.SetPositionModifier(BasisCameraPositionModifier.FreeFly);
            _camera.RefreshCameraMode();

            // Where it lands is deliberately not asserted. Switching follow off also hands the
            // camera back to the hand, and on a camera that has a lens and a volume profile the
            // leftover Follow Me optics put it on Custom — but this fixture has neither, so by
            // every measure that remains it is genuinely a Photo camera and says so. Both are
            // right; what matters is that it stops claiming to be following you when it is not.
            Assert.That(_camera.Modifiers.DrivesPosition, Is.False);
            Assert.That(_camera.CameraMode, Is.Not.EqualTo(BasisCameraMode.FollowMe),
                "Follow being armed is what Follow Me is, so switching it off cannot still be Follow Me.");
        }

        [Test]
        public void ChangingASettingTheModeDoesNotOwn_KeepsTheMode()
        {
            _camera.ApplyCameraMode(BasisCameraMode.Photo);

            // The aim height is not in any preset — the modes leave it to the user.
            _camera.subjectSettings.aimHeightOffset = 0.42f;

            Assert.That(_camera.RefreshCameraMode(), Is.False);
            Assert.That(_camera.CameraMode, Is.EqualTo(BasisCameraMode.Photo),
                "A setting no preset writes must not be able to knock the camera out of its mode.");
        }

        [Test]
        public void AModeThatGreysOutFollow_LeavesTheUsersFramingAlone()
        {
            // Photo and Flying Puck colour the position section as doing nothing.
            // A mode that greys a section out must not quietly reset the values inside it, or the
            // user's framing is gone the next time they come back to Follow Me.
            _camera.ApplyCameraMode(BasisCameraMode.FollowMe);
            Vector3 framing = new Vector3(1.25f, 0.6f, 2.4f);
            _camera.Modifiers.follow.positionOffset = framing;
            _camera.subjectSettings.aimHeightOffset = -0.3f;

            foreach (BasisCameraMode mode in new[]
                     { BasisCameraMode.Photo, BasisCameraMode.FlyingPuck })
            {
                _camera.ApplyCameraMode(mode);

                Assert.That(_camera.Modifiers.follow.positionOffset, Is.EqualTo(framing),
                    $"{mode} greys out the position slot but reset the follow offset.");
                Assert.That(_camera.subjectSettings.aimHeightOffset, Is.EqualTo(-0.3f).Within(1e-4f),
                    $"{mode} greys out the position slot but reset the aim height.");
            }
        }

        [Test]
        public void EditingFollowSettings_DoesNotKnockAModeThatIgnoresThemToCustom()
        {
            _camera.ApplyCameraMode(BasisCameraMode.Photo);

            _camera.subjectSettings.anchorToBody = !_camera.subjectSettings.anchorToBody;
            _camera.Modifiers.follow.positionOffset = new Vector3(3f, 2f, 1f);

            Assert.That(_camera.RefreshCameraMode(), Is.False);
            Assert.That(_camera.CameraMode, Is.EqualTo(BasisCameraMode.Photo),
                "Photo fits no position modifier, so follow's settings cannot take it out of Photo.");
        }

        [Test]
        public void FlyingAPhotoCamera_KeepsItInPhoto()
        {
            // Where the camera is sitting is not how it is configured. Letting go of a handheld
            // camera, or grabbing a flying one back, must not read as leaving the mode.
            _camera.ApplyCameraMode(BasisCameraMode.Photo);

            _camera.PinSpace = CameraPinSpace.WorldSpace;
            _camera.RefreshCameraMode();

            Assert.That(_camera.CameraMode, Is.EqualTo(BasisCameraMode.Photo));
        }

        [Test]
        public void TuningBackToAPresetExactly_ReturnsToThatMode()
        {
            // The label is derived, not sticky: a camera that has been hand-tuned all the way onto
            // a preset is in that preset, and saying Custom would be a lie the user can see.
            _camera.ApplyCameraMode(BasisCameraMode.Photo);
            _camera.useAutoLeveling = true;
            _camera.RefreshCameraMode();
            Assert.That(_camera.CameraMode, Is.EqualTo(BasisCameraMode.Custom), "Precondition.");

            _camera.useAutoLeveling = false;
            _camera.RefreshCameraMode();

            Assert.That(_camera.CameraMode, Is.EqualTo(BasisCameraMode.Photo));
        }

        // ---- What drifted, rather than that something did -------------------------------------

        [Test]
        public void TheComparison_NamesTheValueThatMovedAndNothingElse()
        {
            _camera.ApplyCameraMode(BasisCameraMode.FollowMe);
            _camera.subjectSettings.anchorToBody = !_camera.subjectSettings.anchorToBody;

            BasisCameraPresetDiff diff = _camera.CompareToMode(BasisCameraMode.FollowMe);

            Assert.That(diff.Compared, Is.True);
            Assert.That(diff.Differs(BasisCameraPresetField.AnchorToBody), Is.True);
            Assert.That(diff.Differs(BasisCameraPresetField.PositionModifier), Is.False,
                "Nothing else was touched, so nothing else may be reported as changed.");
            Assert.That(diff.Matches, Is.False);
        }

        [Test]
        public void ComparedMode_OutlivesTheDropToCustom()
        {
            // The whole point of holding it: once the label says Custom it can no longer say what
            // the camera has left, and that is exactly when the question gets asked.
            _camera.ApplyCameraMode(BasisCameraMode.FollowMe);
            _camera.subjectSettings.anchorToBody = !_camera.subjectSettings.anchorToBody;
            _camera.RefreshCameraMode();

            Assert.That(_camera.CameraMode, Is.EqualTo(BasisCameraMode.Custom));
            Assert.That(_camera.ComparedMode, Is.EqualTo(BasisCameraMode.FollowMe));
        }

        [Test]
        public void ComparedMode_FollowsACameraBackOntoAPreset()
        {
            _camera.ApplyCameraMode(BasisCameraMode.FollowMe);
            _camera.ApplyCameraMode(BasisCameraMode.Photo);
            _camera.RefreshCameraMode();

            Assert.That(_camera.ComparedMode, Is.EqualTo(BasisCameraMode.Photo));
        }

        [Test]
        public void ComparingAgainstCustom_ReportsNeitherAMatchNorAChange()
        {
            BasisCameraPresetDiff diff = _camera.CompareToMode(BasisCameraMode.Custom);

            Assert.That(diff.Compared, Is.False);
            Assert.That(diff.Matches, Is.False, "There was no preset to match.");
            Assert.That(diff.HasChanges, Is.False, "And so nothing that could have changed.");
            Assert.That(diff.Differs(BasisCameraPresetField.Body), Is.False);
        }

        // ---- Mode swapping ------------------------------------------------------------------

        [Test]
        public void SwappingBetweenEveryPairOfModes_LandsCleanly()
        {
            // Follow and the shot rig both claim world space on the way in and both hand it back
            // on the way out, so a careless order lets the loser's hand-back fire last and drag
            // the camera out of the pin the winner just took.
            foreach (BasisCameraMode from in Presets)
            {
                foreach (BasisCameraMode to in Presets)
                {
                    _camera.ApplyCameraMode(from);
                    _camera.ApplyCameraMode(to);

                    Assert.That(_camera.CameraMode, Is.EqualTo(to), $"{from} -> {to} did not land.");
                    Assert.That(_camera.MatchesCameraMode(to), Is.True, $"{from} -> {to} left a mismatch.");
                }
            }
        }

        [Test]
        public void FollowMeToCinematic_DisarmsFollowAndKeepsWorldSpace()
        {
            _camera.ApplyCameraMode(BasisCameraMode.FollowMe);
            _camera.ApplyCameraMode(BasisCameraMode.Cinematic);

            // Following and composing are no longer rival modes: Cinematic is Follow Subject in
            // the position slot with Compose in the rotation slot, which is the coupling the
            // modifier slots were introduced to remove.
            Assert.That(_camera.Modifiers.positionModifier,
                Is.EqualTo(BasisCameraPositionModifier.FollowSubject));
            Assert.That(_camera.Modifiers.rotationModifier,
                Is.EqualTo(BasisCameraRotationModifier.Compose));
            Assert.That(_camera.PinSpace, Is.EqualTo(CameraPinSpace.WorldSpace),
                "Swapping modes must not drag the camera back to the hand.");
        }

        [Test]
        public void CinematicToPhoto_StowsTheRigAndReturnsToTheHand()
        {
            _camera.ApplyCameraMode(BasisCameraMode.Cinematic);
            _camera.ApplyCameraMode(BasisCameraMode.Photo);

            Assert.That(_camera.Modifiers.DrivesAnything, Is.False);
            Assert.That(_camera.PinSpace, Is.EqualTo(CameraPinSpace.HandHeld));
        }

        [Test]
        public void RepeatedSwapping_Converges()
        {
            for (int Index = 0; Index < 10; Index++)
            {
                _camera.ApplyCameraMode(BasisCameraMode.FollowMe);
                Assert.That(_camera.MatchesCameraMode(BasisCameraMode.FollowMe), Is.True);

                _camera.ApplyCameraMode(BasisCameraMode.Photo);
                Assert.That(_camera.MatchesCameraMode(BasisCameraMode.Photo), Is.True);
            }
        }

        // ---- Custom -------------------------------------------------------------------------

        [Test]
        public void ApplyingCustom_ChangesNothingButTheLabel()
        {
            _camera.ApplyCameraMode(BasisCameraMode.FollowMe);
            bool followBefore = _camera.Modifiers.DrivesPosition;
            Vector3 offsetBefore = _camera.Modifiers.follow.positionOffset;

            _camera.ApplyCameraMode(BasisCameraMode.Custom);

            Assert.That(_camera.CameraMode, Is.EqualTo(BasisCameraMode.Custom));
            Assert.That(_camera.Modifiers.DrivesPosition, Is.EqualTo(followBefore),
                "Custom has no preset to apply, so it must not disturb the camera.");
            Assert.That(_camera.Modifiers.follow.positionOffset, Is.EqualTo(offsetBefore));
        }

        [Test]
        public void MatchesCameraMode_IsAlwaysFalseForCustom()
        {
            // There is nothing to match against, and answering true would let the resolver settle
            // on Custom before it had tried any real mode.
            Assert.That(_camera.MatchesCameraMode(BasisCameraMode.Custom), Is.False);
        }

        // ---- Restoring a saved mode ---------------------------------------------------------

        [Test]
        public void RestoringAMode_ReArmsOnlyThePin()
        {
            // Where the camera is pinned is the one thing a settings file still cannot carry, so it
            // is the whole of what a restore re-arms. The modifier stack IS saved, so re-running
            // the preset here would overwrite the values the load had only just finished restoring.
            _camera.Modifiers.positionModifier = BasisCameraPositionModifier.Orbit;
            _camera.Modifiers.rotationModifier = BasisCameraRotationModifier.Compose;
            _camera.Modifiers.follow.positionOffset = new Vector3(1.25f, 0.6f, 2.4f);
            _camera.autoFocusFollowSubject = true;
            _camera.useAutoLeveling = true;
            _camera.capture360Enabled = true;

            _camera.RestoreCameraModeForTest(BasisCameraMode.FollowMe);

            Assert.That(_camera.PinSpace, Is.EqualTo(CameraPinSpace.WorldSpace),
                "The pin is the one thing the file could not carry, so it has to come back.");

            Assert.That(_camera.Modifiers.positionModifier, Is.EqualTo(BasisCameraPositionModifier.Orbit),
                "The loaded position modifier was overwritten by the preset's.");
            Assert.That(_camera.Modifiers.rotationModifier, Is.EqualTo(BasisCameraRotationModifier.Compose),
                "The loaded rotation modifier was overwritten by the preset's.");
            Assert.That(_camera.Modifiers.follow.positionOffset, Is.EqualTo(new Vector3(1.25f, 0.6f, 2.4f)),
                "The loaded follow offset was overwritten by the preset's.");
            Assert.That(_camera.useAutoLeveling, Is.True, "The loaded auto-level was overwritten.");
            Assert.That(_camera.capture360Enabled, Is.True, "The loaded 360 toggle was overwritten.");
        }

        [Test]
        public void RestoringAModeTheSettingsNoLongerMatch_SettlesOnCustom()
        {
            // The file says Follow Me but its values have been edited since. The label has to
            // follow the values, not the other way round.
            _camera.useAutoLeveling = true;   // Follow Me wants this off.

            _camera.RestoreCameraModeForTest(BasisCameraMode.FollowMe);

            Assert.That(_camera.CameraMode, Is.EqualTo(BasisCameraMode.Custom));
        }

        [Test]
        public void RestoringCustom_PromotesToAPresetWhenTheValuesMatchOne()
        {
            _camera.ApplyCameraMode(BasisCameraMode.FlyingPuck);

            _camera.RestoreCameraModeForTest(BasisCameraMode.Custom);

            Assert.That(_camera.CameraMode, Is.EqualTo(BasisCameraMode.FlyingPuck),
                "A hand-tuned file that lands exactly on a preset is in that preset.");
        }

        // ---- The mode table -----------------------------------------------------------------

        [Test]
        public void OrderedListsEveryModeExactlyOnce()
        {
            System.Array all = System.Enum.GetValues(typeof(BasisCameraMode));
            Assert.That(BasisCameraModes.Ordered.Length, Is.EqualTo(all.Length),
                "A mode missing from Ordered would never appear in the panel's dropdown.");

            foreach (BasisCameraMode mode in all)
            {
                Assert.That(System.Array.IndexOf(BasisCameraModes.Ordered, mode), Is.GreaterThanOrEqualTo(0),
                    $"{mode} is missing from the presentation order.");
            }
        }

        [Test]
        public void EveryModeHasADescriptor()
        {
            foreach (BasisCameraMode mode in System.Enum.GetValues(typeof(BasisCameraMode)))
            {
                BasisCameraModeDescriptor descriptor = BasisCameraModes.Get(mode);
                Assert.That(descriptor.Mode, Is.EqualTo(mode), $"Get({mode}) returned the wrong descriptor.");
                Assert.That(descriptor.TitleKey, Is.Not.Null.And.Not.Empty);
                Assert.That(descriptor.DescriptionKey, Is.Not.Null.And.Not.Empty);
            }
        }

        [Test]
        public void AModeThatFilmsSomebodyBringsASubjectWithIt()
        {
            _camera.SetSubjectModifier(BasisCameraSubjectModifier.None);

            _camera.ApplyCameraMode(BasisCameraMode.FollowMe);

            Assert.That(_camera.Modifiers.subject.modifier, Is.EqualTo(BasisCameraSubjectModifier.FollowPlayer),
                "Follow Me with nobody selected would otherwise sanitize itself back to a free camera.");
            Assert.That(_camera.Modifiers.DrivesPosition, Is.True);
        }
    }
}
