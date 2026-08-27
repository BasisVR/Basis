using System;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace Basis.Tests.GlobalIllumination
{
    public class BasisGlobalIlluminationFeatureTests
    {
        private BasisGlobalIlluminationFeature feature;
        private GameObject cameraHost;
        private Camera camera;
        private Func<Camera, bool> previousFilter;
        private bool previousKeepWithDebugger;

        [SetUp]
        public void SetUp()
        {
            previousFilter = BasisGlobalIlluminationFeature.CameraFilter;
            previousKeepWithDebugger = BasisGlobalIlluminationFeature.KeepRenderingWithDebugger;
            BasisGlobalIlluminationFeature.CameraFilter = null;
            BasisGlobalIlluminationFeature.KeepRenderingWithDebugger = true;
            feature = ScriptableObject.CreateInstance<BasisGlobalIlluminationFeature>();
            feature.SetActive(true);
            cameraHost = new GameObject("BasisGIFeatureTestCamera");
            camera = cameraHost.AddComponent<Camera>();
        }

        [TearDown]
        public void TearDown()
        {
            BasisGlobalIlluminationFeature.CameraFilter = previousFilter;
            BasisGlobalIlluminationFeature.KeepRenderingWithDebugger = previousKeepWithDebugger;
            if (cameraHost != null) { UnityEngine.Object.DestroyImmediate(cameraHost); }
            if (feature != null) { UnityEngine.Object.DestroyImmediate(feature); }
        }

        [Test]
        public void PreviewCamerasNeverRender()
        {
            Assert.IsFalse(feature.ShouldRender(camera, CameraType.Preview, true));
        }

        [Test]
        public void ReflectionCamerasFollowTheReflectionProbeToggle()
        {
            feature.ReflectionProbes = false;
            Assert.IsFalse(feature.ShouldRender(camera, CameraType.Reflection, true));
            feature.ReflectionProbes = true;
            Assert.AreEqual(BasisGlobalIlluminationFeature.SupportsPlatform(), feature.ShouldRender(camera, CameraType.Reflection, true));
        }

        [Test]
        public void PostProcessingOffStopsTheEffect()
        {
            Assert.IsFalse(feature.ShouldRender(camera, CameraType.Game, false));
        }

        [Test]
        public void InactiveFeatureNeverRenders()
        {
            feature.SetActive(false);
            Assert.IsFalse(feature.ShouldRender(camera, CameraType.Game, true));
        }

        [Test]
        public void CameraFilterCanRejectACamera()
        {
            BasisGlobalIlluminationFeature.CameraFilter = _ => false;
            Assert.IsFalse(feature.ShouldRender(camera, CameraType.Game, true));
            BasisGlobalIlluminationFeature.CameraFilter = _ => true;
            Assert.AreEqual(BasisGlobalIlluminationFeature.SupportsPlatform(), feature.ShouldRender(camera, CameraType.Game, true));
        }

        [Test]
        public void ANullCameraFilterAcceptsEveryCamera()
        {
            BasisGlobalIlluminationFeature.CameraFilter = null;
            Assert.AreEqual(BasisGlobalIlluminationFeature.SupportsPlatform(), feature.ShouldRender(camera, CameraType.Game, true));
        }

        [Test]
        public void DebugViewReachesThePass()
        {
            feature.Create();
            feature.DebugView = BasisGlobalIlluminationDebugView.Obscurance;
            Assert.AreEqual(BasisGlobalIlluminationDebugView.Obscurance, feature.DebugView);
            Assert.IsNotNull(feature.Pass);
            Assert.AreEqual(BasisGlobalIlluminationDebugView.Obscurance, feature.Pass.DebugView);
        }

        [Test]
        public void CreateResolvesTheShaderAndMaterial()
        {
            feature.Create();
            Assert.IsNotNull(feature.Material, "The GI shader did not resolve; the effect would silently do nothing.");
            Assert.AreEqual(BasisGlobalIlluminationFeature.ShaderName, feature.Material.shader.name);
        }

        [Test]
        public void EveryShaderPassTheFeatureIndexesExists()
        {
            feature.Create();
            Assert.IsNotNull(feature.Material);
            Material material = feature.Material;
            Assert.AreEqual("BasisGITrace", material.GetPassName(BasisGlobalIlluminationPass.PassTrace));
            Assert.AreEqual("BasisGITemporal", material.GetPassName(BasisGlobalIlluminationPass.PassTemporal));
            Assert.AreEqual("BasisGIBlur", material.GetPassName(BasisGlobalIlluminationPass.PassBlur));
            Assert.AreEqual("BasisGIComposite", material.GetPassName(BasisGlobalIlluminationPass.PassComposite));
            Assert.AreEqual("BasisGIDebug", material.GetPassName(BasisGlobalIlluminationPass.PassDebug));
            Assert.AreEqual("BasisGICopyColor", material.GetPassName(BasisGlobalIlluminationPass.PassCopyColor));
        }
    }
}
