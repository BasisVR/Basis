using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Camera
{
    /// <summary>
    /// The camera body: the film in it, the wheel between shots, and the date it burns into the
    /// corner of the picture.
    ///
    /// <para>All of it is reachable without a scene, which is the point of where it lives — the
    /// counter and the lockouts are plain state on the camera, and the stamp is geometry rather
    /// than pixels, so what a disposable actually does can be pinned without a render.</para>
    ///
    /// <para>Awake never runs outside play mode, so these cameras arrive with field initializers
    /// and no capture camera and no volume profile. Every method under test null-guards its scene
    /// state for that reason, and the body is deliberately the one half of a camera kind that
    /// survives having neither.</para>
    /// </summary>
    public class BasisCameraBodyTests
    {
        private GameObject _go;
        private BasisHandHeldCamera _camera;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("BodyCameraUnderTest");
            _camera = _go.AddComponent<BasisHandHeldCamera>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null) UnityEngine.Object.DestroyImmediate(_go);
        }

        // ---- The table ----------------------------------------------------------------------

        [Test]
        public void AFreshCameraIsDigitalAndUnconstrained()
        {
            Assert.That(_camera.Body, Is.EqualTo(BasisCameraBodyKind.Digital));
            Assert.That(_camera.BodyTraits.Constrains, Is.False,
                "The camera as it has always been must not have gained a way to refuse a photograph.");
            Assert.That(_camera.EvaluateShutter(), Is.EqualTo(BasisCameraShutterState.Ready));
        }

        [Test]
        public void AnUnknownBodyOffDiskReadsAsDigital()
        {
            // A settings file is text, and a build that has never heard of body 97 must not strand
            // its owner holding a camera with no traits to look up.
            Assert.That(BasisCameraBodies.Sanitize(97), Is.EqualTo(BasisCameraBodyKind.Digital));
            Assert.That(BasisCameraBodies.Sanitize(-3), Is.EqualTo(BasisCameraBodyKind.Digital));
            Assert.That(BasisCameraBodies.Get((BasisCameraBodyKind)97), Is.Not.Null);
        }

        [Test]
        public void EveryModeThatIsAKindHandsOutItsOwnBody()
        {
            // Two kinds sharing a body would make them indistinguishable on a camera with no volume
            // profile, which is exactly the fixture the mode round-trip test runs on.
            var seen = new HashSet<BasisCameraBodyKind>();

            foreach (BasisCameraMode mode in new[]
            {
                BasisCameraMode.Disposable,
                BasisCameraMode.Instant,
                BasisCameraMode.Camcorder,
                BasisCameraMode.Security,
            })
            {
                _camera.ApplyCameraMode(mode);

                Assert.That(_camera.Body, Is.Not.EqualTo(BasisCameraBodyKind.Digital),
                    $"{mode} is a camera kind, so it has to hand out a body of its own.");
                Assert.That(seen.Add(_camera.Body), Is.True,
                    $"{mode} shares a body with a kind before it.");
            }
        }

        [Test]
        public void EveryModeThatIsAJobLeavesTheBodyDigital()
        {
            foreach (BasisCameraMode mode in new[]
            {
                BasisCameraMode.Photo,
                BasisCameraMode.FlyingPuck,
                BasisCameraMode.FollowMe,
                BasisCameraMode.Cinematic,
            })
            {
                _camera.ApplyCameraMode(BasisCameraMode.Disposable);
                _camera.ApplyCameraMode(mode);

                Assert.That(_camera.Body, Is.EqualTo(BasisCameraBodyKind.Digital),
                    $"{mode} is about where the camera goes, so it must hand back a plain camera.");
            }
        }

        // ---- Film ---------------------------------------------------------------------------

        [Test]
        public void PickingDisposable_HandsOutAFullRoll()
        {
            _camera.ApplyCameraMode(BasisCameraMode.Disposable);

            Assert.That(_camera.ExposuresRemaining, Is.EqualTo(_camera.BodyTraits.Exposures));
        }

        [Test]
        public void PickingTheSameKindAgain_IsANewCamera()
        {
            _camera.ApplyCameraMode(BasisCameraMode.Disposable);
            _camera.TryTakeFrameForTest();

            _camera.ApplyCameraMode(BasisCameraMode.Disposable);

            // Choosing a disposable from the picker is being handed one, not picking up the one you
            // were already holding — otherwise an empty camera could never be replaced, only reloaded.
            Assert.That(_camera.ExposuresRemaining, Is.EqualTo(_camera.BodyTraits.Exposures));
        }

        [Test]
        public void TakingAFrame_SpendsItAndLocksTheShutter()
        {
            _camera.ApplyCameraMode(BasisCameraMode.Disposable);
            int before = _camera.ExposuresRemaining;

            Assert.That(_camera.TryTakeFrameForTest(), Is.True);

            Assert.That(_camera.ExposuresRemaining, Is.EqualTo(before - 1));
            Assert.That(_camera.EvaluateShutter(), Is.EqualTo(BasisCameraShutterState.WindingOn));
            Assert.That(_camera.TryTakeFrameForTest(), Is.False,
                "A disposable that could be fired twice in a frame is not a disposable.");
        }

        [Test]
        public void WindingOn_ReleasesTheShutterAgain()
        {
            _camera.ApplyCameraMode(BasisCameraMode.Disposable);
            _camera.TryTakeFrameForTest();

            _camera.AdvanceBodyForTest(_camera.BodyTraits.WindOnSeconds + 0.01f);

            Assert.That(_camera.EvaluateShutter(), Is.EqualTo(BasisCameraShutterState.Ready));
            Assert.That(_camera.TryTakeFrameForTest(), Is.True);
        }

        [Test]
        public void ASpentRoll_WindsItselfOnRatherThanStopping()
        {
            // There is no film to go and buy, so an empty counter was only ever a button the
            // operator had to find before the camera would work again.
            _camera.ApplyCameraMode(BasisCameraMode.Disposable);
            int roll = _camera.BodyTraits.Exposures;

            for (int Frame = 0; Frame < roll; Frame++)
            {
                Assert.That(_camera.TryTakeFrameForTest(), Is.True, $"Frame {Frame + 1} of {roll} was refused.");
                _camera.AdvanceBodyForTest(10f);
            }

            Assert.That(_camera.ExposuresRemaining, Is.Zero, "The roll still runs down; that is what the counter is for.");
            Assert.That(_camera.EvaluateShutter(), Is.EqualTo(BasisCameraShutterState.Ready),
                "Nothing is standing in the way once the wind-on is done.");

            Assert.That(_camera.TryTakeFrameForTest(), Is.True, "The next frame loads a fresh roll by itself.");
            Assert.That(_camera.ExposuresRemaining, Is.EqualTo(roll - 1));
        }

        [Test]
        public void ReloadFilm_StillFillsARollForAnythingThatAsks()
        {
            _camera.ApplyCameraMode(BasisCameraMode.Disposable);
            _camera.TryTakeFrameForTest();

            _camera.ReloadFilm();

            Assert.That(_camera.ExposuresRemaining, Is.EqualTo(_camera.BodyTraits.Exposures));
        }

        [Test]
        public void InstantFilm_HoldsTheShutterWhileAPrintComesUp()
        {
            _camera.ApplyCameraMode(BasisCameraMode.Instant);
            BasisCameraBodyTraits body = _camera.BodyTraits;

            Assert.That(body.DevelopSeconds, Is.GreaterThan(body.WindOnSeconds),
                "The develop wait is the whole character of instant film, so it has to outlast the eject.");

            _camera.TryTakeFrameForTest();
            _camera.AdvanceBodyForTest(body.WindOnSeconds + 0.01f);

            // Wound on but not yet developed: the refusal has to name the one still in the way.
            Assert.That(_camera.EvaluateShutter(), Is.EqualTo(BasisCameraShutterState.Developing));

            _camera.AdvanceBodyForTest(body.DevelopSeconds);
            Assert.That(_camera.EvaluateShutter(), Is.EqualTo(BasisCameraShutterState.Ready));
        }

        [Test]
        public void TapeBodies_NeverRunOutAndNeverWait()
        {
            foreach (BasisCameraMode mode in new[] { BasisCameraMode.Camcorder, BasisCameraMode.Security })
            {
                _camera.ApplyCameraMode(mode);

                for (int Frame = 0; Frame < 5; Frame++)
                {
                    Assert.That(_camera.TryTakeFrameForTest(), Is.True, $"{mode} refused frame {Frame + 1}.");
                }

                Assert.That(_camera.EvaluateShutter(), Is.EqualTo(BasisCameraShutterState.Ready),
                    $"{mode} has nothing to run out of and nothing to wait for.");
            }
        }

        [Test]
        public void ReloadingACameraWithNoFilm_DoesNothing()
        {
            _camera.ApplyCameraMode(BasisCameraMode.Camcorder);
            _camera.ReloadFilm();

            Assert.That(_camera.ExposuresRemaining, Is.Zero,
                "A tape body has no load, so the counter must stay the zero that means 'not counting'.");
        }

        // ---- The flash ----------------------------------------------------------------------

        [Test]
        public void TheFlashArmsItselfOnABodyThatHasOne()
        {
            _camera.ApplyCameraMode(BasisCameraMode.Disposable);
            Assert.That(_camera.FlashEnabled, Is.True);
            Assert.That(_camera.FlashReady, Is.True);

            _camera.ApplyCameraMode(BasisCameraMode.Camcorder);
            Assert.That(_camera.FlashEnabled, Is.False, "A camcorder has nothing on the front to arm.");
            Assert.That(_camera.FlashReady, Is.False);

            _camera.SetFlashEnabled(true);
            Assert.That(_camera.FlashEnabled, Is.False,
                "Switching on a flash that is not fitted must not leave the panel claiming one.");
        }

        [Test]
        public void FiringTheFlash_PutsItOnChargeUntilItRecycles()
        {
            _camera.ApplyCameraMode(BasisCameraMode.Disposable);
            _camera.TryTakeFrameForTest();

            Assert.That(_camera.FlashReady, Is.False, "A flash that recharged instantly would not be one.");

            // Long enough to wind on, but not to recycle: the two clocks are separate on purpose,
            // and a disposable spends most of a roll wound on with the flash still whining.
            _camera.AdvanceBodyForTest(_camera.BodyTraits.WindOnSeconds + 0.01f);
            Assert.That(_camera.EvaluateShutter(), Is.EqualTo(BasisCameraShutterState.Ready));
            Assert.That(_camera.FlashReady, Is.False);

            _camera.AdvanceBodyForTest(_camera.BodyTraits.FlashRecycleSeconds);
            Assert.That(_camera.FlashReady, Is.True);
        }

        [Test]
        public void AFlashThatIsSwitchedOff_NeverGoesOnCharge()
        {
            _camera.ApplyCameraMode(BasisCameraMode.Disposable);
            _camera.SetFlashEnabled(false);

            _camera.TryTakeFrameForTest();

            Assert.That(_camera.FlashRecycleRemaining, Is.Zero,
                "A flash nobody fired has nothing to recover from.");
        }

#if Basis_VOLUMETRIC_SUPPORTED
        [Test]
        public void TheFlashLightsTheShotButNotTheFogInFrontOfTheLens()
        {
            _camera.ApplyCameraMode(BasisCameraMode.Instant);
            GameObject capture = new GameObject("CaptureCamera");
            capture.transform.SetParent(_go.transform, false);
            _camera.captureCamera = capture.AddComponent<UnityEngine.Camera>();

            Assert.That(_camera.TryTakeFrameForTest(), Is.True);

            Light flash = capture.GetComponentInChildren<Light>(true);
            Assert.That(flash, Is.Not.Null, "An instant camera that took a frame with its flash armed must have fired it.");
            Assert.That(flash.enabled, Is.True);
            Assert.That(VolumetricFogLight.TryGetMultiplier(flash, out float multiplier), Is.True,
                "The flash sits on the lens, so any fog it lights is a veil across the photograph.");
            Assert.That(multiplier, Is.Zero);
        }
#endif

        // ---- Loading a saved camera ---------------------------------------------------------

        [Test]
        public void RestoringABody_KeepsWhatWasLeftOnTheLoad()
        {
            _camera.RestoreBodyForTest((int)BasisCameraBodyKind.Disposable, 5, true);

            Assert.That(_camera.Body, Is.EqualTo(BasisCameraBodyKind.Disposable));
            Assert.That(_camera.ExposuresRemaining, Is.EqualTo(5),
                "A disposable that refilled itself on every load would never run out at all.");
        }

        [Test]
        public void RestoringAFileThatPredatesBodies_LoadsAFullRoll()
        {
            // JsonUtility fills an absent field from the constructor, which writes FullRoll — so an
            // older file has to arrive as a fresh camera rather than one that will not fire.
            _camera.RestoreBodyForTest((int)BasisCameraBodyKind.Disposable, BasisHandHeldCamera.FullRoll, true);

            Assert.That(_camera.ExposuresRemaining, Is.EqualTo(_camera.BodyTraits.Exposures));
        }

        [Test]
        public void RestoringACountBiggerThanTheRoll_IsClampedToIt()
        {
            _camera.RestoreBodyForTest((int)BasisCameraBodyKind.Instant, 900, true);

            Assert.That(_camera.ExposuresRemaining, Is.EqualTo(_camera.BodyTraits.Exposures));
        }

        [Test]
        public void RestoringAnArmedFlashOntoABodyWithoutOne_LeavesItOff()
        {
            _camera.RestoreBodyForTest((int)BasisCameraBodyKind.Security, BasisHandHeldCamera.FullRoll, true);

            Assert.That(_camera.FlashEnabled, Is.False);
        }

        [Test]
        public void ABodyOutlivesTheModeItCameFrom()
        {
            _camera.ApplyCameraMode(BasisCameraMode.Disposable);
            _camera.TryTakeFrameForTest();

            // Touch something the mode drives. The label goes; the camera in your hand does not.
            _camera.useAutoLeveling = !_camera.useAutoLeveling;
            _camera.RefreshCameraMode();

            Assert.That(_camera.CameraMode, Is.EqualTo(BasisCameraMode.Custom));
            Assert.That(_camera.Body, Is.EqualTo(BasisCameraBodyKind.Disposable));
            Assert.That(_camera.ExposuresRemaining, Is.EqualTo(_camera.BodyTraits.Exposures - 1));
        }

        [Test]
        public void ACameraWithEveryOverrideFitted_StillMatchesTheKindItWasGiven()
        {
            // The bare fixture above skips every optical compare for want of a volume profile, so
            // this is the only place the look half of a kind is actually exercised: applied through
            // the UI's own setters and then read back off the live overrides.
            using (var rig = new BasisCameraSettingsRig())
            {
                foreach (BasisCameraMode mode in new[]
                {
                    BasisCameraMode.Disposable,
                    BasisCameraMode.Instant,
                    BasisCameraMode.Camcorder,
                    BasisCameraMode.Security,
                })
                {
                    rig.Camera.ApplyCameraMode(mode);

                    Assert.That(rig.Camera.MatchesCameraMode(mode), Is.True,
                        $"Applying {mode} on a camera that has the effects fitted left it not matching {mode}.");
                    Assert.That(rig.Camera.RefreshCameraMode(), Is.False,
                        $"A freshly applied {mode} re-derived to something else.");
                }
            }
        }

        [Test]
        public void EditingTheGrainOfAKind_DropsToCustomButKeepsTheCamera()
        {
            using (var rig = new BasisCameraSettingsRig())
            {
                rig.Camera.ApplyCameraMode(BasisCameraMode.Disposable);

                rig.UI.ChangeFilmGrain(0f);

                Assert.That(rig.Camera.RefreshCameraMode(), Is.True, "The drift should have been noticed.");
                Assert.That(rig.Camera.CameraMode, Is.EqualTo(BasisCameraMode.Custom));
                Assert.That(rig.Camera.Body, Is.EqualTo(BasisCameraBodyKind.Disposable),
                    "Turning the grain off does not turn a disposable into a digital camera.");
            }
        }

        // ---- The print, the fog and the sensor ----------------------------------------------

        [Test]
        public void OnlyAnInstantBodyMountsItsPictureInAPrint()
        {
            Assert.That(BasisCameraPrintFinish.TryGetMount(
                BasisCameraPrintBorder.None, 1024, 1024, out _, out _, out _), Is.False);

            Assert.That(BasisCameraPrintFinish.TryGetMount(
                BasisCameraPrintBorder.Instant, 1024, 1024,
                out RectInt window, out int printWidth, out int printHeight), Is.True);

            Assert.That(window.width, Is.EqualTo(1024), "The photograph is mounted, not resized.");
            Assert.That(window.height, Is.EqualTo(1024));
            Assert.That(printWidth, Is.GreaterThan(1024));
            Assert.That(printHeight, Is.GreaterThan(printWidth), "An instant print is taller than it is wide.");
        }

        [Test]
        public void ThePrintsFatBorderIsTheOneAlongTheBottom()
        {
            BasisCameraPrintFinish.TryGetMount(
                BasisCameraPrintBorder.Instant, 1024, 1024,
                out RectInt window, out int printWidth, out int printHeight);

            int left = window.xMin;
            int right = printWidth - window.xMax;
            int bottom = window.yMin;
            int top = printHeight - window.yMax;

            // Bottom-left origin, matching raw texture rows: the strip everybody writes a name on is
            // the one at y = 0, and it is the whole reason the shape is recognisable.
            Assert.That(left, Is.EqualTo(right), "The sides are an even width of stock.");
            Assert.That(top, Is.EqualTo(left), "The top border is the same as the sides on a 600 print.");
            Assert.That(bottom, Is.GreaterThan(top * 3), "The bottom strip is the wide one.");
        }

        [Test]
        public void ThePrintComesOutTheShapeOfARealOne()
        {
            BasisCameraPrintFinish.TryGetMount(
                BasisCameraPrintBorder.Instant, 1024, 1024, out _, out int printWidth, out int printHeight);

            // A Polaroid 600 print is 3.5 x 4.2 inches around a 3.1 inch square image.
            Assert.That(printWidth / (float)printHeight, Is.EqualTo(3.5f / 4.2f).Within(0.01f));
        }

        [Test]
        public void ABorderScalesWithTheImageItIsAround()
        {
            BasisCameraPrintFinish.TryGetMount(BasisCameraPrintBorder.Instant, 512, 512, out RectInt small, out _, out _);
            BasisCameraPrintFinish.TryGetMount(BasisCameraPrintBorder.Instant, 2048, 2048, out RectInt large, out _, out _);

            Assert.That(large.xMin, Is.EqualTo(small.xMin * 4).Within(2),
                "The border is a share of the picture, so four times the picture is four times the border.");
        }

        [Test]
        public void OnlyTheEndsOfARollComeBackFogged()
        {
            const int roll = 27;

            // The counter is read AFTER the frame has been spent, so 26 left is the first frame taken.
            Assert.That(BasisCameraPrintFinish.ShouldLeak(26, roll), Is.True, "The leader is fogged while loading.");
            Assert.That(BasisCameraPrintFinish.ShouldLeak(1, roll), Is.True);
            Assert.That(BasisCameraPrintFinish.ShouldLeak(0, roll), Is.True, "The last frame sits against the end of the spool.");

            for (int remaining = 2; remaining <= roll - 2; remaining++)
            {
                Assert.That(BasisCameraPrintFinish.ShouldLeak(remaining, roll), Is.False,
                    $"A frame in the middle of the roll ({remaining} left) should not fog.");
            }
        }

        [Test]
        public void AFrameFogsTheSameWayEveryTimeItIsTaken()
        {
            BasisCameraPrintFinish.TryGetLeak(26, 1296, 864, out int firstEdge, out int firstDepth, out float firstStrength);
            BasisCameraPrintFinish.TryGetLeak(26, 1296, 864, out int againEdge, out int againDepth, out float againStrength);

            // Not random: a leak that could land anywhere reads as an effect fired at the picture,
            // and one that is a property of the frame reads as a camera.
            Assert.That(againEdge, Is.EqualTo(firstEdge));
            Assert.That(againDepth, Is.EqualTo(firstDepth));
            Assert.That(againStrength, Is.EqualTo(firstStrength));
            Assert.That(firstDepth, Is.GreaterThan(0));
            Assert.That(firstEdge, Is.InRange(0, 3));
        }

        [Test]
        public void TheFogFadesToNothingBeforeItsBandRuns()
        {
            const int depth = 100;

            Assert.That(BasisCameraPrintFinish.LeakFalloff(0, depth), Is.EqualTo(1f).Within(0.001f));
            Assert.That(BasisCameraPrintFinish.LeakFalloff(depth, depth), Is.Zero,
                "A band with an edge where it stops is a rectangle of orange, not a leak.");
            Assert.That(BasisCameraPrintFinish.LeakFalloff(depth + 50, depth), Is.Zero);

            float previous = 1.1f;
            for (int distance = 0; distance <= depth; distance += 5)
            {
                float here = BasisCameraPrintFinish.LeakFalloff(distance, depth);
                Assert.That(here, Is.LessThan(previous), "The fog has to fall off the whole way in.");
                previous = here;
            }
        }

        [Test]
        public void ATinyPictureIsNotFogged()
        {
            Assert.That(BasisCameraPrintFinish.TryGetLeak(0, 4, 4, out _, out _, out _), Is.False);
            Assert.That(BasisCameraPrintFinish.TryGetLeak(0, 0, 0, out _, out _, out _), Is.False);
        }

        [Test]
        public void EachBodyBringsItsOwnSensor()
        {
            using (var rig = new BasisCameraSettingsRig())
            {
                rig.Camera.ApplyCameraMode(BasisCameraMode.Disposable);
                Assert.That(rig.CaptureCamera.sensorSize.x, Is.EqualTo(36f).Within(0.01f),
                    "A disposable shoots a full 35mm frame.");

                rig.Camera.ApplyCameraMode(BasisCameraMode.Instant);
                Assert.That(rig.CaptureCamera.sensorSize.x, Is.EqualTo(rig.CaptureCamera.sensorSize.y).Within(0.01f),
                    "An instant frame is square.");

                rig.Camera.ApplyCameraMode(BasisCameraMode.Security);
                Assert.That(rig.CaptureCamera.sensorSize.x, Is.LessThan(10f),
                    "A ceiling camera is a small sensor behind a very short lens.");
            }
        }

        [Test]
        public void FittingASensor_DoesNotMoveTheFieldOfView()
        {
            using (var rig = new BasisCameraSettingsRig())
            {
                // On a physical camera the sensor, the focal length and the field of view are one
                // value seen three ways, so writing a sensor size without re-asserting the field of
                // view silently rewrites the framing of every kind.
                rig.Camera.ApplyCameraMode(BasisCameraMode.Disposable);
                float disposable = rig.CaptureCamera.fieldOfView;

                rig.Camera.ApplyCameraMode(BasisCameraMode.Security);

                Assert.That(rig.CaptureCamera.fieldOfView, Is.GreaterThan(disposable),
                    "A security camera is the widest body here, whatever its sensor measures.");
                Assert.That(rig.CaptureCamera.fieldOfView, Is.EqualTo(82f).Within(0.5f));
            }
        }

        // ---- The grading each kind is built out of --------------------------------------------

        [Test]
        public void TheGrainLadderIsPairedUpAndInOrder()
        {
            int[] values = Basis.BasisUI.HandHeldCamera.BasisHandHeldCameraPanelProvider.GrainTypeValuesForTest;
            string[] keys = Basis.BasisUI.HandHeldCamera.BasisHandHeldCameraPanelProvider.GrainTypeKeysForTest;

            Assert.That(values.Length, Is.EqualTo(keys.Length),
                "The dropdown reads both tables at one index; a short one throws on the last row.");

            for (int Index = 1; Index < values.Length; Index++)
            {
                Assert.That(values[Index], Is.GreaterThan(values[Index - 1]),
                    "The ladder runs fine to coarse, and the labels say so.");
            }
        }

        [Test]
        public void PickingAPlacementModeAfterAKind_HandsThePictureBack()
        {
            // Without this a camera kind is a one-way door. The four placement modes write no
            // grading of their own, so picking Photo would leave the grain, the halation, the split
            // toning and the lifted blacks exactly where the disposable put them — and the panel
            // would call the result Photo, because Photo has no opinion about any of them.
            using (var rig = new BasisCameraSettingsRig())
            {
                rig.Camera.ApplyCameraMode(BasisCameraMode.Disposable);
                rig.Camera.ApplyCameraMode(BasisCameraMode.Photo);

                var shipped = new BasisHandHeldCameraUI.CameraSettings();

                Assert.That(rig.FilmGrain.intensity.value, Is.EqualTo(shipped.filmGrain).Within(0.001f));
                Assert.That(rig.FilmGrain.active, Is.False);
                Assert.That(rig.Vignette.intensity.value, Is.EqualTo(shipped.vignette).Within(0.001f));
                Assert.That(rig.LiftGammaGain.lift.value.w, Is.EqualTo(shipped.filmLift).Within(0.001f));
                Assert.That(rig.SplitToning.active, Is.False);
                Assert.That(rig.Bloom.tint.value, Is.EqualTo(shipped.bloomTint));
                Assert.That(rig.ChromaticAberration.intensity.value, Is.EqualTo(shipped.chromaticAberration).Within(0.001f));
                Assert.That(rig.WhiteBalance.temperature.value, Is.EqualTo(shipped.whiteBalanceTemperature).Within(0.001f));
                Assert.That(rig.Camera.CameraMode, Is.EqualTo(BasisCameraMode.Photo));
            }
        }

        [Test]
        public void EveryFilmBodyLiftsItsBlacks()
        {
            using (var rig = new BasisCameraSettingsRig())
            {
                rig.Camera.ApplyCameraMode(BasisCameraMode.Photo);
                Assert.That(rig.LiftGammaGain.lift.value.w, Is.Zero,
                    "An ordinary camera has a true black in it.");

                rig.Camera.ApplyCameraMode(BasisCameraMode.Disposable);
                float disposable = rig.LiftGammaGain.lift.value.w;

                rig.Camera.ApplyCameraMode(BasisCameraMode.Instant);
                float instant = rig.LiftGammaGain.lift.value.w;

                Assert.That(disposable, Is.GreaterThan(0f));
                Assert.That(instant, Is.GreaterThan(disposable),
                    "An instant print has the least black of anything here — it is the whole aesthetic.");
            }
        }

        [Test]
        public void TheLiftIsNeutral_SoItCannotFightTheSplitToningAboveIt()
        {
            using (var rig = new BasisCameraSettingsRig())
            {
                rig.Camera.ApplyCameraMode(BasisCameraMode.Instant);

                Vector4 lift = rig.LiftGammaGain.lift.value;

                // URP subtracts the colour half's own luminance before adding w, so three equal
                // channels cancel exactly and what reaches the shader is a flat offset. Unequal
                // channels here would put a cast in the shadows that nothing on the panel explains.
                Assert.That(lift.x, Is.EqualTo(lift.y).Within(0.0001f));
                Assert.That(lift.y, Is.EqualTo(lift.z).Within(0.0001f));
            }
        }

        [Test]
        public void AFilmBodySplitsItsColourAndAPlainOneDoesNot()
        {
            using (var rig = new BasisCameraSettingsRig())
            {
                rig.Camera.ApplyCameraMode(BasisCameraMode.Photo);
                Assert.That(rig.SplitToning.active, Is.False,
                    "Neutral is grey at both ends, and a camera with no opinion has to reach it.");

                rig.Camera.ApplyCameraMode(BasisCameraMode.Instant);

                Assert.That(rig.SplitToning.active, Is.True);

                // Green in the shade and pink in the highlights is the shift every 600 pack has, and
                // the one thing that tells an instant print from a warm faded photograph.
                Color shadows = rig.SplitToning.shadows.value;
                Color highlights = rig.SplitToning.highlights.value;
                Assert.That(shadows.g, Is.GreaterThan(shadows.r), "Instant film puts green in the shade.");
                Assert.That(highlights.r, Is.GreaterThan(highlights.g), "and pink in the highlights.");
            }
        }

        [Test]
        public void HalationIsRedAndOnlyOnTheFilmBodies()
        {
            using (var rig = new BasisCameraSettingsRig())
            {
                rig.Camera.ApplyCameraMode(BasisCameraMode.Disposable);
                Color halation = rig.Bloom.tint.value;

                Assert.That(halation.r, Is.GreaterThan(halation.b),
                    "Halation is the red end getting through the anti-halation layer, so it is warm.");

                rig.Camera.ApplyCameraMode(BasisCameraMode.Camcorder);
                Color video = rig.Bloom.tint.value;
                Assert.That(video.b, Is.GreaterThan(video.r),
                    "A sensor's bloom has no halation in it at all.");
            }
        }

        [Test]
        public void EachKindPicksItsOwnGrain()
        {
            using (var rig = new BasisCameraSettingsRig())
            {
                rig.Camera.ApplyCameraMode(BasisCameraMode.Disposable);
                var disposable = rig.FilmGrain.type.value;
                float disposableResponse = rig.FilmGrain.response.value;

                rig.Camera.ApplyCameraMode(BasisCameraMode.Camcorder);

                Assert.That(rig.FilmGrain.type.value, Is.Not.EqualTo(disposable),
                    "Sensor noise and film grain are not the same size.");
                Assert.That(rig.FilmGrain.response.value, Is.LessThan(disposableResponse),
                    "Noise from an amplifier lies evenly; grain from an emulsion hides in the shadows.");
            }
        }

        // ---- The stamp ----------------------------------------------------------------------

        [Test]
        public void OnlyABodyThatStampsComposesText()
        {
            DateTime when = new DateTime(2026, 8, 21, 14, 32, 9);

            Assert.That(BasisCameraStampPainter.Compose(BasisCameraStamp.None, when), Is.Null);
            Assert.That(BasisCameraStampPainter.Compose(BasisCameraStamp.Date, when), Is.EqualTo("'26 08 21"));
            Assert.That(BasisCameraStampPainter.Compose(BasisCameraStamp.Timecode, when),
                Does.Contain("14:32:09"));
        }

        [Test]
        public void EveryGlyphLandsInsideThePicture()
        {
            var rects = new List<RectInt>();
            const int width = 1296;
            const int height = 864;

            Assert.That(BasisCameraStampPainter.BuildGlyphs("'26 08 21", width, height, rects), Is.True);
            Assert.That(rects, Is.Not.Empty);

            foreach (RectInt rect in rects)
            {
                Assert.That(rect.xMin, Is.GreaterThanOrEqualTo(0));
                Assert.That(rect.yMin, Is.GreaterThanOrEqualTo(0));
                Assert.That(rect.xMax, Is.LessThanOrEqualTo(width));
                Assert.That(rect.yMax, Is.LessThanOrEqualTo(height));
                Assert.That(rect.width, Is.GreaterThan(0));
                Assert.That(rect.height, Is.GreaterThan(0));
            }
        }

        [Test]
        public void TheStampSitsInTheBottomRightCorner()
        {
            var rects = new List<RectInt>();
            const int width = 1296;
            const int height = 864;
            BasisCameraStampPainter.BuildGlyphs("'26 08 21", width, height, rects);

            int right = 0;
            int top = 0;
            foreach (RectInt rect in rects)
            {
                right = Mathf.Max(right, rect.xMax);
                top = Mathf.Max(top, rect.yMax);
            }

            // Bottom-left origin, matching raw texture rows — so "low y" is the bottom of the shot.
            Assert.That(right, Is.GreaterThan(width / 2), "The stamp belongs on the right.");
            Assert.That(top, Is.LessThan(height / 4), "The stamp belongs at the bottom.");
        }

        [Test]
        public void APictureTooSmallToStampIsLeftAlone()
        {
            var rects = new List<RectInt>();

            // A stamp nobody can read is worse than none: it is a row of orange smudges over
            // somebody's photograph.
            Assert.That(BasisCameraStampPainter.BuildGlyphs("'26 08 21", 64, 48, rects), Is.False);
            Assert.That(rects, Is.Empty);
        }

        [Test]
        public void NothingToStampProducesNothing()
        {
            var rects = new List<RectInt>();

            Assert.That(BasisCameraStampPainter.BuildGlyphs(null, 1280, 720, rects), Is.False);
            Assert.That(BasisCameraStampPainter.BuildGlyphs("", 1280, 720, rects), Is.False);
            Assert.That(rects, Is.Empty);
        }

        [Test]
        public void ASpaceDrawsNothingButStillTakesItsPlace()
        {
            var one = new List<RectInt>();
            var spaced = new List<RectInt>();

            BasisCameraStampPainter.BuildGlyphs("11", 1280, 720, one);
            BasisCameraStampPainter.BuildGlyphs("1 1", 1280, 720, spaced);

            Assert.That(spaced.Count, Is.EqualTo(one.Count), "A space is not a glyph.");

            // Same glyph count, but pushed left by the cell the space took, so the stamp still ends
            // at the same margin.
            int oneLeft = int.MaxValue;
            int spacedLeft = int.MaxValue;
            foreach (RectInt rect in one) oneLeft = Mathf.Min(oneLeft, rect.xMin);
            foreach (RectInt rect in spaced) spacedLeft = Mathf.Min(spacedLeft, rect.xMin);

            Assert.That(spacedLeft, Is.LessThan(oneLeft));
        }

        [Test]
        public void AOneIsTwoSegmentsAndAnEightIsSeven()
        {
            var one = new List<RectInt>();
            var eight = new List<RectInt>();

            BasisCameraStampPainter.BuildGlyphs("1", 1280, 720, one);
            BasisCameraStampPainter.BuildGlyphs("8", 1280, 720, eight);

            Assert.That(one.Count, Is.EqualTo(2));
            Assert.That(eight.Count, Is.EqualTo(7));
        }

        [Test]
        public void ALongStampOnANarrowFrameIsShrunkRatherThanCropped()
        {
            var rects = new List<RectInt>();
            const int width = 640;

            // The tape and security bodies shoot 640 wide, and a clock that ran off the edge would
            // lose its seconds first — which is the half anyone reading a timecode wants.
            string timecode = BasisCameraStampPainter.Compose(
                BasisCameraStamp.Timecode, new DateTime(2026, 8, 21, 14, 32, 9));

            Assert.That(BasisCameraStampPainter.BuildGlyphs(timecode, width, 480, rects), Is.True);

            foreach (RectInt rect in rects)
            {
                Assert.That(rect.xMax, Is.LessThanOrEqualTo(width));
                Assert.That(rect.xMin, Is.GreaterThanOrEqualTo(0));
            }
        }
    }
}
