using System;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityCamera = UnityEngine.Camera;
using CameraSettings = BasisHandHeldCameraUI.CameraSettings;

namespace Basis.Tests.Camera
{
    /// <summary>
    /// Auto brightness is a closed loop: it meters a frame that already carries its own correction.
    /// That is what makes it robust to the tonemapper, and also what makes it easy to build one that
    /// integrates instead of converging. These pin the arithmetic that decides which of the two it
    /// is, and the weighting that decides what "the picture" even means.
    /// </summary>
    public class BasisHandHeldCameraAutoBrightnessTests
    {
        private GameObject _go;
        private GameObject _captureGo;
        private BasisHandHeldCamera _camera;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("HandHeldCameraUnderTest");
            _camera = _go.AddComponent<BasisHandHeldCamera>();

            _captureGo = new GameObject("CaptureCamera");
            _camera.captureCamera = _captureGo.AddComponent<UnityCamera>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null) UnityEngine.Object.DestroyImmediate(_go);
            if (_captureGo != null) UnityEngine.Object.DestroyImmediate(_captureGo);
        }

        // ---------- The loop ----------

        [Test]
        public void ADarkFrameAsksForMoreExposureAndABrightOneForLess()
        {
            float brighten = BasisCameraMetering.GoalStops(0f, 0.1f, 0.4f, 6f);
            float darken = BasisCameraMetering.GoalStops(0f, 0.8f, 0.4f, 6f);

            Assert.That(brighten, Is.GreaterThan(0f));
            Assert.That(darken, Is.LessThan(0f));
        }

        [Test]
        public void HalfTheTargetBrightnessIsExactlyOneStopDown()
        {
            Assert.That(BasisCameraMetering.GoalStops(0f, 0.2f, 0.4f, 6f), Is.EqualTo(1f).Within(1e-4f));
            Assert.That(BasisCameraMetering.GoalStops(0f, 0.8f, 0.4f, 6f), Is.EqualTo(-1f).Within(1e-4f));
        }

        [Test]
        public void AFrameAlreadyOnTargetAsksForNoChange()
        {
            // Whatever exposure got it there is the exposure it keeps. A loop that drifted here
            // would breathe on a still shot.
            Assert.That(BasisCameraMetering.GoalStops(1.75f, 0.45f, 0.45f, 6f), Is.EqualTo(1.75f).Within(1e-4f));
        }

        [Test]
        public void TheErrorIsMeasuredFromTheExposureTheFrameWasShotAt()
        {
            // This is the whole difference between a proportional loop and an integrating one. The
            // reading describes a frame rendered at some exposure; the correction belongs on that
            // value, not on wherever the approach has since travelled.
            float fromZero = BasisCameraMetering.GoalStops(0f, 0.2f, 0.4f, 6f);
            float fromTwo = BasisCameraMetering.GoalStops(2f, 0.2f, 0.4f, 6f);

            Assert.That(fromTwo - fromZero, Is.EqualTo(2f).Within(1e-4f),
                "Two identical readings taken at exposures two stops apart must ask for goals two stops apart.");
        }

        [Test]
        public void TheLoopConvergesRatherThanRunningAway()
        {
            // Simulates the real feedback path: the picture brightens in proportion to the exposure
            // applied, and each reading is taken at the exposure of the frame it describes.
            const float sceneAtZeroStops = 0.05f;
            const float target = 0.45f;

            float stops = 0f;
            for (int Index = 0; Index < 40; Index++)
            {
                float measured = Mathf.Clamp(sceneAtZeroStops * Mathf.Pow(2f, stops), 0f, 1f);
                stops = BasisCameraMetering.GoalStops(stops, measured, target, 6f);
            }

            float settled = Mathf.Clamp(sceneAtZeroStops * Mathf.Pow(2f, stops), 0f, 1f);
            Assert.That(settled, Is.EqualTo(target).Within(0.01f));
        }

        [Test]
        public void TheMeterCannotWanderPastItsRange()
        {
            Assert.That(BasisCameraMetering.GoalStops(0f, 0.001f, 0.9f, 2f), Is.EqualTo(2f).Within(1e-4f));
            Assert.That(BasisCameraMetering.GoalStops(0f, 1f, 0.05f, 2f), Is.EqualTo(-2f).Within(1e-4f));
        }

        [Test]
        public void APitchBlackFrameDoesNotAskForInfiniteExposure()
        {
            float stops = BasisCameraMetering.GoalStops(0f, 0f, 0.45f, 6f);

            Assert.That(float.IsNaN(stops), Is.False);
            Assert.That(float.IsInfinity(stops), Is.False);
            Assert.That(stops, Is.EqualTo(6f).Within(1e-4f), "It should ask for everything it is allowed, and no more.");
        }

        // ---------- Metering ----------

        [Test]
        public void EveryMeteringModeCountsTheCentreOfTheFrame()
        {
            foreach (BasisCameraMeteringMode mode in Enum.GetValues(typeof(BasisCameraMeteringMode)))
            {
                Assert.That(BasisCameraMetering.Weight(mode, 0.5f, 0.5f), Is.GreaterThan(0f),
                    $"{mode} ignores the middle of the picture, so a centred subject would not be metered at all.");
            }
        }

        [Test]
        public void AverageMeteringWeighsTheWholeFrameEqually()
        {
            Assert.That(BasisCameraMetering.Weight(BasisCameraMeteringMode.Average, 0.5f, 0.5f),
                Is.EqualTo(BasisCameraMetering.Weight(BasisCameraMeteringMode.Average, 0.02f, 0.98f)));
        }

        [Test]
        public void CentreWeightedFallsOffTowardsTheEdges()
        {
            float centre = BasisCameraMetering.Weight(BasisCameraMeteringMode.CentreWeighted, 0.5f, 0.5f);
            float midway = BasisCameraMetering.Weight(BasisCameraMeteringMode.CentreWeighted, 0.5f, 0.75f);
            float corner = BasisCameraMetering.Weight(BasisCameraMeteringMode.CentreWeighted, 0f, 0f);

            Assert.That(centre, Is.GreaterThan(midway));
            Assert.That(midway, Is.GreaterThan(corner));
            Assert.That(corner, Is.GreaterThan(0f), "A corner still counts for something, or a bright sky would be ignored outright.");
        }

        [Test]
        public void SpotMeteringIgnoresEverythingButTheMiddle()
        {
            Assert.That(BasisCameraMetering.Weight(BasisCameraMeteringMode.Spot, 0.5f, 0.5f), Is.EqualTo(1f));
            Assert.That(BasisCameraMetering.Weight(BasisCameraMeteringMode.Spot, 0.5f, 0.9f), Is.Zero);
            Assert.That(BasisCameraMetering.Weight(BasisCameraMeteringMode.Spot, 0.05f, 0.05f), Is.Zero);
        }

        [Test]
        public void AFlatFrameMetersToItsOwnBrightnessUnderEveryMode()
        {
            // Whatever the weighting, a picture of one shade reads as that shade — the weights are
            // normalised by their own sum rather than assumed to average to one.
            using (var pixels = FlatFrame(8, 8, 128))
            {
                foreach (BasisCameraMeteringMode mode in Enum.GetValues(typeof(BasisCameraMeteringMode)))
                {
                    float measured = BasisCameraMetering.Measure(pixels, 8, 8, mode);
                    Assert.That(measured, Is.EqualTo(128f / 255f).Within(1e-3f), $"{mode} did not read a flat frame flat.");
                }
            }
        }

        [Test]
        public void SpotMeteringIgnoresABrightSurroundThatAverageMeteringDoesNot()
        {
            // The reason to have the mode at all: a subject against a blown-out background.
            var pixels = new NativeArray<Color32>(32 * 32, Allocator.Temp);
            try
            {
                for (int y = 0; y < 32; y++)
                {
                    for (int x = 0; x < 32; x++)
                    {
                        float radius = Mathf.Sqrt(Mathf.Pow((x + 0.5f) / 32f - 0.5f, 2f) + Mathf.Pow((y + 0.5f) / 32f - 0.5f, 2f)) * 2f;
                        byte level = radius <= 0.2f ? (byte)64 : (byte)255;
                        pixels[y * 32 + x] = new Color32(level, level, level, 255);
                    }
                }

                float spot = BasisCameraMetering.Measure(pixels, 32, 32, BasisCameraMeteringMode.Spot);
                float average = BasisCameraMetering.Measure(pixels, 32, 32, BasisCameraMeteringMode.Average);

                Assert.That(spot, Is.EqualTo(64f / 255f).Within(0.02f), "Spot read the surround it is supposed to exclude.");
                Assert.That(average, Is.GreaterThan(spot + 0.3f));
            }
            finally
            {
                pixels.Dispose();
            }
        }

        [Test]
        public void AnEmptyBufferReportsNoReadingRatherThanZeroBrightness()
        {
            // Zero would be a legitimate reading of a black frame, and would drive the exposure to
            // its limit. "No reading" has to be distinguishable from "very dark".
            Assert.That(BasisCameraMetering.Measure(default, 64, 64, BasisCameraMeteringMode.Average), Is.Negative);

            using (var pixels = FlatFrame(4, 4, 10))
            {
                Assert.That(BasisCameraMetering.Measure(pixels, 0, 0, BasisCameraMeteringMode.Average), Is.Negative);
            }
        }

        // ---------- Wiring ----------

        [Test]
        public void ItContributesNothingUntilItIsSwitchedOn()
        {
            Assert.That(_camera.autoBrightnessEnabled, Is.False);
            Assert.That(_camera.AutoBrightnessOffset, Is.Zero);
            Assert.That(_camera.HasMeasuredBrightness, Is.False, "Nothing has been metered yet, and a zero reading would look like one.");
        }

        [Test]
        public void SwitchingItOnStartsFromWhereTheExposureAlreadyIs()
        {
            // Not from the last room's reading: an offset carried over would visibly jump the
            // picture the moment the toggle is pressed, before the loop had read anything.
            _camera.SetAutoBrightnessEnabled(true);

            Assert.That(_camera.AutoBrightnessStops, Is.Zero);
            Assert.That(_camera.AutoBrightnessOffset, Is.Zero);
            Assert.That(_camera.HasMeasuredBrightness, Is.False);
        }

        [Test]
        public void EverySettingOfItIsClampedToTheSliderItComesFrom()
        {
            _camera.SetAutoBrightnessTarget(50f);
            Assert.That(_camera.autoBrightnessTarget, Is.EqualTo(BasisCameraMetering.MaxTarget));

            _camera.SetAutoBrightnessSpeed(-3f);
            Assert.That(_camera.autoBrightnessSpeed, Is.EqualTo(BasisCameraMetering.MinSpeed));

            _camera.SetAutoBrightnessRange(900f);
            Assert.That(_camera.autoBrightnessRange, Is.EqualTo(BasisCameraMetering.MaxRange));

            _camera.SetAutoBrightnessMetering(77);
            Assert.That(_camera.autoBrightnessMetering, Is.EqualTo((int)BasisCameraMeteringMode.CentreWeighted),
                "An index from a stale file must land on a real mode rather than metering nothing.");
        }

        [Test]
        public void TheResponseCeilingIsLowEnoughToSettleRatherThanHunt()
        {
            // The loop reads a frame that has already been shown, so its correction always arrives
            // late. Left uncapped, a fast response turns that lag into oscillation.
            Assert.That(BasisCameraMetering.MaxSpeed, Is.LessThanOrEqualTo(10f));
            Assert.That(BasisCameraMetering.DefaultSpeed, Is.LessThan(BasisCameraMetering.MaxSpeed));
        }

        [Test]
        public void AFreshCameraDoesNotStartMetering()
        {
            CameraSettings defaults = new CameraSettings();

            Assert.That(defaults.autoBrightness, Is.False);
            Assert.That(defaults.autoBrightnessTarget, Is.EqualTo(BasisCameraMetering.DefaultTarget).Within(1e-4f));
            Assert.That(defaults.autoBrightnessSpeed, Is.GreaterThan(0f));
            Assert.That(defaults.autoBrightnessRange, Is.GreaterThan(0f));
            Assert.That(defaults.autoBrightnessMetering, Is.EqualTo((int)BasisCameraMeteringMode.CentreWeighted));
        }

        [Test]
        public void EverySettingOfItSurvivesApplyThenCapture()
        {
            using (var rig = new BasisCameraSettingsRig())
            {
                CameraSettings original = BasisCameraSettingsRig.DistinctiveSettings();
                rig.UI.ApplySettingsForTest(original);

                Assert.That(rig.Camera.autoBrightnessEnabled, Is.EqualTo(original.autoBrightness));
                Assert.That(rig.Camera.autoBrightnessTarget, Is.EqualTo(original.autoBrightnessTarget).Within(1e-4f));
                Assert.That(rig.Camera.autoBrightnessSpeed, Is.EqualTo(original.autoBrightnessSpeed).Within(1e-4f));
                Assert.That(rig.Camera.autoBrightnessMetering, Is.EqualTo(original.autoBrightnessMetering));
                Assert.That(rig.Camera.autoBrightnessRange, Is.EqualTo(original.autoBrightnessRange).Within(1e-4f));

                CameraSettings captured = rig.UI.CreateCurrentCameraSettingsForTest();

                Assert.That(captured.autoBrightness, Is.EqualTo(original.autoBrightness));
                Assert.That(captured.autoBrightnessTarget, Is.EqualTo(original.autoBrightnessTarget).Within(1e-4f));
                Assert.That(captured.autoBrightnessSpeed, Is.EqualTo(original.autoBrightnessSpeed).Within(1e-4f));
                Assert.That(captured.autoBrightnessMetering, Is.EqualTo(original.autoBrightnessMetering));
                Assert.That(captured.autoBrightnessRange, Is.EqualTo(original.autoBrightnessRange).Within(1e-4f));
            }
        }

        [Test]
        public void TheManualExposureControlKeepsWorkingAsCompensation()
        {
            // Two things write post exposure and they are summed. Whichever wrote last, both must
            // still be in the value — the bug this guards against is one silently wiping the other.
            using (var rig = new BasisCameraSettingsRig())
            {
                rig.UI.ChangeExposureCompensation(BasisHandHeldCameraUI.ExposureStopCount - 1);
                float compensation = BasisHandHeldCameraUI.ExposureStopAt(BasisHandHeldCameraUI.ExposureStopCount - 1);

                Assert.That(rig.ColorAdjustments.postExposure.value, Is.EqualTo(compensation).Within(1e-4f));

                rig.Camera.SetAutoBrightnessEnabled(true);
                Assert.That(rig.ColorAdjustments.postExposure.value, Is.EqualTo(compensation).Within(1e-4f),
                    "Switching metering on before it has read anything must not move the exposure.");

                rig.UI.ChangeExposureCompensation(0);
                Assert.That(rig.ColorAdjustments.postExposure.value,
                    Is.EqualTo(BasisHandHeldCameraUI.ExposureStopAt(0) + rig.Camera.AutoBrightnessOffset).Within(1e-4f));
            }
        }

        private static NativeArray<Color32> FlatFrame(int width, int height, byte level)
        {
            NativeArray<Color32> pixels = new NativeArray<Color32>(width * height, Allocator.Temp);
            for (int Index = 0; Index < pixels.Length; Index++)
            {
                pixels[Index] = new Color32(level, level, level, 255);
            }
            return pixels;
        }
    }
}
