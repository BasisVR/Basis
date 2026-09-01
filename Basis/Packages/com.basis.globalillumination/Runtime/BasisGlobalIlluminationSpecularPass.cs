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
    internal static readonly int idSpecularPriorColor = Shader.PropertyToID("_BasisGISpecularPriorColor");
    internal static readonly int idSSRParams = Shader.PropertyToID("_BasisGISSRParams");
    internal static readonly int idSpecHitDistance = Shader.PropertyToID("_BasisGISpecHitDistance");
    internal static readonly int idSpecHitDistanceValid = Shader.PropertyToID("_BasisGISpecHitDistanceValid");
    /// <summary>The screen space reflection trace, appended to the ray stages shader - see that pass block.</summary>
    public const int PassSSRTrace = 2;
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

        public static float GpuMs =>
            samplerPrepass.gpuElapsedTime + samplerTrace.gpuElapsedTime + samplerResolve.gpuElapsedTime +
            samplerTemporal.gpuElapsedTime + samplerBlur.gpuElapsedTime + samplerUpsample.gpuElapsedTime +
            samplerPublish.gpuElapsedTime;

        public static void SetProfilingEnabled(bool enabled)
        {
            samplerPrepass.enableRecording = enabled;
            samplerTrace.enableRecording = enabled;
            samplerResolve.enableRecording = enabled;
            samplerTemporal.enableRecording = enabled;
            samplerBlur.enableRecording = enabled;
            samplerUpsample.enableRecording = enabled;
            samplerPublish.enableRecording = enabled;
        }

        public static float GpuMsPrepass => samplerPrepass.gpuElapsedTime;
        public static float GpuMsTrace => samplerTrace.gpuElapsedTime;
        public static float GpuMsResolve => samplerResolve.gpuElapsedTime;
        public static float GpuMsTemporal => samplerTemporal.gpuElapsedTime;
        public static float GpuMsBlur => samplerBlur.gpuElapsedTime;
        public static float GpuMsUpsample => samplerUpsample.gpuElapsedTime;
        public static float GpuMsPublish => samplerPublish.gpuElapsedTime;

        private static int invocationFrame = -1, invocationCount;
        public static int InvocationsThisFrame => invocationCount;

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
        private BasisGlobalIlluminationPass owner;
        /// <summary>
        /// Whether the renderer is producing the normals texture this frame, set by the feature the same
        /// way it is for the diffuse pass. The mirror direction is only as good as the normal it reflects
        /// about: reconstruction from depth gives the flat geometric normal, and a normal mapped or
        /// smooth-shaded surface seen off angle reflects visibly wrong with it. When the prepass runs
        /// anyway, the trace reads the real per pixel normal instead, for free.
        /// </summary>
        public bool UseNormalsTexture;

        public SpecularPass()
        {
            profilingSampler = new ProfilingSampler("Basis GI Specular");
            // After the depth prepass, before the opaque draws that consume the result. Both Basis renderers
            // force depth priming, so _CameraDepthTexture is populated by the time this runs.
            renderPassEvent = RenderPassEvent.AfterRenderingPrePasses + 1;
        }

        public void Setup(Material compositeMaterial, Material stagesMaterial, RayTracingShader hardwareShader,
            ComputeShader computeShader, bool computeFallback, bool available, BasisGlobalIlluminationPass diffusePass)
        {
            material = compositeMaterial;
            rayStagesMaterial = stagesMaterial;
            rayTraceShader = hardwareShader;
            rayTraceCompute = computeShader;
            rayComputeFallback = computeFallback;
            rayTracingAvailable = available;
            // The diffuse pass owns the depth pyramid recording and the screen space backend borrows it,
            // rather than carrying a copy of fifty lines whose subtleties - the unbiased representative,
            // the min/max fold - have each been paid for once already.
            owner = diffusePass;
        }

        /// <summary>
        /// Which backend the reflections run on. The volume's Mode decides, exactly as it does for the
        /// diffuse gather: Ray Traced reflects what is off screen too, Screen Space walks the depth buffer
        /// and runs on any GPU - which matters, because Screen Space is the default shipping mode and a GPU
        /// with no ray tracing used to mean no reflections at all rather than cheaper ones.
        /// </summary>
        public static bool ScreenSpaceReflections(BasisGlobalIlluminationSettings settings, bool rayTracingAvailable)
        {
            return !settings.IsRayTraced() || !rayTracingAvailable;
        }

        /// <summary>
        /// Whether this camera will actually get reflections, which is a stricter question than whether the
        /// volume asked for them: the backend has to exist, and for the ray traced one the scene has to hold
        /// traceable geometry. The screen space backend has no such condition - the depth buffer always
        /// exists - so in that mode this is only the material check.
        /// </summary>
        public bool CanRender(BasisGlobalIlluminationSettings settings, Camera camera, int frame)
        {
            if (material == null || rayStagesMaterial == null) { return false; }
            if (!settings.SpecularActive()) { return false; }
            if (ScreenSpaceReflections(settings, rayTracingAvailable)) { return owner != null; }
            return CanRenderRayTraced(settings, camera, frame);
        }

        private bool CanRenderRayTraced(BasisGlobalIlluminationSettings settings, Camera camera, int frame)
        {
            if (!rayTracingAvailable) { return false; }
            BasisGlobalIlluminationRayTracer tracer = BasisGlobalIlluminationRayTracer.GetOrCreate(rayTraceShader, rayTraceCompute, rayComputeFallback);
            if (tracer == null) { return false; }
            return tracer.Refresh(settings.ResolvedSceneSettings(), settings.ResolvedLightSettings(), camera, frame, Time.unscaledTime);
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (material == null || rayStagesMaterial == null) { return; }

            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            if (!resourceData.cameraDepthTexture.IsValid()) { return; }

            BasisGlobalIlluminationSettings settings = BasisGlobalIlluminationSettings.Current;
            if (!settings.SpecularActive()) { return; }

            Camera camera = cameraData.camera;
            int frame = Time.renderedFrameCount;
            bool screenSpace = ScreenSpaceReflections(settings, rayTracingAvailable);
            if (screenSpace && owner == null) { return; }
            if (!screenSpace && !CanRenderRayTraced(settings, camera, frame)) { return; }

            if (invocationFrame != Time.frameCount) { invocationFrame = Time.frameCount; invocationCount = 0; }
            invocationCount++;

            // The keyword follows the handle, not the setting, exactly as the diffuse pass's does: a
            // camera type URP renders no normals prepass for still resolves to an invalid handle.
            TextureHandle normals = screenSpace && UseNormalsTexture && resourceData.cameraNormalsTexture.IsValid()
                ? resourceData.cameraNormalsTexture : TextureHandle.nullHandle;

            RenderTextureDescriptor descriptor = cameraData.cameraTargetDescriptor;
            int divisor = settings.ResolvedResolutionDivisor();
            int tracedWidth = Mathf.Max(1, descriptor.width / divisor);
            int tracedHeight = Mathf.Max(1, descriptor.height / divisor);

            // Reflections keep their own accumulation, indexed the same way the diffuse one is, so a mirror
            // and the player's eye do not pour their reflections into each other's history.
            int hash = BasisGlobalIlluminationHistory.ComputeHash(camera, cameraData.xr);
            BasisGlobalIlluminationHistory history = BasisGlobalIlluminationHistory.Get(hash);
            history.EnsureAllocated(descriptor, tracedWidth, tracedHeight, true);
            bool contiguous = history.SpecularContiguous(frame);
            bool historyValid = settings.specularTemporal && history.SpecularValid && contiguous;

            ApplyKeywords(settings, screenSpace, normals.IsValid());
            FillConstants(settings, frame);

            BasisGlobalIlluminationRayTracer.SkyBinding sky = BasisGlobalIlluminationRayTracer.ResolveSky(settings.fallback, settings.fallbackIntensity);
            if (sky.Cube != null) { Shader.SetGlobalTexture(idSkyCube, sky.Cube); }

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

            TextureHandle traced = UniversalRenderer.CreateRenderGraphTexture(renderGraph, tracedDescriptor, "_BasisGISpecTraced", false, FilterMode.Bilinear, TextureWrapMode.Clamp);
            TextureHandle blurA = UniversalRenderer.CreateRenderGraphTexture(renderGraph, tracedDescriptor, "_BasisGISpecBlurA", false, FilterMode.Bilinear, TextureWrapMode.Clamp);
            TextureHandle blurB = UniversalRenderer.CreateRenderGraphTexture(renderGraph, tracedDescriptor, "_BasisGISpecBlurB", false, FilterMode.Bilinear, TextureWrapMode.Clamp);
            TextureHandle published = UniversalRenderer.CreateRenderGraphTexture(renderGraph, publishedDescriptor, "_BasisGISpecularTexture", false, FilterMode.Bilinear, TextureWrapMode.Clamp);

            TextureHandle historyRead = renderGraph.ImportTexture(history.Specular[history.SpecularRead]);
            TextureHandle historyReadStats = renderGraph.ImportTexture(history.SpecularStats[history.SpecularRead]);
            TextureHandle historyWrite = renderGraph.ImportTexture(history.Specular[history.SpecularWrite]);
            TextureHandle historyWriteStats = renderGraph.ImportTexture(history.SpecularStats[history.SpecularWrite]);

            // The fine level of the depth pyramid, when the screen space backend built one: the temporal
            // gather reads it in place of the full resolution depth texture, exactly as the diffuse
            // pipeline's does. The ray traced backend has no such buffer, and the flag has to say so.
            TextureHandle tracedDepth = TextureHandle.nullHandle;
            // How far beyond its surface each pixel's reflection sits, from the screen space trace: the
            // temporal filter reprojects history by the virtual image the pair describes rather than by the
            // surface, which is what stops head translation smearing every reflection. The ray traced
            // backend does not write one and keeps the surface reprojection it always had.
            TextureHandle hitDistance = TextureHandle.nullHandle;

            if (screenSpace)
            {
                RecordScreenSpaceReflection(renderGraph, resourceData, settings, history, traced, normals,
                    tracedWidth, tracedHeight, divisor, descriptor, frame, sky, out tracedDepth, out hitDistance);
            }
            else
            {
                RecordRayTracedReflection(renderGraph, resourceData, cameraData, settings, camera, traced,
                    tracedWidth, tracedHeight, divisor, descriptor, frame, sky);
            }

            TextureHandle denoiseSource = traced;
            if (settings.specularTemporal)
            {
                using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Basis GI Specular Temporal", out PassData passData, samplerTemporal))
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
                    previousViewProjection[0] = history.PreviousSpecularViewProjection[0];
                    previousViewProjection[1] = history.PreviousSpecularViewProjection[1];
                    // Pooled PassData again: the temporal stage binds motion and the traced depth buffer
                    // whenever their handles look valid, and this pass records earlier in the frame than the
                    // diffuse one that also sets them - so anything left unset here is a handle from a graph
                    // that no longer exists. Motion is never available this early in the frame; the traced
                    // depth is real exactly when the screen space backend built one this frame.
                    passData.motion = TextureHandle.nullHandle;
                    passData.tracedDepth = tracedDepth;
                    passData.tracedDepthValid = tracedDepth.IsValid();
                    passData.specularHitDistance = hitDistance;
                    passData.specularHitDistanceValid = hitDistance.IsValid();
                    builder.SetRenderAttachment(historyWrite, 0, AccessFlags.WriteAll);
                    builder.SetRenderAttachment(historyWriteStats, 1, AccessFlags.WriteAll);
                    builder.UseTexture(traced);
                    builder.UseTexture(historyRead);
                    builder.UseTexture(historyReadStats);
                    builder.UseTexture(resourceData.cameraDepthTexture);
                    if (tracedDepth.IsValid()) { builder.UseTexture(tracedDepth); }
                    if (hitDistance.IsValid()) { builder.UseTexture(hitDistance); }
                    builder.AllowPassCulling(false);
                    builder.AllowGlobalStateModification(true);
                    builder.SetRenderFunc((PassData data, RasterGraphContext context) => Execute(data, context));
                }
                denoiseSource = historyWrite;
            }

            // Half the taps the diffuse gather uses. A mirror ray is exact, so the only thing left to filter
            // is the light resampling at the hit; blurring past that starts destroying the detail that makes
            // a reflection read as a reflection rather than as a coloured sheen.
            bool statsValid = settings.specularTemporal;
            int taps = Mathf.Clamp(Mathf.RoundToInt(settings.smoothing), 0, 4);
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
                passData.source = denoiseSource;
                passData.indirect = denoiseSource;
                // Render graph pools PassData and does not clear it. The Composite stage binds normals when
                // the handle is valid, and a stale one here is a handle from a graph that no longer exists.
                passData.normals = TextureHandle.nullHandle;
                // Same reason, and not optional: the upsample takes each traced parent's depth out of these
                // statistics, so a stale handle here is a stale depth for every weight it computes.
                passData.stats = historyWriteStats;
                passData.statsValid = statsValid;
                // Same reason a third time, for the fields the lightmap mask added to the Composite stage:
                // this pass shares that stage and must not inherit the diffuse composite's mask handle.
                passData.lightmapMask = TextureHandle.nullHandle;
                passData.lightmapMaskValid = false;
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
                data.parameters = new Vector4(1f, 1f / Mathf.Max(0.01f, settings.specularMaxRoughness), 0f, 0f);
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
            history.SpecularValid = settings.specularTemporal;
            history.RecordSpecularFrame(frame);
            // The diffuse pass prunes too, but a world running reflections with the diffuse gather switched
            // off never reaches that call, and cameras that stopped rendering would keep their accumulation
            // for as long as the application ran.
            BasisGlobalIlluminationHistory.PruneStale(frame, HistoryMaxAge);
        }

        /// <summary>
        /// The screen space backend: the depth pyramid the diffuse gather marches, built here from the same
        /// recording the diffuse pass owns, then one fragment pass that casts the mirror ray through it and
        /// reads the previous frame's colour at the hit. Everything downstream of the traced texture is the
        /// same either way.
        ///
        /// The pyramid is built again by the diffuse pass later in the same frame when both are on - the
        /// depth is identical before and after the opaques under depth priming, but the two passes record at
        /// different points and cannot share a graph texture without the diffuse pass learning to reuse this
        /// one. Both builds together are two small reductions; fold them when that pass next changes shape.
        /// </summary>
        private void RecordScreenSpaceReflection(RenderGraph renderGraph, UniversalResourceData resourceData,
            BasisGlobalIlluminationSettings settings, BasisGlobalIlluminationHistory history, TextureHandle traced,
            TextureHandle normals, int tracedWidth, int tracedHeight, int divisor, in RenderTextureDescriptor descriptor,
            int frame, in BasisGlobalIlluminationRayTracer.SkyBinding sky, out TextureHandle tracedDepth, out TextureHandle hitDistance)
        {
            // CONSERVATIVE: the reflection pyramid carries each block's true (nearest, furthest) interval
            // rather than the diffuse gather's unbiased representative. A mirror ray grazes its own floor
            // for dozens of texels, and against a representative its relation to the surface under each
            // texel is a per-row coin flip - the evenly spaced lines across every reflective surface, which
            // disappeared entirely at Full resolution. The interval makes the ambiguity explicit and the
            // march carries it instead of guessing; the diffuse pyramid is untouched, and with its two
            // channels equal the interval tests reduce to the exact arithmetic it always ran.
            owner.RecordDepthPyramid(renderGraph, resourceData, descriptor, tracedWidth, tracedHeight, divisor,
                settings.hierarchicalMarch, true, out tracedDepth, out TextureHandle coarse, out TextureHandle coarseFar);

            RenderTextureDescriptor distanceDescriptor = descriptor;
            distanceDescriptor.width = tracedWidth;
            distanceDescriptor.height = tracedHeight;
            distanceDescriptor.msaaSamples = 1;
            distanceDescriptor.depthStencilFormat = GraphicsFormat.None;
            distanceDescriptor.depthBufferBits = 0;
            distanceDescriptor.useMipMap = false;
            distanceDescriptor.autoGenerateMips = false;
            // Half float carries the sky sentinel exactly - it IS half's largest finite value - and metres
            // at reflection reach never need better than its precision.
            distanceDescriptor.graphicsFormat = GraphicsFormat.R16_SFloat;
            hitDistance = UniversalRenderer.CreateRenderGraphTexture(renderGraph, distanceDescriptor, "_BasisGISpecHitDistance", false, FilterMode.Point, TextureWrapMode.Clamp);

            // Valid means captured recently enough to reproject into, not merely allocated. The first frame
            // after reflections switch on, after a resize, and after a camera gap all read invalid, and the
            // trace answers with the fallback until the capture pass has run - which is the reflection probe
            // behaviour the shader had anyway, arrived at honestly.
            bool priorValid = history.PriorColorContiguous(frame) && history.PriorColor != null;
            TextureHandle prior = priorValid ? renderGraph.ImportTexture(history.PriorColor) : TextureHandle.nullHandle;

            using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Basis GI SSR Trace", out PassData passData, samplerTrace))
            {
                passData.stage = Stage.Trace;
                passData.material = rayStagesMaterial;
                passData.materialPass = PassSSRTrace;
                passData.source = resourceData.cameraDepthTexture;
                passData.sceneColor = prior;
                passData.historyValid = priorValid;
                // Intensity is applied in the trace, so the published value is final: the shared stages
                // deliberately apply none of the diffuse look controls to a reflection.
                passData.rayReference = new Vector4(settings.specularIntensity, priorValid ? 1f : 0f, 0f, 0f);
                passData.previousViewProjection = previousViewProjection;
                previousViewProjection[0] = history.PreviousSpecularViewProjection[0];
                previousViewProjection[1] = history.PreviousSpecularViewProjection[1];
                Configure(passData, settings, tracedWidth, tracedHeight, descriptor, 0, sky);
                passData.coarse = coarse;
                passData.coarseValid = coarse.IsValid();
                passData.coarseTexelSize = owner.coarseTexelSize;
                passData.coarseParams = new Vector4(0f, 0f, 0f, CoarseBlock);
                passData.coarseFar = coarseFar;
                passData.coarseFarTexelSize = owner.coarseFarTexelSize;
                passData.coarseFarBlock = coarseFar.IsValid() ? owner.coarseFarBlock : 0f;
                passData.tracedDepth = tracedDepth;
                passData.tracedDepthValid = true;
                // Pooled PassData: written whether or not the prepass ran, or a stale handle rides in.
                passData.normals = normals;
                builder.SetRenderAttachment(traced, 0, AccessFlags.WriteAll);
                builder.SetRenderAttachment(hitDistance, 1, AccessFlags.WriteAll);
                builder.UseTexture(resourceData.cameraDepthTexture);
                builder.UseTexture(tracedDepth);
                if (coarse.IsValid()) { builder.UseTexture(coarse); }
                if (coarseFar.IsValid()) { builder.UseTexture(coarseFar); }
                if (priorValid) { builder.UseTexture(prior); }
                if (normals.IsValid()) { builder.UseTexture(normals); }
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc((PassData data, RasterGraphContext context) => ExecuteScreenSpaceTrace(data, context));
            }
        }

        /// <summary>
        /// What the screen space trace needs bound, spelled out here rather than grown onto the shared
        /// Execute switch: the shared constants, the pyramid, the previous frame's colour and the matrices
        /// to reproject into it. Every field this reads is written at the one call site above - the pool
        /// rule.
        /// </summary>
        private static void ExecuteScreenSpaceTrace(PassData data, RasterGraphContext context)
        {
            RasterCommandBuffer cmd = context.cmd;
            SetSharedConstants(cmd, data);
            BindTracedDepth(cmd, data);
            if (data.coarseValid)
            {
                cmd.SetGlobalTexture(idCoarseDepth, data.coarse);
                cmd.SetGlobalVector(idCoarseTexelSize, data.coarseTexelSize);
                cmd.SetGlobalVector(idCoarseParams, data.coarseParams);
            }
            BindCoarseFar(cmd, data);
            if (data.sceneColor.IsValid()) { cmd.SetGlobalTexture(idSpecularPriorColor, data.sceneColor); }
            if (data.normals.IsValid()) { cmd.SetGlobalTexture(idNormals, data.normals); }
            cmd.SetGlobalMatrixArray(idPrevViewProjection, data.previousViewProjection);
            cmd.SetGlobalVector(idSSRParams, data.rayReference);
            Blitter.BlitTexture(cmd, data.source, new Vector4(1f, 1f, 0f, 0f), data.material, data.materialPass);
        }

        /// <summary>The ray traced backend, exactly as it was before the screen space one existed.</summary>
        private void RecordRayTracedReflection(RenderGraph renderGraph, UniversalResourceData resourceData,
            UniversalCameraData cameraData, BasisGlobalIlluminationSettings settings, Camera camera, TextureHandle traced,
            int tracedWidth, int tracedHeight, int divisor, in RenderTextureDescriptor descriptor, int frame,
            in BasisGlobalIlluminationRayTracer.SkyBinding sky)
        {
            BasisGlobalIlluminationRayTracer tracer = BasisGlobalIlluminationRayTracer.Instance;
            int viewCount = ViewCountOf(cameraData);
            int scale = Mathf.Clamp(divisor, 1, 4);
            Vector3 viewer = camera.transform.position;
            Vector4 reference = new Vector4(viewer.x, viewer.y, viewer.z, 0f);
            Vector4 fullSize = new Vector4(descriptor.width, descriptor.height, 1f / descriptor.width, 1f / descriptor.height);
            int emitterCount = settings.emitters ? GatherEmitters(camera, settings.ResolvedMaxEmitters()) : 0;

            // Without hardware ray tracing the BVH is walked in a compute shader, and the diffuse gather
            // already caps itself there rather than asking a GPU that cannot afford it for the full budget.
            // A reflection is one ray per pixel whatever happens, so the cap that is left to apply is on the
            // path continued from the hit and on how many lights each of those hits shadow-rays.
            int specularBounces = settings.specularBounces;
            int lightSamples = settings.ResolvedRayTracedLightSamples();
            if (tracer.Context.Backend == RayTracingBackend.Compute)
            {
                specularBounces = Mathf.Min(specularBounces, ComputeBackendBounceCeiling);
                lightSamples = Mathf.Min(lightSamples, ComputeBackendLightSampleCeiling);
            }

            TextureHandle position = UniversalRenderer.CreateRenderGraphTexture(renderGraph,
                RayArrayDescriptor(tracedWidth, tracedHeight, viewCount, GraphicsFormat.R16G16B16A16_SFloat, false), "_BasisGISpecPosition", false);
            TextureHandle normal = UniversalRenderer.CreateRenderGraphTexture(renderGraph,
                RayArrayDescriptor(tracedWidth, tracedHeight, viewCount, GraphicsFormat.R16G16_SFloat, false), "_BasisGISpecNormal", false);
            TextureHandle rayResult = UniversalRenderer.CreateRenderGraphTexture(renderGraph,
                RayArrayDescriptor(tracedWidth, tracedHeight, viewCount, GraphicsFormat.R16G16B16A16_SFloat, true), "_BasisGISpecResult", false);

            using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Basis GI Specular Prepass", out PassData passData, samplerPrepass))
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
                data.trace = new Vector4(settings.maxRayLength, settings.obscuranceRadius, settings.obscuranceIntensity, settings.fadeDistance);
                data.bias = new Vector4(settings.rayTracedNormalBias, settings.rayDistanceBias, settings.emitterIntensity, settings.rayTracedLightIntensity);
                data.options = new Vector4(settings.fireflyClamp, settings.rayBounceThreshold, settings.rayTracedShadows ? 1f : 0f, 0f);
                data.specularParams = new Vector4(settings.specularRayLength, settings.specularIntensity,
                    settings.specularFadeDistance, specularBounces);
                // Mip zero for the same reason the screen space backend reads it: a mirror ray wants the
                // environment as an image, not the diffuse gather's irradiance mip. See Configure. The z
                // flag is whether a PRIMARY miss may claim the sky with confidence: only the Sky fallback
                // asks for that - under Reflection Probe a miss reports no data and the lit shader keeps
                // the surface's own probes, exactly as the screen space backend answers. Bounce misses
                // keep reading the sky as lighting either way; the flag gates only the claim.
                data.sky = new Vector4(0f, sky.IsValid ? sky.Intensity : 0f,
                    settings.fallback == BasisGlobalIlluminationFallback.Sky ? 1f : 0f, 0f);
                data.skyDecode = sky.Decode;
                data.diffuseEnabled = false;
                data.specularEnabled = true;
                data.rayCount = 1;
                data.bounces = specularBounces;
                data.lightCount = tracer.Lights.Count;
                data.lightSamples = lightSamples;
                data.viewCount = viewCount;
                data.frameIndex = frame % 64;
                data.traceMask = settings.TraceCategories;
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
                passData.source = rayResult;
                passData.rayResult = rayResult;
                builder.SetRenderAttachment(traced, 0, AccessFlags.WriteAll);
                builder.UseTexture(rayResult);
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc((PassData data, RasterGraphContext context) => Execute(data, context));
            }
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
                passData.source = source;
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
        private void FillConstants(BasisGlobalIlluminationSettings settings, int frame)
        {
            constants[0] = new Vector4(1f, 1f, settings.obscuranceIntensity, settings.obscuranceRadius);
            constants[1] = new Vector4(settings.specularRayLength, settings.thickness, settings.jitter, settings.specularFadeDistance);
            constants[2] = new Vector4(1f, settings.ResolvedRaySteps(), settings.fireflyClamp, 0f);
            float temporalResponse = Mathf.Clamp(settings.temporalResponse * SpecularTemporalResponseScale,
                BasisGlobalIlluminationSettings.TemporalResponseMin, BasisGlobalIlluminationSettings.TemporalResponseMax);
            constants[3] = new Vector4(frame % 64, temporalResponse, settings.depthRejection, settings.emitterIntensity);
        }

        private void Configure(PassData passData, BasisGlobalIlluminationSettings settings, int tracedWidth, int tracedHeight,
            in RenderTextureDescriptor descriptor, int emitterCount, in BasisGlobalIlluminationRayTracer.SkyBinding sky)
        {
            passData.constants = constants;
            // Mip zero, not the binding's own: ResolveSky picks a mip for the DIFFUSE gather, where a missed
            // ray wants the environment's irradiance and the coarsest levels are the answer. A mirror ray
            // wants the environment as an image - at the diffuse mip the reflected sky is one flat colour,
            // which reads as "the sky is not in the reflection" against any real skybox.
            passData.sky = new Vector4(0f, sky.IsValid ? sky.Intensity : 0f, 0f, 0f);
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
        /// The keywords the reflection stages depend on. The diffuse pass sets the shared ones too, from the
        /// same settings, but it records later in the frame - so on a frame where only reflections run they
        /// would otherwise still be carrying the previous frame's values. The stages material carries the
        /// screen space trace's own pair: which fallback a missed ray reads, and whether the march is
        /// hierarchical - the same two decisions the diffuse gather makes, taken from the same settings.
        /// </summary>
        private void ApplyKeywords(BasisGlobalIlluminationSettings settings, bool screenSpace, bool normalsValid)
        {
            CoreUtils.SetKeyword(material, "_BASISGI_NEIGHBOURHOOD_CLAMP", settings.neighbourhoodClamp);
            CoreUtils.SetKeyword(material, "_BASISGI_BILATERAL_UPSAMPLE", settings.bilateralUpsample && settings.ResolvedResolutionDivisor() > 1);
            if (screenSpace)
            {
                CoreUtils.SetKeyword(rayStagesMaterial, "_BASISGI_FALLBACK_SKY", settings.fallback == BasisGlobalIlluminationFallback.Sky);
                CoreUtils.SetKeyword(rayStagesMaterial, "_BASISGI_FALLBACK_PROBE", settings.fallback == BasisGlobalIlluminationFallback.ReflectionProbe);
                CoreUtils.SetKeyword(rayStagesMaterial, "_BASISGI_HIERARCHICAL_MARCH", settings.hierarchicalMarch);
                CoreUtils.SetKeyword(rayStagesMaterial, "_BASISGI_NORMALS_TEXTURE", normalsValid);
            }
        }
    }

    /// <summary>
    /// Copies the finished camera colour into the reflection history at the end of the frame, for the
    /// screen space reflection trace to read NEXT frame. The trace runs before the opaque draws - the
    /// opaque shaders are what consume its result - so the current frame's colour does not exist yet when
    /// it runs, and the previous frame's, reprojected through the stored view projection, is the only
    /// colour there is. Captured after transparents and before post processing: glass and water belong in a
    /// reflection, tonemapping and bloom do not - the lit shader consuming the reflection feeds it back
    /// into a frame that has not been graded yet.
    ///
    /// Enqueued only when the screen space backend will run; the ray traced backend relights its hits from
    /// the light list and never reads a colour buffer.
    /// </summary>
    public sealed class SpecularColorCapturePass : ScriptableRenderPass
    {
        private static readonly ProfilingSampler samplerCapture = new ProfilingSampler("Basis GI Specular Prior Color");
        public static float GpuMs => samplerCapture.gpuElapsedTime;
        public static void SetProfilingEnabled(bool enabled) { samplerCapture.enableRecording = enabled; }

        private Material material;

        public SpecularColorCapturePass()
        {
            profilingSampler = samplerCapture;
            renderPassEvent = RenderPassEvent.AfterRenderingTransparents;
            // This pass SAMPLES the camera colour, and a camera rendering straight to the backbuffer has
            // no samplable colour to offer - a mirror with post processing off is exactly that camera. The
            // diffuse pass forces the intermediate for its own composite; this pass cannot rely on the
            // diffuse pass being on.
            requiresIntermediateTexture = true;
        }

        public void Setup(Material compositeMaterial) { material = compositeMaterial; }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (material == null) { return; }

            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            if (!resourceData.cameraColor.IsValid()) { return; }

            BasisGlobalIlluminationSettings settings = BasisGlobalIlluminationSettings.Current;
            if (!settings.SpecularActive()) { return; }

            int frame = Time.renderedFrameCount;
            int hash = BasisGlobalIlluminationHistory.ComputeHash(cameraData.camera, cameraData.xr);
            BasisGlobalIlluminationHistory history = BasisGlobalIlluminationHistory.Get(hash);
            history.EnsurePriorColor(cameraData.cameraTargetDescriptor);
            TextureHandle target = renderGraph.ImportTexture(history.PriorColor);

            using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Basis GI Specular Prior Color", out CaptureData data, samplerCapture))
            {
                data.material = material;
                data.source = resourceData.cameraColor;
                builder.SetRenderAttachment(target, 0, AccessFlags.WriteAll);
                builder.UseTexture(resourceData.cameraColor);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (CaptureData data, RasterGraphContext context) =>
                    Blitter.BlitTexture(context.cmd, data.source, new Vector4(1f, 1f, 0f, 0f), data.material, PassCopyColor));
            }

            // The matrices are stored here as well as in the reflection pass, because the pair being stored
            // together is the whole contract: a colour stamped without the view projection that drew it
            // would be reprojected through some other frame's camera. Both writes in one frame store the
            // same matrix - the camera does not move mid-frame - so the repetition costs nothing.
            StoreViewProjection(cameraData, history.PreviousSpecularViewProjection);
            history.RecordPriorColorFrame(frame);
        }

        private sealed class CaptureData
        {
            public Material material;
            public TextureHandle source;
        }
    }

    private sealed class SpecularGlobalData
    {
        public Vector4 parameters;
    }
}
