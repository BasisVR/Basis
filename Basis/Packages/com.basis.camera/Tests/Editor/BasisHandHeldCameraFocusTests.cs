using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityCamera = UnityEngine.Camera;

namespace Basis.Tests.Camera
{
    /// <summary>
    /// The focus distance is written from three places — the slider, click-to-focus and auto-focus —
    /// and the blur solver reads it as depth along the view axis, clamped clear of the lens focal
    /// length. These pin that shared contract; getting it wrong blurs the subject the shot is about.
    ///
    /// Awake never runs here: outside play mode Unity does not invoke it for a plain MonoBehaviour,
    /// so AddComponent yields a camera with field initializers applied and no scene dependencies.
    /// </summary>
    public class BasisHandHeldCameraFocusTests
    {
        private GameObject _go;
        private GameObject _captureGo;
        private BasisHandHeldCamera _camera;
        private DepthOfField _dof;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("HandHeldCameraUnderTest");
            _camera = _go.AddComponent<BasisHandHeldCamera>();

            _captureGo = new GameObject("CaptureCamera");
            _camera.captureCamera = _captureGo.AddComponent<UnityCamera>();
            _captureGo.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            _dof = ScriptableObject.CreateInstance<DepthOfField>();
            _camera.MetaData.depthOfField = _dof;
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null) Object.DestroyImmediate(_go);
            if (_captureGo != null) Object.DestroyImmediate(_captureGo);
            if (_dof != null) Object.DestroyImmediate(_dof);
        }

        [Test]
        public void FocusDepth_IsMeasuredAlongTheViewAxis_NotStraightLine()
        {
            // The shader compares the focus distance against LinearEyeDepth, so an off-centre
            // subject focused by straight-line distance lands 1/cos(angle) too far away.
            Vector3 offAxis = new Vector3(3f, 0f, 4f);

            Assert.That(_camera.TryGetFocusDepth(offAxis, out float depth), Is.True);
            Assert.That(depth, Is.EqualTo(4f).Within(1e-4f),
                "Depth is the view-axis component; the straight-line distance here is 5.");
        }

        [Test]
        public void FocusDepth_FollowsCameraRotation()
        {
            _captureGo.transform.rotation = Quaternion.Euler(0f, 90f, 0f);

            Assert.That(_camera.TryGetFocusDepth(new Vector3(4f, 0f, 3f), out float depth), Is.True);
            Assert.That(depth, Is.EqualTo(4f).Within(1e-4f));
        }

        [Test]
        public void FocusDepth_RejectsAnythingBehindTheLens()
        {
            Assert.That(_camera.TryGetFocusDepth(new Vector3(0f, 0f, -2f), out _), Is.False,
                "A subject behind the camera must leave focus where it is, not drive it negative.");
        }

        [Test]
        public void FocusDepth_RejectsAnythingInsideTheMinimumFocusDistance()
        {
            _dof.focalLength.value = 300f;

            Assert.That(_camera.TryGetFocusDepth(new Vector3(0f, 0f, 0.2f), out _), Is.False);
        }

        [Test]
        public void MinimumFocusDistance_ClearsTheLensFocalLength()
        {
            _dof.focalLength.value = 300f;

            Assert.That(_camera.MinimumFocusDistance, Is.GreaterThan(0.3f),
                "Bokeh's circle of confusion divides by (focusDistance - focalLength).");
        }

        [Test]
        public void ApplyFocusDistance_ClampsClearOfTheLensFocalLength()
        {
            _dof.focalLength.value = 300f;

            _camera.ApplyFocusDistance(0.1f);

            Assert.That(_dof.focusDistance.value, Is.EqualTo(_camera.MinimumFocusDistance).Within(1e-4f));
            Assert.That(MaxCircleOfConfusion(_dof), Is.GreaterThan(0f),
                "A focus distance at or inside the focal length inverts the CoC and swaps near and far blur.");
        }

        [Test]
        public void ApplyFocusDistance_InGaussianMode_PlacesTheFarBlurRamp()
        {
            // Gaussian has no focus distance of its own — it only has a far-blur ramp — so without
            // this mapping the focus slider, click-to-focus and auto-focus all do nothing.
            _dof.mode.value = DepthOfFieldMode.Gaussian;

            _camera.ApplyFocusDistance(6f);

            Assert.That(_dof.gaussianStart.value, Is.EqualTo(6f).Within(1e-4f));
            Assert.That(_dof.gaussianEnd.value, Is.GreaterThan(_dof.gaussianStart.value));
            Assert.That(_dof.gaussianStart.overrideState, Is.True);
            Assert.That(_dof.gaussianEnd.overrideState, Is.True);
        }

        [Test]
        public void ApplyFocusDistance_InBokehMode_LeavesTheGaussianRampAlone()
        {
            _dof.mode.value = DepthOfFieldMode.Bokeh;
            float start = _dof.gaussianStart.value;
            float end = _dof.gaussianEnd.value;

            _camera.ApplyFocusDistance(6f);

            Assert.That(_dof.focusDistance.value, Is.EqualTo(6f).Within(1e-4f));
            Assert.That(_dof.gaussianStart.value, Is.EqualTo(start).Within(1e-4f));
            Assert.That(_dof.gaussianEnd.value, Is.EqualTo(end).Within(1e-4f));
        }

        [Test]
        public void AutoFocus_DoesNotDriveWhileTheCameraIsInHand()
        {
            // Follow resolves to the local player whenever no remote is targeted, and that point
            // sits behind a hand-held lens — focusing on it blurs the entire shot.
            _camera.autoFocusFollowSubject = true;

            Assert.That(_camera.CanAutoFocusOnFollowSubject, Is.False);
        }

        [Test]
        public void AutoFocus_DrivesWhileAutoFollowFliesTheCamera()
        {
            _camera.SetAutoFollowEnabled(true);

            Assert.That(_camera.CanAutoFocusOnFollowSubject, Is.True);
        }

        [Test]
        public void AutoFocus_DrivesWhileAimedAtAChosenRemote()
        {
            _camera.SetFollowTargetPlayer(7);

            Assert.That(_camera.CanAutoFocusOnFollowSubject, Is.True,
                "Picking a follow target is how you keep another player sharp without flying the camera.");
        }

        [Test]
        public void ShippedDefaults_GiveAUsableDepthOfFieldAtPortraitRange()
        {
            var defaults = new BasisHandHeldCameraUI.CameraSettings();
            float circleOfConfusion = new Vector2(defaults.sensorSizeX, defaults.sensorSizeY).magnitude / 1500f;

            BasisHandHeldCameraGizmos.ComputeDepthOfField(defaults.dofFocalLength, defaults.depthAperture,
                3f, circleOfConfusion, out float near, out float far, out _);

            Assert.That(far - near, Is.GreaterThan(0.25f),
                "f/1 on a 125mm lens gave a 3cm band at 3m, so no subject was ever usefully in focus.");
        }

        /// <summary>Mirrors the Bokeh pass's maxCoC so the clamp is checked against the real formula.</summary>
        private static float MaxCircleOfConfusion(DepthOfField dof)
        {
            float f = dof.focalLength.value / 1000f;
            float a = dof.focalLength.value / dof.aperture.value;
            return (a * f) / (dof.focusDistance.value - f);
        }
    }
}
