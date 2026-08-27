using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.TestTools;

namespace Basis.Rendering.RTAO.Tests
{
    /// <summary>
    /// Detects a hard line in the occlusion at a constant depth — the artifact class where something in the
    /// pipeline steps as a function of distance from the camera. Rather than assert an absolute value, this
    /// walks a strip of floor away from the camera with a wall beside it, so there is occlusion at every
    /// depth, and fails if the occlusion jumps between two neighbouring depths far more than it does
    /// anywhere else along the strip. A smooth falloff passes; a step does not.
    /// </summary>
    public sealed class BasisRTAODepthContinuityTests
    {
        private const int RenderWidth = 640;
        private const int RenderHeight = 400;
        private const int WarmupFrames = 8;
        private const float ScanRange = 100f;

        private readonly List<Object> created = new List<Object>();
        private RenderPipelineAsset previousPipeline, previousQualityPipeline;
        private UniversalRendererData rendererData;
        private UniversalRenderPipelineAsset pipeline;
        private BasisRTAOFeature feature;
        private Camera camera;
        private RenderTexture target;

        [SetUp]
        public void SetUp()
        {
            BasisRTAOGpuHarness.SkipUnlessComputeIsAvailable();
            if (BasisRTAOTracing.Resolve(BasisRTAOTracingMode.Auto) == BasisRTAOBackend.None)
                Assert.Ignore("No RTAO backend is available on this device.");

            LogAssert.ignoreFailingMessages = true;

            BasisRTAOResources resources = Track(ScriptableObject.CreateInstance<BasisRTAOResources>());
            resources.PopulateFromPackage();

            feature = Track(ScriptableObject.CreateInstance<BasisRTAOFeature>());
            feature.name = "BasisRTAOFeature";
            SerializedFieldSetter.Set(feature, "resources", resources);
            SerializedFieldSetter.Set(feature, "tracingMode", BasisRTAOTracingMode.Auto);
            SerializedFieldSetter.Set(feature, "debugView", true);
            SerializedFieldSetter.Set(feature, "overrideQualityPreset", true);

            // the shipping look: a tight contact radius, full strength, applied to the final image
            BasisRTAOSettings settings = BasisRTAOSettings.FromQuality(BasisRTAOQuality.High);
            settings.resolutionDivider = 2;
            settings.radius = 0.1f;
            settings.intensity = 1f;
            settings.fadeStart = 500f;
            settings.fadeEnd = 1000f;
            settings.temporalFrames = 4;
            SerializedFieldSetter.Set(feature, "settings", settings);

            BasisRTAOSceneSettings sceneSettings = BasisRTAOTestSettings.EveryLayer;
            sceneSettings.rescanInterval = 0.05f;
            sceneSettings.skinnedMode = BasisRTAOSkinnedMode.Off;
            SerializedFieldSetter.Set(feature, "sceneSettings", sceneSettings);
            feature.Create();

            rendererData = Track(ScriptableObject.CreateInstance<UniversalRendererData>());
            rendererData.rendererFeatures.Add(feature);
            pipeline = Track(UniversalRenderPipelineAsset.Create(rendererData));
            pipeline.supportsCameraDepthTexture = true;
            pipeline.msaaSampleCount = 1;

            previousPipeline = GraphicsSettings.defaultRenderPipeline;
            previousQualityPipeline = QualitySettings.renderPipeline;
            GraphicsSettings.defaultRenderPipeline = pipeline;
            QualitySettings.renderPipeline = pipeline;

            BuildCorridor();
        }

        [TearDown]
        public void TearDown()
        {
            LogAssert.ignoreFailingMessages = false;
            BasisRTAOFeature.HasDebugStageOverride = false;
            BasisRTAOFeature.DebugStageOverride = BasisRTAODebugStage.Final;
            if (camera != null)
                camera.targetTexture = null;

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

        /// <summary>
        /// Both shipping renderers in this project run MSAA with depth priming Forced, and that combination
        /// changes where the depth this whole prepass reads comes from: priming makes URP render depth into
        /// the MSAA attachment and copy it out, rather than filling the depth texture directly. The fixture
        /// defaulted to neither, so it was not testing the configuration the artifact appears in.
        /// </summary>
        private void ConfigureRenderer(int msaaSamples, DepthPrimingMode priming)
        {
            pipeline.msaaSampleCount = msaaSamples;
            rendererData.depthPrimingMode = priming;
            rendererData.SetDirty();
            RebuildTarget(msaaSamples);
        }

        /// <summary>
        /// Setting the sample count on the pipeline asset is not enough to make the camera render multisampled,
        /// and until this existed none of the parameterised MSAA cases rendered at more than one sample - four
        /// runs of the same frame, passing whatever they were asked. UniversalRenderPipeline only reads the
        /// asset's count when <c>camera.allowMSAA</c> is set, and when the camera draws into a render texture
        /// that texture's own <c>antiAliasing</c> is what it takes instead (UniversalRenderPipeline.cs, the
        /// msaaSamples block in InitializeCameraData). The fixture set neither.
        /// </summary>
        private void RebuildTarget(int antiAliasing)
        {
            int samples = Mathf.Max(1, antiAliasing);
            if (target != null && target.antiAliasing == samples)
                return;

            if (target != null)
            {
                camera.targetTexture = null;
                target.Release();
                Object.DestroyImmediate(target);
            }

            target = new RenderTexture(RenderWidth, RenderHeight, 24, RenderTextureFormat.ARGB32)
            {
                name = "BasisRTAODepthScanTarget",
                antiAliasing = samples,
                hideFlags = HideFlags.HideAndDontSave
            };
            target.Create();
            camera.allowMSAA = samples > 1;
            camera.targetTexture = target;
        }

        private T Track<T>(T value) where T : Object
        {
            created.Add(value);
            return value;
        }

        private void BuildCorridor()
        {
            GameObject floor = Track(GameObject.CreatePrimitive(PrimitiveType.Cube));
            floor.name = "BasisRTAOFloor";
            floor.transform.position = new Vector3(0f, -0.5f, 15f);
            floor.transform.localScale = new Vector3(12f, 1f, 60f);

            // a long low wall running away down the strip, so every depth has an occluder within 10 cm
            GameObject wall = Track(GameObject.CreatePrimitive(PrimitiveType.Cube));
            wall.name = "BasisRTAOWall";
            wall.transform.position = new Vector3(0f, 0.5f, 15f);
            wall.transform.localScale = new Vector3(0.2f, 1f, 60f);

            GameObject lightObject = Track(new GameObject("BasisRTAOLight"));
            Light light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1f;
            lightObject.transform.rotation = Quaternion.Euler(55f, -25f, 0f);

            GameObject cameraObject = Track(new GameObject("BasisRTAOCamera"));
            camera = cameraObject.AddComponent<Camera>();
            camera.transform.position = new Vector3(1.2f, 1.1f, -3f);
            camera.transform.LookAt(new Vector3(0.3f, 0f, 9f));
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 200f;
            camera.fieldOfView = 60f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;

            RebuildTarget(1);
        }

        private Texture2D RenderAndReadback()
        {
            for (int i = 0; i < WarmupFrames; i++)
                camera.Render();

            // ReadPixels cannot read a multisampled surface, so the frame is resolved through a copy that
            // carries the target's own format and colour space - the normal decode below undoes an sRGB
            // encode, and a resolve that changed it would tilt every normal it read.
            RenderTextureDescriptor descriptor = target.descriptor;
            descriptor.msaaSamples = 1;
            descriptor.depthBufferBits = 0;
            RenderTexture resolved = RenderTexture.GetTemporary(descriptor);

            RenderTexture previous = RenderTexture.active;
            Graphics.Blit(target, resolved);
            RenderTexture.active = resolved;
            Texture2D image = new Texture2D(RenderWidth, RenderHeight, TextureFormat.RGBA32, false);
            image.ReadPixels(new Rect(0f, 0f, RenderWidth, RenderHeight), 0, 0);
            image.Apply();
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(resolved);
            return image;
        }

        private struct Sample
        {
            public float depth;
            public float occlusion;
        }

        private List<Sample> ScanAlongDepth(Texture2D image)
        {
            List<Sample> samples = new List<Sample>();

            // 5 cm from the wall face, marching away from the camera
            for (float z = 0.5f; z <= 14f; z += 0.25f)
            {
                Vector3 world = new Vector3(0.15f, 0.001f, z);
                Vector3 screen = camera.WorldToScreenPoint(world);
                if (screen.z <= 0f)
                    continue;

                int x = Mathf.RoundToInt(screen.x);
                int y = Mathf.RoundToInt(screen.y);
                if (x < 2 || y < 2 || x >= RenderWidth - 2 || y >= RenderHeight - 2)
                    continue;

                float sum = 0f;
                int count = 0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        sum += image.GetPixel(x + dx, y + dy).r;
                        count++;
                    }
                }

                samples.Add(new Sample { depth = screen.z, occlusion = sum / count });
            }

            return samples;
        }

        [Test]
        public void OcclusionHasNoHardLineAlongDepth()
        {
            Texture2D image = RenderAndReadback();
            List<Sample> samples;
            try
            {
                samples = ScanAlongDepth(image);
            }
            finally
            {
                Object.DestroyImmediate(image);
            }

            Assert.Greater(samples.Count, 12, "The depth scan did not land enough samples on screen to say anything.");

            List<float> steps = new List<float>();
            for (int i = 1; i < samples.Count; i++)
                steps.Add(Mathf.Abs(samples[i].occlusion - samples[i - 1].occlusion));

            List<float> sorted = new List<float>(steps);
            sorted.Sort();
            float median = sorted[sorted.Count / 2];

            int worst = 0;
            for (int i = 1; i < steps.Count; i++)
            {
                if (steps[i] > steps[worst])
                    worst = i;
            }

            // A smooth falloff has every step close to the median. A hard line is one step far above it.
            float allowed = Mathf.Max(0.08f, median * 6f);
            Assert.LessOrEqual(steps[worst], allowed,
                $"Occlusion jumped {steps[worst]:F3} between {samples[worst].depth:F2} m and {samples[worst + 1].depth:F2} m, " +
                $"against a median step of {median:F3} along the whole strip. That is a hard line at a constant depth, " +
                $"not a falloff.");
        }

        /// <summary>
        /// Stands the camera close to a large flat plane with nothing else in the scene. A flat surface has
        /// nothing to occlude it, so the correct answer is full visibility everywhere and any variation at all
        /// is an artifact. That makes this the sharpest available test of a line seen up close on a flat wall:
        /// there is no real occlusion for it to hide inside.
        /// </summary>
        private void StandCloseToAFlatWall(float distance)
        {
            for (int i = created.Count - 1; i >= 0; i--)
            {
                if (created[i] is GameObject existing && existing != camera.gameObject && existing.GetComponent<Light>() == null)
                {
                    Object.DestroyImmediate(existing);
                    created.RemoveAt(i);
                }
            }

            GameObject wall = Track(GameObject.CreatePrimitive(PrimitiveType.Cube));
            wall.name = "BasisRTAOFlatWall";
            wall.transform.position = new Vector3(0f, 0f, 0f);
            wall.transform.localScale = new Vector3(40f, 40f, 1f);

            camera.transform.position = new Vector3(0f, 0f, -0.5f - distance);
            camera.transform.rotation = Quaternion.identity;
        }

        private List<Sample> ScanDownTheScreen(Texture2D image)
        {
            List<Sample> samples = new List<Sample>();
            int x = RenderWidth / 2;

            for (int y = 4; y < RenderHeight - 4; y += 2)
            {
                float sum = 0f;
                for (int dx = -2; dx <= 2; dx++)
                    sum += image.GetPixel(x + dx, y).r;

                samples.Add(new Sample { depth = y, occlusion = sum / 5f });
            }

            return samples;
        }

        [Test]
        public void AFlatWallUpCloseHasNoBandAcrossIt([Values(0.25f, 0.5f, 1f, 2f)] float distance)
        {
            StandCloseToAFlatWall(distance);

            Texture2D image = RenderAndReadback();
            List<Sample> samples;
            try
            {
                samples = ScanDownTheScreen(image);
            }
            finally
            {
                Object.DestroyImmediate(image);
            }

            Assert.Greater(samples.Count, 40);

            float lowest = 1f, highest = 0f;
            int worst = 0;
            for (int i = 0; i < samples.Count; i++)
            {
                lowest = Mathf.Min(lowest, samples[i].occlusion);
                highest = Mathf.Max(highest, samples[i].occlusion);
                if (i > 0 && Mathf.Abs(samples[i].occlusion - samples[i - 1].occlusion) >
                    Mathf.Abs(samples[worst].occlusion - samples[Mathf.Max(0, worst - 1)].occlusion))
                    worst = i;
            }

            float step = worst > 0 ? Mathf.Abs(samples[worst].occlusion - samples[worst - 1].occlusion) : 0f;

            // Nothing in this scene can occlude anything, so the whole wall should read the same.
            Assert.Less(highest - lowest, 0.06f,
                $"Standing {distance:F2} m from a bare flat wall, occlusion ranged from {lowest:F3} to " +
                $"{highest:F3} down the screen. A flat surface has nothing to occlude it, so every one of " +
                "those readings should be the same. This is the artifact.");

            Assert.Less(step, 0.03f,
                $"Occlusion stepped {step:F3} between two neighbouring rows on a bare flat wall at " +
                $"{distance:F2} m. That is a hard line, and there is no geometry that could cause one.");
        }

        /// <summary>
        /// A bare floor with the camera low and looking along it. Nothing here can occlude anything either,
        /// but unlike the wall the surface recedes, so rays leave at grazing angles and the distance the
        /// origin bias and the noise cell are scaled by sweeps the whole screen. A horizontal line at a fixed
        /// distance would land here.
        /// </summary>
        private void StandOnABareFloor(float eyeHeight)
        {
            for (int i = created.Count - 1; i >= 0; i--)
            {
                if (created[i] is GameObject existing && existing != camera.gameObject && existing.GetComponent<Light>() == null)
                {
                    Object.DestroyImmediate(existing);
                    created.RemoveAt(i);
                }
            }

            GameObject floor = Track(GameObject.CreatePrimitive(PrimitiveType.Cube));
            floor.name = "BasisRTAOBareFloor";
            floor.transform.position = new Vector3(0f, -0.5f, 150f);
            floor.transform.localScale = new Vector3(400f, 1f, 400f);

            camera.transform.position = new Vector3(0f, eyeHeight, 0f);
            camera.transform.rotation = Quaternion.Euler(12f, 0f, 0f);
        }

        [Test]
        public void ABareFloorHasNoLineAcrossIt([Values(0.4f, 1.1f, 1.8f)] float eyeHeight)
        {
            StandOnABareFloor(eyeHeight);

            Texture2D image = RenderAndReadback();
            List<Sample> samples;
            try
            {
                samples = ScanDownTheScreen(image);
            }
            finally
            {
                Object.DestroyImmediate(image);
            }

            Assert.Greater(samples.Count, 40);

            // Only rows that actually landed on the floor mean anything; the sky above the horizon reads full
            // visibility by definition and would look like a step next to the floor below it.
            List<Sample> onFloor = new List<Sample>();
            foreach (Sample sample in samples)
            {
                Ray ray = camera.ScreenPointToRay(new Vector3(RenderWidth * 0.5f, sample.depth, 0f));
                if (ray.direction.y < -0.02f)
                    onFloor.Add(sample);
            }

            Assert.Greater(onFloor.Count, 20, "Not enough of the screen landed on the floor to say anything.");

            float lowest = 1f, highest = 0f;
            float worstStep = 0f;
            int worstAt = 0;
            for (int i = 0; i < onFloor.Count; i++)
            {
                lowest = Mathf.Min(lowest, onFloor[i].occlusion);
                highest = Mathf.Max(highest, onFloor[i].occlusion);
                if (i == 0)
                    continue;

                float step = Mathf.Abs(onFloor[i].occlusion - onFloor[i - 1].occlusion);
                if (step > worstStep)
                {
                    worstStep = step;
                    worstAt = i;
                }
            }

            Assert.Less(highest - lowest, 0.06f,
                $"On a bare floor with the camera at {eyeHeight:F2} m, occlusion ranged from {lowest:F3} to " +
                $"{highest:F3}. There is no geometry here that could occlude anything, so this is all artifact.");

            Assert.Less(worstStep, 0.03f,
                $"Occlusion stepped {worstStep:F3} between two neighbouring rows on a bare floor at " +
                $"row {onFloor[worstAt].depth:F0}, camera at {eyeHeight:F2} m. That is a horizontal line " +
                "with nothing in the scene to explain it.");
        }

        /// <summary>
        /// Reads the normal buffer instead of the occlusion. The tracer builds its hemisphere around this
        /// normal, so a fault here is upstream of everything: the rays, the accumulation and the blur all
        /// inherit it. On a bare flat surface the normal is the same at every pixel, so any step at all is a
        /// defect, and unlike occlusion there is no falloff it could be mistaken for.
        /// </summary>
        private void ShowTheNormalBuffer()
        {
            BasisRTAOFeature.HasDebugStageOverride = true;
            BasisRTAOFeature.DebugStageOverride = BasisRTAODebugStage.Normal;
        }

        /// <summary>
        /// The readback target is sRGB encoded, so a channel holding 0.5 comes back as 0.7354. Reading that
        /// as linear tilts a floor's straight-up normal to 33 degrees off vertical, which is what the first
        /// run of this diagnostic reported before the conversion was here.
        /// </summary>
        private static float SrgbToLinear(float value)
        {
            return value <= 0.04045f ? value / 12.92f : Mathf.Pow((value + 0.055f) / 1.055f, 2.4f);
        }

        private static Vector3 DecodeNormal(Color pixel)
        {
            // The debug view writes normal * 0.5 + 0.5.
            Vector3 normal = new Vector3(SrgbToLinear(pixel.r), SrgbToLinear(pixel.g), SrgbToLinear(pixel.b))
                             * 2f - Vector3.one;
            return normal.sqrMagnitude < 1e-6f ? Vector3.zero : normal.normalized;
        }

        private struct NormalSample
        {
            public int row;
            public Vector3 normal;
            public Vector3 surface;
        }

        /// <summary>
        /// Only rows that land on the floor. Above the horizon the prepass writes its sky fallback, which is a
        /// different normal by design, and the step from the last floor row to the first sky row is a right
        /// angle - real, expected, and nothing to do with a line across a surface.
        /// </summary>
        private List<NormalSample> ScanNormalsDownTheScreen(Texture2D image)
        {
            // Edit mode does not step physics, so colliders sit wherever they were when they were created
            // until the transforms are pushed across. Without this every raycast misses and the scan is empty.
            Physics.SyncTransforms();

            List<NormalSample> samples = new List<NormalSample>();
            int x = RenderWidth / 2;

            for (int y = 4; y < RenderHeight - 4; y += 2)
            {
                // The collider is the authority on what this row is looking at. Working it out from the ray
                // direction needs a threshold, and the same threshold cannot be right for a floor receding to
                // a horizon and a wall standing in front of the camera.
                Ray ray = camera.ScreenPointToRay(new Vector3(x, y, 0f));
                // Capped well inside both the floor's extent and the camera's far plane, so every sampled
                // row is looking at the surface this is measuring and not past the end of it.
                if (!Physics.Raycast(ray, out RaycastHit hit, ScanRange))
                    continue;


                Vector3 normal = DecodeNormal(image.GetPixel(x, y));
                if (normal != Vector3.zero)
                    samples.Add(new NormalSample { row = y, normal = normal, surface = hit.normal });
            }

            return samples;
        }

        /// <summary>
        /// Prints the whole normal profile down the screen rather than only the worst step, and prints the
        /// same scene's position and raw buffers beside it. Six attempts at this artifact were reasoned out
        /// and all six were wrong, so this exists to be read rather than to pass: one run says what the
        /// normal actually does, at which row, and whether the buffers it is built from do it too.
        /// </summary>
        [Test]
        public void ReportTheNormalProfileDownABareFloor([Values(1, 2, 4)] int msaaSamples)
        {
            const DepthPrimingMode priming = DepthPrimingMode.Forced;
            const float eyeHeight = 1.1f;
            ConfigureRenderer(msaaSamples, priming);

            StringBuilder report = new StringBuilder();
            report.AppendLine($"--- bare floor, camera {eyeHeight:F2} m, MSAA {msaaSamples} " +
                $"(target {target.antiAliasing}x, allowMSAA {camera.allowMSAA}, asset {pipeline.msaaSampleCount}x), priming {priming} ---");

            foreach (BasisRTAODebugStage stage in new[]
                     { BasisRTAODebugStage.Normal, BasisRTAODebugStage.Position, BasisRTAODebugStage.Raw })
            {
                BasisRTAOFeature.HasDebugStageOverride = true;
                BasisRTAOFeature.DebugStageOverride = stage;
                StandOnABareFloor(eyeHeight);

                Texture2D image = RenderAndReadback();
                try
                {
                    report.AppendLine($"[{stage}]");
                    int x = RenderWidth / 2;
                    Color previous = image.GetPixel(x, 4);

                    for (int y = 4; y < RenderHeight - 4; y += 8)
                    {
                        Color pixel = image.GetPixel(x, y);
                        Ray ray = camera.ScreenPointToRay(new Vector3(x, y, 0f));

                        // Where this row lands on the floor, so a row number can be read as a distance.
                        float distance = ray.direction.y < -1e-4f ? -eyeHeight / ray.direction.y : -1f;

                        float change = Mathf.Abs(pixel.r - previous.r) + Mathf.Abs(pixel.g - previous.g) +
                                       Mathf.Abs(pixel.b - previous.b);

                        string decoded = stage == BasisRTAODebugStage.Normal
                            ? $" angleFromUp={Vector3.Angle(DecodeNormal(pixel), Vector3.up),6:F2}"
                            : string.Empty;

                        report.AppendLine(
                            $"  row {y,4}  dist {distance,8:F2}  rgb({pixel.r:F3},{pixel.g:F3},{pixel.b:F3})" +
                            $"  d={change:F3}{decoded}");
                        previous = pixel;
                    }
                }
                finally
                {
                    Object.DestroyImmediate(image);
                }
            }

            TestContext.WriteLine(report.ToString());
            Debug.Log(report.ToString());
        }

        [Test]
        public void TheNormalBufferHasNoLineAcrossABareFloorInAnyRendererConfiguration(
            [Values(1, 2, 4, 8)] int msaaSamples)
        {
            // Depth priming is Forced on both shipping renderers and this project's URP fork makes MSAA work
            // alongside it, so Forced is the only configuration worth asserting against.
            const DepthPrimingMode priming = DepthPrimingMode.Forced;
            ConfigureRenderer(msaaSamples, priming);
            ShowTheNormalBuffer();
            StandOnABareFloor(1.1f);

            Texture2D configImage = RenderAndReadback();
            List<NormalSample> configSamples;
            try
            {
                configSamples = ScanNormalsDownTheScreen(configImage);
            }
            finally
            {
                Object.DestroyImmediate(configImage);
            }

            Assert.Greater(configSamples.Count, 20);

            float configWorst = 0f;
            int configWorstAt = 1;
            for (int i = 1; i < configSamples.Count; i++)
            {
                float step = Vector3.Angle(configSamples[i].normal, configSamples[i - 1].normal);
                if (step > configWorst)
                {
                    configWorst = step;
                    configWorstAt = i;
                }
            }

            Assert.Less(configWorst, 8f,
                $"MSAA {msaaSamples}, depth priming {priming}: the reconstructed normal turned " +
                $"{configWorst:F1} degrees between screen rows {configSamples[configWorstAt - 1].row} and " +
                $"{configSamples[configWorstAt].row} on a bare flat floor.");
        }

        [Test]
        public void TheNormalBufferHasNoLineAcrossABareFloor([Values(0.4f, 1.1f, 1.8f)] float eyeHeight)
        {
            ShowTheNormalBuffer();
            StandOnABareFloor(eyeHeight);

            Texture2D image = RenderAndReadback();
            List<NormalSample> samples;
            try
            {
                samples = ScanNormalsDownTheScreen(image);
            }
            finally
            {
                Object.DestroyImmediate(image);
            }

            Assert.Greater(samples.Count, 20, "Not enough of the screen carried a normal to say anything.");

            float worstStep = 0f;
            int worstAt = 1;
            for (int i = 1; i < samples.Count; i++)
            {
                float step = Vector3.Angle(samples[i].normal, samples[i - 1].normal);
                if (step > worstStep)
                {
                    worstStep = step;
                    worstAt = i;
                }
            }

            Assert.Less(worstStep, 8f,
                $"The reconstructed normal turned {worstStep:F1} degrees between screen rows " +
                $"{samples[worstAt - 1].row} and {samples[worstAt].row} on a bare flat floor, camera at " +
                $"{eyeHeight:F2} m. The floor is one plane, so the normal is the same at every one of those " +
                "pixels. Everything downstream traces against this.");
        }

        [Test]
        public void TheNormalBufferAgreesWithTheFloorItIsReconstructedFrom([Values(0.4f, 1.1f, 1.8f)] float eyeHeight)
        {
            ShowTheNormalBuffer();
            StandOnABareFloor(eyeHeight);

            Texture2D image = RenderAndReadback();
            List<NormalSample> samples;
            try
            {
                samples = ScanNormalsDownTheScreen(image);
            }
            finally
            {
                Object.DestroyImmediate(image);
            }

            Assert.Greater(samples.Count, 20);

            float worst = 0f;
            int worstAt = 0;
            for (int i = 0; i < samples.Count; i++)
            {
                float error = Vector3.Angle(samples[i].normal, samples[i].surface);
                if (error > worst)
                {
                    worst = error;
                    worstAt = i;
                }
            }

            // A step is one failure mode; being wrong everywhere is another, and a scan for steps alone
            // would pass a normal that is uniformly facing the wrong way.
            Assert.Less(worst, 12f,
                $"The reconstructed normal was {worst:F1} degrees off the collider's own normal at screen " +
                $"row {samples[worstAt].row}, camera at {eyeHeight:F2} m. The surface under it is a flat floor.");
        }

        /// <summary>
        /// The one scene that reproduces the artifact headlessly. Dumps every stage down the screen so the
        /// band can be located rather than reasoned about.
        /// </summary>
        [Test]
        public void ReportTheProfileAcrossACloseFlatWall([Values(0.25f, 0.5f)] float distance)
        {
            StringBuilder report = new StringBuilder();
            report.AppendLine($"--- bare wall at {distance:F2} m, {RenderWidth}x{RenderHeight} ---");

            foreach (BasisRTAODebugStage stage in new[]
                     { BasisRTAODebugStage.Final, BasisRTAODebugStage.Raw,
                       BasisRTAODebugStage.Position, BasisRTAODebugStage.Normal })
            {
                BasisRTAOFeature.HasDebugStageOverride = true;
                BasisRTAOFeature.DebugStageOverride = stage;
                StandCloseToAFlatWall(distance);

                Texture2D image = RenderAndReadback();
                try
                {
                    report.AppendLine($"[{stage}]");
                    int x = RenderWidth / 2;
                    for (int y = 4; y < RenderHeight - 4; y += 12)
                    {
                        Color pixel = image.GetPixel(x, y);
                        report.AppendLine($"  row {y,4}  rgb({pixel.r:F3},{pixel.g:F3},{pixel.b:F3})");
                    }

                    // and one row across the screen, in case the band runs the other way
                    report.AppendLine($"[{stage}] across row {RenderHeight / 2}");
                    for (int col = 4; col < RenderWidth - 4; col += 24)
                    {
                        Color pixel = image.GetPixel(col, RenderHeight / 2);
                        report.AppendLine($"  col {col,4}  rgb({pixel.r:F3},{pixel.g:F3},{pixel.b:F3})");
                    }
                }
                finally
                {
                    Object.DestroyImmediate(image);
                }
            }

            TestContext.WriteLine(report.ToString());
        }

        /// <summary>
        /// The wall version of the floor check above, and the one that matters: a wall is the case where the
        /// wrong answer is smooth. When the reconstruction gives up it substitutes the view vector, which
        /// fans radially out from the screen centre - no step anywhere for a continuity scan to catch, and
        /// on a floor it is within a few degrees of the truth so an agreement check there passes too. Only a
        /// surface square to the camera separates the two, because there the view vector and the real normal
        /// disagree by the full half angle of the frustum at the edge of the screen.
        /// </summary>
        [Test]
        public void TheNormalBufferAgreesWithTheWallItIsReconstructedFrom([Values(0.25f, 0.5f, 1f, 2f)] float distance)
        {
            ShowTheNormalBuffer();
            StandCloseToAFlatWall(distance);

            Texture2D image = RenderAndReadback();
            List<NormalSample> samples;
            try
            {
                samples = ScanNormalsAcrossTheScreen(image);
            }
            finally
            {
                Object.DestroyImmediate(image);
            }

            Assert.Greater(samples.Count, 20);

            float worst = 0f;
            int worstAt = 0;
            for (int i = 0; i < samples.Count; i++)
            {
                float error = Vector3.Angle(samples[i].normal, samples[i].surface);
                if (error > worst)
                {
                    worst = error;
                    worstAt = i;
                }
            }

            Assert.Less(worst, 12f,
                $"The reconstructed normal was {worst:F1} degrees off the collider's own normal at screen " +
                $"column {samples[worstAt].row}, on a flat wall {distance:F2} m away. Every pixel of that " +
                "wall faces the camera, so every pixel has the same normal.");
        }

        /// <summary>
        /// Across rather than down, because the view vector this is trying to catch fans out horizontally as
        /// well and a wall fills the screen in both directions.
        /// </summary>
        private List<NormalSample> ScanNormalsAcrossTheScreen(Texture2D image)
        {
            Physics.SyncTransforms();

            List<NormalSample> samples = new List<NormalSample>();
            int y = RenderHeight / 2;

            for (int x = 4; x < RenderWidth - 4; x += 2)
            {
                Ray ray = camera.ScreenPointToRay(new Vector3(x, y, 0f));
                if (!Physics.Raycast(ray, out RaycastHit hit, ScanRange))
                    continue;

                Vector3 normal = DecodeNormal(image.GetPixel(x, y));
                if (normal != Vector3.zero)
                    samples.Add(new NormalSample { row = x, normal = normal, surface = hit.normal });
            }

            return samples;
        }

        [Test]
        public void TheNormalBufferHasNoLineAcrossAFlatWall([Values(0.25f, 0.5f, 1f, 2f)] float distance)
        {
            ShowTheNormalBuffer();
            StandCloseToAFlatWall(distance);

            Texture2D image = RenderAndReadback();
            List<NormalSample> samples;
            try
            {
                samples = ScanNormalsDownTheScreen(image);
            }
            finally
            {
                Object.DestroyImmediate(image);
            }

            Assert.Greater(samples.Count, 20);

            float worstStep = 0f;
            int worstAt = 1;
            for (int i = 1; i < samples.Count; i++)
            {
                float step = Vector3.Angle(samples[i].normal, samples[i - 1].normal);
                if (step > worstStep)
                {
                    worstStep = step;
                    worstAt = i;
                }
            }

            Assert.Less(worstStep, 8f,
                $"The reconstructed normal turned {worstStep:F1} degrees between screen rows " +
                $"{samples[worstAt - 1].row} and {samples[worstAt].row} on a bare wall {distance:F2} m away.");
        }

        [Test]
        public void TheStripIsActuallyOccludedSoTheScanMeansSomething()
        {
            Texture2D image = RenderAndReadback();
            try
            {
                List<Sample> samples = ScanAlongDepth(image);
                Assert.Greater(samples.Count, 12);

                float nearest = samples[0].occlusion;
                Assert.Less(nearest, 0.95f,
                    $"The nearest sample sits 5 cm from a wall with a 10 cm search radius and read {nearest:F3}. " +
                    "If there is no occlusion here the continuity scan is measuring nothing.");
            }
            finally
            {
                Object.DestroyImmediate(image);
            }
        }
    }
}
