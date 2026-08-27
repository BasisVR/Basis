using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.UnifiedRayTracing;
using UnityEngine.Rendering.Universal;

public sealed class BasisGlobalIlluminationPass : ScriptableRenderPass
{
    public const int PassTrace = 0, PassTemporal = 1, PassBlur = 2, PassComposite = 3, PassDebug = 4, PassCopyColor = 5;
    public const int PassRayPrepass = 0, PassRayResolve = 1;
    public const int MaxEmitters = 48;
    public const int HistoryMaxAge = 60;
    public const float RayDistanceBias = 0.0015f;
    public const float RayBounceThreshold = 0.02f;
    // An a-trous cascade: the same small kernel run again at double the stride each level, so a few
    // cheap passes reach as far as one enormous one would. Both modes use it - a screen space gather at
    // one or two rays per pixel is just as sparse as a traced one, and a fixed radius can only choose
    // between smearing a settled image and leaving a sparse one speckled.
    public const int BlurLevels = 3;
    // The compute backend walks a software BVH instead of ray tracing hardware, so a ray costs orders of
    // magnitude more there. Ceilings rather than a refusal: the mode stays usable on a GPU without DXR, and
    // the frame cost stays somewhere near the rest of the renderer.
    public const int ComputeBackendRayCeiling = 1;
    public const int ComputeBackendBounceCeiling = 1;
    public const int ComputeBackendLightSampleCeiling = 1;
    // The ray traced gather carries far more variance per frame than the screen space one, so it keeps
    // accumulating past where the response slider alone would stop. The slider still orders the two - it is
    // scaled, not ignored - so a player who wants a snappier bounce still gets one.
    public const float RayTemporalResponseScale = 0.35f;

    private enum Stage { CopyColor, Trace, Temporal, Blur, Composite, RayPrepass, RayResolve }

    private sealed class PassData
    {
        public Stage stage;
        public Material material;
        public int materialPass;
        public TextureHandle source, sceneColor, indirect, history, historyStats, normals, rayResult, stats;
        public Vector4 blurAxis;
        public bool historyValid, statsValid;
        public Matrix4x4[] previousViewProjection;
        public Vector4[] constants;
        public Vector4 tint, tracedTexelSize, sourceTexelSize;
        public Vector4 sky, skyDecode;
        public int debugView, emitterCount;
        public Vector4[] emitterSpheres, emitterRadiance;
        public Vector4 rayReference, rayFullSize;
        public int rayScale;
    }

    private sealed class RayTraceData
    {
        public BasisGlobalIlluminationRayTracer tracer;
        public TextureHandle position, normal, result;
        public Texture skyCube;
        public Vector4 reference, size, trace, bias, options, sky, skyDecode;
        public int rayCount, bounces, lightCount, lightSamples, viewCount, frameIndex;
        public int width, height;
    }

    private static readonly int idSceneColor = Shader.PropertyToID("_BasisGISceneColor");
    private static readonly int idIndirect = Shader.PropertyToID("_BasisGIIndirect");
    private static readonly int idHistory = Shader.PropertyToID("_BasisGIHistory");
    private static readonly int idHistoryStats = Shader.PropertyToID("_BasisGIHistoryStats");
    private static readonly int idStats = Shader.PropertyToID("_BasisGIStats");
    private static readonly int idStatsValid = Shader.PropertyToID("_BasisGIStatsValid");
    private static readonly int idNormals = Shader.PropertyToID("_BasisGINormals");
    private static readonly int idParams0 = Shader.PropertyToID("_BasisGIParams0");
    private static readonly int idParams1 = Shader.PropertyToID("_BasisGIParams1");
    private static readonly int idParams2 = Shader.PropertyToID("_BasisGIParams2");
    private static readonly int idParams3 = Shader.PropertyToID("_BasisGIParams3");
    private static readonly int idTint = Shader.PropertyToID("_BasisGITint");
    private static readonly int idTracedTexelSize = Shader.PropertyToID("_BasisGITracedTexelSize");
    private static readonly int idSourceTexelSize = Shader.PropertyToID("_BasisGISourceTexelSize");
    private static readonly int idSkyCube = Shader.PropertyToID("_BasisGISkyCube");
    private static readonly int idSky = Shader.PropertyToID("_BasisGISky");
    private static readonly int idSkyDecode = Shader.PropertyToID("_BasisGISkyDecode");
    private static readonly int idPrevViewProjection = Shader.PropertyToID("_BasisGIPrevViewProjection");
    private static readonly int idHistoryValid = Shader.PropertyToID("_BasisGIHistoryValid");
    private static readonly int idDebugView = Shader.PropertyToID("_BasisGIDebugView");
    private static readonly int idBlurAxis = Shader.PropertyToID("_BasisGIBlurAxis");
    private static readonly int idEmitterCount = Shader.PropertyToID("_BasisGIEmitterCount");
    private static readonly int idEmitterSpheres = Shader.PropertyToID("_BasisGIEmitterSpheres");
    private static readonly int idEmitterRadiance = Shader.PropertyToID("_BasisGIEmitterRadiance");

    private static readonly int idRtReference = Shader.PropertyToID("_BasisGIRtReference");
    private static readonly int idRtFullSize = Shader.PropertyToID("_BasisGIRtFullSize");
    private static readonly int idRtScale = Shader.PropertyToID("_BasisGIRtScale");
    private static readonly int idRtResolveSource = Shader.PropertyToID("_BasisGIRtResolveSource");
    private static readonly int idRtPositionTex = Shader.PropertyToID("_BasisGIRtPositionTex");
    private static readonly int idRtNormalTex = Shader.PropertyToID("_BasisGIRtNormalTex");
    private static readonly int idRtResultTex = Shader.PropertyToID("_BasisGIRtResultTex");
    private static readonly int idRtInstances = Shader.PropertyToID("_BasisGIRtInstances");
    private static readonly int idRtLights = Shader.PropertyToID("_BasisGIRtLights");
    private static readonly int idRtIndices = Shader.PropertyToID("_BasisGIRtIndices");
    private static readonly int idRtNormals = Shader.PropertyToID("_BasisGIRtNormals");
    private static readonly int idRtSkyCube = Shader.PropertyToID("_BasisGIRtSkyCube");
    private static readonly int idRtSkyDecode = Shader.PropertyToID("_BasisGIRtSkyDecode");
    private static readonly int idRtSky = Shader.PropertyToID("_BasisGIRtSky");
    private static readonly int idRtSize = Shader.PropertyToID("_BasisGIRtSize");
    private static readonly int idRtTrace = Shader.PropertyToID("_BasisGIRtTrace");
    private static readonly int idRtBias = Shader.PropertyToID("_BasisGIRtBias");
    private static readonly int idRtOptions = Shader.PropertyToID("_BasisGIRtOptions");
    private static readonly int idRtRayCount = Shader.PropertyToID("_BasisGIRtRayCount");
    private static readonly int idRtBounces = Shader.PropertyToID("_BasisGIRtBounces");
    private static readonly int idRtLightCount = Shader.PropertyToID("_BasisGIRtLightCount");
    private static readonly int idRtLightSamples = Shader.PropertyToID("_BasisGIRtLightSamples");
    private static readonly int idRtViewCount = Shader.PropertyToID("_BasisGIRtViewCount");
    private static readonly int idRtFrameIndex = Shader.PropertyToID("_BasisGIRtFrameIndex");
    private const string RtAccelName = "_BasisGIRtAccel";

    private static readonly ProfilingSampler samplerRayPrepass = new ProfilingSampler("Basis GI Ray Prepass");
    private static readonly ProfilingSampler samplerRayTrace = new ProfilingSampler("Basis GI Ray Trace");
    private static readonly ProfilingSampler samplerRayResolve = new ProfilingSampler("Basis GI Ray Resolve");
    private static readonly ProfilingSampler samplerCopy = new ProfilingSampler("Basis GI Copy Color");
    private static readonly ProfilingSampler samplerTrace = new ProfilingSampler("Basis GI Trace");
    private static readonly ProfilingSampler samplerTemporal = new ProfilingSampler("Basis GI Temporal");
    private static readonly ProfilingSampler samplerBlur = new ProfilingSampler("Basis GI Blur");
    private static readonly ProfilingSampler samplerComposite = new ProfilingSampler("Basis GI Composite");

    private static readonly Vector4[] emitterSpheres = new Vector4[MaxEmitters];
    private static readonly Vector4[] emitterRadiance = new Vector4[MaxEmitters];
    private static readonly List<BasisGlobalIlluminationEmitter> emitterScratch = new List<BasisGlobalIlluminationEmitter>();
    private readonly Matrix4x4[] previousViewProjection = new Matrix4x4[2];
    private readonly Vector4[] constants = new Vector4[4];

    private Material material;
    private Material rayStagesMaterial;
    private RayTracingShader rayTraceShader;
    private ComputeShader rayTraceCompute;
    private bool rayComputeFallback;
    public BasisGlobalIlluminationDebugView DebugView;
    public bool UseNormalsTexture;
    public bool RayTracingAvailable;
    private bool loggedRayTracingFallback;
    private bool loggedComputeBackend;

    public BasisGlobalIlluminationPass(Material material)
    {
        this.material = material;
        profilingSampler = new ProfilingSampler("Basis Global Illumination");
        // Before transparents, not before post processing: the composite is a multiply into the camera
        // colour, and anything already drawn when it runs gets multiplied too - world space UI included.
        // Opaque geometry is what the bounce is for, and it is all that is in the buffer at this point.
        renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;
        requiresIntermediateTexture = true;
    }

    public void SetMaterial(Material value) { material = value; }

    public void SetRayTracing(Material stages, RayTracingShader hardware, ComputeShader compute, bool computeFallback)
    {
        rayStagesMaterial = stages;
        rayTraceShader = hardware;
        rayTraceCompute = compute;
        rayComputeFallback = computeFallback;
    }

    public static int ViewCountOf(UniversalCameraData cameraData)
    {
        return cameraData.xr != null && cameraData.xr.enabled && cameraData.xr.singlePassEnabled ? 2 : 1;
    }

    private static RenderTextureDescriptor RayArrayDescriptor(int width, int height, int viewCount, GraphicsFormat format, bool randomWrite)
    {
        return new RenderTextureDescriptor(width, height, format, GraphicsFormat.None, 0)
        {
            dimension = TextureDimension.Tex2DArray,
            volumeDepth = Mathf.Max(1, viewCount),
            msaaSamples = 1,
            useMipMap = false,
            autoGenerateMips = false,
            enableRandomWrite = randomWrite,
            sRGB = false
        };
    }

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        if (material == null) { return; }

        UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
        UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
        BasisGlobalIlluminationVolume volume = VolumeManager.instance.stack.GetComponent<BasisGlobalIlluminationVolume>();
        if (volume == null || !volume.IsActive()) { return; }
        if (!resourceData.cameraColor.IsValid() || !resourceData.cameraDepthTexture.IsValid()) { return; }

        Camera camera = cameraData.camera;
        RenderTextureDescriptor descriptor = cameraData.cameraTargetDescriptor;
        int divisor = volume.ResolvedResolutionDivisor();
        int tracedWidth = Mathf.Max(1, descriptor.width / divisor);
        int tracedHeight = Mathf.Max(1, descriptor.height / divisor);

        int frame = Time.renderedFrameCount;
        int hash = BasisGlobalIlluminationHistory.ComputeHash(camera, cameraData.xr);
        BasisGlobalIlluminationHistory history = BasisGlobalIlluminationHistory.Get(hash);
        bool contiguous = history.LastFrame >= 0 && frame - history.LastFrame <= 2;
        history.EnsureAllocated(descriptor, tracedWidth, tracedHeight);
        bool historyValid = volume.temporalFilter.value && history.Valid && contiguous;

        ApplyKeywords(volume);

        RenderTextureDescriptor sceneColorDescriptor = descriptor;
        sceneColorDescriptor.width = tracedWidth;
        sceneColorDescriptor.height = tracedHeight;
        sceneColorDescriptor.msaaSamples = 1;
        sceneColorDescriptor.depthStencilFormat = GraphicsFormat.None;
        sceneColorDescriptor.depthBufferBits = 0;
        sceneColorDescriptor.useMipMap = false;
        sceneColorDescriptor.autoGenerateMips = false;
        sceneColorDescriptor.graphicsFormat = GraphicsFormat.B10G11R11_UFloatPack32;

        RenderTextureDescriptor indirectDescriptor = sceneColorDescriptor;
        indirectDescriptor.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;

        TextureHandle sceneColor = UniversalRenderer.CreateRenderGraphTexture(renderGraph, sceneColorDescriptor, "_BasisGISceneColor", false, FilterMode.Bilinear, TextureWrapMode.Clamp);
        TextureHandle traced = UniversalRenderer.CreateRenderGraphTexture(renderGraph, indirectDescriptor, "_BasisGITraced", false, FilterMode.Bilinear, TextureWrapMode.Clamp);
        TextureHandle blurA = UniversalRenderer.CreateRenderGraphTexture(renderGraph, indirectDescriptor, "_BasisGIBlurA", false, FilterMode.Bilinear, TextureWrapMode.Clamp);
        TextureHandle blurB = UniversalRenderer.CreateRenderGraphTexture(renderGraph, indirectDescriptor, "_BasisGIBlurB", false, FilterMode.Bilinear, TextureWrapMode.Clamp);

        TextureHandle historyRead = renderGraph.ImportTexture(history.Indirect[history.Read]);
        TextureHandle historyReadStats = renderGraph.ImportTexture(history.Stats[history.Read]);
        TextureHandle historyWrite = renderGraph.ImportTexture(history.Indirect[history.Write]);
        TextureHandle historyWriteStats = renderGraph.ImportTexture(history.Stats[history.Write]);
        TextureHandle normals = UseNormalsTexture && resourceData.cameraNormalsTexture.IsValid() ? resourceData.cameraNormalsTexture : TextureHandle.nullHandle;

        // The ray traced mode replaces the screen space gather and nothing else: the temporal filter, the
        // bilateral blur and the composite downstream read the same traced texture either way. A GPU or a
        // scene that cannot serve the trace falls back to the screen space gather rather than to nothing.
        bool rayTraced = volume.IsRayTraced() && PrepareRayTracing(volume, camera, frame);

        // The ray budget is resolved before anything reads it, because the denoiser is driven by how many
        // samples a pixel actually paid for: a ceiling applied further down would leave the filter
        // trusting a sample count that was never taken.
        int rayCount = volume.ResolvedRayCount();
        int bounces = volume.ResolvedBounces();
        int lightSamples = volume.ResolvedRayTracedLightSamples();
        if (rayTraced && BasisGlobalIlluminationRayTracer.Instance.Context.Backend == RayTracingBackend.Compute)
        {
            rayCount = Mathf.Min(rayCount, ComputeBackendRayCeiling);
            bounces = Mathf.Min(bounces, ComputeBackendBounceCeiling);
            lightSamples = Mathf.Min(lightSamples, ComputeBackendLightSampleCeiling);
            ReportComputeBackend(volume);
        }

        // Resolved once for the whole frame and handed to both gathers, so a ray that misses is worth
        // the same thing either side of a mode switch.
        BasisGlobalIlluminationRayTracer.SkyBinding sky = BasisGlobalIlluminationRayTracer.ResolveSky(volume.fallback.value, volume.fallbackIntensity.value);
        // A raster pass can only bind render graph resources, and this is an engine texture; it is also
        // resolved from the render settings, so it is the same for every camera in the frame and a plain
        // global is the honest place for it.
        if (sky.Cube != null) { Shader.SetGlobalTexture(idSkyCube, sky.Cube); }

        FillConstants(volume, frame, rayTraced, rayCount);
        int emitterCount = volume.emitters.value ? GatherEmitters(camera, volume.ResolvedMaxEmitters()) : 0;

        if (rayTraced)
        {
            RecordRayTraced(renderGraph, resourceData, cameraData, volume, traced, tracedWidth, tracedHeight, descriptor, frame, emitterCount, rayCount, bounces, lightSamples, sky);
        }
        else
        {
            using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Basis GI Copy Color", out PassData passData, samplerCopy))
            {
                passData.stage = Stage.CopyColor;
                passData.material = material;
                passData.materialPass = PassCopyColor;
                passData.source = resourceData.cameraColor;
                Configure(passData, volume, tracedWidth, tracedHeight, descriptor, emitterCount, sky);
                builder.SetRenderAttachment(sceneColor, 0, AccessFlags.WriteAll);
                builder.UseTexture(resourceData.cameraColor);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc((PassData data, RasterGraphContext context) => Execute(data, context));
            }

            using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Basis GI Trace", out PassData passData, samplerTrace))
            {
                passData.stage = Stage.Trace;
                passData.material = material;
                passData.materialPass = PassTrace;
                passData.source = sceneColor;
                passData.sceneColor = sceneColor;
                passData.normals = normals;
                builder.SetRenderAttachment(traced, 0, AccessFlags.WriteAll);
                builder.UseTexture(sceneColor);
                builder.UseTexture(resourceData.cameraDepthTexture);
                if (normals.IsValid()) { builder.UseTexture(normals); }
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc((PassData data, RasterGraphContext context) => Execute(data, context));
            }
        }

        TextureHandle denoiseSource = traced;
        if (volume.temporalFilter.value)
        {
            using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Basis GI Temporal", out PassData passData, samplerTemporal))
            {
                passData.stage = Stage.Temporal;
                passData.material = material;
                passData.materialPass = PassTemporal;
                passData.source = traced;
                passData.indirect = traced;
                passData.history = historyRead;
                passData.historyStats = historyReadStats;
                passData.historyValid = historyValid;
                passData.previousViewProjection = previousViewProjection;
                previousViewProjection[0] = history.PreviousViewProjection[0];
                previousViewProjection[1] = history.PreviousViewProjection[1];
                builder.SetRenderAttachment(historyWrite, 0, AccessFlags.WriteAll);
                builder.SetRenderAttachment(historyWriteStats, 1, AccessFlags.WriteAll);
                builder.UseTexture(traced);
                builder.UseTexture(historyRead);
                builder.UseTexture(historyReadStats);
                builder.UseTexture(resourceData.cameraDepthTexture);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc((PassData data, RasterGraphContext context) => Execute(data, context));
            }
            denoiseSource = historyWrite;
        }

        // The filter reads the statistics the temporal pass just wrote, so it knows how many frames each
        // pixel has behind it and how far its own luminance has been swinging. Where the temporal filter is
        // switched off there are none, and the filter falls back to treating every pixel as unresolved.
        bool statsValid = volume.temporalFilter.value;
        int taps = Mathf.Clamp(Mathf.RoundToInt(volume.smoothing.value * 2f), 0, 4);
        if (taps > 0)
        {
            int levels = volume.wideBlur.value ? BlurLevels : BlurLevels - 1;
            for (int level = 0; level < levels; level++)
            {
                float stride = 1 << level;
                denoiseSource = RecordBlur(renderGraph, resourceData, denoiseSource, blurA, historyWriteStats, statsValid, new Vector4(stride / tracedWidth, 0f, taps, 0f));
                denoiseSource = RecordBlur(renderGraph, resourceData, denoiseSource, blurB, historyWriteStats, statsValid, new Vector4(0f, stride / tracedHeight, taps, 0f));
            }
        }

        using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Basis GI Composite", out PassData passData, samplerComposite))
        {
            passData.stage = Stage.Composite;
            passData.material = material;
            passData.materialPass = DebugView == BasisGlobalIlluminationDebugView.None ? PassComposite : PassDebug;
            passData.source = denoiseSource;
            passData.indirect = denoiseSource;
            passData.normals = normals;
            builder.SetRenderAttachment(resourceData.cameraColor, 0, AccessFlags.ReadWrite);
            builder.UseTexture(denoiseSource);
            builder.UseTexture(resourceData.cameraDepthTexture);
            if (normals.IsValid()) { builder.UseTexture(normals); }
            builder.AllowGlobalStateModification(true);
            builder.SetRenderFunc((PassData data, RasterGraphContext context) => Execute(data, context));
        }

        StoreViewProjection(cameraData, history.PreviousViewProjection);
        history.Write = history.Read;
        history.Valid = volume.temporalFilter.value;
        history.LastFrame = frame;
        BasisGlobalIlluminationHistory.PruneStale(frame, HistoryMaxAge);
    }

    private void Configure(PassData passData, BasisGlobalIlluminationVolume volume, int tracedWidth, int tracedHeight, in RenderTextureDescriptor descriptor,
        int emitterCount, in BasisGlobalIlluminationRayTracer.SkyBinding sky)
    {
        passData.constants = constants;
        passData.sky = new Vector4(sky.Mip, sky.IsValid ? sky.Intensity : 0f, 0f, 0f);
        passData.skyDecode = sky.Decode;
        passData.tint = volume.tint.value.linear;
        passData.tracedTexelSize = new Vector4(1f / tracedWidth, 1f / tracedHeight, tracedWidth, tracedHeight);
        passData.sourceTexelSize = new Vector4(1f / descriptor.width, 1f / descriptor.height, descriptor.width, descriptor.height);
        passData.debugView = (int)DebugView;
        passData.emitterCount = emitterCount;
        passData.emitterSpheres = emitterSpheres;
        passData.emitterRadiance = emitterRadiance;
    }

    /// <summary>
    /// Brings the shared acceleration structure up to date for this frame. Returns false when the mode cannot
    /// run - no ray tracing on this GPU, the context failed to come up, or the scene holds no traceable
    /// geometry yet - and the caller then renders the screen space gather instead.
    /// </summary>
    private bool PrepareRayTracing(BasisGlobalIlluminationVolume volume, Camera camera, int frame)
    {
        if (camera == null) { return false; }
        if (!RayTracingAvailable || rayStagesMaterial == null)
        {
            ReportRayTracingFallback(DescribeRayTracingGap());
            return false;
        }

        BasisGlobalIlluminationRayTracer tracer = BasisGlobalIlluminationRayTracer.GetOrCreate(rayTraceShader, rayTraceCompute, rayComputeFallback);
        if (tracer == null)
        {
            ReportRayTracingFallback(BasisGlobalIlluminationRayTracer.Failure);
            return false;
        }

        loggedRayTracingFallback = false;
        return tracer.Refresh(volume.ResolvedSceneSettings(), volume.ResolvedLightSettings(), camera.transform.position, frame, Time.unscaledTime);
    }

    /// <summary>Why the ray traced mode cannot run, phrased as something a player or an author can act on.</summary>
    private string DescribeRayTracingGap()
    {
        if (rayStagesMaterial == null) { return "the ray tracing stage shaders failed to load"; }
        if (BasisGlobalIlluminationRayContext.HardwareSupported) { return "the hardware ray tracing shader is missing"; }
        if (!rayComputeFallback)
        {
            return "this GPU has no hardware ray tracing. Run on Direct3D12 or Vulkan, or enable Ray Tracing Compute Fallback on the Basis Global Illumination renderer feature to trace on the compute backend instead";
        }
        if (!BasisGlobalIlluminationRayContext.ComputeSupported) { return "this GPU supports neither hardware ray tracing nor compute shaders"; }
        return "the compute ray tracing kernel is missing";
    }

    private void ReportRayTracingFallback(string reason)
    {
        if (loggedRayTracingFallback) { return; }
        loggedRayTracingFallback = true;
        Debug.LogWarning($"[BasisGI] Ray traced global illumination fell back to the screen space gather: {reason}.");
    }

    private void RecordRayTraced(RenderGraph renderGraph, UniversalResourceData resourceData, UniversalCameraData cameraData,
        BasisGlobalIlluminationVolume volume, TextureHandle traced, int tracedWidth, int tracedHeight,
        in RenderTextureDescriptor descriptor, int frame, int emitterCount, int rayCount, int bounces, int lightSamples,
        BasisGlobalIlluminationRayTracer.SkyBinding sky)
    {
        BasisGlobalIlluminationRayTracer tracer = BasisGlobalIlluminationRayTracer.Instance;

        int viewCount = ViewCountOf(cameraData);
        int scale = Mathf.Clamp(volume.ResolvedResolutionDivisor(), 1, 4);
        Vector3 viewer = cameraData.camera.transform.position;
        Vector4 reference = new Vector4(viewer.x, viewer.y, viewer.z, 0f);
        Vector4 fullSize = new Vector4(descriptor.width, descriptor.height, 1f / descriptor.width, 1f / descriptor.height);

        TextureHandle position = UniversalRenderer.CreateRenderGraphTexture(renderGraph,
            RayArrayDescriptor(tracedWidth, tracedHeight, viewCount, GraphicsFormat.R16G16B16A16_SFloat, false), "_BasisGIRtPosition", false);
        TextureHandle normal = UniversalRenderer.CreateRenderGraphTexture(renderGraph,
            RayArrayDescriptor(tracedWidth, tracedHeight, viewCount, GraphicsFormat.R16G16_SFloat, false), "_BasisGIRtNormal", false);
        TextureHandle result = UniversalRenderer.CreateRenderGraphTexture(renderGraph,
            RayArrayDescriptor(tracedWidth, tracedHeight, viewCount, GraphicsFormat.R16G16B16A16_SFloat, true), "_BasisGIRtResult", false);

        using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Basis GI Ray Prepass", out PassData passData, samplerRayPrepass))
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

        using (IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass("Basis GI Ray Trace", out RayTraceData data, samplerRayTrace))
        {
            data.tracer = tracer;
            data.position = position;
            data.normal = normal;
            data.result = result;
            data.skyCube = sky.Cube;
            data.reference = reference;
            data.size = new Vector4(tracedWidth, tracedHeight, 1f / tracedWidth, 1f / tracedHeight);
            data.trace = new Vector4(volume.maxRayLength.value, volume.obscuranceRadius.value, volume.obscuranceIntensity.value, volume.fadeDistance.value);
            data.bias = new Vector4(volume.rayTracedNormalBias.value, RayDistanceBias, volume.emitterIntensity.value, volume.rayTracedLightIntensity.value);
            data.options = new Vector4(volume.fireflyClamp.value, RayBounceThreshold, volume.rayTracedShadows.value ? 1f : 0f, 0f);
            data.sky = new Vector4(sky.Mip, sky.IsValid ? sky.Intensity : 0f, 0f, 0f);
            data.skyDecode = sky.Decode;
            data.rayCount = rayCount;
            data.bounces = bounces;
            data.lightCount = tracer.Lights.Count;
            data.lightSamples = lightSamples;
            data.viewCount = viewCount;
            data.frameIndex = frame % 64;
            data.width = tracedWidth;
            data.height = tracedHeight;

            builder.UseTexture(position, AccessFlags.Read);
            builder.UseTexture(normal, AccessFlags.Read);
            builder.UseTexture(result, AccessFlags.ReadWrite);
            builder.AllowPassCulling(false);
            builder.SetRenderFunc((RayTraceData data, UnsafeGraphContext context) => ExecuteRayTrace(data, context));
        }

        using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Basis GI Ray Resolve", out PassData passData, samplerRayResolve))
        {
            passData.stage = Stage.RayResolve;
            passData.material = rayStagesMaterial;
            passData.materialPass = PassRayResolve;
            passData.source = resourceData.cameraDepthTexture;
            passData.rayResult = result;
            builder.SetRenderAttachment(traced, 0, AccessFlags.WriteAll);
            builder.UseTexture(result);
            builder.UseTexture(resourceData.cameraDepthTexture);
            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);
            builder.SetRenderFunc((PassData data, RasterGraphContext context) => Execute(data, context));
        }
    }

    private void ReportComputeBackend(BasisGlobalIlluminationVolume volume)
    {
        if (loggedComputeBackend) { return; }
        loggedComputeBackend = true;
        bool clamped = volume.ResolvedRayCount() > ComputeBackendRayCeiling || volume.ResolvedBounces() > ComputeBackendBounceCeiling;
        string budget = clamped
            ? $" The ray budget is capped at {ComputeBackendRayCeiling} ray and {ComputeBackendBounceCeiling} bounce per pixel there, so raising Quality will not change the trace."
            : string.Empty;
        Debug.LogWarning($"[BasisGI] Ray traced global illumination is running on the compute backend: this GPU has no hardware ray tracing, so the BVH is walked in a compute shader and costs far more than it would on Direct3D12 or Vulkan.{budget}");
    }

    private static void ExecuteRayTrace(RayTraceData data, UnsafeGraphContext context)
    {
        BasisGlobalIlluminationRayTracer tracer = data.tracer;
        if (tracer == null || tracer.Context == null || tracer.Scene == null || !tracer.Scene.HasGeometry) { return; }

        CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
        BasisGlobalIlluminationRayScene scene = tracer.Scene;
        if (scene.NeedsBuild) { scene.Build(cmd); }

        IRayTracingShader shader = tracer.Context.TraceShader;
        shader.SetAccelerationStructure(cmd, RtAccelName, scene.AccelerationStructure);
        shader.SetTextureParam(cmd, idRtPositionTex, data.position);
        shader.SetTextureParam(cmd, idRtNormalTex, data.normal);
        shader.SetTextureParam(cmd, idRtResultTex, data.result);
        if (data.skyCube != null) { shader.SetTextureParam(cmd, idRtSkyCube, data.skyCube); }

        shader.SetBufferParam(cmd, idRtInstances, scene.InstanceBuffer);
        shader.SetBufferParam(cmd, idRtIndices, scene.IndexBuffer);
        shader.SetBufferParam(cmd, idRtNormals, scene.NormalBuffer);
        shader.SetBufferParam(cmd, idRtLights, tracer.Lights.Buffer);

        shader.SetVectorParam(cmd, idRtReference, data.reference);
        shader.SetVectorParam(cmd, idRtSize, data.size);
        shader.SetVectorParam(cmd, idRtTrace, data.trace);
        shader.SetVectorParam(cmd, idRtBias, data.bias);
        shader.SetVectorParam(cmd, idRtOptions, data.options);
        shader.SetVectorParam(cmd, idRtSky, data.sky);
        shader.SetVectorParam(cmd, idRtSkyDecode, data.skyDecode);
        shader.SetIntParam(cmd, idRtRayCount, data.rayCount);
        shader.SetIntParam(cmd, idRtBounces, data.bounces);
        shader.SetIntParam(cmd, idRtLightCount, data.lightCount);
        shader.SetIntParam(cmd, idRtLightSamples, data.lightSamples);
        shader.SetIntParam(cmd, idRtViewCount, data.viewCount);
        shader.SetIntParam(cmd, idRtFrameIndex, data.frameIndex);

        GraphicsBuffer scratch = tracer.Context.GetTraceScratch(data.width, data.height, data.viewCount);
        shader.Dispatch(cmd, scratch, (uint)data.width, (uint)data.height, (uint)data.viewCount);
    }

    private TextureHandle RecordBlur(RenderGraph renderGraph, UniversalResourceData resourceData, TextureHandle source, TextureHandle target,
        TextureHandle stats, bool statsValid, Vector4 axis)
    {
        using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Basis GI Blur", out PassData passData, samplerBlur))
        {
            passData.stage = Stage.Blur;
            passData.material = material;
            passData.materialPass = PassBlur;
            passData.source = source;
            passData.indirect = source;
            passData.blurAxis = axis;
            passData.stats = stats;
            passData.statsValid = statsValid;
            builder.SetRenderAttachment(target, 0, AccessFlags.WriteAll);
            builder.UseTexture(source);
            builder.UseTexture(stats);
            builder.UseTexture(resourceData.cameraDepthTexture);
            builder.AllowGlobalStateModification(true);
            builder.SetRenderFunc((PassData data, RasterGraphContext context) => Execute(data, context));
        }
        return target;
    }

    public static Matrix4x4 ComputeViewProjection(UniversalCameraData cameraData, int eye)
    {
        Matrix4x4 projection = GL.GetGPUProjectionMatrix(cameraData.GetProjectionMatrix(eye), true);
        return projection * cameraData.GetViewMatrix(eye);
    }

    public static void StoreViewProjection(UniversalCameraData cameraData, Matrix4x4[] target)
    {
        bool stereo = cameraData.xr != null && cameraData.xr.enabled && cameraData.xr.singlePassEnabled;
        target[0] = ComputeViewProjection(cameraData, 0);
        target[1] = stereo ? ComputeViewProjection(cameraData, 1) : target[0];
    }

    private void ApplyKeywords(BasisGlobalIlluminationVolume volume)
    {
        bool normalsTexture = UseNormalsTexture && volume.normalSource.value == BasisGlobalIlluminationNormalSource.NormalsTexture;
        CoreUtils.SetKeyword(material, "_BASISGI_NORMALS_TEXTURE", normalsTexture);
        CoreUtils.SetKeyword(material, "_BASISGI_FALLBACK_SKY", volume.fallback.value == BasisGlobalIlluminationFallback.Sky);
        CoreUtils.SetKeyword(material, "_BASISGI_FALLBACK_PROBE", volume.fallback.value == BasisGlobalIlluminationFallback.ReflectionProbe);
        CoreUtils.SetKeyword(material, "_BASISGI_EMITTERS", volume.emitters.value);
        CoreUtils.SetKeyword(material, "_BASISGI_EMITTER_OCCLUSION", volume.emitters.value && volume.emitterOcclusion.value);
        CoreUtils.SetKeyword(material, "_BASISGI_RAY_REUSE", volume.rayReuse.value);
        CoreUtils.SetKeyword(material, "_BASISGI_HIT_NORMAL", volume.quality.value >= BasisGlobalIlluminationQuality.High);
        CoreUtils.SetKeyword(material, "_BASISGI_NEIGHBOURHOOD_CLAMP", volume.neighbourhoodClamp.value);
        CoreUtils.SetKeyword(material, "_BASISGI_BILATERAL_UPSAMPLE", volume.bilateralUpsample.value && volume.ResolvedResolutionDivisor() > 1);
    }

    private void FillConstants(BasisGlobalIlluminationVolume volume, int frame, bool rayTraced, int rayCount)
    {
        constants[0] = new Vector4(volume.intensity.value, volume.saturation.value, volume.obscuranceIntensity.value, volume.obscuranceRadius.value);
        constants[1] = new Vector4(volume.maxRayLength.value, volume.thickness.value, volume.jitter.value, volume.fadeDistance.value);
        // The fallback's intensity rides with the sky binding rather than here, because that is where the
        // cubemap it applies to comes from.
        constants[2] = new Vector4(rayCount, volume.ResolvedRaySteps(), volume.fireflyClamp.value, 0f);
        float temporalResponse = rayTraced ? volume.temporalResponse.value * RayTemporalResponseScale : volume.temporalResponse.value;
        constants[3] = new Vector4(frame % 64, temporalResponse, volume.depthRejection.value, volume.emitterIntensity.value);
    }

    /// <summary>The emitter position and radius the shader would read at this slot.</summary>
    public static Vector4 EmitterSphereAt(int slot)
    {
        return slot >= 0 && slot < MaxEmitters ? emitterSpheres[slot] : Vector4.zero;
    }

    /// <summary>The emitter radiance and range the shader would read at this slot.</summary>
    public static Vector4 EmitterRadianceAt(int slot)
    {
        return slot >= 0 && slot < MaxEmitters ? emitterRadiance[slot] : Vector4.zero;
    }

    /// <summary>
    /// Uploads the emitters the shader reads. Ranking lives on the emitter itself so the screen space
    /// gather and the ray traced light list keep the same set, in the same order, with the same fade on
    /// the one at the edge of the budget.
    /// </summary>
    public static int GatherEmitters(Camera camera, int maxEmitters)
    {
        Vector3 viewer = camera.transform.position;
        BasisGlobalIlluminationEmitter.Selection selection = BasisGlobalIlluminationEmitter.Rank(
            emitterScratch, viewer, Mathf.Min(maxEmitters, MaxEmitters));

        for (int slot = 0; slot < selection.Count; slot++)
        {
            BasisGlobalIlluminationEmitter chosen = emitterScratch[slot];
            Vector3 position = chosen.WorldPosition;
            Vector3 radiance = chosen.Radiance * selection.WeightAt(slot);
            emitterSpheres[slot] = new Vector4(position.x, position.y, position.z, Mathf.Max(0.001f, chosen.Radius));
            emitterRadiance[slot] = new Vector4(radiance.x, radiance.y, radiance.z, Mathf.Max(0.001f, chosen.Range));
        }
        for (int slot = selection.Count; slot < MaxEmitters; slot++)
        {
            emitterSpheres[slot] = Vector4.zero;
            emitterRadiance[slot] = Vector4.zero;
        }
        emitterScratch.Clear();
        return selection.Count;
    }

    private static void Execute(PassData data, RasterGraphContext context)
    {
        RasterCommandBuffer cmd = context.cmd;

        switch (data.stage)
        {
            case Stage.CopyColor:
                SetSharedConstants(cmd, data);
                break;
            case Stage.RayPrepass:
                SetSharedConstants(cmd, data);
                cmd.SetGlobalVector(idRtReference, data.rayReference);
                cmd.SetGlobalVector(idRtFullSize, data.rayFullSize);
                cmd.SetGlobalInteger(idRtScale, data.rayScale);
                break;
            case Stage.RayResolve:
                cmd.SetGlobalTexture(idRtResolveSource, data.rayResult);
                break;
            case Stage.Trace:
                cmd.SetGlobalTexture(idSceneColor, data.sceneColor);
                if (data.normals.IsValid()) { cmd.SetGlobalTexture(idNormals, data.normals); }
                break;
            case Stage.Temporal:
                cmd.SetGlobalTexture(idIndirect, data.indirect);
                cmd.SetGlobalTexture(idHistory, data.history);
                cmd.SetGlobalTexture(idHistoryStats, data.historyStats);
                cmd.SetGlobalFloat(idHistoryValid, data.historyValid ? 1f : 0f);
                cmd.SetGlobalMatrixArray(idPrevViewProjection, data.previousViewProjection);
                break;
            case Stage.Blur:
                cmd.SetGlobalTexture(idIndirect, data.indirect);
                cmd.SetGlobalTexture(idStats, data.stats);
                cmd.SetGlobalFloat(idStatsValid, data.statsValid ? 1f : 0f);
                cmd.SetGlobalVector(idBlurAxis, data.blurAxis);
                break;
            case Stage.Composite:
                cmd.SetGlobalTexture(idIndirect, data.indirect);
                if (data.normals.IsValid()) { cmd.SetGlobalTexture(idNormals, data.normals); }
                break;
        }

        Blitter.BlitTexture(cmd, data.source, new Vector4(1f, 1f, 0f, 0f), data.material, data.materialPass);
    }

    private static void SetSharedConstants(RasterCommandBuffer cmd, PassData data)
    {
        cmd.SetGlobalVector(idParams0, data.constants[0]);
        cmd.SetGlobalVector(idParams1, data.constants[1]);
        cmd.SetGlobalVector(idParams2, data.constants[2]);
        cmd.SetGlobalVector(idParams3, data.constants[3]);
        cmd.SetGlobalVector(idTint, data.tint);
        cmd.SetGlobalVector(idTracedTexelSize, data.tracedTexelSize);
        cmd.SetGlobalVector(idSourceTexelSize, data.sourceTexelSize);
        cmd.SetGlobalVector(idSky, data.sky);
        cmd.SetGlobalVector(idSkyDecode, data.skyDecode);
        cmd.SetGlobalInteger(idDebugView, data.debugView);
        cmd.SetGlobalInteger(idEmitterCount, data.emitterCount);
        cmd.SetGlobalVectorArray(idEmitterSpheres, data.emitterSpheres);
        cmd.SetGlobalVectorArray(idEmitterRadiance, data.emitterRadiance);
    }

    public void Dispose()
    {
        BasisGlobalIlluminationHistory.ReleaseAll();
        BasisGlobalIlluminationRayTracer.Release();
    }
}
