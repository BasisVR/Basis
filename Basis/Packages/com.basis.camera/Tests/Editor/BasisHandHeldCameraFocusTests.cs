using NUnit.Framework;
using Basis.Cinematics;
using UnityEditor;
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
        private const string PrefabPath = "Packages/com.basis.camera/Prefabs/Player Held Camera.prefab";

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
        public void RackFocusTo_LeavesTheFocusPlaneWhereItIsUntilThePullRuns()
        {
            _camera.ApplyFocusDistance(2f);
            _camera.focusRackSeconds = 0.5f;

            _camera.RackFocusTo(12f);

            Assert.That(_dof.focusDistance.value, Is.EqualTo(2f).Within(1e-4f),
                "Clicking must start a pull, not cut the focus plane across the room in one frame.");
            Assert.That(_camera.IsRackingFocus, Is.True);
            Assert.That(_camera.FocusRackTarget, Is.EqualTo(12f).Within(1e-4f));
        }

        [Test]
        public void RackFocusTo_CutsWhenNoPullDurationIsSet()
        {
            _camera.ApplyFocusDistance(2f);
            _camera.focusRackSeconds = 0f;

            _camera.RackFocusTo(12f);

            Assert.That(_dof.focusDistance.value, Is.EqualTo(12f).Within(1e-4f));
            Assert.That(_camera.IsRackingFocus, Is.False);
        }

        [Test]
        public void RackFocusTo_ClampsItsTargetClearOfTheLensFocalLength()
        {
            _dof.focalLength.value = 300f;
            _camera.focusRackSeconds = 0.5f;

            _camera.RackFocusTo(0.1f);

            Assert.That(_camera.FocusRackTarget, Is.EqualTo(_camera.MinimumFocusDistance).Within(1e-4f),
                "A pull that lands inside the focal length inverts the circle of confusion at the end of the move.");
        }

        [Test]
        public void ApplyFocusDistance_CancelsAPullAlreadyInFlight()
        {
            // The slider and auto-focus are direct drives; a stale pull left running would keep
            // dragging the focus plane off whatever they just set.
            _camera.ApplyFocusDistance(2f);
            _camera.focusRackSeconds = 0.5f;
            _camera.RackFocusTo(12f);

            _camera.ApplyFocusDistance(4f);

            Assert.That(_camera.IsRackingFocus, Is.False);
            Assert.That(_dof.focusDistance.value, Is.EqualTo(4f).Within(1e-4f));
        }

        [Test]
        public void SampleFocusRack_StartsAndEndsOnItsOwnEndpoints()
        {
            Assert.That(BasisCameraFocusMath.SampleRack(1f, 50f, 0f), Is.EqualTo(1f).Within(1e-3f));
            Assert.That(BasisCameraFocusMath.SampleRack(1f, 50f, 1f), Is.EqualTo(50f).Within(1e-3f));
        }

        [Test]
        public void SampleFocusRack_EasesInDioptreSpaceRatherThanMetres()
        {
            // Blur is a function of 1/distance, so a pull interpolated in metres has already stopped
            // changing the picture by its own halfway point and the second half reads as a stall.
            float halfway = BasisCameraFocusMath.SampleRack(1f, 50f, 0.5f);

            Assert.That(halfway, Is.LessThan(5f), "Metric halfway would be 25.5m.");
            Assert.That(halfway, Is.GreaterThan(1f));
        }

        [Test]
        public void SampleFocusRack_MovesOneWayThroughThePull()
        {
            float previous = BasisCameraFocusMath.SampleRack(1f, 50f, 0f);
            for (int step = 1; step <= 20; step++)
            {
                float sample = BasisCameraFocusMath.SampleRack(1f, 50f, step / 20f);
                Assert.That(sample, Is.GreaterThan(previous), "A focus pull must never back up on itself.");
                previous = sample;
            }
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
            _camera.SetPositionModifier(BasisCameraPositionModifier.FollowSubject);

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
        public void AutoFocus_DoesNotDriveWhileTheStackIsFittedButFilmingNobody()
        {
            // A modifier can be fitted with the Subject slot on None — a dolly track needs no
            // subject to run. Focus then falls back to the local player, which is the same shot-wide
            // blur as holding the camera, so it must not drive here either.
            _camera.SetPositionModifier(BasisCameraPositionModifier.DollyTrack);
            _camera.SetSubjectModifier(BasisCameraSubjectModifier.None);

            Assert.That(_camera.CanAutoFocusOnFollowSubject, Is.False);
        }

        [Test]
        public void AutoFocus_ReportsHavingNoSubject_OnlyWhileFollowSubjectIsSelected()
        {
            // The notice under the focus dropdown reads this, so it has to be false whenever the
            // dropdown says Manual — there is nothing to warn about when nothing is being followed.
            _camera.autoFocusFollowSubject = false;
            Assert.That(_camera.AutoFocusHasNoSubject, Is.False);

            _camera.autoFocusFollowSubject = true;
            Assert.That(_camera.AutoFocusHasNoSubject, Is.True,
                "Follow Subject with the camera in hand and no target tracks nobody.");

            _camera.SetFollowTargetPlayer(7);
            Assert.That(_camera.AutoFocusHasNoSubject, Is.False);
        }

        [Test]
        public void AutoFocus_StopsReportingNoSubject_OnceTheStackFilmsSomebody()
        {
            _camera.autoFocusFollowSubject = true;
            _camera.SetPositionModifier(BasisCameraPositionModifier.FollowSubject);
            _camera.SetSubjectModifier(BasisCameraSubjectModifier.None);

            Assert.That(_camera.Modifiers.positionModifier, Is.EqualTo(BasisCameraPositionModifier.FreeFly),
                "Nobody to follow unfits Follow Subject rather than leaving it filming nobody.");
            Assert.That(_camera.AutoFocusHasNoSubject, Is.True);

            _camera.SetSubjectModifier(BasisCameraSubjectModifier.FollowPlayer);
            _camera.SetPositionModifier(BasisCameraPositionModifier.FollowSubject);

            Assert.That(_camera.AutoFocusHasNoSubject, Is.False);
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

        [Test]
        public void FocusLayers_DropTheLayersTheCaptureCameraDoesNotRender()
        {
            int ui = 1 << 5, overlayUi = 1 << 9, handHeldCameraUi = 1 << 11;
            int requested = ~(1 << 2);
            int cullingMask = ~(ui | overlayUi | handHeldCameraUi);

            int visible = BasisDepthOfFieldInteractionHandler.VisibleFocusLayers(requested, cullingMask);

            Assert.That(visible & ui, Is.Zero,
                "The player's menu is culled from every shot, so it must not be able to take the focus.");
            Assert.That(visible & overlayUi, Is.Zero);
            Assert.That(visible & handHeldCameraUi, Is.Zero,
                "The camera's own panel sits a hand's width from the lens and was the nearest hit for most clicks.");
            Assert.That(visible & 1, Is.Not.Zero, "Default is in the picture, so it stays focusable.");
        }

        [Test]
        public void FocusLayers_KeepALayerTheOperatorPutBackIntoTheCapture()
        {
            int visible = BasisDepthOfFieldInteractionHandler.VisibleFocusLayers(~(1 << 2), ~0);

            Assert.That(visible & (1 << 5), Is.Not.Zero,
                "Turning the UI layer back on for captures makes nameplates real subjects again.");
        }

        [Test]
        public void FocusLayers_AreLeftAloneWhenThereIsNoCameraToReadAMaskFrom()
        {
            int requested = ~(1 << 2);

            Assert.That(BasisDepthOfFieldInteractionHandler.VisibleFocusLayers(requested, 0), Is.EqualTo(requested));
        }

        [Test]
        public void ThePrefabsFocusRayCannotLandOnAnythingTheShotDoesNotShow()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            Assert.That(prefab, Is.Not.Null, PrefabPath);

            BasisDepthOfFieldInteractionHandler handler = prefab.GetComponentInChildren<BasisDepthOfFieldInteractionHandler>(true);
            BasisHandHeldCamera handHeld = prefab.GetComponentInChildren<BasisHandHeldCamera>(true);
            Assert.That(handler, Is.Not.Null);
            Assert.That(handHeld, Is.Not.Null);
            Assert.That(handHeld.captureCamera, Is.Not.Null);

            int cullingMask = handHeld.captureCamera.cullingMask;
            int visible = BasisDepthOfFieldInteractionHandler.VisibleFocusLayers(handler.focusLayers.value, cullingMask);

            Assert.That(visible, Is.Not.Zero, "Narrowing the mask must still leave something focusable.");
            for (int layer = 0; layer < 32; layer++)
            {
                if ((cullingMask & (1 << layer)) != 0) continue;
                Assert.That(visible & (1 << layer), Is.Zero,
                    $"Layer {layer} ({LayerMask.LayerToName(layer)}) is culled from every shot, but the focus ray could still land on it.");
            }
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
