using NUnit.Framework;
using UnityEngine;
using CameraSettings = BasisHandHeldCameraUI.CameraSettings;
using Object = UnityEngine.Object;

namespace Basis.Tests.Camera
{
    public class BasisCameraPassesOutputTests
    {
        private GameObject _go;
        private BasisHandHeldCamera _camera;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("PassesOutputCameraUnderTest");
            _camera = _go.AddComponent<BasisHandHeldCamera>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null) Object.DestroyImmediate(_go);
        }

        [Test]
        public void AFileFromBeforeRenderPassesLoadsTheShippedSeparateSet()
        {
            CameraSettings defaults = new CameraSettings();
            CameraSettings loaded = JsonUtility.FromJson<CameraSettings>("{\"settingsVersion\":13,\"fov\":50}");
            BasisHandHeldCameraUI.MigrateSettingsForTest(loaded);

            Assert.That(loaded.fov, Is.EqualTo(50f).Within(1e-4f), "The file has to have been read.");
            Assert.That(BasisCameraPasses.SetOf(defaults.separateDepth, defaults.separateNormals, defaults.separateMask, defaults.separateMotion), Is.EqualTo(BasisCameraPasses.DefaultSeparateSet));
            Assert.That(BasisCameraPasses.SetOf(loaded.separateDepth, loaded.separateNormals, loaded.separateMask, loaded.separateMotion), Is.EqualTo(BasisCameraPasses.DefaultSeparateSet), "Fields the file never wrote keep their initializer values.");
            Assert.That(loaded.passDepthRange, Is.EqualTo(BasisCameraPasses.DefaultDepthRange));
            Assert.IsFalse(loaded.separatePasses);
        }

        [Test]
        public void ASeparatePassTurnedOffStaysOffAfterALoad()
        {
            CameraSettings loaded = JsonUtility.FromJson<CameraSettings>("{\"settingsVersion\":13,\"separateDepth\":false,\"separateMask\":false,\"passDepthRange\":25}");
            BasisHandHeldCameraUI.MigrateSettingsForTest(loaded);

            Assert.IsFalse(loaded.separateDepth);
            Assert.IsTrue(loaded.separateNormals);
            Assert.IsFalse(loaded.separateMask);
            Assert.That(loaded.passDepthRange, Is.EqualTo(25f).Within(1e-4f));
        }

        [Test]
        public void ASeparatePassWithNoRunningOutputIsNotRenderedLive()
        {
            _camera.SetSeparatePasses(true);
            _camera.IsVideoOutputActive = true;
            _camera.IsWebStreamActive = true;
            try
            {
                Assert.That(_camera.LivePassSet, Is.EqualTo(BasisCameraPassSet.None), "No pass sender is running, so nothing reads a live separate pass.");
                Assert.That(_camera.CapturePassSet, Is.EqualTo(BasisCameraPasses.DefaultSeparateSet), "Photos still save every separate pass.");

                _camera.SetPassOutput(BasisCameraPassOutput.Depth);
                Assert.That(_camera.LivePassSet, Is.EqualTo(BasisCameraPassSet.Depth), "The pass the feed shows is always rendered.");
            }
            finally
            {
                _camera.IsVideoOutputActive = false;
                _camera.IsWebStreamActive = false;
            }
        }
    }
}
