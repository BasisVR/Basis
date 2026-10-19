using Basis.Scripts.Drivers;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Basis.Framework.Tests
{
    public class BasisLocalCameraXRTargetTests
    {
        private GameObject host;
        private BasisLocalCameraDriver driver, savedInstance;

        [SetUp]
        public void SetUp()
        {
            savedInstance = BasisLocalCameraDriver.Instance;
            host = new GameObject("local-camera");
            driver = host.AddComponent<BasisLocalCameraDriver>();
            driver.Camera = host.AddComponent<UnityEngine.Camera>();
            driver.CameraData = host.AddComponent<UniversalAdditionalCameraData>();
            BasisLocalCameraDriver.Instance = driver;
        }

        [TearDown]
        public void TearDown()
        {
            BasisLocalCameraDriver.Instance = savedInstance;
            Object.DestroyImmediate(host);
        }

        [Test]
        public void LeavingXRRenderingTakesTheCameraOffTheHeadsetEyes()
        {
            BasisLocalCameraDriver.AllowXRRenderering(true);
            Assert.That(driver.CameraData.allowXRRendering, Is.True);
            Assert.That(driver.Camera.stereoTargetEye, Is.EqualTo(StereoTargetEyeMask.Both));

            BasisLocalCameraDriver.AllowXRRenderering(false);
            Assert.That(driver.CameraData.allowXRRendering, Is.False);
            Assert.That(driver.Camera.stereoTargetEye, Is.EqualTo(StereoTargetEyeMask.None), "a desktop camera left on Both is sized to the eye texture while a soft swap keeps XR running");
        }

        [Test]
        public void ReturningToXRRenderingPutsTheCameraBackOnBothEyes()
        {
            BasisLocalCameraDriver.AllowXRRenderering(false);
            BasisLocalCameraDriver.AllowXRRenderering(true);
            Assert.That(driver.CameraData.allowXRRendering, Is.True);
            Assert.That(driver.Camera.stereoTargetEye, Is.EqualTo(StereoTargetEyeMask.Both));
        }
    }
}
