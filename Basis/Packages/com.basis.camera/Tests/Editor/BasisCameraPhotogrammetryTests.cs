using System.Collections.Generic;
using Basis;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Camera
{
    /// <summary>
    /// The photogrammetry recorder's edit-mode surface: clamped setters, the defaults the panel
    /// and constructor must agree on, the idle state machine, and the hand-written manifest.
    /// </summary>
    public class BasisCameraPhotogrammetryTests
    {
        private GameObject _host;
        private BasisHandHeldCamera _camera;

        [SetUp]
        public void SetUp()
        {
            _host = new GameObject("PhotogrammetryTest");
            _camera = _host.AddComponent<BasisHandHeldCamera>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_host != null) UnityEngine.Object.DestroyImmediate(_host);
        }

        [Test]
        public void SettersClampToTheRangesThePanelPromises()
        {
            _camera.SetPhotogrammetryDistance(0f);
            Assert.That(_camera.PhotogrammetryDistanceMeters, Is.EqualTo(BasisHandHeldCamera.MinPhotogrammetryDistanceMeters));
            _camera.SetPhotogrammetryDistance(100f);
            Assert.That(_camera.PhotogrammetryDistanceMeters, Is.EqualTo(BasisHandHeldCamera.MaxPhotogrammetryDistanceMeters));

            _camera.SetPhotogrammetryAngle(0f);
            Assert.That(_camera.PhotogrammetryAngleDegrees, Is.EqualTo(BasisHandHeldCamera.MinPhotogrammetryAngleDegrees));
            _camera.SetPhotogrammetryAngle(500f);
            Assert.That(_camera.PhotogrammetryAngleDegrees, Is.EqualTo(BasisHandHeldCamera.MaxPhotogrammetryAngleDegrees));

            _camera.SetPhotogrammetryWidth(1);
            Assert.That(_camera.PhotogrammetryWidth, Is.EqualTo(BasisHandHeldCamera.MinPhotogrammetryWidth));
            _camera.SetPhotogrammetryWidth(99999);
            Assert.That(_camera.PhotogrammetryWidth, Is.EqualTo(BasisHandHeldCamera.MaxPhotogrammetryWidth));
        }

        [Test]
        public void FreshCameraAndFreshSettingsFileAgreeOnEveryPhotogrammetryDefault()
        {
            var defaults = new BasisHandHeldCameraUI.CameraSettings();

            Assert.That(_camera.PhotogrammetryDistanceMeters, Is.EqualTo(defaults.photogrammetryDistanceMeters));
            Assert.That(_camera.PhotogrammetryAngleDegrees, Is.EqualTo(defaults.photogrammetryAngleDegrees));
            Assert.That(_camera.PhotogrammetryWidth, Is.EqualTo(defaults.photogrammetryWidth));

            // A slider that opens at zero reads as broken; these also gate the trigger maths.
            Assert.That(defaults.photogrammetryDistanceMeters, Is.GreaterThan(0f));
            Assert.That(defaults.photogrammetryAngleDegrees, Is.GreaterThan(0f));
            Assert.That(defaults.photogrammetryWidth, Is.GreaterThan(0));
        }

        [Test]
        public void EveryPanelWidthPresetIsInsideTheSetterRange()
        {
            foreach (int preset in BasisHandHeldCamera.PhotogrammetryWidthPresets)
            {
                Assert.That(preset, Is.InRange(BasisHandHeldCamera.MinPhotogrammetryWidth, BasisHandHeldCamera.MaxPhotogrammetryWidth));
            }
        }

        [Test]
        public void RecorderStartsIdleAndRefusesWithoutAFeed()
        {
            Assert.That(_camera.PhotogrammetryState, Is.EqualTo(BasisCameraRecordingState.Idle));
            Assert.That(_camera.IsPhotogrammetryActive, Is.False);

            // No capture camera or render texture exists in edit mode; a start must refuse
            // cleanly rather than spin up a session with nothing to record.
            Assert.That(_camera.StartPhotogrammetrySession(), Is.False);
            Assert.That(_camera.PhotogrammetryState, Is.EqualTo(BasisCameraRecordingState.Idle));

            // A capture or a stop from idle is a no-op, not an error.
            Assert.That(_camera.CapturePhotogrammetryFrameNow(), Is.False);
            _camera.StopPhotogrammetrySession();
            Assert.That(_camera.PhotogrammetryState, Is.EqualTo(BasisCameraRecordingState.Idle));
        }

        // ---- the manifest ------------------------------------------------------------------

        [Test]
        public void ManifestListsFramesInIndexOrderRegardlessOfInputOrder()
        {
            var frames = new List<BasisPhotogrammetryFrame>
            {
                MakeFrame(2, "images/00003.png"),
                MakeFrame(0, "images/00001.png"),
                MakeFrame(1, "images/00002.png"),
            };

            string json = BasisPhotogrammetryManifest.BuildJson(frames);

            int firstIndex = json.IndexOf("00001.png");
            int secondIndex = json.IndexOf("00002.png");
            int thirdIndex = json.IndexOf("00003.png");
            Assert.That(firstIndex, Is.GreaterThan(0));
            Assert.That(secondIndex, Is.GreaterThan(firstIndex));
            Assert.That(thirdIndex, Is.GreaterThan(secondIndex));
        }

        [Test]
        public void ManifestCarriesTopLevelAndPerFrameIntrinsics()
        {
            var frames = new List<BasisPhotogrammetryFrame> { MakeFrame(0, "images/00001.png") };
            string json = BasisPhotogrammetryManifest.BuildJson(frames);

            Assert.That(json, Does.Contain("\"camera_model\": \"OPENCV\""));
            Assert.That(json, Does.Contain("\"fl_x\": 111"));
            Assert.That(json, Does.Contain("\"w\": 1280"));
            Assert.That(json, Does.Contain("\"h\": 720"));
            Assert.That(json, Does.Contain("\"file_path\": \"images/00001.png\""));
            Assert.That(json, Does.Contain("\"transform_matrix\""));
        }

        [Test]
        public void ManifestWithNoFramesIsStillValidShapedJson()
        {
            string json = BasisPhotogrammetryManifest.BuildJson(new List<BasisPhotogrammetryFrame>());

            Assert.That(json, Does.Contain("\"frames\": ["));
            Assert.That(json.TrimEnd(), Does.EndWith("}"));
            Assert.That(json, Does.Not.Contain("camera_model"), "No frames means no intrinsics to report.");
        }

        private static BasisPhotogrammetryFrame MakeFrame(int index, string relativeFilePath)
        {
            return new BasisPhotogrammetryFrame(index, relativeFilePath, Matrix4x4.identity,
                focalLengthX: 111.11f, focalLengthY: 111.11f, principalPointX: 640f, principalPointY: 360f,
                width: 1280, height: 720);
        }
    }
}
