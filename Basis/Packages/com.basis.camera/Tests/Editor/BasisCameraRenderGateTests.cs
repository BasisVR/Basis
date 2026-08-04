using Basis.BasisUI;
using NUnit.Framework;

namespace Basis.Tests.Camera
{
    /// <summary>
    /// The gate that decides whether the capture camera renders at all.
    ///
    /// <para>
    /// The camera is culled when the prop's own viewfinder mesh is off screen, which is the right
    /// call while the viewfinder is the only thing showing the feed — and the wrong one the moment
    /// anything else is. Everything drawing that render texture keeps the last frame it was given,
    /// so a gate that misses a viewer does not go black or throw: it quietly freezes on a still
    /// image that looks like a live one. That is what these tests are here to catch.
    /// </para>
    /// </summary>
    public class BasisCameraRenderGateTests
    {
        private BasisCameraSettingsRig _rig;
        private bool _limitRate;

        [SetUp]
        public void SetUp()
        {
            _rig = new BasisCameraSettingsRig();

            // The render-rate cap is a global setting, and a run with it switched on skips frames
            // on purpose — which every assertion below would read as "stopped rendering".
            _limitRate = BasisSettingsDefaults.LimitHandHeldCameraRate.RawValue;
            BasisSettingsDefaults.LimitHandHeldCameraRate.SetValueWithoutNotify(false);
        }

        [TearDown]
        public void TearDown()
        {
            BasisSettingsDefaults.LimitHandHeldCameraRate.SetValueWithoutNotify(_limitRate);
            _rig?.Dispose();
        }

        [Test]
        public void AVisiblePropRenders()
        {
            _rig.Camera.SetRendererVisibleForTest(true);
            Assert.IsTrue(_rig.CaptureCamera.enabled);
        }

        [Test]
        public void APropOutOfViewStopsRendering()
        {
            _rig.Camera.SetRendererVisibleForTest(false);
            Assert.IsFalse(_rig.CaptureCamera.enabled,
                "With nothing but the viewfinder showing the feed, an off-screen prop should cost nothing.");
        }

        [Test]
        public void ThePanelPreviewKeepsAnOffScreenCameraRendering()
        {
            _rig.Camera.SetRendererVisibleForTest(false);

            _rig.Camera.SetPanelPreviewActive(true);

            Assert.IsTrue(_rig.CaptureCamera.enabled,
                "The settings panel draws the same feed from wherever the menu is, so the prop being off screen says nothing about whether anyone is watching.");
        }

        [Test]
        public void ThePanelPreviewSurvivesThePropLeavingView()
        {
            // The order the bug arrived in: the panel is opened on a camera in your hands, and
            // then you look away — or it flies off, or follows you from behind.
            _rig.Camera.SetPanelPreviewActive(true);
            _rig.Camera.SetRendererVisibleForTest(true);

            _rig.Camera.SetRendererVisibleForTest(false);

            Assert.IsTrue(_rig.CaptureCamera.enabled);
        }

        [Test]
        public void ClosingThePanelLetsAnOffScreenCameraStopAgain()
        {
            _rig.Camera.SetRendererVisibleForTest(false);
            _rig.Camera.SetPanelPreviewActive(true);

            _rig.Camera.SetPanelPreviewActive(false);

            Assert.IsFalse(_rig.CaptureCamera.enabled,
                "A request that outlives the panel would leave the camera rendering for a preview nobody can see.");
        }

        [Test]
        public void AStreamKeepsAnOffScreenCameraRendering()
        {
            // Pre-existing guarantee: a receiver on the other end of a stream has no idea whether
            // the prop is on screen, and a frozen stream is indistinguishable from a still shot.
            _rig.Camera.IsVideoOutputActive = true;
            try
            {
                _rig.Camera.SetRendererVisibleForTest(false);
                Assert.IsTrue(_rig.CaptureCamera.enabled);
            }
            finally
            {
                _rig.Camera.IsVideoOutputActive = false;
            }
        }
    }
}
