using System;
using Basis.BasisUI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using CameraSettings = BasisHandHeldCameraUI.CameraSettings;
using Object = UnityEngine.Object;

namespace Basis.Tests.Camera
{
    public class BasisCameraPassesTests
    {
        private GameObject _go;
        private BasisHandHeldCamera _camera;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("PassesCameraUnderTest");
            _camera = _go.AddComponent<BasisHandHeldCamera>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null) Object.DestroyImmediate(_go);
        }

        private static void AssertVector(Vector3 actual, Vector3 expected, string message)
        {
            Assert.That(Vector3.Distance(actual, expected), Is.LessThan(1e-4f), $"{message}: expected {expected}, got {actual}");
        }

        [Test]
        public void EveryOutputHasAKeyAndEveryPassHasALabelAndASuffix()
        {
            Assert.That(BasisCameraPasses.OutputKeys.Length, Is.EqualTo(Enum.GetValues(typeof(BasisCameraPassOutput)).Length));
            Assert.That(Enum.GetValues(typeof(BasisCameraPass)).Length, Is.EqualTo(BasisCameraPasses.Count));
            Assert.That(BasisCameraPasses.PassKeys.Length, Is.EqualTo(BasisCameraPasses.Count));
            Assert.That(BasisCameraPasses.Suffixes.Length, Is.EqualTo(BasisCameraPasses.Count));
            Assert.That(BasisCameraPasses.QuadPasses.Length, Is.EqualTo(3));
        }

        [Test]
        public void EveryPassStringIsTranslated()
        {
            string[] keys =
            {
                "camera.passes", "camera.passes.output", "camera.passes.output.description", "camera.passes.depthRange", "camera.passes.depthRange.description",
                "camera.passes.separate", "camera.passes.separate.tooltip", "camera.passes.status.none", "camera.passes.status.photos", "camera.passes.status.line",
                "camera.passes.status.failed", "camera.passes.status.starting", "camera.passes.unavailable",
            };
            foreach (string key in keys)
            {
                Assert.That(BasisLocalization.TryGet(key, out string value), Is.True, $"{key} has no translation.");
                Assert.That(value, Is.Not.Empty);
            }
            foreach (string key in BasisCameraPasses.PassKeys)
            {
                Assert.That(BasisLocalization.TryGet(key, out string label), Is.True, $"{key} has no translation.");
                Assert.That(label, Is.Not.Empty);
                Assert.That(BasisLocalization.TryGet(key + ".description", out string description), Is.True, $"{key} has no description.");
                Assert.That(description, Is.Not.Empty);
            }
        }

        [Test]
        public void AnOutputOffTheEndFallsBackToThePicture()
        {
            Assert.That(BasisCameraPasses.SanitizeOutput(-1), Is.EqualTo(BasisCameraPassOutput.Picture));
            Assert.That(BasisCameraPasses.SanitizeOutput(99), Is.EqualTo(BasisCameraPassOutput.Picture));
            Assert.That(BasisCameraPasses.SanitizeOutput((int)BasisCameraPassOutput.QuadView), Is.EqualTo(BasisCameraPassOutput.QuadView));
        }

        [Test]
        public void TheQuadViewNeedsDepthNormalsAndMask()
        {
            Assert.That(BasisCameraPasses.For(BasisCameraPassOutput.Picture), Is.EqualTo(BasisCameraPassSet.None));
            Assert.That(BasisCameraPasses.For(BasisCameraPassOutput.QuadView), Is.EqualTo(BasisCameraPassSet.Depth | BasisCameraPassSet.Normals | BasisCameraPassSet.Mask));
            Assert.That(BasisCameraPasses.For(BasisCameraPassOutput.Depth), Is.EqualTo(BasisCameraPassSet.Depth));
            Assert.That(BasisCameraPasses.For(BasisCameraPassOutput.Normals), Is.EqualTo(BasisCameraPassSet.Normals));
            Assert.That(BasisCameraPasses.For(BasisCameraPassOutput.Mask), Is.EqualTo(BasisCameraPassSet.Mask));
            Assert.That(BasisCameraPasses.For(BasisCameraPassOutput.Motion), Is.EqualTo(BasisCameraPassSet.Motion));
        }

        [Test]
        public void EachPassAsksUrpForOnlyWhatItReads()
        {
            Assert.That(BasisCameraPasses.InputFor(BasisCameraPassSet.None), Is.EqualTo(ScriptableRenderPassInput.None));
            Assert.That(BasisCameraPasses.InputFor(BasisCameraPassSet.Depth), Is.EqualTo(ScriptableRenderPassInput.Depth));
            Assert.That(BasisCameraPasses.InputFor(BasisCameraPassSet.Normals), Is.EqualTo(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Normal));
            Assert.That(BasisCameraPasses.InputFor(BasisCameraPassSet.Mask), Is.EqualTo(ScriptableRenderPassInput.Depth));
            Assert.That(BasisCameraPasses.InputFor(BasisCameraPassSet.Motion), Is.EqualTo(ScriptableRenderPassInput.Motion));
        }

        [Test]
        public void ASetBuiltFromFlagsContainsExactlyThoseFlags()
        {
            BasisCameraPassSet set = BasisCameraPasses.SetOf(true, false, true, false);
            Assert.IsTrue(BasisCameraPasses.Contains(set, BasisCameraPass.Depth));
            Assert.IsFalse(BasisCameraPasses.Contains(set, BasisCameraPass.Normals));
            Assert.IsTrue(BasisCameraPasses.Contains(set, BasisCameraPass.Mask));
            Assert.IsFalse(BasisCameraPasses.Contains(set, BasisCameraPass.Motion));
            Assert.That(BasisCameraPasses.SetOf(true, true, true, true), Is.EqualTo(BasisCameraPasses.AllSet));
        }

        [Test]
        public void APassFileSitsBesideThePhotoWithItsPassNamed()
        {
            Assert.That(BasisCameraPasses.FileName("Screenshot_20261016_071530_1920x1080.png", BasisCameraPass.Depth), Is.EqualTo("Screenshot_20261016_071530_1920x1080_Depth.png"));
            Assert.That(BasisCameraPasses.FileName("Screenshot_20261016_071530_3840x2160.exr", BasisCameraPass.Mask), Is.EqualTo("Screenshot_20261016_071530_3840x2160_Mask.exr"));
        }

        [Test]
        public void APassSenderIsNamedAfterTheMainSender()
        {
            Assert.That(BasisCameraPasses.SenderName("Basis Camera 2", BasisCameraPass.Normals), Is.EqualTo("Basis Camera 2 Normals"));
            Assert.That(BasisCameraPasses.SenderName(string.Empty, BasisCameraPass.Mask), Is.EqualTo(BasisVideoOutputSettings.DefaultSenderName + " Mask"));
        }

        [Test]
        public void DepthIsWhiteAtTheLensAndBlackAtTheRange()
        {
            Assert.That(BasisCameraPasses.DepthDisplay(0f, 10f), Is.EqualTo(1f).Within(1e-5f));
            Assert.That(BasisCameraPasses.DepthDisplay(5f, 10f), Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(BasisCameraPasses.DepthDisplay(10f, 10f), Is.EqualTo(0f).Within(1e-5f));
            Assert.That(BasisCameraPasses.DepthDisplay(50f, 10f), Is.EqualTo(0f).Within(1e-5f));
        }

        [Test]
        public void TheDepthRangeIsClampedAndNeverZero()
        {
            Assert.That(BasisCameraPasses.SanitizeDepthRange(0f), Is.EqualTo(BasisCameraPasses.DefaultDepthRange));
            Assert.That(BasisCameraPasses.SanitizeDepthRange(-3f), Is.EqualTo(BasisCameraPasses.DefaultDepthRange));
            Assert.That(BasisCameraPasses.SanitizeDepthRange(float.NaN), Is.EqualTo(BasisCameraPasses.DefaultDepthRange));
            Assert.That(BasisCameraPasses.SanitizeDepthRange(0.1f), Is.EqualTo(BasisCameraPasses.MinDepthRange));
            Assert.That(BasisCameraPasses.SanitizeDepthRange(10000f), Is.EqualTo(BasisCameraPasses.MaxDepthRange));
        }

        [Test]
        public void ASurfaceFacingTheLensIsBlueInTheNormalsPass()
        {
            Matrix4x4 view = BasisCameraPasses.ViewRotation(Quaternion.identity);
            AssertVector(view.MultiplyVector(Vector3.back), Vector3.forward, "a surface facing the lens");
            AssertVector(view.MultiplyVector(Vector3.right), Vector3.right, "a surface facing right");
            AssertVector(view.MultiplyVector(Vector3.up), Vector3.up, "a surface facing up");

            Color display = BasisCameraPasses.NormalDisplay(Vector3.forward);
            Assert.That(display.r, Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(display.g, Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(display.b, Is.EqualTo(1f).Within(1e-5f));
        }

        [Test]
        public void TheNormalsFollowTheCameraRatherThanTheWorld()
        {
            Matrix4x4 view = BasisCameraPasses.ViewRotation(Quaternion.Euler(0f, 90f, 0f));
            AssertVector(view.MultiplyVector(Vector3.left), Vector3.forward, "a surface facing a camera that looks along +X");
            AssertVector(view.MultiplyVector(Vector3.back), Vector3.right, "the camera's right is world -Z");
            AssertVector(view.MultiplyVector(Vector3.up), Vector3.up, "up stays up");
        }

        [Test]
        public void TheQuadPutsThePictureTopLeft()
        {
            Assert.That(BasisCameraPasses.QuadrantAt(new Vector2(0.25f, 0.75f)), Is.EqualTo(BasisCameraPassOutput.Picture));
            Assert.That(BasisCameraPasses.QuadrantAt(new Vector2(0.75f, 0.75f)), Is.EqualTo(BasisCameraPassOutput.Depth));
            Assert.That(BasisCameraPasses.QuadrantAt(new Vector2(0.25f, 0.25f)), Is.EqualTo(BasisCameraPassOutput.Normals));
            Assert.That(BasisCameraPasses.QuadrantAt(new Vector2(0.75f, 0.25f)), Is.EqualTo(BasisCameraPassOutput.Mask));
            Vector2 centre = BasisCameraPasses.QuadrantUV(new Vector2(0.75f, 0.25f));
            Assert.That(centre.x, Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(centre.y, Is.EqualTo(0.5f).Within(1e-5f));
        }

        [Test]
        public void PassTargetsMatchOnlyTheirOwnSizeAndEncoding()
        {
            RenderTexture reference = new RenderTexture(64, 32, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture display = new RenderTexture(64, 32, 0, BasisCameraPasses.TargetFormat(false), RenderTextureReadWrite.sRGB);
            RenderTexture data = new RenderTexture(64, 32, 0, BasisCameraPasses.TargetFormat(true), RenderTextureReadWrite.Linear);
            RenderTexture small = new RenderTexture(32, 32, 0, BasisCameraPasses.TargetFormat(false), RenderTextureReadWrite.sRGB);
            try
            {
                reference.Create();
                display.Create();
                data.Create();
                small.Create();
                Assert.IsTrue(BasisCameraPasses.Matches(display, reference, false));
                Assert.IsFalse(BasisCameraPasses.Matches(display, reference, true));
                Assert.IsTrue(BasisCameraPasses.Matches(data, reference, true));
                Assert.IsFalse(BasisCameraPasses.Matches(data, reference, false));
                Assert.IsFalse(BasisCameraPasses.Matches(small, reference, false));
                Assert.IsFalse(BasisCameraPasses.Matches(null, reference, false));
            }
            finally
            {
                foreach (RenderTexture texture in new[] { reference, display, data, small })
                {
                    texture.Release();
                    Object.DestroyImmediate(texture);
                }
            }
        }

        [Test]
        public void AFreshCameraShowsThePictureAndRendersNoPasses()
        {
            Assert.That(_camera.PassOutput, Is.EqualTo(BasisCameraPassOutput.Picture));
            Assert.IsFalse(_camera.SeparatePasses);
            Assert.That(_camera.SeparatePassSet, Is.EqualTo(BasisCameraPasses.DefaultSeparateSet));
            Assert.That(_camera.PassDepthRange, Is.EqualTo(BasisCameraPasses.DefaultDepthRange));
            Assert.That(_camera.LivePassSet, Is.EqualTo(BasisCameraPassSet.None));
            Assert.That(_camera.CapturePassSet, Is.EqualTo(BasisCameraPassSet.None));
            Assert.That(_camera.DescribeSeparateOutputs(), Is.Empty);
            Assert.That(_camera.OutputTexture, Is.SameAs(_camera.PreviewTexture));
        }

        [Test]
        public void SeparatePassesRenderForPhotosButWaitForLiveOutputToStream()
        {
            _camera.SetSeparatePasses(true);
            Assert.That(_camera.CapturePassSet, Is.EqualTo(BasisCameraPasses.DefaultSeparateSet));
            Assert.That(_camera.LivePassSet, Is.EqualTo(BasisCameraPassSet.None), "Nothing streams a separate pass until the live output is on.");

            _camera.SetPassOutput(BasisCameraPassOutput.Motion);
            Assert.That(_camera.LivePassSet, Is.EqualTo(BasisCameraPassSet.Motion));
            Assert.That(_camera.CapturePassSet, Is.EqualTo(BasisCameraPasses.DefaultSeparateSet | BasisCameraPassSet.Motion));
        }

        [Test]
        public void TogglingOnePassLeavesTheOthersAlone()
        {
            _camera.SetSeparatePass(BasisCameraPass.Normals, false);
            Assert.IsTrue(_camera.IsSeparatePass(BasisCameraPass.Depth));
            Assert.IsFalse(_camera.IsSeparatePass(BasisCameraPass.Normals));
            Assert.IsTrue(_camera.IsSeparatePass(BasisCameraPass.Mask));
            Assert.IsFalse(_camera.IsSeparatePass(BasisCameraPass.Motion));

            _camera.SetSeparatePass(BasisCameraPass.Motion, true);
            Assert.IsTrue(_camera.IsSeparatePass(BasisCameraPass.Motion));

            _camera.SetSeparatePassSet((BasisCameraPassSet)0xFF);
            Assert.That(_camera.SeparatePassSet, Is.EqualTo(BasisCameraPasses.AllSet), "Bits that name no pass are dropped.");
        }

        [Test]
        public void AnUnknownOutputFromASettingsFileShowsThePicture()
        {
            _camera.SetPassOutput((BasisCameraPassOutput)42);
            Assert.That(_camera.PassOutput, Is.EqualTo(BasisCameraPassOutput.Picture));
        }

        [Test]
        public void TheStatusSaysWhatSeparateOutputsWillDo()
        {
            _camera.SetSeparatePasses(true);
            _camera.SetSeparatePassSet(BasisCameraPassSet.None);
            Assert.That(_camera.DescribeSeparateOutputs(), Is.EqualTo(BasisLocalization.Get("camera.passes.status.none")));

            _camera.SetSeparatePassSet(BasisCameraPassSet.Depth | BasisCameraPassSet.Mask);
            Assert.That(_camera.DescribeSeparateOutputs(), Is.EqualTo(BasisLocalization.Get("camera.passes.status.photos", BasisCameraPasses.Describe(BasisCameraPassSet.Depth | BasisCameraPassSet.Mask))));
            Assert.That(BasisCameraPasses.Describe(BasisCameraPassSet.Depth | BasisCameraPassSet.Mask), Is.EqualTo(BasisLocalization.Get("camera.passes.depth") + ", " + BasisLocalization.Get("camera.passes.mask")));
        }

        [Test]
        public void TheReadoutNamesTheOutputAndTheSeparatePasses()
        {
            CameraSettings settings = BasisCameraSettingsRig.DistinctiveSettings();
            string text = BasisCameraSettingsReadout.Build(settings, 0, null);
            Assert.That(text, Does.Contain(BasisLocalization.Get("camera.passes")));
            Assert.That(text, Does.Contain(BasisLocalization.Get(BasisCameraPasses.OutputKeys[(int)BasisCameraPasses.SanitizeOutput(settings.passOutput)])));
            Assert.That(text, Does.Contain(BasisCameraPasses.Describe(BasisCameraPasses.SetOf(settings.separateDepth, settings.separateNormals, settings.separateMask, settings.separateMotion))));
        }
    }
}
