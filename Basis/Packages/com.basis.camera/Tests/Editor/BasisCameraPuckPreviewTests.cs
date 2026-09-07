using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Camera
{
    /// <summary>
    /// The look-at preview: the screen a detached camera turned back toward you puts up in front of
    /// itself, and takes down when it is turned away.
    ///
    /// <para>
    /// The placement and the feed need a scene, a head and a render texture, but the two decisions
    /// that actually make or break the feature do not — whether it should be up at all, and how big
    /// it is drawn — so both are pure functions and both are exercised here.
    /// </para>
    /// </summary>
    public class BasisCameraPuckPreviewTests
    {
        private BasisCameraSettingsRig _rig;

        private const float ShowAngle = 40f;
        private const float HideAngle = 55f;

        [SetUp]
        public void SetUp() => _rig = new BasisCameraSettingsRig();

        [TearDown]
        public void TearDown() => _rig?.Dispose();

        /// <summary>A camera at the origin pointed along +Z, and a head placed at a given angle off that axis.</summary>
        private static Vector3 HeadAt(float degrees, float distance = 2f) =>
            Quaternion.Euler(0f, degrees, 0f) * Vector3.forward * distance;

        private static bool ShouldShow(float degrees, bool showing) =>
            BasisHandHeldCamera.PuckPreviewShouldShow(
                Vector3.zero, Quaternion.identity, HeadAt(degrees), showing, ShowAngle, HideAngle);

        [Test]
        public void ItIsOffOnAFreshCamera()
        {
            // Spawning a screen in front of every camera anyone puts down is not a default anybody
            // asked for — it is a thing you turn on.
            Assert.That(_rig.Camera.puckLookAtPreview, Is.False);
            Assert.That(new BasisHandHeldCameraUI.CameraSettings().puckLookAtPreview, Is.False);
            Assert.That(_rig.Camera.IsPuckPreviewVisible, Is.False);
        }

        [Test]
        public void ACameraTurnedOnYouShowsIt_AndOneTurnedAwayDoesNot()
        {
            Assert.That(ShouldShow(0f, showing: false), Is.True, "pointed straight at you");
            Assert.That(ShouldShow(180f, showing: false), Is.False, "pointed straight away from you");
            Assert.That(ShouldShow(90f, showing: true), Is.False, "side on, with the preview already up");
        }

        [Test]
        public void TurningItAwayTakesItDownAgain()
        {
            // The whole ask: it comes up when the puck is turned on you and goes when it is turned
            // back around, without anyone touching the setting in between.
            bool showing = ShouldShow(10f, showing: false);
            Assert.That(showing, Is.True);

            showing = ShouldShow(170f, showing);
            Assert.That(showing, Is.False);
        }

        [Test]
        public void TheAngleItGoesAtIsWiderThanTheOneItComesUpAt()
        {
            // Without the band, a hand holding the puck near the boundary flickers the screen in
            // and out on the shake alone — and each flicker is a spawn and a destroy.
            const float Between = 0.5f * (ShowAngle + HideAngle);

            Assert.That(ShouldShow(Between, showing: false), Is.False,
                "Inside the band it should not come up on its own.");
            Assert.That(ShouldShow(Between, showing: true), Is.True,
                "Inside the band one already up should stay up.");
        }

        [Test]
        public void AHideAngleUnderTheShowAngleCannotStrandThePreviewOnScreen()
        {
            // Authored backwards, the band inverts into a dead zone: shown at 40 degrees, asked to
            // hide at 20, anything between the two both should and should not be up. Taking the
            // wider of the two makes the pair unorderable rather than contradictory.
            bool showing = BasisHandHeldCamera.PuckPreviewShouldShow(
                Vector3.zero, Quaternion.identity, HeadAt(10f), false, 40f, 20f);
            Assert.That(showing, Is.True);

            showing = BasisHandHeldCamera.PuckPreviewShouldShow(
                Vector3.zero, Quaternion.identity, HeadAt(30f), showing, 40f, 20f);
            Assert.That(showing, Is.True, "still within the angle it was shown at");

            showing = BasisHandHeldCamera.PuckPreviewShouldShow(
                Vector3.zero, Quaternion.identity, HeadAt(80f), showing, 40f, 20f);
            Assert.That(showing, Is.False, "past both angles it has to go");
        }

        [Test]
        public void AHeadInsideTheCameraCountsAsLookedAt()
        {
            // There is no direction to measure, and a camera you are standing in is as pointed at
            // you as it gets. A normalize on a zero vector would otherwise decide this by NaN.
            Assert.That(BasisHandHeldCamera.PuckPreviewShouldShow(
                Vector3.zero, Quaternion.identity, Vector3.zero, false, ShowAngle, HideAngle), Is.True);
        }

        [Test]
        public void ItNeverShrinksBelowTheSizeItWasAuthoredAt()
        {
            // Up close is the one case that is already readable; scaling down from there would make
            // it the hardest.
            Assert.That(BasisHandHeldCamera.PuckPreviewGrowth(0.1f, 1f, 6f), Is.EqualTo(1f));
            Assert.That(BasisHandHeldCamera.PuckPreviewGrowth(1f, 1f, 6f), Is.EqualTo(1f));
        }

        [Test]
        public void ItGrowsWithRangeToHoldItsApparentSize()
        {
            // Angular size is size over distance, so a screen three times as far away has to be
            // three times as wide to look the same.
            Assert.That(BasisHandHeldCamera.PuckPreviewGrowth(3f, 1f, 6f), Is.EqualTo(3f).Within(1e-4f));
        }

        [Test]
        public void TheGrowthIsCappedSoAFlownCameraCannotFillTheWorld()
        {
            // Held to the cap rather than tracked all the way out: a camera flown across the map
            // would otherwise be given a screen tens of metres wide, buried in whatever stood
            // between the two of you.
            Assert.That(BasisHandHeldCamera.PuckPreviewGrowth(500f, 1f, 6f), Is.EqualTo(6f));
            Assert.That(BasisHandHeldCamera.PuckPreviewGrowth(500f, 1f, 0f), Is.EqualTo(1f),
                "A cap under 1 would shrink the preview rather than hold it.");
        }

        [Test]
        public void AZeroReferenceDistanceLeavesItAtTheAuthoredSize()
        {
            // The reference distance is scaled by avatar height, so a rig that reports zero would
            // divide by it.
            Assert.That(BasisHandHeldCamera.PuckPreviewGrowth(5f, 0f, 6f), Is.EqualTo(1f));
        }

        [Test]
        public void ThePreviewLivesOnALayerNoCaptureCanPutBackInTheShot()
        {
            // It is parked out along the lens axis, square in front of the camera, so the layer is
            // the only thing keeping it out of the picture — the same bargain the puck makes.
            int marker = BasisHandHeldCamera.MarkerLayer;
            Assert.That(marker, Is.GreaterThanOrEqualTo(0), "This project no longer defines the OverlayUI layer.");
            Assert.That(BasisHandHeldCamera.IsCaptureLayerUserTogglable(marker), Is.False);
        }

        [Test]
        public void SwitchingItOffTakesDownAScreenAlreadyUp()
        {
            _rig.Camera.SetPuckLookAtPreview(true);
            Assert.That(_rig.Camera.puckLookAtPreview, Is.True);

            _rig.Camera.SetPuckLookAtPreview(false);

            Assert.That(_rig.Camera.puckLookAtPreview, Is.False);
            Assert.That(_rig.Camera.IsPuckPreviewVisible, Is.False);
        }

        [Test]
        public void TheSettingSurvivesApplyThenCapture()
        {
            BasisHandHeldCameraUI.CameraSettings settings = BasisCameraSettingsRig.DistinctiveSettings();
            settings.puckLookAtPreview = true;

            _rig.UI.ApplySettingsForTest(settings);

            Assert.That(_rig.Camera.puckLookAtPreview, Is.True);
            Assert.That(_rig.UI.CreateCurrentCameraSettingsForTest().puckLookAtPreview, Is.True);
        }

        [Test]
        public void AnOlderFileWithoutItLoadsAsOff()
        {
            // Off is the zero fill, which is what shipped before the setting existed — so no
            // version bump and no migration entry is owed for it.
            var legacy = JsonUtility.FromJson<BasisHandHeldCameraUI.CameraSettings>(
                "{\"settingsVersion\":7,\"detachedMarker\":1}");

            Assert.That(legacy.puckLookAtPreview, Is.False);
        }
    }
}
