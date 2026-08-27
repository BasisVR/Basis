using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.UnifiedRayTracing;
using UnityEngine.Rendering.Universal;

public sealed partial class BasisGlobalIlluminationPass
{
    internal static readonly int idSpecularTexture = Shader.PropertyToID("_BasisGISpecularTexture");
    internal static readonly int idSpecularParams = Shader.PropertyToID("_BasisGISpecularParams");
    internal static readonly int idRtSpecularTex = Shader.PropertyToID("_BasisGIRtSpecularTex");
    internal static readonly int idRtSpecular = Shader.PropertyToID("_BasisGIRtSpecular");
    internal static readonly int idRtDiffuseEnabled = Shader.PropertyToID("_BasisGIRtDiffuseEnabled");
    internal static readonly int idRtSpecularEnabled = Shader.PropertyToID("_BasisGIRtSpecularEnabled");

    /// <summary>
    /// Ray traced reflections, published as <c>_BasisGISpecularTexture</c> for URP's lit shaders to use in
    /// place of the reflection probe.
    ///
    /// <para><b>Why this is a second pass rather than another stage of the diffuse one.</b> The diffuse
    /// gather composites into the camera image, so it runs at BeforeRenderingTransparents - after the opaque
    /// draws. A reflection has to exist <i>before</i> those draws, because the opaque shaders are the thing
    /// that consumes it. That is the same ordering RTAO needs for <c>_ScreenSpaceOcclusionTexture</c> and the
    /// same reason it sits just after the prepasses. The two passes share the kernel, the acceleration
    /// structure, the light list and the sky binding; what they cannot share is a dispatch, because they do
    /// not happen at the same point in the frame. The kernel takes a pair of enables for exactly this - each
    /// pass asks for the half it wants and the other write is skipped.</para>
    ///
    /// <para>The cost of that split is one extra depth-reconstruction prepass per frame when ray traced
    /// diffuse is also on. It is a fullscreen pass at trace resolution against a trace that walks a BVH, so
    /// it does not show up next to the dispatch it feeds.</para>
    /// </summary>
    public sealed class SpecularPass : ScriptableRenderPass
    {
        private static readonly ProfilingSampler samplerPrepass = new ProfilingSampler("Basis GI Specular Prepass");
        private static readonly ProfilingSampler samplerTrace = new ProfilingSampler("Basis GI Specular Trace");
        private static readonly ProfilingSampler samplerResolve = new ProfilingSampler("Basis GI Specular Resolve");
        private static readonly ProfilingSampler samplerTemporal = new ProfilingSampler("Basis GI Specular Temporal");
        private static readonly ProfilingSampler samplerBlur = new ProfilingSampler("Basis GI Specular Blur");
        private static readonly ProfilingSampler samplerUpsample = new ProfilingSampler("Basis GI Specular Upsample");
        private static readonly ProfilingSampler samplerPublish = new ProfilingSampler("Basis GI Specular Publish");

        // Reflections are deterministic per pixel - one mirror ray, no lobe to sample - so the only thing
        // the accumulation has to settle is the light resampling at the hit. It converges far faster than
        // the diffuse gather does, and letting history run as long there would smear a moving reflection
        // for no gain, because a reflection reprojects by the surface it sits on rather than by the thing
        // being reflected. A shorter tail is the honest trade.
        private const float SpecularTemporalResponseScale = 2.5f;

        private readonly Vector4[] constants = new Vector4[4];
        private readonly Matrix4x4[] previousViewProjection = new Matrix4x4[2];

        private Material material;
        private Material rayStagesMaterial;
        private RayTracingShader rayTraceShader;
        private ComputeShader rayTraceCompute;
        private bool rayComputeFallback;
        private bool rayTracingAvailable;

        public SpecularPass()
        {
            profilingSampler = new ProfilingSampler("Basis GI Specular");
            // After the depth prepass, before the opaque draws that consume the result. Both Basis renderers
            // force depth priming, so _CameraDepthTexture is populated by the time this runs.
            renderPassEvent = RenderPassEvent.AfterRenderingPrePasses + 1;
        }

        public void Setup(Material compositeMaterial, Material stagesMaterial, RayTracingShader hardwareShader,
            ComputeShader computeShader, bool computeFallback, bool available)
        {
            material = compositeMaterial;
            rayStagesMaterial = stagesMaterial;
            rayTraceShader = hardwareShader;
            rayTraceCompute = computeShader;
            rayComputeFallback = computeFallback;
            rayTracingAvailable = available;
        }

        /// <summary>
        /// Whether this camera will actually get reflections, which is a stricter question than whether the
        /// volume asked for them: the backend has to exist and the scene has to hold traceable geometry.
        /// </summary>
        public bool CanRender(BasisGlobalIlluminationVolume volume, Camera camera, int frame)
        {
            if (material == null || rayStagesMaterial == null) { return false; }
            if (volume == null || !volume.SpecularActive()) { return false; }
            if (!rayTracingAvailable) { return false; }

            BasisGlobalIlluminationRayTracer tracer = BasisGlobalIlluminationRayTracer.GetOrCreate(rayTraceShader, rayTraceCompute, rayComputeFallback);
            if (tracer == null) { return false; }

            return tracer.Refresh(volume.ResolvedSceneSettings(), volume.ResolvedLightSettings(), camera, frame, Time.unscaledTime);
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (material == null || rayStagesMaterial == null) { return; }

            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            if (!resourceData.cameraDepthTexture.IsValid()) { return; }

            BasisGlobalIlluminationVolume volume = VolumeManager.instance.stack.GetComponent<BasisGlobalIlluminationVolume>();
            if (volume == null || !volume.SpecularActive()) { return; }

            Camera camera = cameraData.camera;
            int frame = Time.renderedFrameCount;
            if (!CanRender(volume, camera, frame)) { return; }

            BasisGlobalIlluminationRayTracer tracer = BasisGlobalIlluminationRayTracer.Instance;
            RenderTextureDescriptor descriptor = cameraData.cameraTargetDescriptor;
            int divisor = volume.ResolvedResolutionDivisor();
            int tracedWidth = Mathf.Max(1, descriptor.width / divisor);
            int tracedHeight = Mathf.Max(1, descriptor.height / divisor);
            int viewCount = ViewCountOf(cameraData);
            int scale = Mathf.Clamp(divisor, 1, 4);

            // Reflections keep their own accumulation, indexed the same way the diffuse one is, so a mirror
            // and the player's eye do not pour their reflections into each other's history.
            int hash = BasisGlobalIlluminationHistory.ComputeHash(camera, cameraData.xr);
            BasisGlobalIlluminationHistory history = BasisGlobalIlluminationHistory.Get(hash);
            history.EnsureAllocated(descriptor, tracedWidth, tracedHeight, true);
            bool contiguous = history.SpecularContiguous(frame);
            bool historyValid = volume.specularTemporal.value && history.SpecularValid && contiguous;

            ApplyKeywords(volume);
            FillConstants(volume, frame);

            Vector3 viewer = camera.transform.position;
            Vector4 reference = new Vector4(viewer.x, viewer.y, viewer.z, 0f);
            Vector4 fullSize = new Vector4(descriptor.width, descriptor.height, 1f / descriptor.width, 1f / descriptor.height);
            BasisGlobalIlluminationRayTracer.SkyBinding sky = BasisGlobalIlluminationRayTracer.ResolveSky(volume.fallback.value, volume.fallbackIntensity.value);
            if (sky.Cube != null) { Shader.SetGlobalTexture(idSkyCube, sky.Cube); }

            int emitterCount = volume.emitters.value ? GatherEmitters(camera, volume.ResolvedMaxEmitters()) : 0;

            // Without hardware ray tracing the BVH is walked in a compute shader, and the diffuse gather
            // already caps itself there rather than asking a GPU that cannot afford it for the full budget.
            // A reflection is one ray per pixel whatever happens, so the cap that is left to apply is on the
            // path continued from the hit and on how many lights each of those hits shadow-rays.
            int specularBounces = volume.specularBounces.value;
            int lightSamples = volume.ResolvedRayTracedLightSamples();
            if (tracer.Context.Backend == RayTracingBackend.Compute)
            {
                specularBounces = Mathf.Min(specularBounces, ComputeBackendBounceCeiling);
                lightSamples = Mathf.Min(lightSamples, ComputeBackendLightSampleCeiling);
            }

            RenderTextureDescriptor tracedDescriptor = descriptor;
            tracedDescriptor.width = tracedWidth;
            tracedDescriptor.height = tracedHeight;
            tracedDescriptor.msaaSamples = 1;
            tracedDescriptor.depthStencilFormat = GraphicsFormat.None;
            tracedDescriptor.depthBufferBits = 0;
            tracedDescriptor.useMipMap = false;
            tracedDescriptor.autoGenerateMips = false;
            tracedDescriptor.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;

            // Full resolution, because the lit shaders sample it by screen UV and a half resolution fetch
            // there would drag a reflection across every silhouette in the frame. The upsample below is what
            // buys the edges back.
            RenderTextureDescriptor publishedDescriptor = tracedDescriptor;
            publishedDescriptor.width = descriptor.width;
            publishedDescriptor.height = descriptor.height;

            TextureHandle position = UniversalRenderer.CreateRenderGraphTexture(renderGraph,
                RayArrayDescriptor(tracedWidth, tracedHeight, viewCount, GraphicsFormat.R16G16B16A16_SFloat, false), "_BasisGISpecPosition", false);
            TextureHandle normal = UniversalRenderer.CreateRenderGraphTexture(renderGraph,
                RayArrayDescriptor(tracedWidth, tracedHeight, viewCount, GraphicsFormat.R16G16_SFloat, false), "_BasisGISpecNormal", false);
            TextureHandle rayResult = UniversalRenderer.CreateRenderGraphTexture(renderGraph,
                RayArrayDescriptor(tracedWidth, tracedHeight, viewCount, GraphicsFormat.R16G16B16A16_SFloat, true), "_BasisGISpecResult", false);
            TextureHandle traced = UniversalRenderer.CreateRenderGraphTexture(renderGraph, tracedDescriptor, "_BasisGISpecTraced", false, FilterMode.Bilinear, TextureWrapMode.Clamp);
            TextureHandle blurA = UniversalRenderer.CreateRenderGraphTexture(renderGraph, tracedDescriptor, "_BasisGISpecBlurA", false, FilterMode.Bilinear, TextureWrapMode.Clamp);
            TextureHandle blurB = UniversalRenderer.CreateRenderGraphTexture(renderGraph, tracedDescriptor, "_BasisGISpecBlurB", false, FilterMode.Bilinear, TextureWrapMode.Clamp);
            TextureHandle published = UniversalRenderer.CreateRenderGraphTexture(renderGraph, publishedDescriptor, "_BasisGISpecularTexture", false, FilterMode.Bilinear, TextureWrapMode.Clamp);

            TextureHandle historyRead = renderGraph.ImportTexture(history.Specular[history.SpecularRead]);
            TextureHandle historyReadStats = renderGraph.ImportTexture(history.SpecularStats[history.SpecularRead]);
            TextureHandle historyWrite = renderGraph.ImportTexture(history.Specular[history.SpecularWrite]);
            TextureHandle historyWriteStats = renderGraph.ImportTexture(history.SpecularStats[history.SpecularWrite]);

            using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Basis GI Specular Prepass", out PassData passData, samplerPrepass))
            {
                passData.stage = Stage.RayPrepass;
                passData.material = rayStagesMaterial;
                passData.materialPass = PassRayPrepass;
                passData.source = resourceData.cameraDepthTexture;
                passData.rayReference = reference;
                passData.rayFullSize = fullSize;
                passData.rayScale = scale;
                Configure(passData, volume, tracedWidth, tracedHeight, descriptor, emitterCount, sky);
                builder.SetRenderAttachment(position, 0, AccessFlags.WriteAll);
                builder.SetRenderAttachment(normal, 1, AccessFlags.WriteAll);
                builder.UseTexture(resourceData.cameraDepthTexture);
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc((PassData data, RasterGraphContext context) => Execute(data, context));
            }

            using (IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass("Basis GI Specular Trace", out RayTraceData data, samplerTrace))
            {
                data.tracer = tracer;
                data.position = position;
                data.normal = normal;
                data.specular = rayResult;
                data.result = rayResult;
                data.skyCube = sky.Cube;
                data.reference = reference;
                data.size = new Vector4(tracedWidth, tracedHeight, 1f / tracedWidth, 1f / tracedHeight);
                data.trace = new Vector4(volume.maxRayLength.value, volume.obscuranceRadius.value, volume.obscuranceIntensity.value, volume.fadeDistance.value);
                data.bias = new Vector4(volume.rayTracedNormalBias.value, RayDistanceBias, volume.emitterIntensity.value, volume.rayTracedLightIntensity.value);
                data.options = new Vector4(volume.fireflyClamp.value, RayBounceThreshold, volume.rayTracedShadows.value ? 1f : 0f, 0f);
                data.specularParams = new Vector4(volume.specularRayLength.value, volume.specularIntensity.value,
                    volume.specularFadeDistance.value, specularBounces);
                data.sky = new Vector4(sky.Mip, sky.IsValid ? sky.Intensity : 0f, 0f, 0f);
                data.skyDecode = sky.Decode;
                data.diffuseEnabled = false;
                data.specularEnabled = true;
                data.rayCount = 1;
                data.bounces = specularBounces;
                data.lightCount = tracer.Lights.Count;
                data.lightSamples = lightSamples;
                data.viewCount = viewCount;
                data.frameIndex = frame % 64;
                data.width = tracedWidth;
                data.height = tracedHeight;

                builder.UseTexture(position, AccessFlags.Read);
                builder.UseTexture(normal, AccessFlags.Read);
                builder.UseTexture(rayResult, AccessFlags.ReadWrite);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc((RayTraceData data, UnsafeGraphContext context) => ExecuteRayTrace(data, context));
            }

            using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Basis GI Specular Resolve", out PassData passData, samplerResolve))
            {
                passData.stage = Stage.RayResolve;
                passData.material = rayStagesMaterial;
                passData.materialPass = PassRayResolve;
                passData.rayResult = rayResult;
                builder.SetRenderAttachment(traced, 0, AccessFlags.WriteAll);
                builder.UseTexture(rayResult);
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc((PassData data, RasterGraphContext context) => Execute(data, context));
            }

            TextureHandle denoiseSource = traced;
            if (volume.specularTemporal.value)
            {
                using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Basis GI Specular Temporal", out PassData passData, samplerTemporal))
                {
                    passData.stage = Stage.Temporal;
                    passData.material = material;
                    passData.materialPass = PassTemporal;
                    passData.indirect = traced;
                    passData.history = historyRead;
                    passData.historyStats = historyReadStats;
                    passData.historyValid = historyValid;
                    passData.previousViewProjection = previousViewProjection;
                    previousViewProjection[0] = history.PreviousSpecularViewProjection[0];
                    previousViewProjection[1] = history.PreviousSpecularViewProjection[1];
                    builder.SetRenderAttachment(historyWrite, 0, AccessFlags.WriteAll);
                    builder.SetRenderAttachment(historyWriteStats, 1, AccessFlags.WriteAll);
                    builder.UseTexture(traced);
                    builder.UseTexture(historyRead);
                    builder.UseTexture(historyReadStats);
                    builder.UseTexture(resourceData.cameraDepthTexture);
                    builder.AllowPassCulling(false);
                    builder.AllowGlobalStateModification(true);
                    builder.SetRenderFunc((PassData data, RasterGraphContext context) => Execute(data, context));
                }
                denoiseSource = historyWrite;
            }

            // Half the taps the diffuse gather uses. A mirror ray is exact, so the only thing left to filter
            // is the light resampling at the hit; blurring past that starts destroying the detail that makes
            // a reflection read as a reflection rather than as a coloured sheen.
            bool statsValid = volume.specularTemporal.value;
            int taps = Mathf.Clamp(Mathf.RoundToInt(volume.smoothing.value), 0, 4);
            if (taps > 0)
            {
                denoiseSource = RecordSpecularBlur(renderGraph, resourceData, denoiseSource, blurA, historyWriteStats, statsValid,
                    new Vector4(1f / tracedWidth, 0f, taps, 0f));
                denoiseSource = RecordSpecularBlur(renderGraph, resourceData, denoiseSource, blurB, historyWriteStats, statsValid,
                    new Vector4(0f, 1f / tracedHeight, taps, 0f));
            }

            using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Basis GI Specular Upsample", out PassData passData, samplerUpsample))
            {
                passData.stage = Stage.Composite;
                passData.material = material;
                passData.materialPass = PassSpecularUpsample;
                passData.indirect = denoiseSource;
                // Render graph pools PassData and does not clear it. The Composite stage binds normals when
                // the handle is valid, and a stale one here is a handle from a graph that no longer exists.
                passData.normals = TextureHandle.nullHandle;
                // Same reason, and not optional: the upsample takes each traced parent's depth out of these
                // statistics, so a stale handle here is a stale depth for every weight it computes.
                passData.stats = historyWriteStats;
                passData.statsValid = statsValid;
                builder.SetRenderAttachment(published, 0, AccessFlags.WriteAll);
                builder.UseTexture(denoiseSource);
                builder.UseTexture(historyWriteStats);
                builder.UseTexture(resourceData.cameraDepthTexture);
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc((PassData data, RasterGraphContext context) => Execute(data, context));
            }

            using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Basis GI Specular Publish", out SpecularGlobalData data, samplerPublish))
            {
                // x gates the whole thing in the lit shader; y is the reciprocal of the roughness at which
                // the traced mirror stops standing in for the lobe, so the shader does a multiply rather
                // than a divide per pixel.
                data.parameters = new Vector4(1f, 1f / Mathf.Max(0.01f, volume.specularMaxRoughness.value), 0f, 0f);
                builder.UseTexture(published, AccessFlags.Read);
                builder.SetGlobalTextureAfterPass(published, idSpecularTexture);
                builder.AllowGlobalStateModification(true);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (SpecularGlobalData data, RasterGraphContext context) =>
                {
                    context.cmd.SetGlobalVector(idSpecularParams, data.parameters);
                });
            }

            StoreViewProjection(cameraData, history.PreviousSpecularViewProjection);
            history.SpecularWrite = history.SpecularRead;
            history.SpecularValid = volume.specularTemporal.value;
            history.RecordSpecularFrame(frame);
            // The diffuse pass prunes too, but a world running reflections with the diffuse gather switched
            // off never reaches that call, and cameras that stopped rendering would keep their accumulation
            // for as long as the application ran.
            BasisGlobalIlluminationHistory.PruneStale(frame, HistoryMaxAge);
        }

        /// <summary>
        /// Turns the effect off for every shader that would otherwise still be sampling a stale reflection.
        /// A camera that stops rendering the pass - the volume switched off, the player walked out of it, a
        /// mirror the filter now rejects - leaves the global texture bound to whatever was last written, and
        /// without this the next opaque draw would reflect the last frame that had reflections.
        /// </summary>
        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            cmd?.SetGlobalVector(idSpecularParams, Vector4.zero);
        }

        private TextureHandle RecordSpecularBlur(RenderGraph renderGraph, UniversalResourceData resourceData, TextureHandle source,
            TextureHandle target, TextureHandle stats, bool statsValid, Vector4 axis)
        {
            using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Basis GI Specular Blur", out PassData passData, samplerBlur))
            {
                passData.stage = Stage.Blur;
                passData.material = material;
                passData.materialPass = PassBlur;
                passData.indirect = source;
                passData.stats = stats;
                passData.statsValid = statsValid;
                passData.blurAxis = axis;
                builder.SetRenderAttachment(target, 0, AccessFlags.WriteAll);
                builder.UseTexture(source);
                builder.UseTexture(stats);
                builder.UseTexture(resourceData.cameraDepthTexture);
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc((PassData data, RasterGraphContext context) => Execute(data, context));
            }
            return target;
        }

        /// <summary>
        /// The subset of the shared constants the reflection stages read. Intensity, saturation and tint are
        /// the diffuse gather's look controls and are deliberately left at neutral here: a reflection that
        /// was tinted or desaturated would no longer match the surface it is reflecting.
        /// </summary>
        private void FillConstants(BasisGlobalIlluminationVolume volume, int frame)
        {
            constants[0] = new Vector4(1f, 1f, volume.obscuranceIntensity.value, volume.obscuranceRadius.value);
            constants[1] = new Vector4(volume.specularRayLength.value, volume.thickness.value, volume.jitter.value, volume.specularFadeDistance.value);
            constants[2] = new Vector4(1f, volume.ResolvedRaySteps(), volume.fireflyClamp.value, 0f);
            float temporalResponse = Mathf.Clamp(volume.temporalResponse.value * SpecularTemporalResponseScale,
                BasisGlobalIlluminationVolume.TemporalResponseMin, BasisGlobalIlluminationVolume.TemporalResponseMax);
            constants[3] = new Vector4(frame % 64, temporalResponse, volume.depthRejection.value, volume.emitterIntensity.value);
        }

        private void Configure(PassData passData, BasisGlobalIlluminationVolume volume, int tracedWidth, int tracedHeight,
            in RenderTextureDescriptor descriptor, int emitterCount, in BasisGlobalIlluminationRayTracer.SkyBinding sky)
        {
            passData.constants = constants;
            passData.sky = new Vector4(sky.Mip, sky.IsValid ? sky.Intensity : 0f, 0f, 0f);
            passData.skyDecode = sky.Decode;
            passData.tint = Color.white;
            passData.tracedTexelSize = new Vector4(1f / tracedWidth, 1f / tracedHeight, tracedWidth, tracedHeight);
            passData.sourceTexelSize = new Vector4(1f / descriptor.width, 1f / descriptor.height, descriptor.width, descriptor.height);
            passData.debugView = 0;
            passData.emitterCount = emitterCount;
            passData.emitterSpheres = emitterSpheres;
            passData.emitterRadiance = emitterRadiance;
        }

        /// <summary>
        /// The two keywords the reflection stages depend on. The diffuse pass sets these too, from the same
        /// volume, but it records later in the frame - so on a frame where only reflections run they would
        /// otherwise still be carrying the previous frame's values.
        /// </summary>
        private void ApplyKeywords(BasisGlobalIlluminationVolume volume)
        {
            CoreUtils.SetKeyword(material, "_BASISGI_NEIGHBOURHOOD_CLAMP", volume.neighbourhoodClamp.value);
            CoreUtils.SetKeyword(material, "_BASISGI_BILATERAL_UPSAMPLE", volume.bilateralUpsample.value && volume.ResolvedResolutionDivisor() > 1);
        }
    }

    private sealed class SpecularGlobalData
    {
        public Vector4 parameters;
    }
}
