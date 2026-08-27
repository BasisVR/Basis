// Global illumination is optional: the define comes from the com.basis.globalillumination package
// being present (asmdef versionDefines), and the effect is not viable on mobile GPUs, so the whole
// integration compiles out on Android.
#if BASIS_HAS_GI && !UNITY_ANDROID
using System;
using System.Collections.Generic;
using System.Globalization;
using Basis.BasisUI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Basis.Tests.Graphics
{
    /// <summary>
    /// The settings module driving a camera that actually renders.
    ///
    /// The existing settings tests prove a slider reaches the volume the module owns. That is not the same
    /// claim as the slider changing anything: the module's volume still has to win the camera's volume
    /// stack, and a stack is decided by layer, priority and weight, none of which a test that only reads
    /// back the module's own component can see. A setting that is persisted, threaded through the state,
    /// written to a volume and then out-prioritised looks perfectly wired up and does nothing on screen.
    /// </summary>
    public class BasisGlobalIlluminationSettingsRenderTests
    {
        private const int Width = 192;
        private const int Height = 128;

        private readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
        private readonly List<VolumeComponent> restoreTargets = new List<VolumeComponent>();
        private readonly List<VolumeComponent> restoreCopies = new List<VolumeComponent>();

        private Func<UnityEngine.Camera, bool> previousFilter;
        private bool previousKeepWithDebugger;
        private bool previousFeatureActive;
        private BasisGlobalIlluminationFeature feature;
        private GameObject host;
        private UnityEngine.Camera camera;
        private RenderTexture target;
        private Texture2D readback;
        private RectInt probe;

        [SetUp]
        public void SetUp()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) { Assert.Ignore("This run has no graphics device."); }
            if (!(GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset)) { Assert.Ignore("The active render pipeline is not URP."); }
            if (!BasisGlobalIlluminationFeature.SupportsPlatform()) { Assert.Ignore("The effect declines to render on this platform."); }
            if (Shader.Find("Universal Render Pipeline/Lit") == null) { Assert.Ignore("The URP lit shader is not available."); }

            previousFilter = BasisGlobalIlluminationFeature.CameraFilter;
            previousKeepWithDebugger = BasisGlobalIlluminationFeature.KeepRenderingWithDebugger;
            BasisGlobalIlluminationFeature.CameraFilter = null;
            BasisGlobalIlluminationFeature.KeepRenderingWithDebugger = true;

            feature = SMModuleGlobalIlluminationURP.FindFeature();
            if (feature == null) { Assert.Ignore("The active render pipeline carries no global illumination feature."); }
            previousFeatureActive = feature.isActive;

            // The module writes the pipeline's own default profiles, which are project assets. Snapshot them
            // so a test run does not silently re-author what the project ships with.
            Remember(VolumeManager.instance.globalDefaultProfile);
            Remember(VolumeManager.instance.qualityDefaultProfile);

            BuildScene();
        }

        [TearDown]
        public void TearDown()
        {
            BasisGlobalIlluminationFeature.CameraFilter = previousFilter;
            BasisGlobalIlluminationFeature.KeepRenderingWithDebugger = previousKeepWithDebugger;

            if (host != null)
            {
                SMModuleGlobalIlluminationURP module = host.GetComponent<SMModuleGlobalIlluminationURP>();
                if (module != null)
                {
                    module.RestoreAuthoredProfiles();
                    module.RestoreAuthoredFeatureValues();
                    if (module.Volume != null && module.Volume.sharedProfile != null)
                    {
                        UnityEngine.Object.DestroyImmediate(module.Volume.sharedProfile);
                    }
                }
                UnityEngine.Object.DestroyImmediate(host);
                host = null;
            }

            for (int index = 0; index < restoreTargets.Count; index++) { CopyParameters(restoreCopies[index], restoreTargets[index]); }
            for (int index = 0; index < restoreCopies.Count; index++) { UnityEngine.Object.DestroyImmediate(restoreCopies[index]); }
            restoreTargets.Clear();
            restoreCopies.Clear();

            if (feature != null) { feature.SetActive(previousFeatureActive); }

            if (camera != null) { camera.targetTexture = null; }
            for (int index = owned.Count - 1; index >= 0; index--)
            {
                if (owned[index] != null) { UnityEngine.Object.DestroyImmediate(owned[index]); }
            }
            owned.Clear();

            if (readback != null) { UnityEngine.Object.DestroyImmediate(readback); readback = null; }
            if (target != null) { target.Release(); UnityEngine.Object.DestroyImmediate(target); target = null; }
        }

        private void Remember(VolumeProfile profile)
        {
            if (profile == null || !profile.TryGet(out BasisGlobalIlluminationVolume component)) { return; }
            VolumeComponent copy = UnityEngine.Object.Instantiate(component);
            copy.hideFlags = HideFlags.HideAndDontSave;
            restoreTargets.Add(component);
            restoreCopies.Add(copy);
        }

        private static void CopyParameters(VolumeComponent from, VolumeComponent to)
        {
            if (from == null || to == null) { return; }
            for (int index = 0; index < to.parameters.Count && index < from.parameters.Count; index++)
            {
                to.parameters[index].SetValue(from.parameters[index]);
                to.parameters[index].overrideState = from.parameters[index].overrideState;
            }
            to.active = from.active;
        }

        private T Own<T>(T value) where T : UnityEngine.Object
        {
            owned.Add(value);
            return value;
        }

        private void BuildScene()
        {
            target = new RenderTexture(Width, Height, 24, RenderTextureFormat.DefaultHDR) { name = "BasisGISettingsTarget" };
            target.Create();
            readback = new Texture2D(Width, Height, TextureFormat.RGBAFloat, false, true);

            GameObject cameraHost = Own(new GameObject("BasisGISettingsCamera"));
            camera = cameraHost.AddComponent<UnityEngine.Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 60f;
            camera.targetTexture = target;
            camera.allowMSAA = false;

            UniversalAdditionalCameraData cameraData = camera.GetUniversalAdditionalCameraData();
            cameraData.renderPostProcessing = true;
            cameraData.volumeLayerMask = ~0;
            cameraData.antialiasing = AntialiasingMode.None;

            GameObject sunHost = Own(new GameObject("BasisGISettingsSun"));
            sunHost.transform.rotation = Quaternion.Euler(52f, -24f, 0f);
            Light sun = sunHost.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 0.5f;
            sun.shadows = LightShadows.None;

            Material surface = Own(new Material(Shader.Find("Universal Render Pipeline/Lit")));
            surface.SetColor("_BaseColor", new Color(0.8f, 0.8f, 0.8f));
            AddBox(new Vector3(0f, 0f, 0f), new Vector3(14f, 0.2f, 14f), surface);
            AddBox(new Vector3(0f, 2f, 3.2f), new Vector3(14f, 5f, 0.2f), surface);

            Material glow = Own(new Material(Shader.Find("Universal Render Pipeline/Lit")));
            glow.SetColor("_BaseColor", Color.black);
            glow.SetColor("_EmissionColor", new Color(16f, 0.5f, 0.5f));
            glow.EnableKeyword("_EMISSION");
            glow.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            AddBox(new Vector3(-0.7f, 0.6f, 0.55f), new Vector3(0.45f, 0.45f, 0.45f), glow);

            GameObject emitterHost = Own(new GameObject("BasisGISettingsEmitter"));
            emitterHost.transform.position = new Vector3(0.6f, 0.75f, 0.2f);
            BasisGlobalIlluminationEmitter emitter = emitterHost.AddComponent<BasisGlobalIlluminationEmitter>();
            emitter.Color = Color.green;
            emitter.Intensity = 20f;
            emitter.Radius = 0.35f;
            emitter.Range = 12f;
            emitter.Register();

            camera.transform.position = new Vector3(0.1f, 1.7f, -2.6f);
            camera.transform.rotation = Quaternion.LookRotation(new Vector3(-0.1f, 0.3f, 0.6f) - camera.transform.position, Vector3.up);

            Vector3 screen = camera.WorldToScreenPoint(new Vector3(-0.05f, 0.101f, 0.55f));
            probe = new RectInt(Mathf.RoundToInt(screen.x) - 7, Mathf.RoundToInt(screen.y) - 7, 14, 14);
        }

        private void AddBox(Vector3 position, Vector3 scale, Material material)
        {
            GameObject box = Own(GameObject.CreatePrimitive(PrimitiveType.Cube));
            box.transform.position = position;
            box.transform.localScale = scale;
            box.GetComponent<MeshRenderer>().sharedMaterial = material;
        }

        private Color Render(int frames)
        {
            for (int frame = 0; frame < frames; frame++) { camera.Render(); }

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = target;
            readback.ReadPixels(new Rect(0, 0, Width, Height), 0, 0, false);
            readback.Apply(false);
            RenderTexture.active = previous;

            Color total = Color.clear;
            int count = 0;
            for (int y = probe.yMin; y < probe.yMax; y++)
            {
                for (int x = probe.xMin; x < probe.xMax; x++)
                {
                    if (x < 0 || y < 0 || x >= Width || y >= Height) { continue; }
                    total += readback.GetPixel(x, y);
                    count++;
                }
            }
            return count == 0 ? Color.clear : total / count;
        }

        private static string Invariant(float value) { return value.ToString(CultureInfo.InvariantCulture); }

        private static float Difference(Color a, Color b)
        {
            return Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b);
        }

        /// <summary>What the shader will actually read, as opposed to what the module wrote.</summary>
        private static BasisGlobalIlluminationVolume Stacked()
        {
            return VolumeManager.instance.stack != null ? VolumeManager.instance.stack.GetComponent<BasisGlobalIlluminationVolume>() : null;
        }

        private SMModuleGlobalIlluminationURP StartModule(BasisGlobalIlluminationMode mode)
        {
            host = new GameObject("BasisGISettingsModule");
            SMModuleGlobalIlluminationURP module = host.AddComponent<SMModuleGlobalIlluminationURP>();
            module.ValidSettingsChange(BasisSettingsDefaults.UseGlobalIllumination.BindingKey, "true");
            module.ValidSettingsChange(BasisSettingsDefaults.GlobalIlluminationMode.BindingKey,
                mode == BasisGlobalIlluminationMode.RayTraced ? "ray traced" : "screen space");
            module.ApplyOverride();
            return module;
        }

        private void AssertSliderMovesTheImage(BasisGlobalIlluminationMode mode, string bindingKey, float low, float high, Func<BasisGlobalIlluminationVolume, float> read)
        {
            SMModuleGlobalIlluminationURP module = StartModule(mode);

            module.ValidSettingsChange(bindingKey, Invariant(low));
            Color lowImage = Render(24);
            float lowOwned = read(module.GlobalIllumination);
            float lowStacked = Stacked() != null ? read(Stacked()) : float.NaN;

            module.ValidSettingsChange(bindingKey, Invariant(high));
            Color highImage = Render(24);
            float highOwned = read(module.GlobalIllumination);
            float highStacked = Stacked() != null ? read(Stacked()) : float.NaN;

            float difference = Difference(lowImage, highImage);
            string trace = $"{mode}/{bindingKey}: owned {lowOwned:F3}->{highOwned:F3}, stacked {lowStacked:F3}->{highStacked:F3}, image {lowImage} -> {highImage} (diff {difference:F4})";
            Debug.Log("[BasisGI] " + trace);

            Assert.AreEqual(high, highOwned, 0.001f, "the module did not write its own volume: " + trace);
            Assert.AreEqual(high, highStacked, 0.001f,
                "the module wrote its own volume but the camera's volume stack resolved something else, so the shader never sees the setting: " + trace);
            Assert.Greater(difference, 0.002f, "the setting reached the shader and still changed nothing on screen: " + trace);
        }

        [Test]
        public void TheIntensitySliderMovesTheScreenSpaceImage()
        {
            AssertSliderMovesTheImage(BasisGlobalIlluminationMode.ScreenSpace,
                BasisSettingsDefaults.GlobalIlluminationIntensity.BindingKey,
                BasisSettingsDefaults.GI_INTENSITY_MIN, BasisSettingsDefaults.GI_INTENSITY_MAX,
                gi => gi.intensity.value);
        }

        [Test]
        public void TheIntensitySliderMovesTheRayTracedImage()
        {
            AssertSliderMovesTheImage(BasisGlobalIlluminationMode.RayTraced,
                BasisSettingsDefaults.GlobalIlluminationIntensity.BindingKey,
                BasisSettingsDefaults.GI_INTENSITY_MIN, BasisSettingsDefaults.GI_INTENSITY_MAX,
                gi => gi.intensity.value);
        }

        [Test]
        public void TheEmitterIntensitySliderMovesTheScreenSpaceImage()
        {
            AssertSliderMovesTheImage(BasisGlobalIlluminationMode.ScreenSpace,
                BasisSettingsDefaults.GlobalIlluminationEmitterIntensity.BindingKey,
                BasisSettingsDefaults.GI_EMITTER_INTENSITY_MIN, BasisSettingsDefaults.GI_EMITTER_INTENSITY_MAX,
                gi => gi.emitterIntensity.value);
        }

        [Test]
        public void TheEmitterIntensitySliderMovesTheRayTracedImage()
        {
            AssertSliderMovesTheImage(BasisGlobalIlluminationMode.RayTraced,
                BasisSettingsDefaults.GlobalIlluminationEmitterIntensity.BindingKey,
                BasisSettingsDefaults.GI_EMITTER_INTENSITY_MIN, BasisSettingsDefaults.GI_EMITTER_INTENSITY_MAX,
                gi => gi.emitterIntensity.value);
        }
    }
}
#endif
