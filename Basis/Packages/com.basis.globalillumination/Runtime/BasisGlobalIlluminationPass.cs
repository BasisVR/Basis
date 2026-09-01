using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.UnifiedRayTracing;
using UnityEngine.Rendering.Universal;

// Partial so the reflection pass can live in its own file and still reach the shader id table, the stage
// enum and the Execute switch this one owns. See BasisGlobalIlluminationSpecularPass.cs for why reflections
// need a second pass rather than another stage of this one.
public sealed partial class BasisGlobalIlluminationPass : ScriptableRenderPass
{
    public const int PassTrace = 0, PassTemporal = 1, PassBlur = 2, PassComposite = 3, PassDebug = 4, PassCopyColor = 5;
    public const int PassSpecularUpsample = 6;
    public const int PassCoarseSeed = 7, PassCoarseReduce = 8;
    public const int PassLightmapMask = 9;
    // How many traced texels one texel of the finished coarse summary stands for. Eight is the point
    // where a cell is big enough that skipping one is worth the tap that decided it, and still small
    // enough that the fine walk inside a cell it cannot rule out is only eight steps long.
    public const int CoarseBlock = 8;
    public const int PassRayPrepass = 0, PassRayResolve = 1;
    public const int MaxEmitters = 48;
    public const int HistoryMaxAge = 60;
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

    private enum Stage { CopyColor, Trace, Temporal, Blur, Composite, RayPrepass, RayResolve, Coarse, LightmapMask }

    private sealed class PassData
    {
        public Stage stage;
        public Material material;
        public int materialPass;
        public TextureHandle source, sceneColor, indirect, history, historyStats, normals, motion, rayResult, stats;
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
        public TextureHandle coarse;
        public Vector4 coarseTexelSize, coarseParams;
        public bool coarseValid;
        // The second fold of the coarse summary, and its span in traced texels. Pooled-PassData rule as
        // ever: every pass that runs Stage.Trace writes all three, valid or not.
        public TextureHandle coarseFar;
        public Vector4 coarseFarTexelSize;
        public float coarseFarBlock;
        public TextureHandle tracedDepth;
        public bool tracedDepthValid;
        public TextureHandle lightmapMask;
        public bool lightmapMaskValid;
        public Vector4 lightmapParams;
        public RendererListHandle lightmapRenderers;
        // The reflection trace's hit distances, which turn the temporal stage into its specular flavour:
        // virtual point reprojection instead of surface reprojection. Pooled-PassData rule as ever - every
        // pass that runs Stage.Temporal writes both fields, valid or not.
        public TextureHandle specularHitDistance;
        public bool specularHitDistanceValid;
        // Whether the depth seed writes the block's true (nearest, furthest) interval instead of one
        // representative texel - the reflection pyramid's mode. Pool rule: written at every Stage.Coarse
        // call site.
        public bool coarseConservative;
    }

    private sealed class RayTraceData
    {
        public BasisGlobalIlluminationRayTracer tracer;
        public TextureHandle position, normal, result, specular;
        public Texture skyCube;
        public Vector4 reference, size, trace, bias, options, sky, skyDecode, specularParams;
        public int rayCount, bounces, lightCount, lightSamples, viewCount, frameIndex;
        // Which halves of the structure this trace may hit. The structure can hold more than this effect
        // asked for - it is shared with ambient occlusion, which answers the same question differently -
        // so the ray narrows it rather than the contents.
        public int traceMask;
        public int width, height;
        // The kernel serves both gathers from one entry point, and the two passes that use it want different
        // halves. Render graph pools this object and does not clear it between frames, so both flags are
        // written explicitly at every call site rather than relying on a field initialiser that only ever
        // runs on the first allocation.
        public bool diffuseEnabled;
        public bool specularEnabled;
    }

    private static readonly int idSceneColor = Shader.PropertyToID("_BasisGISceneColor");
    private static readonly int idIndirect = Shader.PropertyToID("_BasisGIIndirect");
    private static readonly int idHistory = Shader.PropertyToID("_BasisGIHistory");
    private static readonly int idHistoryStats = Shader.PropertyToID("_BasisGIHistoryStats");
    private static readonly int idMotion = Shader.PropertyToID("_BasisGIMotion");
    private static readonly int idCoarseDepth = Shader.PropertyToID("_BasisGICoarseDepth");
    private static readonly int idCoarseTexelSize = Shader.PropertyToID("_BasisGICoarseTexelSize");
    private static readonly int idCoarseParams = Shader.PropertyToID("_BasisGICoarseParams");
    internal static readonly int idCoarseFarDepth = Shader.PropertyToID("_BasisGICoarseFarDepth");
    internal static readonly int idCoarseFarTexelSize = Shader.PropertyToID("_BasisGICoarseFarTexelSize");
    internal static readonly int idCoarseFarBlock = Shader.PropertyToID("_BasisGICoarseFarBlock");
    private static readonly int idTracedDepth = Shader.PropertyToID("_BasisGITracedDepth");
    private static readonly int idTracedDepthValid = Shader.PropertyToID("_BasisGITracedDepthValid");
    private static readonly int idSeedConservative = Shader.PropertyToID("_BasisGIDepthSeedConservative");
    private static readonly int idStats = Shader.PropertyToID("_BasisGIStats");
    private static readonly int idStatsValid = Shader.PropertyToID("_BasisGIStatsValid");
    private static readonly int idNormals = Shader.PropertyToID("_BasisGINormals");
    private static readonly int idLightmapMask = Shader.PropertyToID("_BasisGILightmapMask");
    private static readonly int idLightmapParams = Shader.PropertyToID("_BasisGILightmapParams");
    private static readonly int idLightmapMaskForce = Shader.PropertyToID("_BasisGILightmapMaskForce");

    /// <summary>
    /// Test and diagnosis hook, negative in production. At zero or above the mask pass records even in an
    /// unbaked scene and writes THIS value for everything it keeps, in place of the LIGHTMAP_ON split.
    /// That severs the one link the render harness cannot exercise - whether the engine drives LIGHTMAP_ON
    /// for a runtime-assigned lightmapIndex in edit mode - so the draw, the frontmost test, the sample
    /// alignment and the composite's receive arithmetic stay provable end to end without it.
    /// </summary>
    public static float LightmapMaskForcedValue = -1f;
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
    private static readonly int idRtTraceMask = Shader.PropertyToID("_BasisGIRtTraceMask");
    private const string RtAccelName = "_BasisGIRtAccel";

    private static readonly ProfilingSampler samplerRayPrepass = new ProfilingSampler("Basis GI Ray Prepass");
    private static readonly ProfilingSampler samplerRayTrace = new ProfilingSampler("Basis GI Ray Trace");
    private static readonly ProfilingSampler samplerRayResolve = new ProfilingSampler("Basis GI Ray Resolve");
    private static readonly ProfilingSampler samplerCopy = new ProfilingSampler("Basis GI Copy Color");
    private static readonly ProfilingSampler samplerLightmapMask = new ProfilingSampler("Basis GI Lightmap Mask");
    private static readonly ProfilingSampler samplerCoarse = new ProfilingSampler("Basis GI Coarse Depth");
    private static readonly ProfilingSampler samplerTrace = new ProfilingSampler("Basis GI Trace");
    private static readonly ProfilingSampler samplerTemporal = new ProfilingSampler("Basis GI Temporal");
    private static readonly ProfilingSampler samplerBlur = new ProfilingSampler("Basis GI Blur");
    private static readonly ProfilingSampler samplerComposite = new ProfilingSampler("Basis GI Composite");

    public static float GpuMs =>
        samplerCopy.gpuElapsedTime + samplerLightmapMask.gpuElapsedTime + samplerCoarse.gpuElapsedTime + samplerTrace.gpuElapsedTime +
        samplerTemporal.gpuElapsedTime + samplerBlur.gpuElapsedTime + samplerComposite.gpuElapsedTime +
        samplerRayPrepass.gpuElapsedTime + samplerRayTrace.gpuElapsedTime + samplerRayResolve.gpuElapsedTime;

    public static void SetProfilingEnabled(bool enabled)
    {
        samplerCopy.enableRecording = enabled;
        samplerLightmapMask.enableRecording = enabled;
        samplerCoarse.enableRecording = enabled;
        samplerTrace.enableRecording = enabled;
        samplerTemporal.enableRecording = enabled;
        samplerBlur.enableRecording = enabled;
        samplerComposite.enableRecording = enabled;
        samplerRayPrepass.enableRecording = enabled;
        samplerRayTrace.enableRecording = enabled;
        samplerRayResolve.enableRecording = enabled;
    }

    public static float GpuMsRayPrepass => samplerRayPrepass.gpuElapsedTime;
    public static float GpuMsRayTrace => samplerRayTrace.gpuElapsedTime;
    public static float GpuMsRayResolve => samplerRayResolve.gpuElapsedTime;
    public static float GpuMsCopyColor => samplerCopy.gpuElapsedTime;
    public static float GpuMsLightmapMask => samplerLightmapMask.gpuElapsedTime;
    public static float GpuMsCoarseDepth => samplerCoarse.gpuElapsedTime;
    public static float GpuMsTrace => samplerTrace.gpuElapsedTime;
    public static float GpuMsTemporal => samplerTemporal.gpuElapsedTime;
    public static float GpuMsBlur => samplerBlur.gpuElapsedTime;
    public static float GpuMsComposite => samplerComposite.gpuElapsedTime;

    private static int invocationFrame = -1, invocationCount;
    public static int InvocationsThisFrame => invocationCount;

    private static readonly Vector4[] emitterSpheres = new Vector4[MaxEmitters];
    private static readonly Vector4[] emitterRadiance = new Vector4[MaxEmitters];
    private static readonly List<BasisGlobalIlluminationEmitter> emitterScratch = new List<BasisGlobalIlluminationEmitter>();
    private readonly Matrix4x4[] previousViewProjection = new Matrix4x4[2];
    private readonly Vector4[] constants = new Vector4[4];
    private Vector4 coarseTexelSize;
    internal Vector4 coarseFarTexelSize;
    /// How many traced texels one far texel spans, or zero when the far level was not built. Written by
    /// every RecordDepthPyramid call, so a camera that ran without the hierarchical march leaves it zero
    /// and the march's far test compiles down to nothing it can read stale.
    internal float coarseFarBlock;

    private Material material;
    private Material rayStagesMaterial;
    private RayTracingShader rayTraceShader;
    private ComputeShader rayTraceCompute;
    private bool rayComputeFallback;
    public BasisGlobalIlluminationDebugView DebugView;
    public bool UseNormalsTexture;
    public bool UseMotionVectors;
    public bool RayTracingAvailable;
    private bool loggedRayTracingFallback;
    private bool loggedComputeBackend;

    /// <summary>
    /// Per-camera snapshot of the traced-resolution linear depth buffer built below (see
    /// RecordDepthPyramid), for any renderer feature that wants to reuse this screen-space depth reduction
    /// instead of running its own - the volumetric fog is the first consumer. Plain statics rather than
    /// making a third party read the shader global directly: URP records one camera's whole pass list
    /// before moving to the next, so by the time a later pass on THIS camera reads these they are
    /// guaranteed fresh, and SharedTracedDepthCamera lets a reader reject a value left over from a camera
    /// this pass never ran on this frame - a check a shader global has no way to make.
    /// </summary>
    public static bool SharedTracedDepthValid;
    public static TextureHandle SharedTracedDepth;
    public static Camera SharedTracedDepthCamera;

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
        // Reset immediately, before any early-return below (including ray traced mode with the temporal
        // filter off, which otherwise never touches this flag at all). A global outlives the camera and the
        // frame that set it, so every path out of this method has to leave a fresh answer for a third party
        // reading it - such as the volumetric fog, which runs later in the same camera's frame and has no
        // way to tell "no GI this frame" from "stale handle from three frames ago, ray traced mode, or a
        // camera GI never touched". Whichever pass actually builds the buffer overrides this again, through
        // the command buffer, further down - see BindTracedDepth.
        Shader.SetGlobalFloat(idTracedDepthValid, 0f);
        SharedTracedDepthValid = false;
        SharedTracedDepthCamera = null;

        if (material == null) { return; }

        UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
        UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
        BasisGlobalIlluminationSettings settings = BasisGlobalIlluminationSettings.Current;
        // DiffuseActive, not IsActive: IsActive is also true when only reflections were asked for, and
        // those are recorded by SpecularPass at a different point in the frame.
        if (!settings.DiffuseActive()) { return; }
        if (!resourceData.cameraColor.IsValid() || !resourceData.cameraDepthTexture.IsValid()) { return; }

        if (invocationFrame != Time.frameCount) { invocationFrame = Time.frameCount; invocationCount = 0; }
        invocationCount++;

        Camera camera = cameraData.camera;
        RenderTextureDescriptor descriptor = cameraData.cameraTargetDescriptor;
        int divisor = settings.ResolvedResolutionDivisor();
        int tracedWidth = Mathf.Max(1, descriptor.width / divisor);
        int tracedHeight = Mathf.Max(1, descriptor.height / divisor);

        int frame = Time.renderedFrameCount;
        int hash = BasisGlobalIlluminationHistory.ComputeHash(camera, cameraData.xr);
        BasisGlobalIlluminationHistory history = BasisGlobalIlluminationHistory.Get(hash);
        bool contiguous = history.Contiguous(frame);
        history.EnsureAllocated(descriptor, tracedWidth, tracedHeight);
        bool historyValid = settings.temporalFilter && history.Valid && contiguous;

        // Requesting Motion is not the same as getting it: a camera type URP renders no motion pass for
        // still resolves to an invalid handle, and the reprojection has to fall back to the matrix rather
        // than read an unbound texture. The keyword follows the handle, not the setting, for that reason.
        TextureHandle motion = UseMotionVectors && resourceData.motionVectorColor.IsValid() ? resourceData.motionVectorColor : TextureHandle.nullHandle;
        bool motionValid = motion.IsValid();

        ApplyKeywords(settings, motionValid);

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
        bool rayTraced = settings.IsRayTraced() && PrepareRayTracing(settings, camera, frame);

        // Both modes share the composite, so both get the lightmap receive mask - it only exists at all in
        // a scene that actually baked something. The keyword is set after the pass is recorded, from the
        // handle rather than the intent: with the keyword on and no texture bound the sample reads zero,
        // which is full suppression everywhere - exactly the fail-dangerous direction the mask's clear-to-
        // one polarity exists to rule out.
        TextureHandle lightmapMask = settings.lightmappedReceive < 1f && (SceneHasLightmaps() || LightmapMaskForcedValue >= 0f)
            ? RecordLightmapMask(renderGraph, frameData, resourceData, cameraData, descriptor, tracedWidth, tracedHeight)
            : TextureHandle.nullHandle;
        CoreUtils.SetKeyword(material, "_BASISGI_LIGHTMAP_MASK", lightmapMask.IsValid());

        // Declared out here because the temporal filter runs in both modes and wants it whenever the screen
        // space gather built it. The ray traced mode reconstructs its own positions and never asks.
        TextureHandle tracedDepth = TextureHandle.nullHandle;

        // The ray budget is resolved before anything reads it, because the denoiser is driven by how many
        // samples a pixel actually paid for: a ceiling applied further down would leave the filter
        // trusting a sample count that was never taken.
        int rayCount = settings.ResolvedRayCount();
        int bounces = settings.ResolvedBounces();
        int lightSamples = settings.ResolvedRayTracedLightSamples();
        if (rayTraced && BasisGlobalIlluminationRayTracer.Instance.Context.Backend == RayTracingBackend.Compute)
        {
            rayCount = Mathf.Min(rayCount, ComputeBackendRayCeiling);
            bounces = Mathf.Min(bounces, ComputeBackendBounceCeiling);
            lightSamples = Mathf.Min(lightSamples, ComputeBackendLightSampleCeiling);
            ReportComputeBackend(settings);
        }

        // Resolved once for the whole frame and handed to both gathers, so a ray that misses is worth
        // the same thing either side of a mode switch.
        BasisGlobalIlluminationRayTracer.SkyBinding sky = BasisGlobalIlluminationRayTracer.ResolveSky(settings.fallback, settings.fallbackIntensity);
        // A raster pass can only bind render graph resources and this is an engine texture, so the cubemap
        // goes on the global slot directly rather than through the command buffer. That is only safe
        // because of an invariant worth stating: ResolveSky picks the cubemap from RenderSettings alone, so
        // every camera in the frame resolves the SAME texture. Only the mip and the intensity vary per
        // camera - those come from the volume - and both ride _BasisGISky through the command buffer, where
        // they are sequenced against pass execution properly. An immediate global is not sequenced, so if
        // the cubemap ever becomes per camera (a per volume custom reflection, say) this has to move to the
        // command buffer or the last camera to record will decide the sky for every camera that renders.
        if (sky.Cube != null) { Shader.SetGlobalTexture(idSkyCube, sky.Cube); }

        FillConstants(settings, frame, rayTraced, rayCount);
        int emitterCount = settings.emitters ? GatherEmitters(camera, settings.ResolvedMaxEmitters()) : 0;

        if (rayTraced)
        {
            RecordRayTraced(renderGraph, resourceData, cameraData, settings, traced, tracedWidth, tracedHeight, descriptor, frame, emitterCount, rayCount, bounces, lightSamples, sky);
        }
        else
        {
            using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Basis GI Copy Color", out PassData passData, samplerCopy))
            {
                passData.stage = Stage.CopyColor;
                passData.material = material;
                passData.materialPass = PassCopyColor;
                passData.source = resourceData.cameraColor;
                Configure(passData, settings, tracedWidth, tracedHeight, descriptor, emitterCount, sky);
                builder.SetRenderAttachment(sceneColor, 0, AccessFlags.WriteAll);
                builder.UseTexture(resourceData.cameraColor);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc((PassData data, RasterGraphContext context) => Execute(data, context));
            }

            RecordDepthPyramid(renderGraph, resourceData, descriptor, tracedWidth, tracedHeight, divisor,
                settings.hierarchicalMarch, false, out tracedDepth, out TextureHandle coarse, out TextureHandle coarseFar);

            // Published for the whole frame, not just this pass: BindTracedDepth below covers GI's own
            // downstream stages, this covers everyone else's.
            SharedTracedDepthValid = true;
            SharedTracedDepth = tracedDepth;
            SharedTracedDepthCamera = camera;

            using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Basis GI Trace", out PassData passData, samplerTrace))
            {
                passData.stage = Stage.Trace;
                passData.material = material;
                passData.materialPass = PassTrace;
                passData.source = sceneColor;
                passData.sceneColor = sceneColor;
                passData.normals = normals;
                passData.coarse = coarse;
                passData.coarseValid = coarse.IsValid();
                passData.coarseTexelSize = coarseTexelSize;
                passData.coarseParams = new Vector4(0f, 0f, 0f, CoarseBlock);
                passData.coarseFar = coarseFar;
                passData.coarseFarTexelSize = coarseFarTexelSize;
                passData.coarseFarBlock = coarseFar.IsValid() ? coarseFarBlock : 0f;
                passData.tracedDepth = tracedDepth;
                passData.tracedDepthValid = true;
                builder.SetRenderAttachment(traced, 0, AccessFlags.WriteAll);
                builder.UseTexture(sceneColor);
                builder.UseTexture(resourceData.cameraDepthTexture);
                builder.UseTexture(tracedDepth);
                if (coarse.IsValid()) { builder.UseTexture(coarse); }
                if (coarseFar.IsValid()) { builder.UseTexture(coarseFar); }
                if (normals.IsValid()) { builder.UseTexture(normals); }
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc((PassData data, RasterGraphContext context) => Execute(data, context));
            }
        }

        TextureHandle denoiseSource = traced;
        if (settings.temporalFilter)
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
                passData.motion = motion;
                passData.previousViewProjection = previousViewProjection;
                passData.tracedDepth = tracedDepth;
                passData.tracedDepthValid = tracedDepth.IsValid();
                // Pooled PassData: the reflection temporal sets these, so this pass has to unset them or
                // inherit a dead graph's handle and run the diffuse accumulation as a reflection.
                passData.specularHitDistance = TextureHandle.nullHandle;
                passData.specularHitDistanceValid = false;
                previousViewProjection[0] = history.PreviousViewProjection[0];
                previousViewProjection[1] = history.PreviousViewProjection[1];
                builder.SetRenderAttachment(historyWrite, 0, AccessFlags.WriteAll);
                builder.SetRenderAttachment(historyWriteStats, 1, AccessFlags.WriteAll);
                builder.UseTexture(traced);
                builder.UseTexture(historyRead);
                builder.UseTexture(historyReadStats);
                builder.UseTexture(resourceData.cameraDepthTexture);
                if (tracedDepth.IsValid()) { builder.UseTexture(tracedDepth); }
                if (motionValid) { builder.UseTexture(motion); }
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc((PassData data, RasterGraphContext context) => Execute(data, context));
            }
            denoiseSource = historyWrite;
        }

        // The filter reads the statistics the temporal pass just wrote, so it knows how many frames each
        // pixel has behind it and how far its own luminance has been swinging. Where the temporal filter is
        // switched off there are none, and the filter falls back to treating every pixel as unresolved.
        bool statsValid = settings.temporalFilter;
        int taps = Mathf.Clamp(Mathf.RoundToInt(settings.smoothing * 2f), 0, 4);
        if (taps > 0)
        {
            int levels = settings.wideBlur ? BlurLevels : BlurLevels - 1;
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
            // The upsample takes each traced parent's depth out of the statistics that parent wrote, rather
            // than out of the full resolution depth texture. Anything else that calls BasisGIUpsample has to
            // bind these two as well - the shader falls back to the depth texture when the valid flag is
            // clear, but not when it is left set by whichever pass ran before it.
            passData.stats = historyWriteStats;
            passData.statsValid = statsValid;
            passData.lightmapMask = lightmapMask;
            passData.lightmapMaskValid = lightmapMask.IsValid();
            passData.lightmapParams = new Vector4(settings.lightmappedReceive, 0f, 0f, 0f);
            builder.SetRenderAttachment(resourceData.cameraColor, 0, AccessFlags.ReadWrite);
            builder.UseTexture(denoiseSource);
            builder.UseTexture(historyWriteStats);
            builder.UseTexture(resourceData.cameraDepthTexture);
            if (lightmapMask.IsValid()) { builder.UseTexture(lightmapMask); }
            if (normals.IsValid()) { builder.UseTexture(normals); }
            builder.AllowGlobalStateModification(true);
            builder.SetRenderFunc((PassData data, RasterGraphContext context) => Execute(data, context));
        }

        StoreViewProjection(cameraData, history.PreviousViewProjection);
        history.Write = history.Read;
        history.Valid = settings.temporalFilter;
        history.RecordFrame(frame);
        BasisGlobalIlluminationHistory.PruneStale(frame, HistoryMaxAge);
    }

    private void Configure(PassData passData, BasisGlobalIlluminationSettings settings, int tracedWidth, int tracedHeight, in RenderTextureDescriptor descriptor,
        int emitterCount, in BasisGlobalIlluminationRayTracer.SkyBinding sky)
    {
        passData.constants = constants;
        passData.sky = new Vector4(sky.Mip, sky.IsValid ? sky.Intensity : 0f, 0f, 0f);
        passData.skyDecode = sky.Decode;
        passData.tint = settings.tint.linear;
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
    private bool PrepareRayTracing(BasisGlobalIlluminationSettings settings, Camera camera, int frame)
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
        return tracer.Refresh(settings.ResolvedSceneSettings(), settings.ResolvedLightSettings(), camera, frame, Time.unscaledTime);
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
        BasisGlobalIlluminationSettings settings, TextureHandle traced, int tracedWidth, int tracedHeight,
        in RenderTextureDescriptor descriptor, int frame, int emitterCount, int rayCount, int bounces, int lightSamples,
        BasisGlobalIlluminationRayTracer.SkyBinding sky)
    {
        BasisGlobalIlluminationRayTracer tracer = BasisGlobalIlluminationRayTracer.Instance;

        int viewCount = ViewCountOf(cameraData);
        int scale = Mathf.Clamp(settings.ResolvedResolutionDivisor(), 1, 4);
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
            Configure(passData, settings, tracedWidth, tracedHeight, descriptor, emitterCount, sky);
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
            data.trace = new Vector4(settings.maxRayLength, settings.obscuranceRadius, settings.obscuranceIntensity, settings.fadeDistance);
            data.bias = new Vector4(settings.rayTracedNormalBias, settings.rayDistanceBias, settings.emitterIntensity, settings.rayTracedLightIntensity);
            data.options = new Vector4(settings.fireflyClamp, settings.rayBounceThreshold, settings.rayTracedShadows ? 1f : 0f, 0f);
            data.sky = new Vector4(sky.Mip, sky.IsValid ? sky.Intensity : 0f, 0f, 0f);
            data.skyDecode = sky.Decode;
            data.specularParams = Vector4.zero;
            data.specular = result;
            data.diffuseEnabled = true;
            data.specularEnabled = false;
            data.rayCount = rayCount;
            data.bounces = bounces;
            data.lightCount = tracer.Lights.Count;
            data.lightSamples = lightSamples;
            data.viewCount = viewCount;
            data.frameIndex = frame % 64;
            data.traceMask = settings.TraceCategories;
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

    private void ReportComputeBackend(BasisGlobalIlluminationSettings settings)
    {
        if (loggedComputeBackend) { return; }
        loggedComputeBackend = true;
        bool clamped = settings.ResolvedRayCount() > ComputeBackendRayCeiling || settings.ResolvedBounces() > ComputeBackendBounceCeiling;
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
        // Both outputs are declared in the kernel whether or not this dispatch writes them, and an unbound
        // RWTexture is a device removal on some backends rather than a silently ignored write - so whichever
        // half is off still gets the other half's target bound to it. The enables are what stop the write.
        shader.SetTextureParam(cmd, idRtResultTex, data.diffuseEnabled ? data.result : data.specular);
        shader.SetTextureParam(cmd, idRtSpecularTex, data.specularEnabled ? data.specular : data.result);
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
        shader.SetVectorParam(cmd, idRtSpecular, data.specularParams);
        shader.SetIntParam(cmd, idRtDiffuseEnabled, data.diffuseEnabled ? 1 : 0);
        shader.SetIntParam(cmd, idRtSpecularEnabled, data.specularEnabled ? 1 : 0);
        shader.SetIntParam(cmd, idRtRayCount, data.rayCount);
        shader.SetIntParam(cmd, idRtBounces, data.bounces);
        shader.SetIntParam(cmd, idRtLightCount, data.lightCount);
        shader.SetIntParam(cmd, idRtLightSamples, data.lightSamples);
        shader.SetIntParam(cmd, idRtViewCount, data.viewCount);
        shader.SetIntParam(cmd, idRtFrameIndex, data.frameIndex);
        shader.SetIntParam(cmd, idRtTraceMask, data.traceMask);

        GraphicsBuffer scratch = tracer.Context.GetTraceScratch(data.width, data.height, data.viewCount);
        shader.Dispatch(cmd, scratch, (uint)data.width, (uint)data.height, (uint)data.viewCount);
    }

    // LightmapSettings.lightmaps copies the array on every read, so the answer is kept for the frame in
    // play mode. The editor re-reads every call: a render harness assigns lightmaps between renders inside
    // one engine frame, and a frame-stamped cache there would hold the stale answer for the whole test.
    private static int lightmapCheckFrame = -1;
    private static bool lightmapCheckResult;

    private static bool SceneHasLightmaps()
    {
        int frame = Time.frameCount;
        if (!Application.isPlaying || lightmapCheckFrame != frame)
        {
            lightmapCheckFrame = frame;
            LightmapData[] lightmaps = LightmapSettings.lightmaps;
            lightmapCheckResult = lightmaps != null && lightmaps.Length > 0;
        }
        return lightmapCheckResult;
    }

    // Lazy rather than a field initializer: ShaderTagId calls Shader.TagToID, which Unity forbids while a
    // ScriptableObject deserializes, and the renderer feature constructs this pass - see the identical
    // trap documented on the old SSGI integration. Shader.PropertyToID in static fields is fine.
    private static ShaderTagId[] lightmapMaskTags;

    private static void EnsureLightmapMaskTags()
    {
        if (lightmapMaskTags != null) { return; }
        // Every tag URP itself draws opaques with, plus the GBuffer tag, so a renderer qualifies if its
        // OWN shader would have drawn at all - the override material is what actually runs. A shader
        // matching none of these does not draw and its pixels stay at the cleared one, which is the old
        // behaviour rather than a suppression.
        lightmapMaskTags = new[]
        {
            new ShaderTagId("UniversalForward"),
            new ShaderTagId("UniversalForwardOnly"),
            new ShaderTagId("SRPDefaultUnlit"),
            new ShaderTagId("UniversalGBuffer")
        };
    }

    /// <summary>
    /// One at every traced texel whose frontmost surface is dynamic, zero where it is lightmapped. The
    /// composite multiplies bounce and obscurance onto the camera image, and a lightmapped surface already
    /// carries both in its lightmap - re-applying them is double counting, which is why a carefully baked
    /// world reads blown out and crushed the moment the effect is switched on.
    ///
    /// The opaque renderer list is redrawn at traced resolution with the mask pass as the override
    /// material: LIGHTMAP_ON decides the value written, the camera's own culling decides the set, and a
    /// hand depth test against the camera depth keeps everything but the frontmost surface quiet. The
    /// target CLEARS TO ONE, and that polarity is the load-bearing decision - the first version of this
    /// mask cleared to zero-means-lightmapped, and every way the pass could fail to draw (a
    /// BatchRendererGroup refusing a variant, an empty list, a culled pass) collapsed to "global
    /// illumination is gone". This way round every failure leaves the image exactly as it was.
    /// </summary>
    private TextureHandle RecordLightmapMask(RenderGraph renderGraph, ContextContainer frameData, UniversalResourceData resourceData,
        UniversalCameraData cameraData, in RenderTextureDescriptor descriptor, int tracedWidth, int tracedHeight)
    {
        UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();

        TextureDesc maskDescriptor = new TextureDesc(tracedWidth, tracedHeight)
        {
            format = GraphicsFormat.R8_UNorm,
            dimension = descriptor.dimension,
            slices = Mathf.Max(1, descriptor.volumeDepth),
            msaaSamples = MSAASamples.None,
            clearBuffer = true,
            clearColor = Color.white,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            name = "_BasisGILightmapMask"
        };
        TextureHandle mask = renderGraph.CreateTexture(maskDescriptor);

        EnsureLightmapMaskTags();
        SortingSettings sorting = new SortingSettings(cameraData.camera) { criteria = SortingCriteria.CommonOpaque };
        DrawingSettings drawing = new DrawingSettings(lightmapMaskTags[0], sorting)
        {
            overrideMaterial = material,
            overrideMaterialPassIndex = PassLightmapMask,
            // Lightmap binding is what drives LIGHTMAP_ON per draw, and that keyword is the whole payload.
            perObjectData = PerObjectData.Lightmaps,
            enableInstancing = true
        };
        for (int index = 1; index < lightmapMaskTags.Length; index++) { drawing.SetShaderPassName(index, lightmapMaskTags[index]); }
        FilteringSettings filtering = new FilteringSettings(RenderQueueRange.opaque);
        RendererListParams listParams = new RendererListParams(renderingData.cullResults, drawing, filtering);
        RendererListHandle rendererList = renderGraph.CreateRendererList(listParams);

        using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Basis GI Lightmap Mask", out PassData passData, samplerLightmapMask))
        {
            passData.stage = Stage.LightmapMask;
            passData.material = material;
            passData.lightmapRenderers = rendererList;
            passData.tracedTexelSize = new Vector4(1f / tracedWidth, 1f / tracedHeight, tracedWidth, tracedHeight);
            // Write, not WriteAll: the cleared one IS the mask for every pixel nothing draws to, so the
            // clear must survive into the pass.
            builder.SetRenderAttachment(mask, 0, AccessFlags.Write);
            builder.UseRendererList(rendererList);
            builder.UseTexture(resourceData.cameraDepthTexture);
            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);
            builder.SetRenderFunc((PassData data, RasterGraphContext context) => Execute(data, context));
        }

        return mask;
    }

    /// <summary>
    /// Builds the three levels of linear depth the gather walks: one texel per TRACED texel, one texel per
    /// <see cref="CoarseBlock"/> of those, and one more per <see cref="CoarseBlock"/> of THOSE. All three
    /// carry the same pair - the closest real surface beneath the texel and the furthest - so each level is
    /// a straight fold of the one above it.
    ///
    /// The fine level is the expensive thing this exists for. The march runs at traced resolution but used
    /// to read the FULL resolution depth texture on every step, every binary refine step and every emitter
    /// shadow step, linearising each tap by hand. At Half that is four times the texels it needs, walked at
    /// a stride of about a traced texel, so most of every cache line fetched was for texels no step would
    /// ever ask about. Reducing once, up front, into something the march can read texel for texel is the
    /// whole optimisation; the coarse summary then folds out of it for free.
    ///
    /// One pass per level, and the reason each reads a DIFFERENT texture is worth stating, because the
    /// obvious implementations of this are both wrong. Folding full resolution straight down to a block of
    /// sixty four would put hundreds of taps in a single fragment and leave the machine idle while a handful
    /// of threads did all the work. Folding through the mip chain of ONE texture would have a pass sampling
    /// the level below the level it is writing - render graph rejects that outright as a resource used for
    /// input and output at once, and it is a real read-write hazard even where a validator lets it past.
    /// Plain separate textures have neither problem and cost a few hundred kilobytes. (This is the same
    /// conclusion URP's own MipGenerator reaches in 17.6, from the other direction: it cannot bind an SRV
    /// of a mip level either, so it packs every level into one texture and folds it in compute instead.)
    ///
    /// The fine level is built whether or not the hierarchical march is on, because the plain march reads
    /// it too. Both folds are the hierarchical march's own.
    /// </summary>
    private void RecordDepthPyramid(RenderGraph renderGraph, UniversalResourceData resourceData,
        in RenderTextureDescriptor descriptor, int tracedWidth, int tracedHeight, int divisor, bool hierarchical,
        bool conservative, out TextureHandle tracedDepth, out TextureHandle coarse, out TextureHandle coarseFar)
    {
        int coarseWidth = Mathf.Max(1, (tracedWidth + CoarseBlock - 1) / CoarseBlock);
        int coarseHeight = Mathf.Max(1, (tracedHeight + CoarseBlock - 1) / CoarseBlock);
        int farWidth = Mathf.Max(1, (coarseWidth + CoarseBlock - 1) / CoarseBlock);
        int farHeight = Mathf.Max(1, (coarseHeight + CoarseBlock - 1) / CoarseBlock);

        RenderTextureDescriptor coarseDescriptor = descriptor;
        coarseDescriptor.msaaSamples = 1;
        coarseDescriptor.depthStencilFormat = GraphicsFormat.None;
        coarseDescriptor.depthBufferBits = 0;
        coarseDescriptor.useMipMap = false;
        coarseDescriptor.autoGenerateMips = false;
        // Two channels of half. Eye depth resolves to about a twentieth of a percent there, and what this
        // feeds is a conservative "could anything in this block be hit" test with the thickness setting
        // already sitting between it and a wrong answer.
        coarseDescriptor.graphicsFormat = GraphicsFormat.R16G16_SFloat;

        coarseDescriptor.width = tracedWidth;
        coarseDescriptor.height = tracedHeight;
        tracedDepth = UniversalRenderer.CreateRenderGraphTexture(renderGraph, coarseDescriptor, "_BasisGITracedDepth", false, FilterMode.Point, TextureWrapMode.Clamp);

        using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Basis GI Traced Depth", out PassData passData, samplerCoarse))
        {
            passData.stage = Stage.Coarse;
            passData.material = material;
            passData.materialPass = PassCoarseSeed;
            passData.source = resourceData.cameraDepthTexture;
            // One traced texel folds exactly the block of full resolution texels it stands for, so at Full
            // resolution the span is one and the pair this writes is the depth buffer's own value twice -
            // which is what makes every test downstream reduce to the arithmetic it replaced.
            passData.coarseParams = new Vector4(divisor, descriptor.width, descriptor.height, CoarseBlock);
            passData.coarseValid = false;
            passData.coarseConservative = conservative;
            builder.SetRenderAttachment(tracedDepth, 0, AccessFlags.WriteAll);
            builder.UseTexture(resourceData.cameraDepthTexture);
            builder.AllowGlobalStateModification(true);
            builder.SetRenderFunc((PassData data, RasterGraphContext context) => Execute(data, context));
        }

        coarse = TextureHandle.nullHandle;
        coarseFar = TextureHandle.nullHandle;
        coarseFarBlock = 0f;
        coarseFarTexelSize = Vector4.zero;
        if (!hierarchical) { return; }

        coarseDescriptor.width = coarseWidth;
        coarseDescriptor.height = coarseHeight;
        coarse = UniversalRenderer.CreateRenderGraphTexture(renderGraph, coarseDescriptor, "_BasisGICoarseDepth", false, FilterMode.Point, TextureWrapMode.Clamp);

        coarseTexelSize = new Vector4(1f / coarseWidth, 1f / coarseHeight, coarseWidth, coarseHeight);

        using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Basis GI Coarse Reduce", out PassData passData, samplerCoarse))
        {
            passData.stage = Stage.Coarse;
            passData.material = material;
            passData.materialPass = PassCoarseReduce;
            passData.source = tracedDepth;
            passData.coarse = tracedDepth;
            passData.coarseValid = true;
            // A minimum of minima is still the minimum underneath, and a maximum of maxima is still the
            // maximum, so folding the fine level rather than the depth buffer leaves the coarse summary
            // holding exactly what it held when it was built from full resolution directly.
            passData.coarseParams = new Vector4(CoarseBlock, tracedWidth, tracedHeight, CoarseBlock);
            // The reduce does not read the flag, but the global it binds must not carry a dead value.
            passData.coarseConservative = conservative;
            builder.SetRenderAttachment(coarse, 0, AccessFlags.WriteAll);
            builder.UseTexture(tracedDepth);
            builder.AllowGlobalStateModification(true);
            builder.SetRenderFunc((PassData data, RasterGraphContext context) => Execute(data, context));
        }

        // The same fold once more. This level is what stops the cell ceiling being a ceiling on how far a
        // ray can see: a mirror ray crossing empty room clears a whole block of coarse cells per tap here
        // instead of walking them one at a time, which is why BASISGI_SSR_MAX_CELLS had to be 128 to reach
        // across the screen at all. It costs one more pass over a texture sixty four times smaller than the
        // coarse level - the fold reads the same sixty four taps per fragment the pass above does, off a
        // texture small enough to sit in cache.
        coarseDescriptor.width = farWidth;
        coarseDescriptor.height = farHeight;
        coarseFar = UniversalRenderer.CreateRenderGraphTexture(renderGraph, coarseDescriptor, "_BasisGICoarseFarDepth", false, FilterMode.Point, TextureWrapMode.Clamp);

        coarseFarTexelSize = new Vector4(1f / farWidth, 1f / farHeight, farWidth, farHeight);
        // In TRACED texels, which is the unit the march walks in - not in coarse texels.
        coarseFarBlock = CoarseBlock * CoarseBlock;

        using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Basis GI Coarse Reduce Far", out PassData passData, samplerCoarse))
        {
            passData.stage = Stage.Coarse;
            passData.material = material;
            passData.materialPass = PassCoarseReduce;
            passData.source = coarse;
            // The reduce pass reads whatever is bound as the coarse texture, so the level being folded is
            // named here rather than the level being written.
            passData.coarse = coarse;
            passData.coarseValid = true;
            passData.coarseParams = new Vector4(CoarseBlock, coarseWidth, coarseHeight, CoarseBlock);
            passData.coarseConservative = conservative;
            builder.SetRenderAttachment(coarseFar, 0, AccessFlags.WriteAll);
            builder.UseTexture(coarse);
            builder.AllowGlobalStateModification(true);
            builder.SetRenderFunc((PassData data, RasterGraphContext context) => Execute(data, context));
        }
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

    private void ApplyKeywords(BasisGlobalIlluminationSettings settings, bool motionValid)
    {
        CoreUtils.SetKeyword(material, "_BASISGI_MOTION_VECTORS", motionValid);
        bool normalsTexture = UseNormalsTexture && settings.normalSource == BasisGlobalIlluminationNormalSource.NormalsTexture;
        CoreUtils.SetKeyword(material, "_BASISGI_NORMALS_TEXTURE", normalsTexture);
        CoreUtils.SetKeyword(material, "_BASISGI_FALLBACK_SKY", settings.fallback == BasisGlobalIlluminationFallback.Sky);
        CoreUtils.SetKeyword(material, "_BASISGI_FALLBACK_PROBE", settings.fallback == BasisGlobalIlluminationFallback.ReflectionProbe);
        CoreUtils.SetKeyword(material, "_BASISGI_EMITTERS", settings.emitters);
        CoreUtils.SetKeyword(material, "_BASISGI_EMITTER_OCCLUSION", settings.emitters && settings.emitterOcclusion);
        CoreUtils.SetKeyword(material, "_BASISGI_RAY_REUSE", settings.rayReuse);
        CoreUtils.SetKeyword(material, "_BASISGI_HIT_NORMAL", settings.quality >= BasisGlobalIlluminationQuality.High);
        CoreUtils.SetKeyword(material, "_BASISGI_HIERARCHICAL_MARCH", settings.hierarchicalMarch);
        CoreUtils.SetKeyword(material, "_BASISGI_NEIGHBOURHOOD_CLAMP", settings.neighbourhoodClamp);
        CoreUtils.SetKeyword(material, "_BASISGI_BILATERAL_UPSAMPLE", settings.bilateralUpsample && settings.ResolvedResolutionDivisor() > 1);
    }

    private void FillConstants(BasisGlobalIlluminationSettings settings, int frame, bool rayTraced, int rayCount)
    {
        constants[0] = new Vector4(settings.intensity, settings.saturation, settings.obscuranceIntensity, settings.obscuranceRadius);
        constants[1] = new Vector4(settings.maxRayLength, settings.thickness, settings.jitter, settings.fadeDistance);
        // The fallback's intensity rides with the sky binding rather than here, because that is where the
        // cubemap it applies to comes from.
        constants[2] = new Vector4(rayCount, settings.ResolvedRaySteps(), settings.fireflyClamp, 0f);
        float temporalResponse = rayTraced ? settings.temporalResponse * RayTemporalResponseScale : settings.temporalResponse;
        constants[3] = new Vector4(frame % 64, temporalResponse, settings.depthRejection, settings.emitterIntensity);
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
            case Stage.LightmapMask:
                // Geometry, not a fullscreen blit. The texel size is bound here rather than inherited so
                // this pass does not depend on which stage happened to run before it.
                cmd.SetGlobalVector(idTracedTexelSize, data.tracedTexelSize);
                cmd.SetGlobalFloat(idLightmapMaskForce, LightmapMaskForcedValue);
                cmd.DrawRendererList(data.lightmapRenderers);
                return;
            case Stage.RayPrepass:
                SetSharedConstants(cmd, data);
                cmd.SetGlobalVector(idRtReference, data.rayReference);
                cmd.SetGlobalVector(idRtFullSize, data.rayFullSize);
                cmd.SetGlobalInteger(idRtScale, data.rayScale);
                break;
            case Stage.RayResolve:
                cmd.SetGlobalTexture(idRtResolveSource, data.rayResult);
                break;
            case Stage.Coarse:
                cmd.SetGlobalVector(idCoarseParams, data.coarseParams);
                cmd.SetGlobalFloat(idSeedConservative, data.coarseConservative ? 1f : 0f);
                if (data.coarseValid) { cmd.SetGlobalTexture(idCoarseDepth, data.coarse); }
                break;
            case Stage.Trace:
                cmd.SetGlobalTexture(idSceneColor, data.sceneColor);
                BindTracedDepth(cmd, data);
                if (data.coarseValid)
                {
                    cmd.SetGlobalTexture(idCoarseDepth, data.coarse);
                    cmd.SetGlobalVector(idCoarseTexelSize, data.coarseTexelSize);
                    cmd.SetGlobalVector(idCoarseParams, data.coarseParams);
                }
                BindCoarseFar(cmd, data);
                if (data.normals.IsValid()) { cmd.SetGlobalTexture(idNormals, data.normals); }
                break;
            case Stage.Temporal:
                BindTracedDepth(cmd, data);
                cmd.SetGlobalTexture(idIndirect, data.indirect);
                cmd.SetGlobalTexture(idHistory, data.history);
                cmd.SetGlobalTexture(idHistoryStats, data.historyStats);
                if (data.motion.IsValid()) { cmd.SetGlobalTexture(idMotion, data.motion); }
                cmd.SetGlobalFloat(idHistoryValid, data.historyValid ? 1f : 0f);
                cmd.SetGlobalMatrixArray(idPrevViewProjection, data.previousViewProjection);
                // Written by every temporal, exactly like the traced depth flag: it is a global, and the
                // diffuse temporal runs after the specular one left it standing at one.
                cmd.SetGlobalFloat(idSpecHitDistanceValid, data.specularHitDistanceValid ? 1f : 0f);
                if (data.specularHitDistanceValid) { cmd.SetGlobalTexture(idSpecHitDistance, data.specularHitDistance); }
                break;
            case Stage.Blur:
                cmd.SetGlobalTexture(idIndirect, data.indirect);
                cmd.SetGlobalTexture(idStats, data.stats);
                cmd.SetGlobalFloat(idStatsValid, data.statsValid ? 1f : 0f);
                cmd.SetGlobalVector(idBlurAxis, data.blurAxis);
                break;
            case Stage.Composite:
                cmd.SetGlobalTexture(idIndirect, data.indirect);
                cmd.SetGlobalTexture(idStats, data.stats);
                cmd.SetGlobalFloat(idStatsValid, data.statsValid ? 1f : 0f);
                if (data.lightmapMaskValid)
                {
                    cmd.SetGlobalTexture(idLightmapMask, data.lightmapMask);
                    cmd.SetGlobalVector(idLightmapParams, data.lightmapParams);
                }
                if (data.normals.IsValid()) { cmd.SetGlobalTexture(idNormals, data.normals); }
                break;
        }

        Blitter.BlitTexture(cmd, data.source, new Vector4(1f, 1f, 0f, 0f), data.material, data.materialPass);
    }

    /// <summary>
    /// Binds the traced depth buffer, and says so even when there is none.
    ///
    /// These are globals, and a global outlives the camera that set it. A screen space camera leaves the
    /// flag standing at one, and a ray traced camera rendering afterwards would read it, believe a buffer
    /// it never built was bound, and sample whatever texture was left in the slot. So every pass that reads
    /// the flag writes it first, rather than only the passes that have something to put there.
    /// </summary>
    private static void BindTracedDepth(RasterCommandBuffer cmd, PassData data)
    {
        cmd.SetGlobalFloat(idTracedDepthValid, data.tracedDepthValid ? 1f : 0f);
        if (data.tracedDepthValid) { cmd.SetGlobalTexture(idTracedDepth, data.tracedDepth); }
    }

    /// <summary>
    /// Binds the far level of the coarse summary, and says so even when there is none - for exactly the
    /// reason BindTracedDepth does. The block size doubles as the flag, so a camera that ran without the
    /// hierarchical march writes zero here and the march skips the far test rather than reading whatever
    /// texture and dimensions the previous camera left in the slots.
    /// </summary>
    private static void BindCoarseFar(RasterCommandBuffer cmd, PassData data)
    {
        cmd.SetGlobalFloat(idCoarseFarBlock, data.coarseFarBlock);
        if (data.coarseFarBlock < 1f) { return; }
        cmd.SetGlobalTexture(idCoarseFarDepth, data.coarseFar);
        cmd.SetGlobalVector(idCoarseFarTexelSize, data.coarseFarTexelSize);
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
