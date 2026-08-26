using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.UnifiedRayTracing;
using UnityEngine.Rendering.Universal;

namespace Basis.Rendering.RTAO
{
    public static class BasisRTAOShaderIds
    {
        public static readonly int ScreenSpaceOcclusionTexture = Shader.PropertyToID("_ScreenSpaceOcclusionTexture");
        public static readonly int AmbientOcclusionParam = Shader.PropertyToID("_AmbientOcclusionParam");
        public static readonly int Reference = Shader.PropertyToID("_BasisRtaoReference");
        public static readonly int FullSize = Shader.PropertyToID("_BasisRtaoFullSize");
        public static readonly int AOSize = Shader.PropertyToID("_BasisRtaoAOSize");
        public static readonly int Size = Shader.PropertyToID("_BasisRtaoSize");
        public static readonly int Scale = Shader.PropertyToID("_BasisRtaoScale");
        public static readonly int Trace = Shader.PropertyToID("_BasisRtaoTrace");
        public static readonly int Bias = Shader.PropertyToID("_BasisRtaoBias");
        public static readonly int RayCount = Shader.PropertyToID("_BasisRtaoRayCount");
        public static readonly int ViewCount = Shader.PropertyToID("_BasisRtaoViewCount");
        public static readonly int FrameIndex = Shader.PropertyToID("_BasisRtaoFrameIndex");
        public static readonly int StereoCoherent = Shader.PropertyToID("_BasisRtaoStereoCoherent");
        public static readonly int PositionTex = Shader.PropertyToID("_BasisRtaoPositionTex");
        public static readonly int NormalTex = Shader.PropertyToID("_BasisRtaoNormalTex");
        public static readonly int RawTex = Shader.PropertyToID("_BasisRtaoRawTex");
        public static readonly int ResultTex = Shader.PropertyToID("_BasisRtaoResultTex");
        public static readonly int HistoryTex = Shader.PropertyToID("_BasisRtaoHistoryTex");
        public static readonly int HistoryDepthTex = Shader.PropertyToID("_BasisRtaoHistoryDepthTex");
        public static readonly int TemporalOutTex = Shader.PropertyToID("_BasisRtaoTemporalOutTex");
        public static readonly int TemporalDepthOutTex = Shader.PropertyToID("_BasisRtaoTemporalDepthOutTex");
        public static readonly int BlurSourceTex = Shader.PropertyToID("_BasisRtaoBlurSourceTex");
        public static readonly int BlurTargetTex = Shader.PropertyToID("_BasisRtaoBlurTargetTex");
        public static readonly int AOTex = Shader.PropertyToID("_BasisRtaoAOTex");
        public static readonly int ResolvedTex = Shader.PropertyToID("_BasisRtaoResolvedTex");
        public static readonly int ResolvedAfterOpaqueTex = Shader.PropertyToID("_BasisRtaoResolvedAfterOpaqueTex");
        public static readonly int DebugStageTex = Shader.PropertyToID("_BasisRtaoDebugStageTex");
        public static readonly int DebugResolvedTex = Shader.PropertyToID("_BasisRtaoDebugResolvedTex");
        public static readonly int DebugFromStageArray = Shader.PropertyToID("_BasisRtaoDebugFromStageArray");
        public static readonly int DebugInterpretation = Shader.PropertyToID("_BasisRtaoDebugInterpretation");
        public static readonly int DebugStageScale = Shader.PropertyToID("_BasisRtaoDebugStageScale");
        public static readonly int ViewPlane = Shader.PropertyToID("_BasisRtaoViewPlane");
        public static readonly int PrevViewPlane = Shader.PropertyToID("_BasisRtaoPrevViewPlane");
        public static readonly int PrevViewProj = Shader.PropertyToID("_BasisRtaoPrevViewProj");
        public static readonly int TemporalParams = Shader.PropertyToID("_BasisRtaoTemporalParams");
        public static readonly int BlurParams = Shader.PropertyToID("_BasisRtaoBlurParams");
        public static readonly int BlurDirection = Shader.PropertyToID("_BasisRtaoBlurDirection");
        public static readonly int HasHistory = Shader.PropertyToID("_BasisRtaoHasHistory");
        public static readonly int Composite = Shader.PropertyToID("_BasisRtaoComposite");
        public static readonly int AccelStruct = Shader.PropertyToID("_BasisRtaoAccel");
        public static readonly int ScreenParams = Shader.PropertyToID("_BasisRtaoScreenParams");
        public const string AccelStructName = "_BasisRtaoAccel";
    }

    public sealed class BasisRTAOResolvedTexture : ContextItem
    {
        public TextureHandle handle;

        // The stages behind the composited result, kept so the debug view can show where an artifact starts
        // rather than only that it ended up in the picture. All but handle are at trace resolution.
        public TextureHandle raw, temporal, denoised, position, normal;
        public int scale = 1;

        public override void Reset()
        {
            handle = TextureHandle.nullHandle;
            raw = TextureHandle.nullHandle;
            temporal = TextureHandle.nullHandle;
            denoised = TextureHandle.nullHandle;
            position = TextureHandle.nullHandle;
            normal = TextureHandle.nullHandle;
            scale = 1;
        }
    }

    internal sealed class BasisRTAOPass : ScriptableRenderPass, IDisposable
    {
        private const int TemporalKernelGroup = 8;
        // A fixed tap count spread over more pixels only gets noisier, so the search is capped - but the cap
        // has to sit above what the world radius normally projects to, or it, not the slider, sets the range.
        private const float ScreenSpaceMaxRadiusPixels = 96f;

        // Half float. Full float was tried against a hard line artifact and made no difference, so the extra
        // 18 MB at half res stereo bought nothing; the line is not position precision.
        public const GraphicsFormat PositionFormat = GraphicsFormat.R16G16B16A16_SFloat;

        private static readonly GlobalKeyword OcclusionKeyword = GlobalKeyword.Create("_SCREEN_SPACE_OCCLUSION");

        private readonly BasisRTAOHistory history = new BasisRTAOHistory();
        private readonly Matrix4x4[] viewProjection = new Matrix4x4[2];
        private readonly Matrix4x4[] previousViewProjection = new Matrix4x4[2];
        private readonly Vector4[] viewPlane = new Vector4[2];
        private readonly Vector4[] previousViewPlane = new Vector4[2];

        private BasisRTAOResources resources;
        private BasisRTAOContext context;
        private BasisRTAOScene scene;
        private Material prepassMaterial, compositeMaterial;
        private MaterialPropertyBlock compositeBlock;
        private ComputeShader denoise;
        private int temporalKernel = -1, blurKernel = -1;
        private BasisRTAOSettings settings = BasisRTAOSettings.Default;
        private BasisRTAOSceneSettings sceneSettings = BasisRTAOSceneSettings.Default;
        private ComputeShader screenSpace;
        private int screenSpaceKernel = -1;
        private BasisRTAOTracingMode tracingMode = BasisRTAOTracingMode.Auto;
        private BasisRTAOApplyMode applyMode = BasisRTAOApplyMode.Lighting;
        private BasisRTAOBackend backend = BasisRTAOBackend.None;
        private bool debugView;
        private int frameIndex;
        private string failure;

        public string Failure => failure;
        public BasisRTAOBackend Backend => backend;
        public BasisRTAOScene Scene => scene;
        public BasisRTAOContext Context => context;
        public BasisRTAOHistory History => history;
        internal Material CompositeMaterial => compositeMaterial;

        public BasisRTAOPass()
        {
            profilingSampler = new ProfilingSampler("BasisRTAO");
            renderPassEvent = RenderPassEvent.AfterRenderingPrePasses + 1;
        }

        public void Setup(BasisRTAOResources rtaoResources, in BasisRTAOSettings rtaoSettings, in BasisRTAOSceneSettings rtaoSceneSettings, BasisRTAOTracingMode mode, BasisRTAOApplyMode apply, bool debug)
        {
            BasisRTAOBackend resolved = BasisRTAOTracing.Resolve(mode);
            if (!ReferenceEquals(resources, rtaoResources) || resolved != backend)
            {
                ReleaseGraphicsState();
                resources = rtaoResources;
            }

            settings = rtaoSettings.Validated();
            sceneSettings = rtaoSceneSettings;
            tracingMode = mode;
            applyMode = apply;
            backend = resolved;
            debugView = debug;

            ReportBackendOnce(resolved, mode);
        }

        private static BasisRTAOBackend lastReportedBackend = (BasisRTAOBackend)(-1);

        /// <summary>
        /// Says which backend is actually running, once, whenever it changes.
        ///
        /// Nothing used to report this, and the difference is not cosmetic: the screen space path reads the
        /// depth buffer rather than the acceleration structure, so the layer mask, BasisRTAOExclude and the
        /// shadow casting filter all silently stop applying. Occlusion from the whole scene then looks like a
        /// layer mask that is not working, rather than like a backend that never consults one.
        /// </summary>
        private static void ReportBackendOnce(BasisRTAOBackend resolved, BasisRTAOTracingMode mode)
        {
            if (resolved == lastReportedBackend)
                return;

            lastReportedBackend = resolved;

            switch (resolved)
            {
                case BasisRTAOBackend.Hardware:
                    Debug.Log("[BasisRTAO] Tracing on hardware ray tracing.");
                    break;
                case BasisRTAOBackend.ScreenSpace:
                    Debug.LogWarning(
                        $"[BasisRTAO] Tracing mode {mode} resolved to the screen space fallback" +
                        (mode == BasisRTAOTracingMode.Auto
                            ? " because this device reports no hardware ray tracing - on Windows that needs Direct3D12, and Direct3D11 has no ray tracing path at all."
                            : ".") +
                        " That path reads the depth buffer instead of the acceleration structure, so the layer" +
                        " mask, BasisRTAOExclude and the shadow casting filter do not apply and everything drawn" +
                        " occludes.");
                    break;
                case BasisRTAOBackend.ComputeBvh:
                    Debug.Log("[BasisRTAO] Tracing on the compute BVH backend.");
                    break;
                default:
                    Debug.LogWarning("[BasisRTAO] No usable backend; ambient occlusion is not running.");
                    break;
            }
        }

        public bool EnsureReady()
        {
            if (resources == null)
            {
                failure = "BasisRTAOResources asset is not assigned on the renderer feature.";
                return false;
            }

            if (backend == BasisRTAOBackend.None)
            {
                failure = "No RTAO backend is available on this device.";
                return false;
            }

            if (!resources.IsComplete(backend))
            {
                failure = $"BasisRTAOResources is missing: {resources.DescribeMissing(backend)}.";
                return false;
            }

            if (BasisRTAOTracing.IsRayTraced(backend))
            {
                if (context == null)
                {
                    context = BasisRTAOContext.Create(resources, backend, out string error);
                    if (context == null)
                    {
                        failure = error;
                        return false;
                    }
                }

                if (scene == null)
                    scene = new BasisRTAOScene(context);
            }
            else if (screenSpace == null)
            {
                screenSpace = resources.ScreenSpaceShader;
                screenSpaceKernel = screenSpace != null ? screenSpace.FindKernel("BasisRTAOScreenSpaceTrace") : -1;
                if (screenSpaceKernel < 0)
                {
                    failure = "The screen space fallback kernel failed to load.";
                    return false;
                }
            }

            if (prepassMaterial == null)
                prepassMaterial = CoreUtils.CreateEngineMaterial(resources.PrepassShader);
            if (compositeMaterial == null)
                compositeMaterial = CoreUtils.CreateEngineMaterial(resources.CompositeShader);
            if (denoise == null)
            {
                denoise = resources.DenoiseShader;
                temporalKernel = denoise.FindKernel("BasisRTAOTemporal");
                blurKernel = denoise.FindKernel("BasisRTAOBlur");
            }

            compositeBlock ??= new MaterialPropertyBlock();

            if (prepassMaterial == null || compositeMaterial == null || denoise == null || temporalKernel < 0 || blurKernel < 0)
            {
                failure = "RTAO shaders failed to load. Check that the package shaders compiled for this platform.";
                return false;
            }

            failure = null;
            return true;
        }

        public static int ViewCountOf(bool xrEnabled, bool singlePassEnabled)
        {
            return xrEnabled && singlePassEnabled ? 2 : 1;
        }

        public static int ViewCountOf(UniversalCameraData cameraData)
        {
            return ViewCountOf(cameraData.xr.enabled, cameraData.xr.singlePassEnabled);
        }

        public static Vector4 ViewPlaneOf(Matrix4x4 viewMatrix)
        {
            Vector3 forward = new Vector3(-viewMatrix.m20, -viewMatrix.m21, -viewMatrix.m22);
            return new Vector4(forward.x, forward.y, forward.z, -viewMatrix.m23);
        }

        public static Vector2Int TraceResolution(int fullWidth, int fullHeight, int divider)
        {
            int clamped = Mathf.Clamp(divider, 1, 4);
            return new Vector2Int(Mathf.Max(1, fullWidth / clamped), Mathf.Max(1, fullHeight / clamped));
        }

        private static RenderTextureDescriptor ArrayDescriptor(int width, int height, int viewCount, GraphicsFormat format, bool randomWrite)
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

        private class PrepassData
        {
            public Material material;
            public TextureHandle position, normal, depth;
        }

        private class TraceData
        {
            public BasisRTAOContext context;
            public BasisRTAOScene scene;
            public TextureHandle position, normal, result;
            public Vector4 reference, trace, bias, size;
            public int rayCount, viewCount, frameIndex, stereoCoherent;
            public int width, height;
        }

        private class ScreenSpaceData
        {
            public ComputeShader shader;
            public int kernel;
            public TextureHandle position, normal, result;
            public Vector4[] viewPlane;
            public Vector4 reference, trace, bias, size, screenParams;
            public int rayCount, viewCount, frameIndex, stereoCoherent, width, height;
        }

        private class TemporalData
        {
            public ComputeShader shader;
            public int kernel;
            public TextureHandle position, normal, raw, historyIn, historyDepthIn, historyOut, historyDepthOut;
            public Matrix4x4[] previousViewProjection;
            public Vector4[] viewPlane, previousViewPlane;
            public Vector4 reference, size, temporalParams;
            public int viewCount, hasHistory, width, height;
        }

        private class BlurData
        {
            public ComputeShader shader;
            public int kernel;
            public TextureHandle position, source, target;
            public Vector4[] viewPlane;
            public Vector4 reference, size, blurParams, direction, temporalParams;
            public int viewCount, width, height;
        }

        private class CompositeData
        {
            public Material material;
            public MaterialPropertyBlock block;
            public TextureHandle ao, position, depth;
        }

        private class GlobalData
        {
            public TextureHandle resolved;
            public float directLightingStrength;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();

            if (cameraData.cameraType != CameraType.Game)
                return;
            if (!BasisRTAOFeature.AcceptsCamera(cameraData.camera))
                return;
            if (!EnsureReady())
                return;
            if (!resourceData.cameraDepthTexture.IsValid())
                return;

            RenderTextureDescriptor cameraDescriptor = cameraData.cameraTargetDescriptor;
            int fullWidth = cameraDescriptor.width, fullHeight = cameraDescriptor.height;
            if (fullWidth <= 0 || fullHeight <= 0)
                return;

            int viewCount = ViewCountOf(cameraData);
            Vector2Int traceSize = TraceResolution(fullWidth, fullHeight, settings.resolutionDivider);
            int scale = Mathf.Clamp(settings.resolutionDivider, 1, 4);

            Camera camera = cameraData.camera;
            Vector3 reference = camera.transform.position;

            bool rayTraced = BasisRTAOTracing.IsRayTraced(backend);
            if (rayTraced)
            {
                Vector3 viewer = BasisRTAOFeature.ViewerPosition != null ? BasisRTAOFeature.ViewerPosition() : reference;
                scene.Refresh(sceneSettings, viewer, Time.unscaledTime, Time.frameCount);
                if (!scene.HasGeometry)
                    return;
            }

            for (int eye = 0; eye < 2; eye++)
            {
                int sourceEye = eye < viewCount ? eye : 0;
                Matrix4x4 view = cameraData.GetViewMatrix(sourceEye);
                viewProjection[eye] = cameraData.GetProjectionMatrix(sourceEye) * view;
                viewPlane[eye] = ViewPlaneOf(view);
            }

            BasisRTAOHistory.Entry historyEntry = history.Get(camera, traceSize.x, traceSize.y, viewCount, Time.frameCount);
            history.Evict(Time.frameCount);

            previousViewProjection[0] = historyEntry.previousViewProjection[0];
            previousViewProjection[1] = historyEntry.previousViewProjection[1];
            previousViewPlane[0] = historyEntry.previousViewPlane[0];
            previousViewPlane[1] = historyEntry.previousViewPlane[1];
            bool hasHistory = historyEntry.framesRendered > 0;

            Vector4 referenceVector = new Vector4(reference.x, reference.y, reference.z, 0f);
            Vector4 fullSize = new Vector4(fullWidth, fullHeight, 1f / fullWidth, 1f / fullHeight);
            Vector4 traceSizeVector = new Vector4(traceSize.x, traceSize.y, 1f / traceSize.x, 1f / traceSize.y);

            TextureHandle positionTexture = UniversalRenderer.CreateRenderGraphTexture(renderGraph,
                ArrayDescriptor(traceSize.x, traceSize.y, viewCount, PositionFormat, false), "_BasisRtaoPosition", false);
            TextureHandle normalTexture = UniversalRenderer.CreateRenderGraphTexture(renderGraph,
                ArrayDescriptor(traceSize.x, traceSize.y, viewCount, GraphicsFormat.R16G16_SFloat, false), "_BasisRtaoNormal", false);
            TextureHandle rawTexture = UniversalRenderer.CreateRenderGraphTexture(renderGraph,
                ArrayDescriptor(traceSize.x, traceSize.y, viewCount, GraphicsFormat.R16G16_SFloat, true), "_BasisRtaoRaw", false);

            TextureHandle historyIn = renderGraph.ImportTexture(historyEntry.PreviousVisibility);
            TextureHandle historyDepthIn = renderGraph.ImportTexture(historyEntry.PreviousDepth);
            TextureHandle historyOut = renderGraph.ImportTexture(historyEntry.CurrentVisibility);
            TextureHandle historyDepthOut = renderGraph.ImportTexture(historyEntry.CurrentDepth);

            RecordPrepass(renderGraph, resourceData, positionTexture, normalTexture, referenceVector, fullSize, scale);
            if (rayTraced)
            {
                RecordTrace(renderGraph, positionTexture, normalTexture, rawTexture, referenceVector, traceSizeVector, traceSize, viewCount);
            }
            else
            {
                float projectionScale = BasisRTAOTracing.ProjectionScale(cameraData.GetProjectionMatrix(0), traceSize.y, camera.orthographic);
                RecordScreenSpace(renderGraph, positionTexture, normalTexture, rawTexture, referenceVector, traceSizeVector, traceSize, viewCount, projectionScale);
            }
            RecordTemporal(renderGraph, positionTexture, normalTexture, rawTexture, historyIn, historyDepthIn, historyOut, historyDepthOut,
                referenceVector, traceSizeVector, traceSize, viewCount, hasHistory);

            TextureHandle denoised = historyOut;
            int denoisePasses = settings.blurMaxRadius > 0 ? settings.denoisePasses : 0;
            if (denoisePasses > 0)
            {
                // Fixed roles, no ping-pong: the horizontal half always lands in the scratch and the vertical
                // half always lands in the result. Rotating the two made a later pass read and write the same
                // texture inside one compute dispatch, so its threads sampled neighbours that had already been
                // overwritten. A single pass never showed it, which is why only High and Maximum broke.
                TextureHandle scratch = UniversalRenderer.CreateRenderGraphTexture(renderGraph,
                    ArrayDescriptor(traceSize.x, traceSize.y, viewCount, GraphicsFormat.R16G16B16A16_SFloat, true), "_BasisRtaoDenoiseScratch", false);
                TextureHandle result = UniversalRenderer.CreateRenderGraphTexture(renderGraph,
                    ArrayDescriptor(traceSize.x, traceSize.y, viewCount, GraphicsFormat.R16G16B16A16_SFloat, true), "_BasisRtaoDenoiseResult", false);

                TextureHandle source = historyOut;
                for (int pass = 0; pass < denoisePasses; pass++)
                {
                    int stride = 1 << pass;
                    RecordBlur(renderGraph, positionTexture, source, scratch, new Vector4(1f, 0f, stride, 0f),
                        referenceVector, traceSizeVector, traceSize, viewCount, pass);
                    RecordBlur(renderGraph, positionTexture, scratch, result, new Vector4(0f, 1f, stride, 0f),
                        referenceVector, traceSizeVector, traceSize, viewCount, pass);
                    source = result;
                }
                denoised = source;
            }

            RenderTextureDescriptor resolvedDescriptor = cameraDescriptor;
            resolvedDescriptor.colorFormat = RenderTextureFormat.R8;
            resolvedDescriptor.depthStencilFormat = GraphicsFormat.None;
            resolvedDescriptor.msaaSamples = 1;
            resolvedDescriptor.useMipMap = false;
            resolvedDescriptor.autoGenerateMips = false;
            TextureHandle resolved = UniversalRenderer.CreateRenderGraphTexture(renderGraph, resolvedDescriptor, "_ScreenSpaceOcclusionTexture", false, FilterMode.Bilinear);

            RecordComposite(renderGraph, resourceData, denoised, positionTexture, resolved, referenceVector, fullSize,
                new Vector4(traceSize.x, traceSize.y, 1f / traceSize.x, 1f / traceSize.y), scale);
            if (applyMode == BasisRTAOApplyMode.Lighting)
                RecordGlobal(renderGraph, resolved);

            BasisRTAOResolvedTexture published = frameData.GetOrCreate<BasisRTAOResolvedTexture>();
            published.handle = resolved;
            published.raw = rawTexture;
            published.temporal = historyOut;
            published.denoised = denoised;
            published.position = positionTexture;
            published.normal = normalTexture;
            published.scale = scale;

            historyEntry.previousViewProjection[0] = viewProjection[0];
            historyEntry.previousViewProjection[1] = viewProjection[1];
            historyEntry.previousViewPlane[0] = viewPlane[0];
            historyEntry.previousViewPlane[1] = viewPlane[1];
            historyEntry.framesRendered++;
            historyEntry.Swap();
            frameIndex++;
        }

        private void RecordPrepass(RenderGraph renderGraph, UniversalResourceData resourceData, TextureHandle position, TextureHandle normal,
            Vector4 reference, Vector4 fullSize, int scale)
        {
            using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass<PrepassData>("BasisRTAO Prepass", out PrepassData data, profilingSampler))
            {
                prepassMaterial.SetVector(BasisRTAOShaderIds.Reference, reference);
                prepassMaterial.SetVector(BasisRTAOShaderIds.FullSize, fullSize);
                prepassMaterial.SetInteger(BasisRTAOShaderIds.Scale, scale);

                data.material = prepassMaterial;
                data.position = position;
                data.normal = normal;
                data.depth = resourceData.cameraDepthTexture;

                builder.SetRenderAttachment(position, 0, AccessFlags.WriteAll);
                builder.SetRenderAttachment(normal, 1, AccessFlags.WriteAll);
                builder.UseTexture(data.depth, AccessFlags.Read);
                builder.AllowPassCulling(false);

                builder.SetRenderFunc(static (PrepassData data, RasterGraphContext ctx) =>
                {
                    CoreUtils.DrawFullScreen(ctx.cmd, data.material, null, 0);
                });
            }
        }

        private void RecordTrace(RenderGraph renderGraph, TextureHandle position, TextureHandle normal, TextureHandle result,
            Vector4 reference, Vector4 size, Vector2Int traceSize, int viewCount)
        {
            using (IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass<TraceData>("BasisRTAO Trace", out TraceData data, profilingSampler))
            {
                data.context = context;
                data.scene = scene;
                data.position = position;
                data.normal = normal;
                data.result = result;
                data.reference = reference;
                data.trace = new Vector4(settings.raysPerPixel, settings.radius, settings.distanceFalloff, 0f);
                data.bias = new Vector4(settings.normalBias, settings.distanceBias, settings.noiseCellSize, 0f);
                data.size = size;
                data.rayCount = settings.raysPerPixel;
                data.viewCount = viewCount;
                data.frameIndex = frameIndex;
                data.stereoCoherent = settings.stereoCoherentNoise ? 1 : 0;
                data.width = traceSize.x;
                data.height = traceSize.y;

                builder.UseTexture(position, AccessFlags.Read);
                builder.UseTexture(normal, AccessFlags.Read);
                builder.UseTexture(result, AccessFlags.ReadWrite);
                builder.AllowPassCulling(false);

                builder.SetRenderFunc(static (TraceData data, UnsafeGraphContext ctx) =>
                {
                    CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd);
                    if (data.scene.NeedsBuild)
                        data.scene.Build(cmd);

                    IRayTracingShader shader = data.context.TraceShader;
                    shader.SetAccelerationStructure(cmd, BasisRTAOShaderIds.AccelStructName, data.scene.AccelerationStructure);
                    shader.SetTextureParam(cmd, BasisRTAOShaderIds.PositionTex, data.position);
                    shader.SetTextureParam(cmd, BasisRTAOShaderIds.NormalTex, data.normal);
                    shader.SetTextureParam(cmd, BasisRTAOShaderIds.ResultTex, data.result);
                    shader.SetVectorParam(cmd, BasisRTAOShaderIds.Reference, data.reference);
                    shader.SetVectorParam(cmd, BasisRTAOShaderIds.Trace, data.trace);
                    shader.SetVectorParam(cmd, BasisRTAOShaderIds.Bias, data.bias);
                    shader.SetVectorParam(cmd, BasisRTAOShaderIds.Size, data.size);
                    shader.SetIntParam(cmd, BasisRTAOShaderIds.RayCount, data.rayCount);
                    shader.SetIntParam(cmd, BasisRTAOShaderIds.ViewCount, data.viewCount);
                    shader.SetIntParam(cmd, BasisRTAOShaderIds.FrameIndex, data.frameIndex);
                    shader.SetIntParam(cmd, BasisRTAOShaderIds.StereoCoherent, data.stereoCoherent);

                    GraphicsBuffer scratch = data.context.GetTraceScratch(data.width, data.height, data.viewCount);
                    shader.Dispatch(cmd, scratch, (uint)data.width, (uint)data.height, (uint)data.viewCount);
                });
            }
        }

        private void RecordScreenSpace(RenderGraph renderGraph, TextureHandle position, TextureHandle normal, TextureHandle result,
            Vector4 reference, Vector4 size, Vector2Int traceSize, int viewCount, float projectionScale)
        {
            using (IComputeRenderGraphBuilder builder = renderGraph.AddComputePass<ScreenSpaceData>("BasisRTAO Screen Space", out ScreenSpaceData data, profilingSampler))
            {
                data.shader = screenSpace;
                data.kernel = screenSpaceKernel;
                data.position = position;
                data.normal = normal;
                data.result = result;
                data.viewPlane = viewPlane;
                data.reference = reference;
                data.trace = new Vector4(settings.raysPerPixel, settings.radius, settings.distanceFalloff, 0f);
                data.bias = new Vector4(settings.normalBias, settings.distanceBias, settings.noiseCellSize, 0f);
                data.size = size;
                data.screenParams = new Vector4(projectionScale, ScreenSpaceMaxRadiusPixels, 2f, 0f);
                data.rayCount = Mathf.Max(4, settings.raysPerPixel * 4);
                data.viewCount = viewCount;
                data.frameIndex = frameIndex;
                data.stereoCoherent = settings.stereoCoherentNoise ? 1 : 0;
                data.width = traceSize.x;
                data.height = traceSize.y;

                builder.UseTexture(position, AccessFlags.Read);
                builder.UseTexture(normal, AccessFlags.Read);
                builder.UseTexture(result, AccessFlags.Write);
                builder.AllowPassCulling(false);

                builder.SetRenderFunc(static (ScreenSpaceData data, ComputeGraphContext ctx) =>
                {
                    ComputeCommandBuffer cmd = ctx.cmd;
                    cmd.SetComputeTextureParam(data.shader, data.kernel, BasisRTAOShaderIds.PositionTex, data.position);
                    cmd.SetComputeTextureParam(data.shader, data.kernel, BasisRTAOShaderIds.NormalTex, data.normal);
                    cmd.SetComputeTextureParam(data.shader, data.kernel, BasisRTAOShaderIds.ResultTex, data.result);
                    cmd.SetComputeVectorArrayParam(data.shader, BasisRTAOShaderIds.ViewPlane, data.viewPlane);
                    cmd.SetComputeVectorParam(data.shader, BasisRTAOShaderIds.Reference, data.reference);
                    cmd.SetComputeVectorParam(data.shader, BasisRTAOShaderIds.Trace, data.trace);
                    cmd.SetComputeVectorParam(data.shader, BasisRTAOShaderIds.Bias, data.bias);
                    cmd.SetComputeVectorParam(data.shader, BasisRTAOShaderIds.Size, data.size);
                    cmd.SetComputeVectorParam(data.shader, BasisRTAOShaderIds.ScreenParams, data.screenParams);
                    cmd.SetComputeIntParam(data.shader, BasisRTAOShaderIds.RayCount, data.rayCount);
                    cmd.SetComputeIntParam(data.shader, BasisRTAOShaderIds.ViewCount, data.viewCount);
                    cmd.SetComputeIntParam(data.shader, BasisRTAOShaderIds.FrameIndex, data.frameIndex);
                    cmd.SetComputeIntParam(data.shader, BasisRTAOShaderIds.StereoCoherent, data.stereoCoherent);
                    cmd.DispatchCompute(data.shader, data.kernel,
                        CoreUtils.DivRoundUp(data.width, TemporalKernelGroup),
                        CoreUtils.DivRoundUp(data.height, TemporalKernelGroup),
                        data.viewCount);
                });
            }
        }

        private void RecordTemporal(RenderGraph renderGraph, TextureHandle position, TextureHandle normal, TextureHandle raw,
            TextureHandle historyIn, TextureHandle historyDepthIn, TextureHandle historyOut, TextureHandle historyDepthOut,
            Vector4 reference, Vector4 size, Vector2Int traceSize, int viewCount, bool hasHistory)
        {
            using (IComputeRenderGraphBuilder builder = renderGraph.AddComputePass<TemporalData>("BasisRTAO Temporal", out TemporalData data, profilingSampler))
            {
                data.shader = denoise;
                data.kernel = temporalKernel;
                data.position = position;
                data.normal = normal;
                data.raw = raw;
                data.historyIn = historyIn;
                data.historyDepthIn = historyDepthIn;
                data.historyOut = historyOut;
                data.historyDepthOut = historyDepthOut;
                data.previousViewProjection = previousViewProjection;
                data.viewPlane = viewPlane;
                data.previousViewPlane = previousViewPlane;
                data.reference = reference;
                data.size = size;
                data.temporalParams = new Vector4(settings.temporalFrames, settings.temporalMinAlpha, settings.temporalDepthTolerance, settings.temporalNormalTolerance);
                data.viewCount = viewCount;
                data.hasHistory = hasHistory ? 1 : 0;
                data.width = traceSize.x;
                data.height = traceSize.y;

                builder.UseTexture(position, AccessFlags.Read);
                builder.UseTexture(normal, AccessFlags.Read);
                builder.UseTexture(raw, AccessFlags.Read);
                builder.UseTexture(historyIn, AccessFlags.Read);
                builder.UseTexture(historyDepthIn, AccessFlags.Read);
                builder.UseTexture(historyOut, AccessFlags.Write);
                builder.UseTexture(historyDepthOut, AccessFlags.Write);
                builder.AllowPassCulling(false);

                builder.SetRenderFunc(static (TemporalData data, ComputeGraphContext ctx) =>
                {
                    ComputeCommandBuffer cmd = ctx.cmd;
                    cmd.SetComputeTextureParam(data.shader, data.kernel, BasisRTAOShaderIds.PositionTex, data.position);
                    cmd.SetComputeTextureParam(data.shader, data.kernel, BasisRTAOShaderIds.NormalTex, data.normal);
                    cmd.SetComputeTextureParam(data.shader, data.kernel, BasisRTAOShaderIds.RawTex, data.raw);
                    cmd.SetComputeTextureParam(data.shader, data.kernel, BasisRTAOShaderIds.HistoryTex, data.historyIn);
                    cmd.SetComputeTextureParam(data.shader, data.kernel, BasisRTAOShaderIds.HistoryDepthTex, data.historyDepthIn);
                    cmd.SetComputeTextureParam(data.shader, data.kernel, BasisRTAOShaderIds.TemporalOutTex, data.historyOut);
                    cmd.SetComputeTextureParam(data.shader, data.kernel, BasisRTAOShaderIds.TemporalDepthOutTex, data.historyDepthOut);
                    cmd.SetComputeMatrixArrayParam(data.shader, BasisRTAOShaderIds.PrevViewProj, data.previousViewProjection);
                    cmd.SetComputeVectorArrayParam(data.shader, BasisRTAOShaderIds.ViewPlane, data.viewPlane);
                    cmd.SetComputeVectorArrayParam(data.shader, BasisRTAOShaderIds.PrevViewPlane, data.previousViewPlane);
                    cmd.SetComputeVectorParam(data.shader, BasisRTAOShaderIds.Reference, data.reference);
                    cmd.SetComputeVectorParam(data.shader, BasisRTAOShaderIds.Size, data.size);
                    cmd.SetComputeVectorParam(data.shader, BasisRTAOShaderIds.TemporalParams, data.temporalParams);
                    cmd.SetComputeIntParam(data.shader, BasisRTAOShaderIds.ViewCount, data.viewCount);
                    cmd.SetComputeIntParam(data.shader, BasisRTAOShaderIds.HasHistory, data.hasHistory);
                    cmd.DispatchCompute(data.shader, data.kernel,
                        CoreUtils.DivRoundUp(data.width, TemporalKernelGroup),
                        CoreUtils.DivRoundUp(data.height, TemporalKernelGroup),
                        data.viewCount);
                });
            }
        }

        private void RecordBlur(RenderGraph renderGraph, TextureHandle position, TextureHandle source, TextureHandle target,
            Vector4 direction, Vector4 reference, Vector4 size, Vector2Int traceSize, int viewCount, int iteration = 0)
        {
            string passName = direction.x > 0f
                ? $"BasisRTAO Denoise {iteration} Horizontal"
                : $"BasisRTAO Denoise {iteration} Vertical";
            using (IComputeRenderGraphBuilder builder = renderGraph.AddComputePass<BlurData>(passName, out BlurData data, profilingSampler))
            {
                data.shader = denoise;
                data.kernel = blurKernel;
                data.position = position;
                data.source = source;
                data.target = target;
                data.viewPlane = viewPlane;
                data.reference = reference;
                data.size = size;
                data.blurParams = new Vector4(settings.blurMaxRadius, settings.blurMinRadius, settings.blurDepthSigma, settings.blurNormalPower);
                data.temporalParams = new Vector4(settings.temporalFrames, settings.temporalMinAlpha, settings.temporalDepthTolerance, settings.temporalNormalTolerance);
                data.direction = direction;
                data.viewCount = viewCount;
                data.width = traceSize.x;
                data.height = traceSize.y;

                builder.UseTexture(position, AccessFlags.Read);
                builder.UseTexture(source, AccessFlags.Read);
                builder.UseTexture(target, AccessFlags.Write);
                builder.AllowPassCulling(false);

                builder.SetRenderFunc(static (BlurData data, ComputeGraphContext ctx) =>
                {
                    ComputeCommandBuffer cmd = ctx.cmd;
                    cmd.SetComputeTextureParam(data.shader, data.kernel, BasisRTAOShaderIds.PositionTex, data.position);
                    cmd.SetComputeTextureParam(data.shader, data.kernel, BasisRTAOShaderIds.BlurSourceTex, data.source);
                    cmd.SetComputeTextureParam(data.shader, data.kernel, BasisRTAOShaderIds.BlurTargetTex, data.target);
                    cmd.SetComputeVectorArrayParam(data.shader, BasisRTAOShaderIds.ViewPlane, data.viewPlane);
                    cmd.SetComputeVectorParam(data.shader, BasisRTAOShaderIds.Reference, data.reference);
                    cmd.SetComputeVectorParam(data.shader, BasisRTAOShaderIds.Size, data.size);
                    cmd.SetComputeVectorParam(data.shader, BasisRTAOShaderIds.BlurParams, data.blurParams);
                    cmd.SetComputeVectorParam(data.shader, BasisRTAOShaderIds.TemporalParams, data.temporalParams);
                    cmd.SetComputeVectorParam(data.shader, BasisRTAOShaderIds.BlurDirection, data.direction);
                    cmd.SetComputeIntParam(data.shader, BasisRTAOShaderIds.ViewCount, data.viewCount);
                    cmd.DispatchCompute(data.shader, data.kernel,
                        CoreUtils.DivRoundUp(data.width, TemporalKernelGroup),
                        CoreUtils.DivRoundUp(data.height, TemporalKernelGroup),
                        data.viewCount);
                });
            }
        }

        private void RecordComposite(RenderGraph renderGraph, UniversalResourceData resourceData, TextureHandle ao, TextureHandle position,
            TextureHandle target, Vector4 reference, Vector4 fullSize, Vector4 aoSize, int scale)
        {
            using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass<CompositeData>("BasisRTAO Composite", out CompositeData data, profilingSampler))
            {
                compositeMaterial.SetVector(BasisRTAOShaderIds.Reference, reference);
                compositeMaterial.SetVector(BasisRTAOShaderIds.FullSize, fullSize);
                compositeMaterial.SetVector(BasisRTAOShaderIds.AOSize, aoSize);
                compositeMaterial.SetVector(BasisRTAOShaderIds.Composite,
                    new Vector4(settings.intensity, settings.power, settings.fadeStart, settings.fadeEnd));
                compositeMaterial.SetInteger(BasisRTAOShaderIds.Scale, scale);

                data.material = compositeMaterial;
                data.block = compositeBlock;
                data.ao = ao;
                data.position = position;
                data.depth = resourceData.cameraDepthTexture;

                builder.SetRenderAttachment(target, 0, AccessFlags.WriteAll);
                builder.UseTexture(ao, AccessFlags.Read);
                builder.UseTexture(position, AccessFlags.Read);
                builder.UseTexture(data.depth, AccessFlags.Read);
                builder.AllowPassCulling(false);

                builder.SetRenderFunc(static (CompositeData data, RasterGraphContext ctx) =>
                {
                    data.block.Clear();
                    data.block.SetTexture(BasisRTAOShaderIds.AOTex, data.ao);
                    data.block.SetTexture(BasisRTAOShaderIds.PositionTex, data.position);
                    CoreUtils.DrawFullScreen(ctx.cmd, data.material, data.block, 0);
                });
            }
        }

        private void RecordGlobal(RenderGraph renderGraph, TextureHandle resolved)
        {
            using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass<GlobalData>("BasisRTAO Bind", out GlobalData data, profilingSampler))
            {
                data.resolved = resolved;
                data.directLightingStrength = settings.directLightingStrength;

                builder.AllowGlobalStateModification(true);
                builder.UseTexture(resolved, AccessFlags.Read);
                builder.SetGlobalTextureAfterPass(resolved, BasisRTAOShaderIds.ScreenSpaceOcclusionTexture);
                builder.AllowPassCulling(false);

                builder.SetRenderFunc(static (GlobalData data, RasterGraphContext ctx) =>
                {
                    ctx.cmd.SetKeyword(OcclusionKeyword, true);
                    ctx.cmd.SetGlobalVector(BasisRTAOShaderIds.AmbientOcclusionParam, new Vector4(1f, 0f, 0f, data.directLightingStrength));
                });
            }
        }

        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            cmd?.SetKeyword(OcclusionKeyword, false);
        }

        public bool DebugView => debugView;

        private void ReleaseGraphicsState()
        {
            scene?.Dispose();
            scene = null;
            context?.Dispose();
            context = null;
            CoreUtils.Destroy(prepassMaterial);
            prepassMaterial = null;
            CoreUtils.Destroy(compositeMaterial);
            compositeMaterial = null;
            denoise = null;
            temporalKernel = -1;
            blurKernel = -1;
            screenSpace = null;
            screenSpaceKernel = -1;
        }

        public void Dispose()
        {
            ReleaseGraphicsState();
            history.Dispose();
        }
    }
}
