using NUnit.Framework;
using UnityEngine;
using UnityCamera = UnityEngine.Camera;
using CameraSettings = BasisHandHeldCameraUI.CameraSettings;

namespace Basis.Tests.Camera
{
    /// <summary>
    /// The alignment grid is a viewfinder overlay, and the one thing it must never be is part of a
    /// shot. The design that guarantees that — a texture of its own, which only the viewfinder
    /// surfaces are pointed at — is what these pin, along with the tables the panel indexes into.
    ///
    /// Awake never runs here: outside play mode Unity does not invoke it for a plain MonoBehaviour,
    /// so AddComponent yields a camera with field initializers applied and no scene dependencies.
    /// </summary>
    public class BasisHandHeldCameraGridTests
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
            if (_go != null) Object.DestroyImmediate(_go);
            if (_captureGo != null) Object.DestroyImmediate(_captureGo);
        }

        [Test]
        public void ThereIsALabelForEveryPatternTheEnumNames()
        {
            // The dropdown resolves a selection by its position in the key table and hands that
            // index to the shader tables, so a table that has drifted draws a different grid or
            // reads off the end of the array.
            Assert.That(BasisCameraGrid.PatternKeys.Length,
                Is.EqualTo(System.Enum.GetValues(typeof(BasisCameraGridPattern)).Length));
        }

        [Test]
        public void ThirdsIsTheFirstPatternAndTheOneAFreshCameraUses()
        {
            Assert.That(BasisCameraGrid.PatternKeys[0], Is.EqualTo("camera.grid.thirds"));
            Assert.That((int)BasisCameraGridPattern.Thirds, Is.Zero);

            Assert.That(new CameraSettings().viewfinderGridPattern, Is.Zero);
            Assert.That(_camera.viewfinderGridPattern, Is.Zero);
        }

        [Test]
        public void APatternIndexFromAStaleFileCannotReadOffTheTable()
        {
            Assert.That(BasisCameraGrid.Pattern(-4), Is.Zero);
            Assert.That(BasisCameraGrid.Pattern(9999),
                Is.EqualTo(BasisCameraGrid.PatternKeys.Length - 1));

            _camera.SetViewfinderGridPattern(9999);
            Assert.That(_camera.viewfinderGridPattern,
                Is.EqualTo(BasisCameraGrid.PatternKeys.Length - 1));
        }

        [Test]
        public void TheOpacityIsClampedToTheSliderItComesFrom()
        {
            _camera.SetViewfinderGridOpacity(4f);
            Assert.That(_camera.viewfinderGridOpacity, Is.EqualTo(BasisCameraGrid.MaxOpacity));

            _camera.SetViewfinderGridOpacity(-4f);
            Assert.That(_camera.viewfinderGridOpacity, Is.EqualTo(BasisCameraGrid.MinOpacity));
        }

        [Test]
        public void NeitherEndOfTheOpacitySliderIsAnOffSwitch()
        {
            // The toggle owns whether the grid is drawn. A zero at one end would be a slider
            // position that reads as broken, and the toggle beside it already covers that case.
            Assert.That(BasisCameraGrid.MinOpacity, Is.GreaterThan(0f));
            Assert.That(BasisCameraGrid.MaxOpacity, Is.EqualTo(1f));
            Assert.That(BasisCameraGrid.DefaultOpacity,
                Is.InRange(BasisCameraGrid.MinOpacity, BasisCameraGrid.MaxOpacity));
        }

        [Test]
        public void TheLinesKeepTheirWeightAsTheFeedResolutionClimbs()
        {
            // A line measured in pixels would halve in apparent width every time the viewfinder
            // gained resolution, and disappear on a 4K feed shown on a preview this small.
            float small = BasisCameraGrid.LineThickness(480);
            float standard = BasisCameraGrid.LineThickness(1080);
            float large = BasisCameraGrid.LineThickness(2160);

            Assert.That(small, Is.GreaterThanOrEqualTo(1f), "A line thinner than a pixel is not drawn at all.");
            Assert.That(standard, Is.GreaterThan(small));
            Assert.That(large, Is.GreaterThan(standard));
        }

        [Test]
        public void AnAbsurdFeedHeightStillGivesADrawableLine()
        {
            Assert.That(BasisCameraGrid.LineThickness(0), Is.EqualTo(1f));
            Assert.That(BasisCameraGrid.LineThickness(100000), Is.LessThanOrEqualTo(6f));
        }

        [Test]
        public void SwitchingItOnDoesNotByItselfPutTheViewfinderOnTheOverlay()
        {
            // The overlay only becomes the viewfinder's feed once a frame has been drawn into it.
            // Claiming it earlier would show one frame of black on the prop and the desktop output at
            // once.
            _camera.SetViewfinderGridEnabled(true);

            Assert.That(_camera.IsViewfinderGridLive, Is.False);
            Assert.That(_camera.ViewfinderTexture, Is.EqualTo(_camera.PreviewTexture));
        }

        [Test]
        public void TheViewfinderFallsBackToTheFeedWhenTheGridIsOff()
        {
            _camera.SetViewfinderGridEnabled(false);
            Assert.That(_camera.ViewfinderTexture, Is.EqualTo(_camera.PreviewTexture));
        }

        [Test]
        public void EverySettingOfItSurvivesApplyThenCapture()
        {
            using (var rig = new BasisCameraSettingsRig())
            {
                CameraSettings original = BasisCameraSettingsRig.DistinctiveSettings();
                rig.UI.ApplySettingsForTest(original);

                Assert.That(rig.Camera.viewfinderGridEnabled, Is.EqualTo(original.viewfinderGrid));
                Assert.That(rig.Camera.viewfinderGridPattern, Is.EqualTo(original.viewfinderGridPattern));
                Assert.That(rig.Camera.viewfinderGridOpacity, Is.EqualTo(original.viewfinderGridOpacity).Within(1e-4f));

                CameraSettings captured = rig.UI.CreateCurrentCameraSettingsForTest();

                Assert.That(captured.viewfinderGrid, Is.EqualTo(original.viewfinderGrid));
                Assert.That(captured.viewfinderGridPattern, Is.EqualTo(original.viewfinderGridPattern));
                Assert.That(captured.viewfinderGridOpacity, Is.EqualTo(original.viewfinderGridOpacity).Within(1e-4f));
            }
        }

        [Test]
        public void AFreshCameraDoesNotStartDrawingLinesOverThePicture()
        {
            CameraSettings defaults = new CameraSettings();

            Assert.That(defaults.viewfinderGrid, Is.False);
            Assert.That(defaults.viewfinderGridOpacity,
                Is.EqualTo(BasisCameraGrid.DefaultOpacity).Within(1e-4f));
            Assert.That(defaults.viewfinderGridOpacity, Is.GreaterThan(0f),
                "A saved opacity of zero would come back as an invisible grid rather than a usable one.");
        }
    }
}
