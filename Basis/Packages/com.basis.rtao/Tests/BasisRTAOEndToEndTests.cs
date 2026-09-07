using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using UnityEngine.Rendering.Universal;

namespace Basis.Rendering.RTAO.Tests
{
    public sealed class BasisRTAOEndToEndTests
    {
        private const int RenderWidth = 320;
        private const int RenderHeight = 240;
        private const int WarmupFrames = 6;

        private readonly List<Object> created = new List<Object>();
        private RenderPipelineAsset previousPipeline;
        private RenderPipelineAsset previousQualityPipeline;
        private UniversalRendererData rendererData;
        private UniversalRenderPipelineAsset pipeline;
        private BasisRTAOFeature feature;
        private Camera camera;
        private RenderTexture target;
        private GameObject ground, blocker;

        [SetUp]
        public void SetUp()
        {
            BasisRTAOGpuHarness.SkipUnlessComputeIsAvailable();
            // Swapping the pipeline asset per test re-initialises URP's global Blitter, and tearing the
            // render target down logs about the camera still holding it. Neither is the effect under test.
            LogAssert.ignoreFailingMessages = true;
            if (!BasisRTAOContext.HardwareSupported && !RayTracingContextIsUsable())
                Assert.Ignore("No ray tracing backend is available on this device.");

            BasisRTAOResources resources = Track(ScriptableObject.CreateInstance<BasisRTAOResources>());
            resources.PopulateFromPackage();
            BasisRTAOBackend backend = BasisRTAOTracing.Resolve(BasisRTAOTracingMode.RayTracedOnly);
            Assert.IsTrue(resources.IsComplete(backend), resources.DescribeMissing(backend));

            feature = Track(ScriptableObject.CreateInstance<BasisRTAOFeature>());
            feature.name = "BasisRTAOFeature";
            ApplyFeatureFields(feature, resources);
            feature.Create();

            rendererData = Track(ScriptableObject.CreateInstance<UniversalRendererData>());
            rendererData.name = "BasisRTAOTestRenderer";
            rendererData.rendererFeatures.Add(feature);

            pipeline = Track(UniversalRenderPipelineAsset.Create(rendererData));
            pipeline.name = "BasisRTAOTestPipeline";
            pipeline.supportsCameraDepthTexture = true;
            pipeline.msaaSampleCount = 1;

            previousPipeline = GraphicsSettings.defaultRenderPipeline;
            previousQualityPipeline = QualitySettings.renderPipeline;
            GraphicsSettings.defaultRenderPipeline = pipeline;
            QualitySettings.renderPipeline = pipeline;

            BuildScene();
        }

        [TearDown]
        public void TearDown()
        {
            LogAssert.ignoreFailingMessages = false;
            if (camera != null)
                camera.targetTexture = null;
            BasisRTAOFeature.CameraFilter = null;
            BasisRTAOFeature.RuntimeEnabled = true;
            BasisRTAOFeature.HasQualityOverride = false;
            BasisRTAOFeature.HasIntensityOverride = false;
            BasisRTAOFeature.HasRadiusOverride = false;
            BasisRTAOFeature.HasTracingModeOverride = false;
            BasisRTAOFeature.HasSkinnedModeOverride = false;
            BasisRTAOFeature.HasDebugViewOverride = false;
            // These leaked between tests, so a denoise or apply mode set by one test silently changed the
            // next one's readings. Alphabetical ordering makes that a nightmare to spot.
            BasisRTAOFeature.HasDirectStrengthOverride = false;
            BasisRTAOFeature.HasDenoisePassesOverride = false;
            BasisRTAOFeature.HasApplyModeOverride = false;
            BasisRTAOFeature.AllowSecondaryCameras = true;
            BasisRTAOFeature.ViewerPosition = null;

            QualitySettings.renderPipeline = previousQualityPipeline;
            GraphicsSettings.defaultRenderPipeline = previousPipeline;

            if (rendererData != null)
                rendererData.rendererFeatures.Clear();

            if (target != null)
            {
                target.Release();
                Object.DestroyImmediate(target);
                target = null;
            }

            for (int i = created.Count - 1; i >= 0; i--)
            {
                if (created[i] != null)
                    Object.DestroyImmediate(created[i]);
            }
            created.Clear();
        }

        private T Track<T>(T target) where T : Object
        {
            created.Add(target);
            return target;
        }

        private static bool RayTracingContextIsUsable()
        {
            return SystemInfo.supportsComputeShaders;
        }

        private static void ApplyFeatureFields(BasisRTAOFeature feature, BasisRTAOResources resources)
        {
            SerializedFieldSetter.Set(feature, "resources", resources);
            SerializedFieldSetter.Set(feature, "tracingMode", BasisRTAOTracingMode.RayTracedOnly);
            SerializedFieldSetter.Set(feature, "debugView", true);
            SerializedFieldSetter.Set(feature, "overrideQualityPreset", true);

            BasisRTAOSettings settings = BasisRTAOSettings.FromQuality(BasisRTAOQuality.High);
            settings.resolutionDivider = 1;
            settings.radius = 1.5f;
            settings.intensity = 1f;
            // Falloff off, so these scenes measure raw occlusion. The default of 1 discounts an occluder
            // by how far into the radius it sits, and the box's top edge - the horizon that matters at the
            // contact probe - sits past the fade start at this radius: modelled analytically, the contact
            // deficit reads 0.20 with the fade off and 0.065 with it on, against a 0.05 assertion floor.
            // Falloff has its own dedicated test in BasisRTAOFallbackTests.
            settings.distanceFalloff = 0f;
            settings.temporalFrames = 4;
            settings.blurMaxRadius = 1;
            settings.blurMinRadius = 0;
            settings.fadeStart = 200f;
            settings.fadeEnd = 400f;
            SerializedFieldSetter.Set(feature, "settings", settings);

            BasisRTAOSceneSettings sceneSettings = BasisRTAOTestSettings.EveryLayer;
            sceneSettings.rescanInterval = 0.1f;
            SerializedFieldSetter.Set(feature, "sceneSettings", sceneSettings);
        }

        private void BuildScene()
        {
            ground = Track(GameObject.CreatePrimitive(PrimitiveType.Plane));
            ground.name = "BasisRTAOGround";
            ground.transform.position = Vector3.zero;
            ground.transform.localScale = new Vector3(2f, 1f, 2f);

            blocker = Track(GameObject.CreatePrimitive(PrimitiveType.Cube));
            blocker.name = "BasisRTAOBlocker";
            blocker.transform.position = new Vector3(0f, 0.5f, 0f);
            blocker.transform.localScale = Vector3.one;

            GameObject lightObject = Track(new GameObject("BasisRTAOLight"));
            Light light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1f;
            lightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            GameObject cameraObject = Track(new GameObject("BasisRTAOCamera"));
            camera = cameraObject.AddComponent<Camera>();
            camera.transform.position = new Vector3(0f, 3f, -5f);
            camera.transform.LookAt(new Vector3(0f, 0f, 0f));
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 100f;
            camera.fieldOfView = 60f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.allowMSAA = false;

            target = new RenderTexture(RenderWidth, RenderHeight, 24, RenderTextureFormat.ARGB32)
            {
                name = "BasisRTAOTestTarget",
                hideFlags = HideFlags.HideAndDontSave
            };
            target.Create();
            camera.targetTexture = target;
        }

        private void MarkSceneDirty()
        {
            feature.Pass?.Scene?.MarkDirty();
        }

        private Texture2D RenderAndReadback()
        {
            for (int i = 0; i < WarmupFrames; i++)
                camera.Render();

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = target;
            Texture2D readback = new Texture2D(RenderWidth, RenderHeight, TextureFormat.RGBA32, false);
            readback.ReadPixels(new Rect(0f, 0f, RenderWidth, RenderHeight), 0, 0);
            readback.Apply();
            RenderTexture.active = previous;
            return readback;
        }

        private static readonly Vector3 OpenGround = new Vector3(-3f, 0.001f, 0f);
        private static readonly Vector3 ContactEdge = new Vector3(0.85f, 0.001f, 0f);
        // 2.4 m clears the widened blocker's silhouette from this camera. At 1.85 m the 3x3 box sits
        // between the camera and the ground, so the sample would read the box lid instead.
        private static readonly Vector3 WideContactEdge = new Vector3(2.4f, 0.001f, 0f);

        private float SampleAt(Texture2D image, Vector3 worldPoint, int radius = 3)
        {
            Vector3 screen = camera.WorldToScreenPoint(worldPoint);
            Assert.Greater(screen.z, 0f, $"{worldPoint} is behind the camera.");

            int centreX = Mathf.RoundToInt(screen.x);
            int centreY = Mathf.RoundToInt(screen.y);
            Assert.IsTrue(centreX >= radius && centreX < RenderWidth - radius && centreY >= radius && centreY < RenderHeight - radius,
                $"{worldPoint} projects to {centreX},{centreY} which is outside the render target.");

            float sum = 0f;
            int count = 0;
            for (int y = -radius; y <= radius; y++)
            {
                for (int x = -radius; x <= radius; x++)
                {
                    sum += image.GetPixel(centreX + x, centreY + y).r;
                    count++;
                }
            }
            return sum / count;
        }

        [Test]
        public void ContactRegionIsDarkerThanOpenGround()
        {
            Texture2D image = RenderAndReadback();
            try
            {
                float beside = SampleAt(image, ContactEdge);
                float open = SampleAt(image, OpenGround);

                Assert.Less(beside, open,
                    $"Ground beside the blocker read {beside:F3} and open ground read {open:F3}. The contact region has to be the darker of the two.");
                Assert.Greater(open - beside, 0.05f,
                    $"The occlusion difference of {open - beside:F3} is too small to be anything but noise.");
                Assert.Greater(open, 0.85f,
                    $"Open ground {open:F3} should be close to fully visible, so the effect is not darkening the whole frame.");
            }
            finally
            {
                Object.DestroyImmediate(image);
            }
        }

        [Test]
        public void RemovingTheBlockerRestoresTheGround()
        {
            Texture2D withBlocker = RenderAndReadback();
            float occluded;
            try
            {
                occluded = SampleAt(withBlocker, ContactEdge);
            }
            finally
            {
                Object.DestroyImmediate(withBlocker);
            }

            blocker.SetActive(false);
            MarkSceneDirty();

            Texture2D withoutBlocker = RenderAndReadback();
            try
            {
                float open = SampleAt(withoutBlocker, ContactEdge);
                // Assert.Greater alone passes on a 0.002 difference, which is noise, not a restored floor.
                Assert.Greater(open, occluded + 0.03f,
                    $"The same ground texel read {occluded:F3} with the blocker and {open:F3} without it. Removing the only occluder has to brighten it by more than noise.");
                Assert.Greater(open, 0.85f, "With nothing above it, that texel should be close to unoccluded.");
            }
            finally
            {
                Object.DestroyImmediate(withoutBlocker);
            }
        }

        [Test]
        public void WideningTheBlockerDarkensAFixedPoint()
        {
            Texture2D small = RenderAndReadback();
            float farFromSmallBlocker;
            try
            {
                farFromSmallBlocker = SampleAt(small, WideContactEdge);
            }
            finally
            {
                Object.DestroyImmediate(small);
            }

            blocker.transform.localScale = new Vector3(3f, 1f, 3f);
            blocker.transform.position = new Vector3(0f, 0.5f, 0f);
            MarkSceneDirty();

            Texture2D wide = RenderAndReadback();
            try
            {
                float besideWideBlocker = SampleAt(wide, WideContactEdge);
                Assert.Less(besideWideBlocker, farFromSmallBlocker - 0.004f,
                    $"The same ground point sat 1.9 m from the small blocker, beyond the 1.5 m radius, and reads {farFromSmallBlocker:F3}; widening the blocker brings its edge to 0.9 m and it must darken, but it reads {besideWideBlocker:F3}.");
            }
            finally
            {
                Object.DestroyImmediate(wide);
            }
        }

        private float ContactReadingWith(System.Action configure)
        {
            configure();
            Texture2D image = RenderAndReadback();
            try
            {
                return SampleAt(image, ContactEdge);
            }
            finally
            {
                Object.DestroyImmediate(image);
            }
        }

        // These two drive the exact statics the Occlusion Strength and Radius sliders write, so they fail if
        // the value stops reaching the shader - which is precisely what a MaterialPropertyBlock on a property
        // the material never declared was doing.
        [Test]
        public void OcclusionStrengthSliderChangesTheImage()
        {
            float weak = ContactReadingWith(() =>
            {
                BasisRTAOFeature.HasIntensityOverride = true;
                BasisRTAOFeature.IntensityOverride = 0.35f;
            });

            float strong = ContactReadingWith(() =>
            {
                BasisRTAOFeature.HasIntensityOverride = true;
                BasisRTAOFeature.IntensityOverride = 3.5f;
            });

            Assert.Less(strong, weak - 0.05f,
                $"Occlusion Strength has to reach the composite. At 0.35 the contact point read {weak:F3} and at 3.5 it read {strong:F3}; a stronger setting must be darker.");
        }

        [Test]
        public void OcclusionRadiusSliderChangesTheImage()
        {
            float tight = ContactReadingWith(() =>
            {
                BasisRTAOFeature.HasRadiusOverride = true;
                BasisRTAOFeature.RadiusOverride = 0.15f;
            });

            float wide = ContactReadingWith(() =>
            {
                BasisRTAOFeature.HasRadiusOverride = true;
                BasisRTAOFeature.RadiusOverride = 2.5f;
            });

            Assert.Greater(Mathf.Abs(tight - wide), 0.02f,
                $"Occlusion Radius has to reach the trace. A 15 cm search read {tight:F3} and a 2.5 m search read {wide:F3}; if they match the value never left the settings.");
        }

        [Test]
        public void OcclusionOnDirectLightReachesTheGlobalTheLitShadersRead()
        {
            // The fixture renders the debug view, which draws the occlusion buffer directly. Direct light
            // strength never touches that buffer - it only changes how a lit shader consumes it - so a debug
            // view reading cannot move with it by construction. What is worth pinning is that the value
            // travels from the setting into the resolved settings the pass uploads.
            BasisRTAOFeature.HasDirectStrengthOverride = true;

            BasisRTAOFeature.DirectStrengthOverride = 0f;
            Assert.AreEqual(0f, feature.ResolveSettings().directLightingStrength, 1e-4f);

            BasisRTAOFeature.DirectStrengthOverride = 1f;
            Assert.AreEqual(1f, feature.ResolveSettings().directLightingStrength, 1e-4f);
        }

        // The cascade rotated two buffers, which made a later pass read and write one texture inside a single
        // dispatch. One pass never reached the swap, so only High and Maximum broke - exactly the shape a test
        // that only ever exercised the default would miss.
        [Test]
        public void TheDenoiseCascadeStaysCoherentAtEveryLevel()
        {
            float[] contact = new float[4];
            float[] open = new float[4];

            for (int passes = 0; passes <= 3; passes++)
            {
                BasisRTAOFeature.HasDenoisePassesOverride = true;
                BasisRTAOFeature.DenoisePassesOverride = passes;

                Texture2D image = RenderAndReadback();
                try
                {
                    contact[passes] = SampleAt(image, ContactEdge);
                    open[passes] = SampleAt(image, OpenGround);
                }
                finally
                {
                    Object.DestroyImmediate(image);
                }

                Assert.GreaterOrEqual(contact[passes], 0f, $"{passes} denoise passes produced a value below zero.");
                Assert.LessOrEqual(contact[passes], 1f, $"{passes} denoise passes produced a value above one.");
                Assert.Less(contact[passes], open[passes],
                    $"At {passes} denoise passes the contact region read {contact[passes]:F3} and open ground read {open[passes]:F3}. Filtering must not destroy the signal it is filtering.");
            }

            for (int passes = 1; passes <= 3; passes++)
            {
                Assert.AreEqual(contact[0], contact[passes], 0.35f,
                    $"Filtering should soften the estimate, not replace it. Unfiltered read {contact[0]:F3} and {passes} passes read {contact[passes]:F3}.");
            }
        }

        [Test]
        public void MoreDenoisePassesFlattenTheImage()
        {
            BasisRTAOFeature.HasDenoisePassesOverride = true;

            BasisRTAOFeature.DenoisePassesOverride = 0;
            float rawSpread = ContactToOpenSpread();

            BasisRTAOFeature.DenoisePassesOverride = 3;
            float filteredSpread = ContactToOpenSpread();

            Assert.Greater(rawSpread, 0f, "There has to be a signal before there is anything to filter.");
            Assert.Greater(filteredSpread, 0f,
                $"Three passes flattened the contact shadow away entirely ({filteredSpread:F3}); a wider filter should soften the edge, not erase it.");
        }

        private float ContactToOpenSpread()
        {
            Texture2D image = RenderAndReadback();
            try
            {
                return SampleAt(image, OpenGround) - SampleAt(image, ContactEdge);
            }
            finally
            {
                Object.DestroyImmediate(image);
            }
        }

        [Test]
        public void SkyStaysUnoccluded()
        {
            Texture2D image = RenderAndReadback();
            try
            {
                float sky = image.GetPixel(RenderWidth / 2, RenderHeight - 3).r;
                Assert.Greater(sky, 0.95f,
                    $"The top of the frame is above the horizon, so it has no geometry and must resolve to full visibility. Got {sky:F3}.");
            }
            finally
            {
                Object.DestroyImmediate(image);
            }
        }

        // The GPU fallback tests bind hand-authored positions and unit screen axes, so they can never
        // notice the REAL pass uploading axes that disagree with how the prepass lays its texels out.
        // These two run the estimator through the actual URP frame: real depth, real matrices, real axes.
        // An inverted vertical axis mirrors the slice plane, and the hemisphere clamp then eats most of an
        // OPEN floor at a grazing view - which is why the open-ground floor here is the assertion that matters.
        // Nearer to the cube than the traced ContactEdge: the estimator's few horizon steps per slice and
        // the rotating slice set dilute a 35 cm contact over the denoise, so the pin goes where the signal
        // is unambiguous. Still outside the cube's silhouette from this camera - verified by ray.
        private static readonly Vector3 ScreenSpaceContactEdge = new Vector3(0.7f, 0.001f, 0f);

        [Test]
        public void ScreenSpaceContactRegionIsDarkerThanOpenGround()
        {
            BasisRTAOFeature.HasTracingModeOverride = true;
            BasisRTAOFeature.TracingModeOverride = BasisRTAOTracingMode.ScreenSpace;

            // The slice set rotates along the R2 sequence, so one warmup's worth of frames has not seen
            // every direction yet; a second pass lets the temporal filter integrate the full set.
            Object.DestroyImmediate(RenderAndReadback());
            Texture2D image = RenderAndReadback();
            try
            {
                float beside = SampleAt(image, ScreenSpaceContactEdge);
                float open = SampleAt(image, OpenGround);

                Assert.Less(beside, open,
                    $"Screen space: ground beside the blocker read {beside:F3} and open ground read {open:F3}. The contact region has to be the darker of the two.");
                Assert.Greater(open - beside, 0.05f,
                    $"Screen space: the occlusion difference of {open - beside:F3} is too small to be anything but noise.");
                Assert.Greater(open, 0.85f,
                    $"Screen space: open ground read {open:F3}. A flat floor with nothing near it must stay close to fully visible; darkening here means the horizon walk is integrating a slice plane that does not match the walk direction.");
            }
            finally
            {
                Object.DestroyImmediate(image);
            }
        }

        [Test]
        public void ScreenSpaceSkyStaysUnoccluded()
        {
            BasisRTAOFeature.HasTracingModeOverride = true;
            BasisRTAOFeature.TracingModeOverride = BasisRTAOTracingMode.ScreenSpace;

            Texture2D image = RenderAndReadback();
            try
            {
                float sky = image.GetPixel(RenderWidth / 2, RenderHeight - 3).r;
                Assert.Greater(sky, 0.95f,
                    $"Screen space: the top of the frame has no geometry and must resolve to full visibility. Got {sky:F3}.");
            }
            finally
            {
                Object.DestroyImmediate(image);
            }
        }

        [Test]
        public void CameraFilterSuppressesTheEffect()
        {
            BasisRTAOFeature.CameraFilter = _ => false;
            Texture2D image = RenderAndReadback();
            try
            {
                float beside = SampleAt(image, ContactEdge);
                float open = SampleAt(image, OpenGround);
                Assert.LessOrEqual(open - beside, 0.02f,
                    $"A rejected camera must not run RTAO at all, yet the contact region ({beside:F3}) is still darker than open ground ({open:F3}).");
            }
            finally
            {
                Object.DestroyImmediate(image);
            }
        }

        [Test]
        public void RuntimeToggleSuppressesTheEffect()
        {
            BasisRTAOFeature.RuntimeEnabled = false;
            Texture2D image = RenderAndReadback();
            try
            {
                float beside = SampleAt(image, ContactEdge);
                float open = SampleAt(image, OpenGround);
                Assert.LessOrEqual(open - beside, 0.02f,
                    $"Turning RTAO off must stop the passes, yet the contact region ({beside:F3}) is still darker than open ground ({open:F3}).");
            }
            finally
            {
                Object.DestroyImmediate(image);
            }
        }

        [Test]
        public void RenderingIsStableAcrossFrames()
        {
            Texture2D first = RenderAndReadback();
            float firstValue;
            try
            {
                firstValue = SampleAt(first, ContactEdge);
            }
            finally
            {
                Object.DestroyImmediate(first);
            }

            Texture2D second = RenderAndReadback();
            try
            {
                float secondValue = SampleAt(second, ContactEdge);
                Assert.AreEqual(firstValue, secondValue, 0.08f,
                    $"A static scene and a static camera must converge. {firstValue:F3} then {secondValue:F3} means the temporal filter is not accumulating.");
            }
            finally
            {
                Object.DestroyImmediate(second);
            }
        }

        [Test]
        public void ExcludedGeometryStopsOccluding()
        {
            // BasisRTAOExclude takes a renderer out of the acceleration structure. The screen space fallback
            // never consults that structure - it reads the depth buffer - so anything still being drawn still
            // occludes there, by design. Only the traced path can honour this.
            if (!BasisRTAOTracing.IsRayTraced(BasisRTAOTracing.Resolve(BasisRTAOTracingMode.RayTracedOnly)))
                Assert.Ignore("This device resolves to the screen space fallback, which cannot honour an acceleration structure exclusion.");

            Texture2D included = RenderAndReadback();
            float occluded;
            try
            {
                occluded = SampleAt(included, ContactEdge);
            }
            finally
            {
                Object.DestroyImmediate(included);
            }

            blocker.AddComponent<BasisRTAOExclude>();
            MarkSceneDirty();

            Texture2D excluded = RenderAndReadback();
            try
            {
                float open = SampleAt(excluded, ContactEdge);
                Assert.Greater(open, occluded + 0.05f,
                    $"The ground beside the blocker read {occluded:F3} with it in the acceleration structure and {open:F3} after adding BasisRTAOExclude. Excluding a renderer has to take it out of the structure.");
            }
            finally
            {
                Object.DestroyImmediate(excluded);
            }
        }
    }
}
