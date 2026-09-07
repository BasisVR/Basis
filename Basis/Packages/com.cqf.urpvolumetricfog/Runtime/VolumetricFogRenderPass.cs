using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

/// <summary>
/// The volumetric fog render pass.
/// </summary>
public sealed class VolumetricFogRenderPass : ScriptableRenderPass
{
    #region Definitions

    /// <summary>
    /// Downsampling factor for the camera depth texture that the volumetric fog will use to render
    /// the fog.
    /// </summary>
    private enum DownsampleFactor : byte
    {
        Half = 2,
    }

    /// <summary>
    /// The subpasses the volumetric fog render pass is made of.
    /// </summary>
    private enum PassStage : byte
    {
        DownsampleDepth,
        VolumetricFogRender,
        VolumetricFogBlur,
        VolumetricFogUpsampleComposition
    }

    /// <summary>
    /// Holds the data needed by the execution of the volumetric fog render pass subpasses.
    /// </summary>
    private class PassData
    {
        public PassStage stage;

        public TextureHandle source;
        public TextureHandle target;

        public Material material;
        public int materialPassIndex;
        public int materialAdditionalPassIndex;

        public TextureHandle downsampledCameraDepthTarget;
        public UniversalLightData lightData;
        public Texture2D blueNoiseTexture;
        public VolumetricFogVolumeComponent fogVolume;
        public int blurIterations;
    }

    /// <summary>
    /// What an external depth source hands back for a given camera: whether it has anything usable this
    /// frame, and a linear (closest, furthest) eye-depth pair in rg. This pass does not know or care where
    /// the texture came from - it only needs enough to fold it into the same checkerboard-packed raw depth
    /// DownsampleDepth already produces, at whatever resolution the source happens to be.
    /// </summary>
    public struct ExternalDepthResult
    {
        public bool valid;
        public TextureHandle depth;
    }

    #endregion

    #region Public Attributes

    public const RenderPassEvent DefaultRenderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
    public const VolumetricFogRenderPassEvent DefaultVolumetricFogRenderPassEvent = (VolumetricFogRenderPassEvent)DefaultRenderPassEvent;

    /// <summary>
    /// Optional hook: when set and it returns a valid result for the camera being recorded, the downsample
    /// pass reduces THAT texture instead of the full resolution camera depth. Left null by default so this
    /// package still builds and runs standalone with no other package present; a host project wires this to
    /// whatever screen-space depth reduction it already has lying around for the same camera this frame.
    /// </summary>
    public static Func<Camera, ExternalDepthResult> ExternalDepthProvider;

    #endregion

    #region Private Attributes

    private const string DownsampledCameraDepthRTName = "_DownsampledCameraDepth";
    private const string VolumetricFogRenderRTName = "_VolumetricFog";
    private const string VolumetricFogBlurRTName = "_VolumetricFogBlur";

    private static readonly int DownsampledCameraDepthTextureId = Shader.PropertyToID("_DownsampledCameraDepthTexture");

    private static readonly int DistanceId = Shader.PropertyToID("_Distance");
    private static readonly int BaseHeightId = Shader.PropertyToID("_BaseHeight");
    private static readonly int MaximumHeightId = Shader.PropertyToID("_MaximumHeight");
    private static readonly int GroundHeightId = Shader.PropertyToID("_GroundHeight");
    private static readonly int DensityId = Shader.PropertyToID("_Density");
    private static readonly int AbsortionId = Shader.PropertyToID("_Absortion");
    private static readonly int APVContributionWeigthId = Shader.PropertyToID("_APVContributionWeight");
    private static readonly int BakedAPVFogVolumeId = Shader.PropertyToID("_BakedAPVFogVolume");
    private static readonly int BakedAPVVolumeBoundsMinId = Shader.PropertyToID("_BakedAPVVolumeBoundsMin");
    private static readonly int BakedAPVVolumeInvSizeId = Shader.PropertyToID("_BakedAPVVolumeInvSize");
    private static readonly int TintId = Shader.PropertyToID("_Tint");
    private static readonly int MaxStepsId = Shader.PropertyToID("_MaxSteps");

    private static readonly int MainLightAnisotropyId = Shader.PropertyToID("_MainLightAnisotropy");
    private static readonly int MainLightScatteringId = Shader.PropertyToID("_MainLightScattering");

    private static readonly int LTCGIScatteringId = Shader.PropertyToID("_LTCGIScattering");

    private static readonly int BlueNoiseTextureId = Shader.PropertyToID("_BlueNoiseTexture");
    private static readonly int BlueNoiseParamsId = Shader.PropertyToID("_BlueNoiseParams");

    private int downsampleDepthPassIndex;
    private int downsampleDepthFromExternalPassIndex;
    private int volumetricFogRenderPassIndex;
    private int volumetricFogHorizontalBlurPassIndex;
    private int volumetricFogVerticalBlurPassIndex;
    private int volumetricFogUpsampleCompositionPassIndex;

    private Material downsampleDepthMaterial;
    private Material volumetricFogMaterial;

    // Per-camera volume and optional blue-noise texture, assigned by the renderer feature.
    public VolumetricFogVolumeComponent fogVolume;
    public Texture2D blueNoiseTexture;

    private ProfilingSampler downsampleDepthProfilingSampler;

    #endregion

    #region Initialization Methods

    /// <summary>
    /// Constructor.
    /// </summary>
    /// <param name="downsampleDepthMaterial"></param>
    /// <param name="volumetricFogMaterial"></param>
    /// <param name="passEvent"></param>
    public VolumetricFogRenderPass(Material downsampleDepthMaterial, Material volumetricFogMaterial, RenderPassEvent passEvent) : base()
    {
        profilingSampler = new ProfilingSampler("Volumetric Fog");
        downsampleDepthProfilingSampler = new ProfilingSampler("Downsample Depth");
        renderPassEvent = passEvent;
        requiresIntermediateTexture = false;

        this.downsampleDepthMaterial = downsampleDepthMaterial;
        this.volumetricFogMaterial = volumetricFogMaterial;

        InitializePassesIndices();
    }

    /// <summary>
    /// Initializes the passes indices.
    /// </summary>
    private void InitializePassesIndices()
    {
        downsampleDepthPassIndex = downsampleDepthMaterial.FindPass("DownsampleDepth");
        // -1 (not found) on an older shader that predates this pass - the external hook then simply never
        // engages, same as ExternalDepthProvider being null.
        downsampleDepthFromExternalPassIndex = downsampleDepthMaterial.FindPass("DownsampleDepthFromExternal");
        volumetricFogRenderPassIndex = volumetricFogMaterial.FindPass("VolumetricFogRender");
        volumetricFogHorizontalBlurPassIndex = volumetricFogMaterial.FindPass("VolumetricFogHorizontalBlur");
        volumetricFogVerticalBlurPassIndex = volumetricFogMaterial.FindPass("VolumetricFogVerticalBlur");
        volumetricFogUpsampleCompositionPassIndex = volumetricFogMaterial.FindPass("VolumetricFogUpsampleComposition");
    }

    #endregion

    #region Scriptable Render Pass Methods

    /// <summary>
    /// <inheritdoc/>
    /// </summary>
    /// <param name="renderGraph"></param>
    /// <param name="frameData"></param>
    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
        UniversalLightData lightData = frameData.Get<UniversalLightData>();
        UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();

        VolumetricFogVolumeComponent activeFog = fogVolume;
        if (activeFog == null) return;

        int blurIterations = activeFog.blurIterations.value;

        CreateRenderGraphTextures(renderGraph, cameraData, activeFog, blurIterations > 0, out TextureHandle downsampledCameraDepthTarget, out TextureHandle volumetricFogRenderTarget, out TextureHandle volumetricFogBlurRenderTarget);

        // An external producer already reduced this camera's depth for its own screen-space effect this
        // frame (GI's traced-resolution buffer, when Basis wires it up - see the bridge in Basis Framework).
        // Reusing it here means this pass reads a texture a fraction of camera-depth's size instead of
        // gathering the full resolution buffer; everything downstream (the raymarch, the bilateral upsample)
        // reads the result exactly as before either way, so nothing else in this file changes.
        ExternalDepthResult external = ExternalDepthProvider != null ? ExternalDepthProvider(cameraData.camera) : default;
        bool useExternalDepth = external.valid && external.depth.IsValid() && downsampleDepthFromExternalPassIndex >= 0;

        using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass(
            useExternalDepth ? "Downsample Depth Pass (Shared)" : "Downsample Depth Pass", out PassData passData, downsampleDepthProfilingSampler))
        {
            passData.stage = PassStage.DownsampleDepth;
            passData.target = downsampledCameraDepthTarget;
            passData.material = downsampleDepthMaterial;

            if (useExternalDepth)
            {
                passData.source = external.depth;
                passData.materialPassIndex = downsampleDepthFromExternalPassIndex;
                builder.UseTexture(external.depth);
            }
            else
            {
                passData.source = resourceData.cameraDepthTexture;
                passData.materialPassIndex = downsampleDepthPassIndex;
                builder.UseTexture(resourceData.cameraDepthTexture);
            }

            builder.SetRenderAttachment(downsampledCameraDepthTarget, 0, AccessFlags.WriteAll);
            builder.SetRenderFunc((PassData data, RasterGraphContext context) => ExecutePass(data, context));
        }

        using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Volumetric Fog Render Pass", out PassData passData, profilingSampler))
        {
            passData.stage = PassStage.VolumetricFogRender;
            passData.source = downsampledCameraDepthTarget;
            passData.target = volumetricFogRenderTarget;
            passData.material = volumetricFogMaterial;
            passData.materialPassIndex = volumetricFogRenderPassIndex;
            passData.downsampledCameraDepthTarget = downsampledCameraDepthTarget;
            passData.lightData = lightData;
            passData.blueNoiseTexture = blueNoiseTexture;
            passData.fogVolume = activeFog;

            builder.SetRenderAttachment(volumetricFogRenderTarget, 0, AccessFlags.WriteAll);
            builder.UseTexture(downsampledCameraDepthTarget);
            if (resourceData.mainShadowsTexture.IsValid())
                builder.UseTexture(resourceData.mainShadowsTexture);
            if (resourceData.additionalShadowsTexture.IsValid())
                builder.UseTexture(resourceData.additionalShadowsTexture);
            builder.SetRenderFunc((PassData data, RasterGraphContext context) => ExecutePass(data, context));
        }

        if (blurIterations > 0)
        {
            using (IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass("Volumetric Fog Blur Pass", out PassData passData, profilingSampler))
            {
                passData.stage = PassStage.VolumetricFogBlur;
                passData.source = volumetricFogRenderTarget;
                passData.target = volumetricFogBlurRenderTarget;
                passData.material = volumetricFogMaterial;
                passData.materialPassIndex = volumetricFogHorizontalBlurPassIndex;
                passData.materialAdditionalPassIndex = volumetricFogVerticalBlurPassIndex;
                passData.blurIterations = blurIterations;

                builder.UseTexture(volumetricFogRenderTarget, AccessFlags.ReadWrite);
                builder.UseTexture(volumetricFogBlurRenderTarget, AccessFlags.ReadWrite);
                builder.SetRenderFunc((PassData data, UnsafeGraphContext context) => ExecuteUnsafeBlurPass(data, context));
            }
        }

        // Blends over the camera color rather than sampling it into a new composition target: reading the
        // MSAA color as a texture forces an early resolve and flattens the samples per pixel.
        using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Volumetric Fog Upsample Composition Pass", out PassData passData, profilingSampler))
        {
            passData.stage = PassStage.VolumetricFogUpsampleComposition;
            passData.source = volumetricFogRenderTarget;
            passData.material = volumetricFogMaterial;
            passData.materialPassIndex = volumetricFogUpsampleCompositionPassIndex;

            builder.SetRenderAttachment(resourceData.cameraColor, 0, AccessFlags.ReadWrite);
            builder.UseTexture(resourceData.cameraDepthTexture);
            builder.UseTexture(downsampledCameraDepthTarget);
            builder.UseTexture(volumetricFogRenderTarget);
            builder.SetRenderFunc((PassData data, RasterGraphContext context) => ExecutePass(data, context));
        }
    }

    #endregion

    #region Methods

    /// <summary>
    /// Updates the volumetric fog material parameters.
    /// </summary>
    /// <param name="volumetricFogMaterial"></param>
    /// <param name="mainLightIndex"></param>
    private static void UpdateVolumetricFogMaterialParameters(Material volumetricFogMaterial, int mainLightIndex, Texture2D blueNoiseTexture, VolumetricFogVolumeComponent fogVolume)
    {
        bool enableMainLightContribution = fogVolume.enableMainLightContribution.value && fogVolume.scattering.value > 0.0f && mainLightIndex > -1;

        // APV can be sampled live (Unity's APV, once per step) or from a pre-baked world-space volume
        // (one trilinear tap, static). Baked mode only engages once a bake exists; until then APV simply
        // contributes nothing rather than silently falling back to the expensive live path.
        bool wantAPVContribution = fogVolume.enableAPVContribution.value && fogVolume.APVContributionWeight.value > 0.0f;
        bool bakedAPVMode = fogVolume.apvMode.value == VolumetricFogAPVMode.Baked;
        bool useBakedAPV = wantAPVContribution && bakedAPVMode && VolumetricFogAPVBaker.IsReady;
        bool useLiveAPV = wantAPVContribution && !bakedAPVMode;
        bool enableAPVContribution = useBakedAPV || useLiveAPV;

        if (enableAPVContribution)
            volumetricFogMaterial.EnableKeyword("_APV_CONTRIBUTION_ENABLED");
        else
            volumetricFogMaterial.DisableKeyword("_APV_CONTRIBUTION_ENABLED");

        if (useBakedAPV)
        {
            volumetricFogMaterial.EnableKeyword("_APV_BAKED");
            volumetricFogMaterial.SetTexture(BakedAPVFogVolumeId, VolumetricFogAPVBaker.BakedVolume);
            volumetricFogMaterial.SetVector(BakedAPVVolumeBoundsMinId, VolumetricFogAPVBaker.BoundsMin);
            volumetricFogMaterial.SetVector(BakedAPVVolumeInvSizeId, VolumetricFogAPVBaker.BoundsInvSize);
        }
        else
        {
            volumetricFogMaterial.DisableKeyword("_APV_BAKED");
        }

        if (enableMainLightContribution)
            volumetricFogMaterial.DisableKeyword("_MAIN_LIGHT_CONTRIBUTION_DISABLED");
        else
            volumetricFogMaterial.EnableKeyword("_MAIN_LIGHT_CONTRIBUTION_DISABLED");

        if (enableMainLightContribution)
        {
            volumetricFogMaterial.SetFloat(MainLightAnisotropyId, fogVolume.anisotropy.value);
            volumetricFogMaterial.SetFloat(MainLightScatteringId, fogVolume.scattering.value);
        }

        volumetricFogMaterial.SetFloat(DistanceId, fogVolume.distance.value);
        volumetricFogMaterial.SetFloat(BaseHeightId, fogVolume.baseHeight.value);
        volumetricFogMaterial.SetFloat(MaximumHeightId, fogVolume.maximumHeight.value);
        // Use a large finite sentinel (not float.MinValue) when ground is disabled so the shader's
        // height-band intersection math never produces infinities.
        volumetricFogMaterial.SetFloat(GroundHeightId, (fogVolume.enableGround.overrideState && fogVolume.enableGround.value) ? fogVolume.groundHeight.value : -1.0e9f);
        volumetricFogMaterial.SetFloat(DensityId, fogVolume.density.value);
        volumetricFogMaterial.SetFloat(AbsortionId, 1.0f / fogVolume.attenuationDistance.value);
        volumetricFogMaterial.SetFloat(APVContributionWeigthId, fogVolume.enableAPVContribution.value ? fogVolume.APVContributionWeight.value : 0.0f);
        volumetricFogMaterial.SetColor(TintId, fogVolume.tint.value);
        volumetricFogMaterial.SetInteger(MaxStepsId, fogVolume.maxSteps.value);
        volumetricFogMaterial.SetFloat(LTCGIScatteringId, fogVolume.enableLTCGIContribution.value ? fogVolume.LTCGIScattering.value : 0.0f);

        Texture2D noiseTexture = blueNoiseTexture != null ? blueNoiseTexture : Texture2D.blackTexture;
        volumetricFogMaterial.SetTexture(BlueNoiseTextureId, noiseTexture);

        // R2 low-discrepancy sequence gives a well-spread per-frame scroll so the tiled blue noise
        // decorrelates frame to frame. Frame index is wrapped to keep float precision.
        int frame = Time.renderedFrameCount & 4095;
        float scrollX = (0.5f + 0.7548776662466927f * frame) % 1.0f;
        float scrollY = (0.5f + 0.5698402909980532f * frame) % 1.0f;
        volumetricFogMaterial.SetVector(BlueNoiseParamsId, new Vector4(1.0f / noiseTexture.width, 1.0f / noiseTexture.height, scrollX, scrollY));
    }

    /// <summary>
    /// Creates and returns all the necessary render graph textures.
    /// </summary>
    /// <param name="renderGraph"></param>
    /// <param name="cameraData"></param>
    /// <param name="createBlurTarget"></param>
    /// <param name="downsampledCameraDepthTarget"></param>
    /// <param name="volumetricFogRenderTarget"></param>
    /// <param name="volumetricFogBlurRenderTarget"></param>
    private void CreateRenderGraphTextures(RenderGraph renderGraph, UniversalCameraData cameraData, VolumetricFogVolumeComponent fogVolume, bool createBlurTarget, out TextureHandle downsampledCameraDepthTarget, out TextureHandle volumetricFogRenderTarget, out TextureHandle volumetricFogBlurRenderTarget)
    {
        int fogDownsampleFactor = (int)fogVolume.resolution.value;

        RenderTextureDescriptor cameraTargetDescriptor = cameraData.cameraTargetDescriptor;
        cameraTargetDescriptor.depthStencilFormat = GraphicsFormat.None;

        Vector2Int originalResolution = new Vector2Int(cameraTargetDescriptor.width, cameraTargetDescriptor.height);

        // The downsampled depth, fog and blur buffers are sampled in screen space (the unsafe blur pass
        // samples them as non-MSAA), so they must be single sample even though depth priming keeps MSAA on.
        cameraTargetDescriptor.msaaSamples = 1;

        // The downsampled depth stays at half resolution regardless of the fog resolution, since the
        // depth-aware upsample relies on it to reconstruct sharp edges back at full resolution.
        cameraTargetDescriptor.width = Mathf.Max(1, originalResolution.x / (int)DownsampleFactor.Half);
        cameraTargetDescriptor.height = Mathf.Max(1, originalResolution.y / (int)DownsampleFactor.Half);
        cameraTargetDescriptor.graphicsFormat = GraphicsFormat.R32_SFloat;
        downsampledCameraDepthTarget = UniversalRenderer.CreateRenderGraphTexture(renderGraph, cameraTargetDescriptor, DownsampledCameraDepthRTName, false);

        // The fog and its blur run at the volume-selected resolution (half or quarter).
        cameraTargetDescriptor.width = Mathf.Max(1, originalResolution.x / fogDownsampleFactor);
        cameraTargetDescriptor.height = Mathf.Max(1, originalResolution.y / fogDownsampleFactor);
        cameraTargetDescriptor.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
        volumetricFogRenderTarget = UniversalRenderer.CreateRenderGraphTexture(renderGraph, cameraTargetDescriptor, VolumetricFogRenderRTName, false);
        volumetricFogBlurRenderTarget = createBlurTarget
            ? UniversalRenderer.CreateRenderGraphTexture(renderGraph, cameraTargetDescriptor, VolumetricFogBlurRTName, false)
            : TextureHandle.nullHandle;
    }

    /// <summary>
    /// Executes the pass with the information from the pass data.
    /// </summary>
    /// <param name="passData"></param>
    /// <param name="context"></param>
    private static void ExecutePass(PassData passData, RasterGraphContext context)
    {
        PassStage stage = passData.stage;

        if (stage == PassStage.VolumetricFogRender)
        {
            passData.material.SetTexture(DownsampledCameraDepthTextureId, passData.downsampledCameraDepthTarget);
            UpdateVolumetricFogMaterialParameters(passData.material, passData.lightData.mainLightIndex, passData.blueNoiseTexture, passData.fogVolume);
        }

        Blitter.BlitTexture(context.cmd, passData.source, Vector2.one, passData.material, passData.materialPassIndex);
    }

    /// <summary>
    /// Executes the unsafe pass that does up to multiple separable blurs to the volumetric fog.
    /// </summary>
    /// <param name="passData"></param>
    /// <param name="context"></param>
    private static void ExecuteUnsafeBlurPass(PassData passData, UnsafeGraphContext context)
    {
        CommandBuffer unsafeCmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
        int blurIterations = passData.blurIterations;

        for (int i = 0; i < blurIterations; ++i)
        {
            Blitter.BlitCameraTexture(unsafeCmd, passData.source, passData.target, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, passData.material, passData.materialPassIndex);
            Blitter.BlitCameraTexture(unsafeCmd, passData.target, passData.source, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, passData.material, passData.materialAdditionalPassIndex);
        }
    }

    /// <summary>
    /// Disposes the resources used by this pass.
    /// </summary>
    public void Dispose()
    {
    }

    #endregion
}
