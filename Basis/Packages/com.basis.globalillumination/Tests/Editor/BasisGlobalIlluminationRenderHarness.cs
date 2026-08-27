using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Basis.Tests.GlobalIllumination
{
    /// <summary>
    /// A real camera rendering a real scene through the project's own renderer, so a test can read the light
    /// that actually reached a pixel. Everything about this effect that matters - whether a bounce arrives,
    /// and whether it holds still - is invisible to a test that only inspects the values fed to the shader.
    /// </summary>
    public sealed class BasisGlobalIlluminationRenderHarness : IDisposable
    {
        public const int Width = 192;
        public const int Height = 128;

        private readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
        private readonly Func<Camera, bool> previousFilter;
        private readonly bool previousKeepWithDebugger;
        private readonly BasisGlobalIlluminationDebugView previousDebugView;
        private readonly Texture previousReflection;
        private readonly DefaultReflectionMode previousReflectionMode;
        private Cubemap reflection;
        private Texture2D readback;

        public Camera Camera { get; private set; }
        public RenderTexture Target { get; private set; }
        public BasisGlobalIlluminationVolume Settings { get; private set; }
        public Volume Volume { get; private set; }
        /// <summary>The renderer feature the active pipeline actually runs, or null when it carries none.</summary>
        public BasisGlobalIlluminationFeature Feature { get; private set; }

        /// <summary>Why this machine cannot run a render test, or null when it can.</summary>
        public static string Unavailable
        {
            get
            {
                if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) { return "this run has no graphics device"; }
                if (!(GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset)) { return "the active render pipeline is not URP"; }
                if (!BasisGlobalIlluminationFeature.SupportsPlatform()) { return "the effect declines to render on this platform"; }
                if (Shader.Find("Universal Render Pipeline/Lit") == null) { return "the URP lit shader is not available"; }
                return null;
            }
        }

        /// <summary>
        /// The feature instance the active pipeline actually runs. Walking the renderer's own feature list is
        /// the only way to be sure: a renderer asset can carry an orphaned copy of a feature as a sub-asset,
        /// and a search by type finds that one just as readily as the one URP enqueues - leaving a test
        /// setting a debug view, or reading a capability, on an object that never renders anything.
        /// </summary>
        private static BasisGlobalIlluminationFeature ResolveFeature()
        {
            if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset asset)
            {
                foreach (ScriptableRendererData data in asset.rendererDataList)
                {
                    if (data == null) { continue; }
                    for (int index = 0; index < data.rendererFeatures.Count; index++)
                    {
                        if (data.rendererFeatures[index] is BasisGlobalIlluminationFeature feature) { return feature; }
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Renders the effect's own output instead of the scene it was composited into. A probe on a lit
        /// wall is mostly wall: measuring the bounce through the multiply hides both how much of it there
        /// is and how much it moved.
        /// </summary>
        public void SetDebugView(BasisGlobalIlluminationDebugView view)
        {
            if (Feature != null) { Feature.DebugView = view; }
        }

        /// <summary>Whether this GPU can run the ray traced mode at all.</summary>
        public bool RayTracingAvailable => Feature != null && Feature.RayTracingAvailable;

        /// <summary>Whether the last render actually traced rather than falling back to screen space.</summary>
        public bool RayTracingRan
        {
            get
            {
                BasisGlobalIlluminationRayTracer tracer = BasisGlobalIlluminationRayTracer.Instance;
                return tracer != null && tracer.Ready;
            }
        }

        /// <summary>
        /// Throws the shared acceleration structure away so the next render rebuilds it from the scene as
        /// it stands now. The scene is only rescanned on a timer, and a test that changes a material and
        /// renders again would otherwise be shown the material it had before.
        /// </summary>
        public void ResetRayTracing()
        {
            BasisGlobalIlluminationRayTracer.Release();

            // Rebuilding the tracer also throws away every resolved texture average, and those come back
            // through async readbacks whose callbacks are dispatched by the editor loop - which a manual
            // render loop never reaches. Left alone they land at a different moment in every run, so a
            // textured surface bounces white for an unpredictable stretch of it and two measurements of the
            // same settings disagree by more than most settings do. Forcing them to complete is what makes a
            // traced measurement mean the same thing twice.
            for (int frame = 0; frame < 3; frame++) { Render(); }
            UnityEngine.Rendering.AsyncGPUReadback.WaitAllRequests();
            Render();
        }

        /// <summary>
        /// Everything a failing render test needs in order to be diagnosed from its message alone: which
        /// pipeline ran, which renderer features were live, and what the effect made of them.
        /// </summary>
        public string Describe()
        {
            System.Text.StringBuilder text = new System.Text.StringBuilder();
            text.Append("pipeline=").Append(GraphicsSettings.currentRenderPipeline != null ? GraphicsSettings.currentRenderPipeline.name : "<none>");
            text.Append(" quality=").Append(QualitySettings.names.Length > 0 ? QualitySettings.names[QualitySettings.GetQualityLevel()] : "?");
            text.Append(" device=").Append(SystemInfo.graphicsDeviceType);
            text.Append(" renderGraph=").Append(GraphicsSettings.GetRenderPipelineSettings<RenderGraphSettings>() != null
                ? (GraphicsSettings.GetRenderPipelineSettings<RenderGraphSettings>().enableRenderCompatibilityMode ? "compatibility" : "on")
                : "?");

            BasisGlobalIlluminationFeature[] features = Resources.FindObjectsOfTypeAll<BasisGlobalIlluminationFeature>();
            text.Append(" features=").Append(features.Length);
            for (int index = 0; index < features.Length; index++)
            {
                BasisGlobalIlluminationFeature feature = features[index];
                text.Append(" [").Append(feature.name)
                    .Append(" active=").Append(feature.isActive)
                    .Append(" material=").Append(feature.Material != null)
                    .Append(" pass=").Append(feature.Pass != null)
                    .Append(" shouldRender=").Append(feature.ShouldRender(Camera, CameraType.Game, true))
                    .Append("]");
            }

            BasisGlobalIlluminationVolume stacked = VolumeManager.instance.stack != null
                ? VolumeManager.instance.stack.GetComponent<BasisGlobalIlluminationVolume>()
                : null;
            text.Append(" stacked=").Append(stacked != null)
                .Append(" stackedActive=").Append(stacked != null && stacked.IsActive())
                .Append(" stackedMode=").Append(stacked != null ? stacked.mode.value.ToString() : "?")
                .Append(" stackedIntensity=").Append(stacked != null ? stacked.intensity.value : -1f);

            UniversalAdditionalCameraData cameraData = Camera.GetUniversalAdditionalCameraData();
            text.Append(" postFx=").Append(cameraData != null && cameraData.renderPostProcessing);
            text.Append(" renderedFrame=").Append(Time.renderedFrameCount);
            text.Append(" rtAvailable=").Append(RayTracingAvailable).Append(" rtRan=").Append(RayTracingRan);
            BasisGlobalIlluminationRayTracer tracer = BasisGlobalIlluminationRayTracer.Instance;
            if (tracer != null && tracer.Scene != null)
            {
                text.Append(" rtInstances=").Append(tracer.Scene.InstanceCount).Append(" rtLights=").Append(tracer.Lights.Count);
            }
            return text.ToString();
        }

        public static void SkipIfUnavailable()
        {
            string reason = Unavailable;
            if (reason != null) { Assert.Ignore("Render harness unavailable: " + reason + "."); }
        }

        public BasisGlobalIlluminationRenderHarness()
        {
            previousFilter = BasisGlobalIlluminationFeature.CameraFilter;
            previousKeepWithDebugger = BasisGlobalIlluminationFeature.KeepRenderingWithDebugger;
            BasisGlobalIlluminationFeature.CameraFilter = null;
            BasisGlobalIlluminationFeature.KeepRenderingWithDebugger = true;

            Feature = ResolveFeature();
            previousDebugView = Feature != null ? Feature.DebugView : BasisGlobalIlluminationDebugView.None;

            // A dim but non-black environment, so the fallback a missed ray reads is something rather than
            // nothing. Without one the fallback settings are untestable - not because they do not work, but
            // because there is no sky in the room for them to let through.
            previousReflection = RenderSettings.customReflectionTexture;
            previousReflectionMode = RenderSettings.defaultReflectionMode;
            reflection = new Cubemap(4, TextureFormat.RGBAHalf, false) { name = "BasisGIHarnessSky" };
            Color[] face = new Color[16];
            for (int texel = 0; texel < face.Length; texel++) { face[texel] = new Color(1.6f, 1.9f, 2.6f, 1f); }
            for (int side = 0; side < 6; side++) { reflection.SetPixels(face, (CubemapFace)side); }
            reflection.Apply(false, false);
            RenderSettings.customReflectionTexture = reflection;
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
            DynamicGI.UpdateEnvironment();

            Target = new RenderTexture(Width, Height, 24, RenderTextureFormat.DefaultHDR) { name = "BasisGIHarnessTarget" };
            Target.Create();
            readback = new Texture2D(Width, Height, TextureFormat.RGBAFloat, false, true);

            GameObject cameraHost = Own(new GameObject("BasisGIHarnessCamera"));
            Camera = cameraHost.AddComponent<Camera>();
            Camera.clearFlags = CameraClearFlags.SolidColor;
            Camera.backgroundColor = Color.black;
            Camera.nearClipPlane = 0.05f;
            Camera.farClipPlane = 60f;
            Camera.fieldOfView = 60f;
            Camera.targetTexture = Target;
            Camera.allowMSAA = false;

            UniversalAdditionalCameraData cameraData = Camera.GetUniversalAdditionalCameraData();
            cameraData.renderPostProcessing = true;
            cameraData.renderShadows = true;
            cameraData.volumeLayerMask = ~0;
            cameraData.antialiasing = AntialiasingMode.None;

            VolumeProfile profile = Own(ScriptableObject.CreateInstance<VolumeProfile>()) as VolumeProfile;
            profile.name = "BasisGIHarnessProfile";
            Settings = profile.Add<BasisGlobalIlluminationVolume>(true);

            GameObject volumeHost = Own(new GameObject("BasisGIHarnessVolume"));
            Volume = volumeHost.AddComponent<Volume>();
            Volume.isGlobal = true;
            Volume.priority = 10000f;
            Volume.sharedProfile = profile;

            // Every parameter this harness sets has to override whatever the project's own default profile
            // says, so the tests describe the state they are testing rather than inheriting it.
            OverrideEverything(Settings);
            Settings.enable.value = true;
            Settings.mode.value = BasisGlobalIlluminationMode.ScreenSpace;
        }

        private static void OverrideEverything(VolumeComponent component)
        {
            for (int index = 0; index < component.parameters.Count; index++) { component.parameters[index].overrideState = true; }
        }

        public T Own<T>(T target) where T : UnityEngine.Object
        {
            owned.Add(target);
            return target;
        }

        /// <summary>A directional light, so the scene has something for the walls to reflect.</summary>
        public Light AddSun(Quaternion rotation, float intensity)
        {
            GameObject host = Own(new GameObject("BasisGIHarnessSun"));
            host.transform.rotation = rotation;
            Light light = host.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = intensity;
            light.shadows = LightShadows.None;
            return light;
        }

        public Material CreateLitMaterial(Color baseColor, Color emission)
        {
            Material material = Own(new Material(Shader.Find("Universal Render Pipeline/Lit")));
            material.SetColor("_BaseColor", baseColor);
            SetEmission(material, emission);
            return material;
        }

        /// <summary>
        /// A lit material whose colour lives in its base map rather than its base colour, which is what almost
        /// every real material does. The traced gather folds a map in as an average, so a scene built only
        /// from untextured materials cannot tell whether that folding works at all.
        /// </summary>
        public Material CreateTexturedMaterial(Color mapColour)
        {
            Texture2D map = Own(new Texture2D(2, 2, TextureFormat.RGBA32, false, false) { name = "BasisGIHarnessBaseMap" });
            Color[] texels = { mapColour, mapColour, mapColour, mapColour };
            map.SetPixels(texels);
            map.Apply(false, false);

            Material material = Own(new Material(Shader.Find("Universal Render Pipeline/Lit")));
            material.SetColor("_BaseColor", Color.white);
            material.SetTexture("_BaseMap", map);
            SetEmission(material, Color.black);
            return material;
        }

        public static void SetEmission(Material material, Color emission)
        {
            bool lit = emission.maxColorComponent > 0f;
            if (material.HasProperty("_EmissionColor")) { material.SetColor("_EmissionColor", emission); }
            if (material.HasProperty("_EmissionEnabled")) { material.SetFloat("_EmissionEnabled", lit ? 1f : 0f); }
            CoreUtils.SetKeyword(material, "_EMISSION", lit);
            material.globalIlluminationFlags = lit
                ? MaterialGlobalIlluminationFlags.RealtimeEmissive
                : MaterialGlobalIlluminationFlags.EmissiveIsBlack;
        }

        public GameObject AddBox(Vector3 position, Vector3 scale, Material material)
        {
            GameObject box = Own(GameObject.CreatePrimitive(PrimitiveType.Cube));
            box.name = "BasisGIHarnessBox";
            box.transform.position = position;
            box.transform.localScale = scale;
            box.GetComponent<MeshRenderer>().sharedMaterial = material;
            return box;
        }

        public BasisGlobalIlluminationEmitter AddEmitter(Vector3 position, Color color, float intensity, float radius, float range)
        {
            GameObject host = Own(new GameObject("BasisGIHarnessEmitter"));
            host.transform.position = position;
            BasisGlobalIlluminationEmitter emitter = host.AddComponent<BasisGlobalIlluminationEmitter>();
            emitter.Color = color;
            emitter.Intensity = intensity;
            emitter.Radius = radius;
            emitter.Range = range;
            emitter.Register();
            return emitter;
        }

        public void Render()
        {
            Camera.Render();
        }

        /// <summary>Pulls the last rendered frame back off the GPU so it can be read a pixel at a time.</summary>
        private void Capture()
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = Target;
            readback.ReadPixels(new Rect(0, 0, Width, Height), 0, 0, false);
            readback.Apply(false);
            RenderTexture.active = previous;
        }

        /// <summary>Mean linear colour over a pixel rectangle of the last rendered frame.</summary>
        public Color Sample(RectInt region)
        {
            Capture();

            Color total = Color.clear;
            int count = 0;
            for (int y = region.yMin; y < region.yMax; y++)
            {
                for (int x = region.xMin; x < region.xMax; x++)
                {
                    if (x < 0 || y < 0 || x >= Width || y >= Height) { continue; }
                    total += readback.GetPixel(x, y);
                    count++;
                }
            }
            return count == 0 ? Color.clear : total / count;
        }

        /// <summary>
        /// What a probe read, as three numbers, because different settings move different ones.
        ///
        /// Intensity and tint move the <see cref="Level"/>. A blur that is doing its job changes how much
        /// neighbouring pixels disagree without touching their average, which is <see cref="Contrast"/>. And
        /// the temporal settings barely move either once a run has been averaged - what they change is how
        /// far the image moves between one frame and the next, which is <see cref="Swing"/>. A sweep that
        /// only watched the average would call two thirds of the panel dead.
        /// </summary>
        public readonly struct Reading
        {
            public readonly Color Level;
            public readonly float Contrast;
            public readonly float Swing;
            /// <summary>
            /// The probe's own luminance, texel by texel, from the last frame of the run. Two runs jitter the
            /// camera through the same poses, so these line up and can be differenced directly - which is the
            /// only way to see a setting whose whole effect is confined to a one pixel band at a silhouette.
            /// </summary>
            public readonly float[] Pixels;

            public Reading(Color level, float contrast, float swing, float[] pixels)
            {
                Level = level;
                Contrast = contrast;
                Swing = swing;
                Pixels = pixels;
            }

            /// <summary>The largest disagreement between this probe's texels and another's.</summary>
            public float PixelDifference(Reading other)
            {
                if (Pixels == null || other.Pixels == null || Pixels.Length != other.Pixels.Length) { return 0f; }
                float worst = 0f;
                for (int index = 0; index < Pixels.Length; index++)
                {
                    worst = Mathf.Max(worst, Mathf.Abs(Pixels[index] - other.Pixels[index]));
                }
                return worst;
            }
        }

        /// <summary>Reads the whole target back once, so several probes cost one readback between them.</summary>
        public void CaptureFrame()
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = Target;
            readback.ReadPixels(new Rect(0, 0, Width, Height), 0, 0, false);
            readback.Apply(false);
            RenderTexture.active = previous;
        }

        private Color LevelOf(RectInt region)
        {
            Color total = Color.clear;
            int count = 0;
            for (int y = region.yMin; y < region.yMax; y++)
            {
                for (int x = region.xMin; x < region.xMax; x++)
                {
                    if (x < 0 || y < 0 || x >= Width || y >= Height) { continue; }
                    total += readback.GetPixel(x, y);
                    count++;
                }
            }
            return count == 0 ? Color.clear : total / count;
        }

        private float[] PixelsOf(RectInt region)
        {
            float[] pixels = new float[Mathf.Max(0, region.width) * Mathf.Max(0, region.height)];
            int cursor = 0;
            for (int y = region.yMin; y < region.yMax; y++)
            {
                for (int x = region.xMin; x < region.xMax; x++)
                {
                    if (cursor >= pixels.Length) { break; }
                    bool inside = x >= 0 && y >= 0 && x < Width && y < Height;
                    pixels[cursor++] = inside ? Luminance(readback.GetPixel(x, y)) : 0f;
                }
            }
            return pixels;
        }

        private float ContrastOf(RectInt region, Color level)
        {
            float target = Luminance(level);
            float total = 0f;
            int count = 0;
            for (int y = region.yMin; y < region.yMax; y++)
            {
                for (int x = region.xMin; x < region.xMax; x++)
                {
                    if (x < 0 || y < 0 || x >= Width || y >= Height) { continue; }
                    float difference = Luminance(readback.GetPixel(x, y)) - target;
                    total += difference * difference;
                    count++;
                }
            }
            return count == 0 ? 0f : Mathf.Sqrt(total / count);
        }

        private static float Luminance(Color colour)
        {
            return colour.r * 0.2126f + colour.g * 0.7152f + colour.b * 0.0722f;
        }

        /// <summary>
        /// A settled reading per probe: the level and contrast averaged over the run, and the largest jump
        /// in level between two consecutive frames of it.
        /// </summary>
        public Reading[] ConvergedReadings(RectInt[] regions, int warmup = 26, int average = 8)
        {
            Quaternion baseRotation = Camera.transform.rotation;
            Color[] level = new Color[regions.Length];
            float[] contrast = new float[regions.Length];
            float[] swing = new float[regions.Length];
            float[] previousLuminance = new float[regions.Length];
            float[][] pixels = new float[regions.Length][];
            int counted = 0;

            for (int frame = 0; frame < warmup + average; frame++)
            {
                float phase = frame * 0.7548776662f;
                Camera.transform.rotation = baseRotation * Quaternion.Euler(
                    Mathf.Sin(phase * 6.2831853f * 1.618f) * 0.02f,
                    Mathf.Cos(phase * 6.2831853f) * 0.02f,
                    0f);
                Render();
                if (frame < warmup - 1) { continue; }

                CaptureFrame();
                for (int index = 0; index < regions.Length; index++)
                {
                    Color frameLevel = LevelOf(regions[index]);
                    float frameLuminance = Luminance(frameLevel);
                    if (frame >= warmup)
                    {
                        level[index] += frameLevel;
                        contrast[index] += ContrastOf(regions[index], frameLevel);
                        swing[index] = Mathf.Max(swing[index], Mathf.Abs(frameLuminance - previousLuminance[index]));
                        pixels[index] = PixelsOf(regions[index]);
                    }
                    previousLuminance[index] = frameLuminance;
                }
                if (frame >= warmup) { counted++; }
            }

            Camera.transform.rotation = baseRotation;
            Reading[] readings = new Reading[regions.Length];
            for (int index = 0; index < regions.Length; index++)
            {
                readings[index] = counted == 0
                    ? new Reading(Color.clear, 0f, 0f, null)
                    : new Reading(level[index] / counted, contrast[index] / counted, swing[index], pixels[index]);
            }
            return readings;
        }

        public Color RenderAndSample(RectInt region)
        {
            Render();
            return Sample(region);
        }

        /// <summary>
        /// A settled reading, averaged over several frames of a camera that is never quite still.
        ///
        /// One frame is not a reading. The gather's per-pixel ray directions are rotated by the frame
        /// counter, and in edit mode that counter barely advances between one manual render and the
        /// next - so a still camera samples the same two directions per pixel every time and whether a
        /// probe finds a small bright source at all comes down to which frame the run happened to start
        /// on. Nudging the camera and averaging is what turns that back into an estimate of the light
        /// rather than of one arbitrary set of rays.
        /// </summary>
        public Color Converged(RectInt region, int warmup = 24, int average = 8)
        {
            Quaternion baseRotation = Camera.transform.rotation;
            Color total = Color.clear;
            int counted = 0;

            for (int frame = 0; frame < warmup + average; frame++)
            {
                float phase = frame * 0.7548776662f;
                Camera.transform.rotation = baseRotation * Quaternion.Euler(
                    Mathf.Sin(phase * 6.2831853f * 1.618f) * 0.35f,
                    Mathf.Cos(phase * 6.2831853f) * 0.35f,
                    0f);
                Render();
                if (frame < warmup) { continue; }
                total += Sample(region);
                counted++;
            }

            Camera.transform.rotation = baseRotation;
            return counted == 0 ? Color.clear : total / counted;
        }

        /// <summary>
        /// Renders a run of frames while nudging the camera a fraction of a pixel each one, and returns what
        /// the probe read on every one.
        ///
        /// A perfectly still camera makes the trace deterministic and hides exactly the instability this is
        /// looking for. The nudge is deliberately tiny: a larger drift resamples the half resolution buffer
        /// across whole pixels every frame and puts a floor of a couple of percent under everything, which
        /// would bury the thing being measured. What this isolates is the accumulation coming apart, not the
        /// image being resampled.
        /// </summary>
        public Color[] RenderJitteredRun(RectInt region, int frames, int warmup = 12, float jitterDegrees = 0.02f)
        {
            Quaternion baseRotation = Camera.transform.rotation;
            Vector3 basePosition = Camera.transform.position;
            Color[] samples = new Color[frames];

            for (int frame = 0; frame < warmup + frames; frame++)
            {
                float phase = frame * 0.7548776662f;
                float yaw = Mathf.Sin(phase * 6.2831853f) * jitterDegrees;
                float pitch = Mathf.Cos(phase * 6.2831853f * 1.618f) * jitterDegrees;
                Camera.transform.rotation = baseRotation * Quaternion.Euler(pitch, yaw, 0f);
                Camera.transform.position = basePosition + new Vector3(0f, 0f, Mathf.Sin(phase * 3f) * 0.0005f);

                if (frame < warmup) { Render(); continue; }
                samples[frame - warmup] = RenderAndSample(region);
            }

            Camera.transform.rotation = baseRotation;
            Camera.transform.position = basePosition;
            return samples;
        }

        public static float Mean(Color[] samples, Func<Color, float> channel)
        {
            float total = 0f;
            for (int index = 0; index < samples.Length; index++) { total += channel(samples[index]); }
            return samples.Length == 0 ? 0f : total / samples.Length;
        }

        /// <summary>
        /// The largest jump between two consecutive frames, as a fraction of the run's mean. This is what a
        /// player actually perceives as flicker: not how noisy a frame is, but how far it moved from the one
        /// before it.
        /// </summary>
        public static float RelativeFrameToFrameSwing(Color[] samples, Func<Color, float> channel)
        {
            float mean = Mean(samples, channel);
            if (mean <= 1e-5f) { return 0f; }

            float worst = 0f;
            for (int index = 1; index < samples.Length; index++)
            {
                worst = Mathf.Max(worst, Mathf.Abs(channel(samples[index]) - channel(samples[index - 1])));
            }
            return worst / mean;
        }

        public static float Range(Color[] samples, Func<Color, float> channel)
        {
            if (samples.Length == 0) { return 0f; }
            float low = float.MaxValue, high = float.MinValue;
            for (int index = 0; index < samples.Length; index++)
            {
                float value = channel(samples[index]);
                low = Mathf.Min(low, value);
                high = Mathf.Max(high, value);
            }
            return high - low;
        }

        /// <summary>
        /// How grainy the last rendered frame is over a region, in the units of the signal itself.
        ///
        /// The region is convolved with the kernel that annihilates any linear ramp, so a smooth gradient of
        /// real light contributes nothing and what is left is only what changed from one pixel to the next.
        /// That is the difference between measuring noise and measuring contrast: a plain deviation over the
        /// same region calls a bright patch of floor noisy, and a bounce is supposed to have contrast. The
        /// constant is Immerkaer's, which turns the mean absolute response of that kernel back into the
        /// standard deviation of the noise that produced it.
        /// </summary>
        public float SpatialNoise(RectInt region, Func<Color, float> channel)
        {
            Capture();
            return SpatialNoiseOfCapture(region, channel);
        }

        private float SpatialNoiseOfCapture(RectInt region, Func<Color, float> channel)
        {
            float total = 0f;
            int count = 0;
            for (int y = region.yMin + 1; y < region.yMax - 1; y++)
            {
                for (int x = region.xMin + 1; x < region.xMax - 1; x++)
                {
                    if (x < 1 || y < 1 || x >= Width - 1 || y >= Height - 1) { continue; }
                    float response =
                        4f * channel(readback.GetPixel(x, y))
                        - 2f * (channel(readback.GetPixel(x - 1, y)) + channel(readback.GetPixel(x + 1, y))
                              + channel(readback.GetPixel(x, y - 1)) + channel(readback.GetPixel(x, y + 1)))
                        + channel(readback.GetPixel(x - 1, y - 1)) + channel(readback.GetPixel(x + 1, y - 1))
                        + channel(readback.GetPixel(x - 1, y + 1)) + channel(readback.GetPixel(x + 1, y + 1));
                    total += Mathf.Abs(response);
                    count++;
                }
            }
            return count == 0 ? 0f : total / count * 0.20888568f;
        }

        private float MeanOfCapture(RectInt region, Func<Color, float> channel)
        {
            float total = 0f;
            int count = 0;
            for (int y = region.yMin; y < region.yMax; y++)
            {
                for (int x = region.xMin; x < region.xMax; x++)
                {
                    if (x < 0 || y < 0 || x >= Width || y >= Height) { continue; }
                    total += channel(readback.GetPixel(x, y));
                    count++;
                }
            }
            return count == 0 ? 0f : total / count;
        }

        /// <summary>What a region reads on average and how much grain sits on it, over a run of frames.</summary>
        public struct Grain
        {
            public float Level;
            public float Noise;
            /// <summary>Grain as a fraction of the level it sits on - what a viewer actually sees as noise.</summary>
            public float Relative => Level <= 1e-5f ? 0f : Noise / Level;
            public override string ToString() { return $"level={Level:F4} noise={Noise:F5} rel={Relative:P1}"; }
        }

        /// <summary>
        /// Grain measured over a run of frames with the camera never quite still, which is the state the
        /// filter is actually asked to hold up in. A still camera lets the accumulation settle onto one set
        /// of rays and reports a frame far cleaner than anything a player would see.
        /// </summary>
        public Grain MeasuredGrain(RectInt region, Func<Color, float> channel, int frames = 8, int warmup = 32, float jitterDegrees = 0.02f)
        {
            Quaternion baseRotation = Camera.transform.rotation;
            Vector3 basePosition = Camera.transform.position;
            Grain grain = new Grain();
            int counted = 0;

            for (int frame = 0; frame < warmup + frames; frame++)
            {
                float phase = frame * 0.7548776662f;
                Camera.transform.rotation = baseRotation * Quaternion.Euler(
                    Mathf.Cos(phase * 6.2831853f * 1.618f) * jitterDegrees,
                    Mathf.Sin(phase * 6.2831853f) * jitterDegrees,
                    0f);
                Camera.transform.position = basePosition + new Vector3(0f, 0f, Mathf.Sin(phase * 3f) * 0.0005f);

                Render();
                if (frame < warmup) { continue; }

                Capture();
                grain.Level += MeanOfCapture(region, channel);
                grain.Noise += SpatialNoiseOfCapture(region, channel);
                counted++;
            }

            Camera.transform.rotation = baseRotation;
            Camera.transform.position = basePosition;
            if (counted > 0)
            {
                grain.Level /= counted;
                grain.Noise /= counted;
            }
            return grain;
        }

        public static float Red(Color colour) { return colour.r; }
        public static float Green(Color colour) { return colour.g; }
        public static float Blue(Color colour) { return colour.b; }
        /// <summary>Rec. 709 luminance: grain is judged on brightness, which is what the eye picks it out by.</summary>
        public static float Luma(Color colour) { return colour.r * 0.2126f + colour.g * 0.7152f + colour.b * 0.0722f; }

        public void Dispose()
        {
            BasisGlobalIlluminationFeature.CameraFilter = previousFilter;
            BasisGlobalIlluminationFeature.KeepRenderingWithDebugger = previousKeepWithDebugger;
            if (Feature != null) { Feature.DebugView = previousDebugView; }
            RenderSettings.customReflectionTexture = previousReflection;
            RenderSettings.defaultReflectionMode = previousReflectionMode;

            if (Camera != null) { Camera.targetTexture = null; }
            for (int index = owned.Count - 1; index >= 0; index--)
            {
                if (owned[index] != null) { UnityEngine.Object.DestroyImmediate(owned[index]); }
            }
            owned.Clear();
            BasisGlobalIlluminationEmitter.Registered.Clear();

            BasisGlobalIlluminationRayTracer.Release();
            if (reflection != null) { UnityEngine.Object.DestroyImmediate(reflection); reflection = null; }
            if (readback != null) { UnityEngine.Object.DestroyImmediate(readback); readback = null; }
            if (Target != null) { Target.Release(); UnityEngine.Object.DestroyImmediate(Target); Target = null; }
        }
    }
}
