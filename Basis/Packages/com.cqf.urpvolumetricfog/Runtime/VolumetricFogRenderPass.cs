using System;
using System.Collections.Generic;
using Unity.Collections;
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
    /// The subpasses the volumetric fog render pass is made of.
    /// </summary>
    private enum PassStage : byte
    {
        DownsampleDepth,
        VolumetricFogRender,
        VolumetricFogTemporal,
        VolumetricFogBlur,
        VolumetricFogUpsampleComposition,
        VolumetricFogComposition,
        VolumetricFogFroxelApply
    }

    private struct FogParameters
    {
        public bool apvEnabled;
        public bool apvBaked;
        public bool mainLightEnabled;
        public float distance;
        public float baseHeight;
        public float maximumHeight;
        public float groundHeight;
        public float density;
        public float absorption;
        public float apvWeight;
        public float anisotropy;
        public float scattering;
        public float ltcgiScattering;
        public float analyticOpticalDepth;
        public float sunTrims;
        public bool additionalLightsEnabled;
        public float additionalAnisotropy;
        public float additionalScattering;
        public int maxSteps;
        public Color tint;
        public Texture blueNoise;
        public Vector4 blueNoiseParams;
        public Texture bakedVolume;
        public Vector3 bakedBoundsMin;
        public Vector3 bakedInvSize;
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
        public FogParameters parameters;
        public int blurIterations;
        public int downsampleFactor;
        public bool compositeDirectly;

        public TextureHandle fogHistory;
        public TextureHandle depthHistory;
        public Matrix4x4[] previousViewProjection;
        public float historyValid;

        public ComputeShader computeShader;
        public int kernelIndex;
        public bool temporal;
        public bool depthArray;
        public TextureHandle froxelLighting;
        public TextureHandle froxelLightingHistory;
        public TextureHandle froxelIntegrated;
        public TextureHandle froxelColumnDepth;
        public TextureHandle froxelColumn;
        public TextureHandle froxelColumnLight;
        public TextureHandle sceneDepth;
        public Vector3Int froxelTextureSize;
        public Vector2Int depthGridSize;
        public Matrix4x4[] inverseViewProjection;
        public Matrix4x4[] eyeViewProjection;
        public Vector4[] cameraPositions;
        public Matrix4x4 froxelViewProjection;
        public Vector4 froxelParams;
        public Vector4 froxelGridSize;
        public Vector4 froxelApplyParams;
        public Vector4 froxelDepthParams;
        public Vector4 froxelColumnParams;
        public Vector4 froxelTemporal;
        public Vector4 froxelTemporalParams;
        public Vector4 froxelLightFlags;
        public Vector4[] additionalLightMultipliers;
        public float froxelShared;
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
    private const string VolumetricFogTemporalRTName = "_VolumetricFogTemporal";
    private const string FroxelLightingRTName = "_VolumetricFogFroxelLighting";
    private const string FroxelIntegratedRTName = "_VolumetricFogFroxelIntegrated";
    private const string FroxelColumnDepthRTName = "_VolumetricFogFroxelColumnDepth";
    private const string FroxelColumnRTName = "_VolumetricFogFroxelColumn";
    private const string FroxelColumnLightRTName = "_VolumetricFogFroxelColumnLight";
    private const int MaxCameraHistories = 8;

    private static readonly int DownsampledCameraDepthTextureId = Shader.PropertyToID("_DownsampledCameraDepthTexture");
    private static readonly int DownsampleDepthFactorId = Shader.PropertyToID("_DownsampleDepthFactor");

    private static readonly int SrcBlendId = Shader.PropertyToID("_VFSrcBlend");
    private static readonly int DstBlendId = Shader.PropertyToID("_VFDstBlend");
    private static readonly int SrcBlendAlphaId = Shader.PropertyToID("_VFSrcBlendAlpha");
    private static readonly int DstBlendAlphaId = Shader.PropertyToID("_VFDstBlendAlpha");

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

    private static readonly int AnalyticOpticalDepthId = Shader.PropertyToID("_VFAnalyticOpticalDepth");
    private static readonly int SunTrimsId = Shader.PropertyToID("_VFSunTrims");
    private static readonly int AdditionalAnisotropyId = Shader.PropertyToID("_VFAdditionalAnisotropy");
    private static readonly int AdditionalScatteringId = Shader.PropertyToID("_VFAdditionalScattering");
    private static readonly int AdditionalLightMultipliersId = Shader.PropertyToID("_VFAdditionalLightMultipliers");

    private static readonly int FogHistoryId = Shader.PropertyToID("_VFFogHistory");
    private static readonly int DepthHistoryId = Shader.PropertyToID("_VFDepthHistory");
    private static readonly int PrevViewProjId = Shader.PropertyToID("_VFPrevViewProj");
    private static readonly int TemporalParamsId = Shader.PropertyToID("_VFTemporalParams");

    private static readonly int FroxelParamsId = Shader.PropertyToID("_VFFroxelParams");
    private static readonly int FroxelGridSizeId = Shader.PropertyToID("_VFFroxelGridSize");
    private static readonly int FroxelApplyParamsId = Shader.PropertyToID("_VFFroxelApplyParams");
    private static readonly int FroxelDepthParamsId = Shader.PropertyToID("_VFFroxelDepthParams");
    private static readonly int FroxelColumnParamsId = Shader.PropertyToID("_VFFroxelColumnParams");
    private static readonly int FroxelLightingId = Shader.PropertyToID("_VFFroxelLighting");
    private static readonly int FroxelIntegratedId = Shader.PropertyToID("_VFFroxelIntegrated");
    private static readonly int FroxelLightingSourceId = Shader.PropertyToID("_VFFroxelLightingSource");
    private static readonly int FroxelLightingHistoryId = Shader.PropertyToID("_VFFroxelLightingHistory");
    private static readonly int FroxelColumnDepthId = Shader.PropertyToID("_VFFroxelColumnDepth");
    private static readonly int FroxelColumnDepthSourceId = Shader.PropertyToID("_VFFroxelColumnDepthSource");
    private static readonly int FroxelColumnId = Shader.PropertyToID("_VFFroxelColumn");
    private static readonly int FroxelColumnLightId = Shader.PropertyToID("_VFFroxelColumnLight");
    private static readonly int FroxelColumnSourceId = Shader.PropertyToID("_VFFroxelColumnSource");
    private static readonly int FroxelColumnLightSourceId = Shader.PropertyToID("_VFFroxelColumnLightSource");
    private static readonly int FroxelSceneDepthId = Shader.PropertyToID("_VFFroxelSceneDepth");
    private static readonly int FroxelSceneDepthArrayId = Shader.PropertyToID("_VFFroxelSceneDepthArray");
    private static readonly int FroxelInvViewProjId = Shader.PropertyToID("_VFFroxelInvViewProj");
    private static readonly int FroxelEyeViewProjId = Shader.PropertyToID("_VFFroxelEyeViewProj");
    private static readonly int FroxelPrevViewProjId = Shader.PropertyToID("_VFFroxelPrevViewProj");
    private static readonly int FroxelCameraPosId = Shader.PropertyToID("_VFFroxelCameraPos");
    private static readonly int FroxelTemporalId = Shader.PropertyToID("_VFFroxelTemporal");
    private static readonly int FroxelTemporalParamsId = Shader.PropertyToID("_VFFroxelTemporalParams");
    private static readonly int FroxelLightFlagsId = Shader.PropertyToID("_VFFroxelLightFlags");
    private static readonly int FroxelVolumeId = Shader.PropertyToID("_VFFroxelVolume");
    private static readonly int FroxelViewProjId = Shader.PropertyToID("_VFFroxelViewProj");
    private static readonly int FroxelSharedId = Shader.PropertyToID("_VFFroxelShared");

    private int downsampleDepthPassIndex;
    private int downsampleDepthFromExternalPassIndex;
    private int volumetricFogRenderPassIndex;
    private int volumetricFogTemporalPassIndex;
    private int volumetricFogHorizontalBlurPassIndex;
    private int volumetricFogVerticalBlurPassIndex;
    private int volumetricFogUpsampleCompositionPassIndex;
    private int volumetricFogCompositionPassIndex;
    private int volumetricFogFroxelApplyPassIndex;

    private Material downsampleDepthMaterial;
    private Material volumetricFogMaterial;

    private ComputeShader froxelComputeShader;
    private ComputeShader froxelColumnComputeShader;
    private int froxelLightKernel = -1;
    private int froxelIntegrateKernel = -1;
    private int froxelColumnDepthKernel = -1;
    private int froxelColumnDepthArrayKernel = -1;
    private int froxelColumnKernel = -1;

    private readonly Vector4[] additionalLightMultipliers;
    private int additionalLightMultipliersWritten;

    private readonly Dictionary<(Camera camera, int view), VolumetricFogCameraHistory> histories = new Dictionary<(Camera camera, int view), VolumetricFogCameraHistory>();
    private readonly List<(Camera camera, int view)> deadHistoryKeys = new List<(Camera camera, int view)>();

    // Per-camera volume and optional blue-noise texture, assigned by the renderer feature.
    public VolumetricFogVolumeComponent fogVolume;
    public Texture2D blueNoiseTexture;

    private ProfilingSampler downsampleDepthProfilingSampler;
    private ProfilingSampler froxelProfilingSampler;

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
        froxelProfilingSampler = new ProfilingSampler("Volumetric Fog Froxels");
        renderPassEvent = passEvent;
        requiresIntermediateTexture = false;
        additionalLightMultipliers = new Vector4[UniversalRenderPipeline.maxVisibleAdditionalLights];
        Array.Fill(additionalLightMultipliers, Vector4.one);

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
        volumetricFogTemporalPassIndex = volumetricFogMaterial.FindPass("VolumetricFogTemporal");
        volumetricFogHorizontalBlurPassIndex = volumetricFogMaterial.FindPass("VolumetricFogHorizontalBlur");
        volumetricFogVerticalBlurPassIndex = volumetricFogMaterial.FindPass("VolumetricFogVerticalBlur");
        volumetricFogUpsampleCompositionPassIndex = volumetricFogMaterial.FindPass("VolumetricFogUpsampleComposition");
        volumetricFogCompositionPassIndex = volumetricFogMaterial.FindPass("VolumetricFogComposition");
        volumetricFogFroxelApplyPassIndex = volumetricFogMaterial.FindPass("VolumetricFogFroxelApply");
    }

    public void SetFroxelComputeShaders(ComputeShader computeShader, ComputeShader columnComputeShader)
    {
        froxelComputeShader = computeShader;
        froxelColumnComputeShader = columnComputeShader;
        froxelLightKernel = -1;
        froxelIntegrateKernel = -1;
        froxelColumnDepthKernel = -1;
        froxelColumnDepthArrayKernel = -1;
        froxelColumnKernel = -1;

        if (computeShader != null && computeShader.HasKernel("CSFroxelLight"))
            froxelLightKernel = computeShader.FindKernel("CSFroxelLight");

        if (columnComputeShader != null)
        {
            if (columnComputeShader.HasKernel("CSFroxelColumnDepth"))
                froxelColumnDepthKernel = columnComputeShader.FindKernel("CSFroxelColumnDepth");
            if (columnComputeShader.HasKernel("CSFroxelColumnDepthArray"))
                froxelColumnDepthArrayKernel = columnComputeShader.FindKernel("CSFroxelColumnDepthArray");
            if (columnComputeShader.HasKernel("CSFroxelColumn"))
                froxelColumnKernel = columnComputeShader.FindKernel("CSFroxelColumn");
            if (columnComputeShader.HasKernel("CSFroxelIntegrate"))
                froxelIntegrateKernel = columnComputeShader.FindKernel("CSFroxelIntegrate");
        }
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

        bool perspective = !cameraData.camera.orthographic;
        int viewCount = cameraData.xr.enabled ? Mathf.Clamp(cameraData.xr.viewCount, 1, 2) : 1;
        bool useTemporal = VolumetricFogQuality.TemporalReprojection && perspective;
        bool useFroxels = VolumetricFogQuality.FroxelVolume && perspective && froxelComputeShader != null && froxelColumnComputeShader != null
            && froxelLightKernel >= 0 && froxelIntegrateKernel >= 0 && froxelColumnKernel >= 0 && volumetricFogFroxelApplyPassIndex >= 0 && SystemInfo.supportsComputeShaders;
        bool sharedStereo = useFroxels && VolumetricFogQuality.SharedStereoVolume && viewCount == 2;
        int downsampleFactor = Mathf.Max(1, (int)VolumetricFogQuality.Resolution);
        FogParameters parameters = BuildParameters(activeFog, lightData.mainLightIndex, ScaledMaxSteps(downsampleFactor));

        VolumetricFogCameraHistory history = null;
        bool historyRecent = false;
        if (useTemporal || useFroxels)
        {
            history = GetHistory(cameraData.camera, cameraData.xr.enabled ? cameraData.xr.multipassId : 0);
            historyRecent = history.lastFrame >= 0 && Time.frameCount - history.lastFrame <= 2;
            UpdateCameraMatrices(history, cameraData, viewCount, sharedStereo);
        }

        if (useFroxels && parameters.additionalLightsEnabled)
            UpdateAdditionalLightMultipliers(lightData);

        if (useFroxels)
            RecordFroxelPasses(renderGraph, cameraData, resourceData, history, parameters, viewCount, sharedStereo, useTemporal, historyRecent);
        else
            RecordRaymarchPasses(renderGraph, cameraData, resourceData, history, parameters, downsampleFactor, useTemporal, historyRecent);

        if (history != null)
            history.lastFrame = Time.frameCount;
    }

    #endregion

    #region Methods

    private void RecordRaymarchPasses(RenderGraph renderGraph, UniversalCameraData cameraData, UniversalResourceData resourceData, VolumetricFogCameraHistory history, in FogParameters parameters, int downsampleFactor, bool useTemporal, bool historyRecent)
    {
        int blurIterations = VolumetricFogQuality.BlurIterations;
        bool fullResolution = downsampleFactor == 1;
        bool compositeDirectly = fullResolution && blurIterations == 0 && !useTemporal;

        RenderTextureDescriptor descriptor = FogTargetDescriptor(cameraData, downsampleFactor);
        TextureHandle downsampledCameraDepthTarget = fullResolution ? TextureHandle.nullHandle : CreateFogTexture(renderGraph, descriptor, GraphicsFormat.R32_SFloat, DownsampledCameraDepthRTName);
        TextureHandle volumetricFogRenderTarget = compositeDirectly ? TextureHandle.nullHandle : CreateFogTexture(renderGraph, descriptor, GraphicsFormat.R16G16B16A16_SFloat, VolumetricFogRenderRTName);
        TextureHandle volumetricFogBlurRenderTarget = blurIterations > 0 ? CreateFogTexture(renderGraph, descriptor, GraphicsFormat.R16G16B16A16_SFloat, VolumetricFogBlurRTName) : TextureHandle.nullHandle;
        TextureHandle volumetricFogTemporalTarget = useTemporal ? CreateFogTexture(renderGraph, descriptor, GraphicsFormat.R16G16B16A16_SFloat, VolumetricFogTemporalRTName) : TextureHandle.nullHandle;
        TextureHandle fogDepthTexture = fullResolution ? resourceData.cameraDepthTexture : downsampledCameraDepthTarget;

        if (!fullResolution)
        {
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
                passData.downsampleFactor = downsampleFactor;

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
        }

        using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Volumetric Fog Render Pass", out PassData passData, profilingSampler))
        {
            passData.stage = PassStage.VolumetricFogRender;
            passData.source = fogDepthTexture;
            passData.target = compositeDirectly ? resourceData.cameraColor : volumetricFogRenderTarget;
            passData.material = volumetricFogMaterial;
            passData.materialPassIndex = volumetricFogRenderPassIndex;
            passData.downsampledCameraDepthTarget = fogDepthTexture;
            passData.parameters = parameters;
            passData.compositeDirectly = compositeDirectly;

            if (compositeDirectly)
                builder.SetRenderAttachment(resourceData.cameraColor, 0, AccessFlags.ReadWrite);
            else
                builder.SetRenderAttachment(volumetricFogRenderTarget, 0, AccessFlags.WriteAll);
            builder.UseTexture(fogDepthTexture);
            if (resourceData.mainShadowsTexture.IsValid())
                builder.UseTexture(resourceData.mainShadowsTexture);
            if (resourceData.additionalShadowsTexture.IsValid())
                builder.UseTexture(resourceData.additionalShadowsTexture);
            builder.SetRenderFunc((PassData data, RasterGraphContext context) => ExecutePass(data, context));
        }

        TextureHandle fogResult = volumetricFogRenderTarget;

        if (useTemporal)
        {
            RenderTextureDescriptor historyDescriptor = descriptor;
            historyDescriptor.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
            history.EnsureFogHistory(historyDescriptor);

            int writeIndex = history.fogWriteIndex;
            int readIndex = 1 - writeIndex;
            TextureHandle historyWrite = renderGraph.ImportTexture(history.fogHistory[writeIndex]);
            TextureHandle historyRead = renderGraph.ImportTexture(history.fogHistory[readIndex]);
            TextureHandle depthWrite = renderGraph.ImportTexture(history.depthHistory[writeIndex]);
            TextureHandle depthRead = renderGraph.ImportTexture(history.depthHistory[readIndex]);

            using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Volumetric Fog Temporal Pass", out PassData passData, profilingSampler))
            {
                passData.stage = PassStage.VolumetricFogTemporal;
                passData.source = volumetricFogRenderTarget;
                passData.material = volumetricFogMaterial;
                passData.materialPassIndex = volumetricFogTemporalPassIndex;
                passData.downsampledCameraDepthTarget = fogDepthTexture;
                passData.fogHistory = historyRead;
                passData.depthHistory = depthRead;
                passData.previousViewProjection = history.previousViewProjection;
                passData.historyValid = history.fogHistoryValid && historyRecent ? 1.0f : 0.0f;

                builder.SetRenderAttachment(historyWrite, 0, AccessFlags.WriteAll);
                builder.SetRenderAttachment(volumetricFogTemporalTarget, 1, AccessFlags.WriteAll);
                builder.SetRenderAttachment(depthWrite, 2, AccessFlags.WriteAll);
                builder.UseTexture(volumetricFogRenderTarget);
                builder.UseTexture(fogDepthTexture);
                builder.UseTexture(historyRead);
                builder.UseTexture(depthRead);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc((PassData data, RasterGraphContext context) => ExecutePass(data, context));
            }

            history.fogWriteIndex = readIndex;
            history.fogHistoryValid = true;
            fogResult = volumetricFogTemporalTarget;
        }

        if (blurIterations > 0)
        {
            using (IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass("Volumetric Fog Blur Pass", out PassData passData, profilingSampler))
            {
                passData.stage = PassStage.VolumetricFogBlur;
                passData.source = fogResult;
                passData.target = volumetricFogBlurRenderTarget;
                passData.material = volumetricFogMaterial;
                passData.materialPassIndex = volumetricFogHorizontalBlurPassIndex;
                passData.materialAdditionalPassIndex = volumetricFogVerticalBlurPassIndex;
                passData.blurIterations = blurIterations;

                builder.UseTexture(fogResult, AccessFlags.ReadWrite);
                builder.UseTexture(volumetricFogBlurRenderTarget, AccessFlags.ReadWrite);
                builder.UseTexture(fogDepthTexture);
                builder.SetRenderFunc((PassData data, UnsafeGraphContext context) => ExecuteUnsafeBlurPass(data, context));
            }
        }

        if (compositeDirectly)
            return;

        // Blends over the camera color rather than sampling it into a new composition target: reading the
        // MSAA color as a texture forces an early resolve and flattens the samples per pixel.
        using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass(fullResolution ? "Volumetric Fog Composition Pass" : "Volumetric Fog Upsample Composition Pass", out PassData passData, profilingSampler))
        {
            passData.stage = fullResolution ? PassStage.VolumetricFogComposition : PassStage.VolumetricFogUpsampleComposition;
            passData.source = fogResult;
            passData.material = volumetricFogMaterial;
            passData.materialPassIndex = fullResolution ? volumetricFogCompositionPassIndex : volumetricFogUpsampleCompositionPassIndex;

            builder.SetRenderAttachment(resourceData.cameraColor, 0, AccessFlags.ReadWrite);
            if (!fullResolution)
            {
                builder.UseTexture(resourceData.cameraDepthTexture);
                builder.UseTexture(downsampledCameraDepthTarget);
            }
            builder.UseTexture(fogResult);
            builder.SetRenderFunc((PassData data, RasterGraphContext context) => ExecutePass(data, context));
        }
    }

    private void RecordFroxelPasses(RenderGraph renderGraph, UniversalCameraData cameraData, UniversalResourceData resourceData, VolumetricFogCameraHistory history, in FogParameters parameters, int viewCount, bool sharedStereo, bool useTemporal, bool historyRecent)
    {
        RenderTextureDescriptor cameraDescriptor = cameraData.cameraTargetDescriptor;
        int volumeViews = sharedStereo ? 1 : viewCount;
        int divisor = VolumetricFogQuality.FroxelDivisor;
        Vector3Int grid = new Vector3Int(Mathf.Max(1, cameraDescriptor.width / divisor), Mathf.Max(1, cameraDescriptor.height / divisor), VolumetricFogQuality.FroxelSlices);
        Vector3Int textureSize = new Vector3Int(grid.x * volumeViews, grid.y, grid.z);
        Vector2Int depthGridSize = new Vector2Int(grid.x * viewCount, grid.y);
        bool depthArray = cameraDescriptor.dimension == TextureDimension.Tex2DArray;
        int columnDepthKernel = depthArray ? froxelColumnDepthArrayKernel : froxelColumnDepthKernel;
        bool culling = VolumetricFogQuality.FroxelDepthCulling && columnDepthKernel >= 0;

        bool temporal = useTemporal && history.EnsureFroxelHistory(textureSize);
        int writeIndex = history.froxelWriteIndex;
        int readIndex = 1 - writeIndex;
        TextureHandle lighting;
        TextureHandle lightingHistory = TextureHandle.nullHandle;
        if (temporal)
        {
            lighting = renderGraph.ImportTexture(history.froxelLightingHandle[writeIndex]);
            lightingHistory = renderGraph.ImportTexture(history.froxelLightingHandle[readIndex]);
        }
        else
        {
            lighting = CreateFroxelTexture(renderGraph, textureSize, FroxelLightingRTName);
        }
        TextureHandle integrated = CreateFroxelTexture(renderGraph, textureSize, FroxelIntegratedRTName);
        TextureHandle columnDepth = culling ? CreateColumnTexture(renderGraph, depthGridSize, GraphicsFormat.R32_SFloat, FroxelColumnDepthRTName) : TextureHandle.nullHandle;
        TextureHandle column = CreateColumnTexture(renderGraph, new Vector2Int(textureSize.x, textureSize.y), GraphicsFormat.R32G32B32A32_SFloat, FroxelColumnRTName);
        TextureHandle columnLight = CreateColumnTexture(renderGraph, new Vector2Int(textureSize.x, textureSize.y), GraphicsFormat.R16G16B16A16_SFloat, FroxelColumnLightRTName);

        float near = cameraData.camera.nearClipPlane;
        float range = Mathf.Max(parameters.distance - near, 0.01f);
        Vector4 froxelParams = new Vector4(near, range, 1.0f / range, volumeViews);
        Vector4 froxelGridSize = new Vector4(grid.x, grid.y, grid.z, 1.0f / grid.z);
        Vector4 froxelApplyParams = new Vector4(1.0f / volumeViews, 0.5f / grid.x, 1.0f - 0.5f / grid.x, 0.5f / grid.z);
        Vector4 froxelDepthParams = new Vector4(divisor, cameraDescriptor.width, cameraDescriptor.height, viewCount);
        Vector4 froxelColumnParams = new Vector4(sharedStereo ? 1.0f : 0.0f, parameters.mainLightEnabled ? 1.0f : 0.0f, grid.x, culling ? 1.0f : 0.0f);
        bool historyUsable = temporal && history.froxelHistoryValid && historyRecent;
        Vector4 froxelTemporal = new Vector4(temporal ? 1.0f : 0.0f, historyUsable ? 1.0f : 0.0f, temporal && VolumetricFogQuality.FroxelHalfRateUpdate ? 1.0f : 0.0f, Time.frameCount & 1);

        if (culling)
        {
            using (IComputeRenderGraphBuilder builder = renderGraph.AddComputePass("Volumetric Fog Froxel Column Depth", out PassData passData, froxelProfilingSampler))
            {
                passData.computeShader = froxelColumnComputeShader;
                passData.kernelIndex = columnDepthKernel;
                passData.depthArray = depthArray;
                passData.sceneDepth = resourceData.cameraDepthTexture;
                passData.froxelColumnDepth = columnDepth;
                passData.depthGridSize = depthGridSize;
                passData.froxelParams = froxelParams;
                passData.froxelGridSize = froxelGridSize;
                passData.froxelApplyParams = froxelApplyParams;
                passData.froxelDepthParams = froxelDepthParams;

                builder.UseTexture(resourceData.cameraDepthTexture, AccessFlags.Read);
                builder.UseTexture(columnDepth, AccessFlags.Write);
                builder.SetRenderFunc((PassData data, ComputeGraphContext context) => ExecuteFroxelColumnDepthPass(data, context));
            }
        }

        using (IComputeRenderGraphBuilder builder = renderGraph.AddComputePass("Volumetric Fog Froxel Columns", out PassData passData, froxelProfilingSampler))
        {
            passData.computeShader = froxelColumnComputeShader;
            passData.kernelIndex = froxelColumnKernel;
            passData.parameters = parameters;
            passData.froxelColumnDepth = columnDepth;
            passData.froxelColumn = column;
            passData.froxelColumnLight = columnLight;
            passData.froxelTextureSize = textureSize;
            passData.inverseViewProjection = history.inverseViewProjection;
            passData.eyeViewProjection = history.eyeViewProjection;
            passData.cameraPositions = history.cameraPosition;
            passData.froxelParams = froxelParams;
            passData.froxelGridSize = froxelGridSize;
            passData.froxelApplyParams = froxelApplyParams;
            passData.froxelDepthParams = froxelDepthParams;
            passData.froxelColumnParams = froxelColumnParams;

            if (culling)
                builder.UseTexture(columnDepth, AccessFlags.Read);
            builder.UseTexture(column, AccessFlags.Write);
            builder.UseTexture(columnLight, AccessFlags.Write);
            builder.SetRenderFunc((PassData data, ComputeGraphContext context) => ExecuteFroxelColumnPass(data, context));
        }

        using (IComputeRenderGraphBuilder builder = renderGraph.AddComputePass("Volumetric Fog Froxel Lighting", out PassData passData, froxelProfilingSampler))
        {
            passData.computeShader = froxelComputeShader;
            passData.kernelIndex = froxelLightKernel;
            passData.parameters = parameters;
            passData.temporal = temporal;
            passData.eyeViewProjection = history.eyeViewProjection;
            passData.froxelTemporalParams = new Vector4(VolumetricFogQuality.TemporalFeedback, VolumetricFogQuality.TemporalLightResponse ? 1.0f : 0.0f, 0.0f, 0.0f);
            passData.froxelLightFlags = new Vector4(parameters.apvEnabled ? 1.0f : 0.0f, parameters.apvBaked ? 1.0f : 0.0f, parameters.additionalLightsEnabled ? 1.0f : 0.0f, sharedStereo ? 1.0f : 0.0f);
            passData.additionalLightMultipliers = additionalLightMultipliers;
            passData.froxelLighting = lighting;
            passData.froxelLightingHistory = lightingHistory;
            passData.froxelColumn = column;
            passData.froxelColumnLight = columnLight;
            passData.froxelTextureSize = textureSize;
            passData.previousViewProjection = history.previousViewProjection;
            passData.cameraPositions = history.cameraPosition;
            passData.froxelParams = froxelParams;
            passData.froxelGridSize = froxelGridSize;
            passData.froxelApplyParams = froxelApplyParams;
            passData.froxelTemporal = froxelTemporal;

            builder.UseTexture(column, AccessFlags.Read);
            builder.UseTexture(columnLight, AccessFlags.Read);
            builder.UseTexture(lighting, AccessFlags.Write);
            if (temporal)
                builder.UseTexture(lightingHistory, AccessFlags.Read);
            if (resourceData.mainShadowsTexture.IsValid())
                builder.UseTexture(resourceData.mainShadowsTexture, AccessFlags.Read);
            builder.AllowGlobalStateModification(true);
            builder.AllowPassCulling(false);
            builder.SetRenderFunc((PassData data, ComputeGraphContext context) => ExecuteFroxelLightPass(data, context));
        }

        using (IComputeRenderGraphBuilder builder = renderGraph.AddComputePass("Volumetric Fog Froxel Integrate", out PassData passData, froxelProfilingSampler))
        {
            passData.computeShader = froxelColumnComputeShader;
            passData.kernelIndex = froxelIntegrateKernel;
            passData.froxelLighting = lighting;
            passData.froxelIntegrated = integrated;
            passData.froxelColumn = column;
            passData.froxelTextureSize = textureSize;
            passData.froxelParams = froxelParams;
            passData.froxelGridSize = froxelGridSize;
            passData.froxelApplyParams = froxelApplyParams;

            builder.UseTexture(column, AccessFlags.Read);
            builder.UseTexture(lighting, AccessFlags.Read);
            builder.UseTexture(integrated, AccessFlags.Write);
            builder.SetRenderFunc((PassData data, ComputeGraphContext context) => ExecuteFroxelIntegratePass(data, context));
        }

        if (temporal)
        {
            history.froxelWriteIndex = readIndex;
            history.froxelHistoryValid = true;
        }

        using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Volumetric Fog Froxel Apply Pass", out PassData passData, profilingSampler))
        {
            passData.stage = PassStage.VolumetricFogFroxelApply;
            passData.source = resourceData.cameraDepthTexture;
            passData.froxelIntegrated = integrated;
            passData.material = volumetricFogMaterial;
            passData.materialPassIndex = volumetricFogFroxelApplyPassIndex;
            passData.froxelParams = froxelParams;
            passData.froxelGridSize = froxelGridSize;
            passData.froxelApplyParams = froxelApplyParams;
            passData.froxelViewProjection = history.viewProjection[0];
            passData.froxelShared = sharedStereo ? 1.0f : 0.0f;

            builder.SetRenderAttachment(resourceData.cameraColor, 0, AccessFlags.ReadWrite);
            builder.UseTexture(integrated);
            builder.UseTexture(resourceData.cameraDepthTexture);
            builder.SetRenderFunc((PassData data, RasterGraphContext context) => ExecutePass(data, context));
        }
    }

    private static int ScaledMaxSteps(int downsampleFactor)
    {
        int maxSteps = VolumetricFogQuality.MaxSteps;
        if (!VolumetricFogQuality.ScaleStepsWithResolution)
            return maxSteps;

        float scale = downsampleFactor == 1 ? 0.5f : (downsampleFactor >= 4 ? 2.0f : 1.0f);
        return Mathf.Clamp(Mathf.RoundToInt(maxSteps * scale), VolumetricFogQuality.MinSteps, VolumetricFogQuality.MaxStepsLimit);
    }

    private FogParameters BuildParameters(VolumetricFogVolumeComponent fogVolume, int mainLightIndex, int maxSteps)
    {
        FogParameters parameters = default;

        // APV can be sampled live (Unity's APV, once per step) or from a pre-baked world-space volume
        // (one trilinear tap, static). Baked mode only engages once a bake exists; until then APV simply
        // contributes nothing rather than silently falling back to the expensive live path.
        bool wantAPVContribution = fogVolume.enableAPVContribution.value && fogVolume.APVContributionWeight.value > 0.0f;
        bool bakedAPVMode = VolumetricFogQuality.APVMode == VolumetricFogAPVMode.Baked;
        parameters.apvBaked = wantAPVContribution && bakedAPVMode && VolumetricFogAPVBaker.IsReady;
        parameters.apvEnabled = parameters.apvBaked || (wantAPVContribution && !bakedAPVMode);
        parameters.mainLightEnabled = fogVolume.enableMainLightContribution.value && fogVolume.scattering.value > 0.0f && mainLightIndex > -1;

        parameters.distance = fogVolume.distance.value;
        parameters.baseHeight = fogVolume.baseHeight.value;
        parameters.maximumHeight = fogVolume.maximumHeight.value;
        // Use a large finite sentinel (not float.MinValue) when ground is disabled so the shader's
        // height-band intersection math never produces infinities.
        parameters.groundHeight = (fogVolume.enableGround.overrideState && fogVolume.enableGround.value) ? fogVolume.groundHeight.value : -1.0e9f;
        parameters.density = fogVolume.density.value;
        parameters.absorption = 1.0f / fogVolume.attenuationDistance.value;
        parameters.apvWeight = fogVolume.enableAPVContribution.value ? fogVolume.APVContributionWeight.value : 0.0f;
        parameters.anisotropy = fogVolume.anisotropy.value;
        parameters.scattering = fogVolume.scattering.value;
        parameters.tint = fogVolume.tint.value;
        parameters.ltcgiScattering = fogVolume.enableLTCGIContribution.value ? fogVolume.LTCGIScattering.value : 0.0f;
        parameters.analyticOpticalDepth = VolumetricFogQuality.AnalyticOpticalDepth ? 1.0f : 0.0f;
        parameters.sunTrims = VolumetricFogQuality.SunPathTrims ? 1.0f : 0.0f;
        parameters.additionalLightsEnabled = fogVolume.enableAdditionalLightsContribution.value && fogVolume.additionalLightsScattering.value > 0.0f;
        parameters.additionalAnisotropy = fogVolume.additionalLightsAnisotropy.value;
        parameters.additionalScattering = fogVolume.additionalLightsScattering.value;
        parameters.maxSteps = maxSteps;

        Texture2D noiseTexture = blueNoiseTexture != null ? blueNoiseTexture : Texture2D.blackTexture;
        parameters.blueNoise = noiseTexture;
        // R2 low-discrepancy sequence gives a well-spread per-frame scroll so the tiled blue noise
        // decorrelates frame to frame. Frame index is wrapped to keep float precision.
        int frame = Time.renderedFrameCount & 4095;
        float scrollX = (0.5f + 0.7548776662466927f * frame) % 1.0f;
        float scrollY = (0.5f + 0.5698402909980532f * frame) % 1.0f;
        parameters.blueNoiseParams = new Vector4(1.0f / noiseTexture.width, 1.0f / noiseTexture.height, scrollX, scrollY);

        if (parameters.apvBaked)
        {
            parameters.bakedVolume = VolumetricFogAPVBaker.BakedVolume;
            parameters.bakedBoundsMin = VolumetricFogAPVBaker.BoundsMin;
            parameters.bakedInvSize = VolumetricFogAPVBaker.BoundsInvSize;
        }

        return parameters;
    }

    private void UpdateAdditionalLightMultipliers(UniversalLightData lightData)
    {
        for (int i = 0; i < additionalLightMultipliersWritten; ++i)
            additionalLightMultipliers[i].x = 1.0f;
        additionalLightMultipliersWritten = 0;

        if (!VolumetricFogLight.AnyActive)
            return;

        NativeArray<VisibleLight> visibleLights = lightData.visibleLights;
        for (int i = 0, lightIndex = 0; i < visibleLights.Length && lightIndex < additionalLightMultipliers.Length; ++i)
        {
            if (i == lightData.mainLightIndex)
                continue;

            if (VolumetricFogLight.TryGetMultiplier(visibleLights[i].light, out float multiplier))
            {
                additionalLightMultipliers[lightIndex].x = multiplier;
                additionalLightMultipliersWritten = lightIndex + 1;
            }

            ++lightIndex;
        }
    }

    private static void ApplyParameters(Material material, in FogParameters parameters)
    {
        CoreUtils.SetKeyword(material, "_APV_CONTRIBUTION_ENABLED", parameters.apvEnabled);
        CoreUtils.SetKeyword(material, "_APV_BAKED", parameters.apvBaked);
        CoreUtils.SetKeyword(material, "_MAIN_LIGHT_CONTRIBUTION_DISABLED", !parameters.mainLightEnabled);

        if (parameters.apvBaked)
        {
            material.SetTexture(BakedAPVFogVolumeId, parameters.bakedVolume);
            material.SetVector(BakedAPVVolumeBoundsMinId, parameters.bakedBoundsMin);
            material.SetVector(BakedAPVVolumeInvSizeId, parameters.bakedInvSize);
        }

        material.SetFloat(MainLightAnisotropyId, parameters.anisotropy);
        material.SetFloat(MainLightScatteringId, parameters.scattering);
        material.SetFloat(DistanceId, parameters.distance);
        material.SetFloat(BaseHeightId, parameters.baseHeight);
        material.SetFloat(MaximumHeightId, parameters.maximumHeight);
        material.SetFloat(GroundHeightId, parameters.groundHeight);
        material.SetFloat(DensityId, parameters.density);
        material.SetFloat(AbsortionId, parameters.absorption);
        material.SetFloat(APVContributionWeigthId, parameters.apvWeight);
        material.SetColor(TintId, parameters.tint);
        material.SetInteger(MaxStepsId, parameters.maxSteps);
        material.SetFloat(LTCGIScatteringId, parameters.ltcgiScattering);
        material.SetFloat(AnalyticOpticalDepthId, parameters.analyticOpticalDepth);
        material.SetFloat(SunTrimsId, parameters.sunTrims);
        material.SetTexture(BlueNoiseTextureId, parameters.blueNoise);
        material.SetVector(BlueNoiseParamsId, parameters.blueNoiseParams);
    }

    private static void ApplyFogParameters(ComputeCommandBuffer cmd, ComputeShader computeShader, int kernelIndex, in FogParameters parameters)
    {
        computeShader.SetTexture(kernelIndex, BakedAPVFogVolumeId, parameters.apvBaked && parameters.bakedVolume != null ? parameters.bakedVolume : CoreUtils.blackVolumeTexture);
        if (parameters.apvBaked)
        {
            cmd.SetComputeVectorParam(computeShader, BakedAPVVolumeBoundsMinId, parameters.bakedBoundsMin);
            cmd.SetComputeVectorParam(computeShader, BakedAPVVolumeInvSizeId, parameters.bakedInvSize);
        }

        cmd.SetComputeFloatParam(computeShader, MainLightAnisotropyId, parameters.anisotropy);
        cmd.SetComputeFloatParam(computeShader, MainLightScatteringId, parameters.scattering);
        cmd.SetComputeFloatParam(computeShader, DistanceId, parameters.distance);
        cmd.SetComputeFloatParam(computeShader, BaseHeightId, parameters.baseHeight);
        cmd.SetComputeFloatParam(computeShader, MaximumHeightId, parameters.maximumHeight);
        cmd.SetComputeFloatParam(computeShader, GroundHeightId, parameters.groundHeight);
        cmd.SetComputeFloatParam(computeShader, DensityId, parameters.density);
        cmd.SetComputeFloatParam(computeShader, AbsortionId, parameters.absorption);
        cmd.SetComputeFloatParam(computeShader, APVContributionWeigthId, parameters.apvWeight);
        cmd.SetComputeVectorParam(computeShader, TintId, parameters.tint);
        cmd.SetComputeVectorParam(computeShader, BlueNoiseParamsId, parameters.blueNoiseParams);
        cmd.SetComputeFloatParam(computeShader, AdditionalAnisotropyId, parameters.additionalAnisotropy);
        cmd.SetComputeFloatParam(computeShader, AdditionalScatteringId, parameters.additionalScattering);
        computeShader.SetTexture(kernelIndex, BlueNoiseTextureId, parameters.blueNoise);
    }

    private static void ApplyFroxelGridParameters(ComputeCommandBuffer cmd, ComputeShader computeShader, PassData data)
    {
        cmd.SetComputeVectorParam(computeShader, FroxelParamsId, data.froxelParams);
        cmd.SetComputeVectorParam(computeShader, FroxelGridSizeId, data.froxelGridSize);
        cmd.SetComputeVectorParam(computeShader, FroxelApplyParamsId, data.froxelApplyParams);
    }

    private static void SetRenderPassBlend(Material volumetricFogMaterial, bool compositeDirectly)
    {
        volumetricFogMaterial.SetFloat(SrcBlendId, (float)BlendMode.One);
        volumetricFogMaterial.SetFloat(DstBlendId, (float)(compositeDirectly ? BlendMode.SrcAlpha : BlendMode.Zero));
        volumetricFogMaterial.SetFloat(SrcBlendAlphaId, (float)(compositeDirectly ? BlendMode.Zero : BlendMode.One));
        volumetricFogMaterial.SetFloat(DstBlendAlphaId, (float)(compositeDirectly ? BlendMode.One : BlendMode.Zero));
    }

    private static RenderTextureDescriptor FogTargetDescriptor(UniversalCameraData cameraData, int downsampleFactor)
    {
        RenderTextureDescriptor descriptor = cameraData.cameraTargetDescriptor;
        descriptor.depthStencilFormat = GraphicsFormat.None;

        // The downsampled depth, fog and blur buffers are sampled in screen space (the unsafe blur pass
        // samples them as non-MSAA), so they must be single sample even though depth priming keeps MSAA on.
        descriptor.msaaSamples = 1;

        descriptor.width = Mathf.Max(1, descriptor.width / downsampleFactor);
        descriptor.height = Mathf.Max(1, descriptor.height / downsampleFactor);
        return descriptor;
    }

    private static TextureHandle CreateFogTexture(RenderGraph renderGraph, RenderTextureDescriptor descriptor, GraphicsFormat format, string name)
    {
        descriptor.graphicsFormat = format;
        return UniversalRenderer.CreateRenderGraphTexture(renderGraph, descriptor, name, false);
    }

    private static TextureHandle CreateFroxelTexture(RenderGraph renderGraph, Vector3Int size, string name)
    {
        TextureDesc desc = new TextureDesc(size.x, size.y)
        {
            slices = size.z,
            dimension = TextureDimension.Tex3D,
            format = GraphicsFormat.R16G16B16A16_SFloat,
            enableRandomWrite = true,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            msaaSamples = MSAASamples.None,
            useMipMap = false,
            name = name
        };
        return renderGraph.CreateTexture(desc);
    }

    private static TextureHandle CreateColumnTexture(RenderGraph renderGraph, Vector2Int size, GraphicsFormat format, string name)
    {
        TextureDesc desc = new TextureDesc(size.x, size.y)
        {
            slices = 1,
            dimension = TextureDimension.Tex2D,
            format = format,
            enableRandomWrite = true,
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            msaaSamples = MSAASamples.None,
            useMipMap = false,
            name = name
        };
        return renderGraph.CreateTexture(desc);
    }

    private VolumetricFogCameraHistory GetHistory(Camera camera, int view)
    {
        (Camera camera, int view) key = (camera, view);
        if (histories.TryGetValue(key, out VolumetricFogCameraHistory history))
            return history;

        if (histories.Count >= MaxCameraHistories)
            PruneHistories();

        history = new VolumetricFogCameraHistory();
        histories.Add(key, history);
        return history;
    }

    private void PruneHistories()
    {
        deadHistoryKeys.Clear();
        foreach (KeyValuePair<(Camera camera, int view), VolumetricFogCameraHistory> pair in histories)
        {
            if (pair.Key.camera == null)
                deadHistoryKeys.Add(pair.Key);
        }
        foreach ((Camera camera, int view) key in deadHistoryKeys)
        {
            histories[key].Release();
            histories.Remove(key);
        }
        deadHistoryKeys.Clear();
    }

    private static void UpdateCameraMatrices(VolumetricFogCameraHistory history, UniversalCameraData cameraData, int viewCount, bool sharedStereo)
    {
        Matrix4x4 sharedViewProjection = Matrix4x4.identity;
        Vector4 sharedPosition = Vector4.zero;
        if (sharedStereo)
            sharedViewProjection = SharedStereoViewProjection(cameraData, out sharedPosition);

        for (int i = 0; i < 2; ++i)
        {
            int viewIndex = Mathf.Min(i, viewCount - 1);
            history.previousViewProjection[i] = history.viewProjection[i];

            Matrix4x4 view = cameraData.GetViewMatrix(viewIndex);
            Matrix4x4 projection = GL.GetGPUProjectionMatrix(cameraData.GetProjectionMatrix(viewIndex), true);
            Matrix4x4 eyeViewProjection = projection * view;
            history.eyeViewProjection[i] = eyeViewProjection;

            Matrix4x4 viewProjection = sharedStereo ? sharedViewProjection : eyeViewProjection;
            history.viewProjection[i] = viewProjection;
            history.inverseViewProjection[i] = viewProjection.inverse;
            history.cameraPosition[i] = sharedStereo ? sharedPosition : (Vector4)view.inverse.GetColumn(3);
        }
    }

    // One frustum from the midpoint between the eyes that covers both eye frusta, so a single froxel volume
    // can serve both eyes. The margin keeps near geometry at the frustum edge inside the volume despite the
    // eye offset.
    private static Matrix4x4 SharedStereoViewProjection(UniversalCameraData cameraData, out Vector4 position)
    {
        Matrix4x4 view0 = cameraData.GetViewMatrix(0);
        Matrix4x4 view1 = cameraData.GetViewMatrix(1);
        Matrix4x4 projection0 = cameraData.GetProjectionMatrix(0);
        Matrix4x4 projection1 = cameraData.GetProjectionMatrix(1);

        Matrix4x4 cameraToWorld = view0.inverse;
        Vector3 position0 = cameraToWorld.GetColumn(3);
        Vector3 position1 = view1.inverse.GetColumn(3);
        Vector3 midpoint = (position0 + position1) * 0.5f;
        cameraToWorld.SetColumn(3, new Vector4(midpoint.x, midpoint.y, midpoint.z, 1.0f));
        Matrix4x4 centerView = cameraToWorld.inverse;

        float near = cameraData.camera.nearClipPlane;
        float far = cameraData.camera.farClipPlane;
        float margin = Vector3.Distance(position0, position1) * 0.5f / Mathf.Max(near, 0.5f);
        float tanLeft = Mathf.Max(TangentLeft(projection0), TangentLeft(projection1)) + margin;
        float tanRight = Mathf.Max(TangentRight(projection0), TangentRight(projection1)) + margin;
        float tanBottom = Mathf.Max(TangentBottom(projection0), TangentBottom(projection1)) + margin;
        float tanTop = Mathf.Max(TangentTop(projection0), TangentTop(projection1)) + margin;
        Matrix4x4 centerProjection = Matrix4x4.Frustum(-tanLeft * near, tanRight * near, -tanBottom * near, tanTop * near, near, far);

        position = new Vector4(midpoint.x, midpoint.y, midpoint.z, 1.0f);
        return GL.GetGPUProjectionMatrix(centerProjection, true) * centerView;
    }

    private static float TangentLeft(Matrix4x4 projection) => (1.0f - projection.m02) / projection.m00;
    private static float TangentRight(Matrix4x4 projection) => (1.0f + projection.m02) / projection.m00;
    private static float TangentBottom(Matrix4x4 projection) => (1.0f - projection.m12) / projection.m11;
    private static float TangentTop(Matrix4x4 projection) => (1.0f + projection.m12) / projection.m11;

    /// <summary>
    /// Executes the pass with the information from the pass data.
    /// </summary>
    /// <param name="passData"></param>
    /// <param name="context"></param>
    private static void ExecutePass(PassData passData, RasterGraphContext context)
    {
        Material material = passData.material;

        switch (passData.stage)
        {
            case PassStage.DownsampleDepth:
                material.SetInteger(DownsampleDepthFactorId, passData.downsampleFactor);
                break;
            case PassStage.VolumetricFogRender:
                material.SetTexture(DownsampledCameraDepthTextureId, passData.downsampledCameraDepthTarget);
                SetRenderPassBlend(material, passData.compositeDirectly);
                ApplyParameters(material, passData.parameters);
                break;
            case PassStage.VolumetricFogTemporal:
                material.SetTexture(DownsampledCameraDepthTextureId, passData.downsampledCameraDepthTarget);
                material.SetTexture(FogHistoryId, passData.fogHistory);
                material.SetTexture(DepthHistoryId, passData.depthHistory);
                material.SetMatrixArray(PrevViewProjId, passData.previousViewProjection);
                material.SetVector(TemporalParamsId, new Vector4(passData.historyValid, 0.0f, 0.0f, 0.0f));
                break;
            case PassStage.VolumetricFogFroxelApply:
                material.SetTexture(FroxelVolumeId, passData.froxelIntegrated);
                material.SetVector(FroxelParamsId, passData.froxelParams);
                material.SetVector(FroxelGridSizeId, passData.froxelGridSize);
                material.SetVector(FroxelApplyParamsId, passData.froxelApplyParams);
                material.SetMatrix(FroxelViewProjId, passData.froxelViewProjection);
                material.SetFloat(FroxelSharedId, passData.froxelShared);
                break;
        }

        Blitter.BlitTexture(context.cmd, passData.source, Vector2.one, material, passData.materialPassIndex);
    }

    private static void ExecuteFroxelColumnDepthPass(PassData data, ComputeGraphContext context)
    {
        ComputeCommandBuffer cmd = context.cmd;
        ComputeShader computeShader = data.computeShader;
        int kernelIndex = data.kernelIndex;

        ApplyFroxelGridParameters(cmd, computeShader, data);
        cmd.SetComputeVectorParam(computeShader, FroxelDepthParamsId, data.froxelDepthParams);
        cmd.SetComputeTextureParam(computeShader, kernelIndex, data.depthArray ? FroxelSceneDepthArrayId : FroxelSceneDepthId, data.sceneDepth);
        cmd.SetComputeTextureParam(computeShader, kernelIndex, FroxelColumnDepthId, data.froxelColumnDepth);

        cmd.DispatchCompute(computeShader, kernelIndex, Mathf.CeilToInt(data.depthGridSize.x / 8.0f), Mathf.CeilToInt(data.depthGridSize.y / 8.0f), 1);
    }

    private static void ExecuteFroxelColumnPass(PassData data, ComputeGraphContext context)
    {
        ComputeCommandBuffer cmd = context.cmd;
        ComputeShader computeShader = data.computeShader;
        int kernelIndex = data.kernelIndex;

        ApplyFogParameters(cmd, computeShader, kernelIndex, data.parameters);
        ApplyFroxelGridParameters(cmd, computeShader, data);
        cmd.SetComputeVectorParam(computeShader, FroxelDepthParamsId, data.froxelDepthParams);
        cmd.SetComputeVectorParam(computeShader, FroxelColumnParamsId, data.froxelColumnParams);
        cmd.SetComputeMatrixArrayParam(computeShader, FroxelInvViewProjId, data.inverseViewProjection);
        cmd.SetComputeMatrixArrayParam(computeShader, FroxelEyeViewProjId, data.eyeViewProjection);
        cmd.SetComputeVectorArrayParam(computeShader, FroxelCameraPosId, data.cameraPositions);
        if (data.froxelColumnDepth.IsValid())
            cmd.SetComputeTextureParam(computeShader, kernelIndex, FroxelColumnDepthSourceId, data.froxelColumnDepth);
        else
            computeShader.SetTexture(kernelIndex, FroxelColumnDepthSourceId, Texture2D.blackTexture);
        cmd.SetComputeTextureParam(computeShader, kernelIndex, FroxelColumnId, data.froxelColumn);
        cmd.SetComputeTextureParam(computeShader, kernelIndex, FroxelColumnLightId, data.froxelColumnLight);

        cmd.DispatchCompute(computeShader, kernelIndex, Mathf.CeilToInt(data.froxelTextureSize.x / 8.0f), Mathf.CeilToInt(data.froxelTextureSize.y / 8.0f), 1);
    }

    private static void ExecuteFroxelLightPass(PassData data, ComputeGraphContext context)
    {
        ComputeCommandBuffer cmd = context.cmd;
        ComputeShader computeShader = data.computeShader;
        int kernelIndex = data.kernelIndex;
        ApplyFogParameters(cmd, computeShader, kernelIndex, data.parameters);
        ApplyFroxelGridParameters(cmd, computeShader, data);
        cmd.SetComputeVectorParam(computeShader, FroxelTemporalId, data.froxelTemporal);
        cmd.SetComputeVectorParam(computeShader, FroxelTemporalParamsId, data.froxelTemporalParams);
        cmd.SetComputeVectorParam(computeShader, FroxelLightFlagsId, data.froxelLightFlags);
        if (data.froxelLightFlags.z > 0.5f)
            cmd.SetComputeVectorArrayParam(computeShader, AdditionalLightMultipliersId, data.additionalLightMultipliers);
        cmd.SetComputeMatrixArrayParam(computeShader, FroxelEyeViewProjId, data.eyeViewProjection);
        cmd.SetComputeMatrixArrayParam(computeShader, FroxelPrevViewProjId, data.previousViewProjection);
        cmd.SetComputeVectorArrayParam(computeShader, FroxelCameraPosId, data.cameraPositions);
        cmd.SetComputeTextureParam(computeShader, kernelIndex, FroxelColumnSourceId, data.froxelColumn);
        cmd.SetComputeTextureParam(computeShader, kernelIndex, FroxelColumnLightSourceId, data.froxelColumnLight);
        cmd.SetComputeTextureParam(computeShader, kernelIndex, FroxelLightingId, data.froxelLighting);
        if (data.temporal)
            cmd.SetComputeTextureParam(computeShader, kernelIndex, FroxelLightingHistoryId, data.froxelLightingHistory);
        else
            computeShader.SetTexture(kernelIndex, FroxelLightingHistoryId, CoreUtils.blackVolumeTexture);

        cmd.DispatchCompute(computeShader, kernelIndex, Mathf.CeilToInt(data.froxelTextureSize.x / 8.0f), Mathf.CeilToInt(data.froxelTextureSize.y / 8.0f), data.froxelTextureSize.z);
    }

    private static void ExecuteFroxelIntegratePass(PassData data, ComputeGraphContext context)
    {
        ComputeCommandBuffer cmd = context.cmd;
        ComputeShader computeShader = data.computeShader;
        int kernelIndex = data.kernelIndex;

        ApplyFroxelGridParameters(cmd, computeShader, data);
        cmd.SetComputeTextureParam(computeShader, kernelIndex, FroxelColumnSourceId, data.froxelColumn);
        cmd.SetComputeTextureParam(computeShader, kernelIndex, FroxelLightingSourceId, data.froxelLighting);
        cmd.SetComputeTextureParam(computeShader, kernelIndex, FroxelIntegratedId, data.froxelIntegrated);

        cmd.DispatchCompute(computeShader, kernelIndex, Mathf.CeilToInt(data.froxelTextureSize.x / 8.0f), Mathf.CeilToInt(data.froxelTextureSize.y / 8.0f), 1);
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
        foreach (KeyValuePair<(Camera camera, int view), VolumetricFogCameraHistory> pair in histories)
            pair.Value.Release();
        histories.Clear();
    }

    #endregion
}
