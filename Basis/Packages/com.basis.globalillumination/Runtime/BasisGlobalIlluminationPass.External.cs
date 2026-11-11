using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

public partial class BasisGlobalIlluminationPass
{
    private ComputeShader externalCompute;
    private int kernelPackGBuffer = -1, kernelPackVertices, kernelPackIndices, kernelPackTexture, kernelUnpack;
    private bool externalSubmit, externalMirror;
    private ulong externalFrame;
    private BasisExternalRayTracer.Context externalContext;
    private float externalEmissionScale = -1f;
    private uint externalParity, externalFetchParity;
    private ulong externalFetchFrame;
    private bool externalBehind;
    private static readonly int idExLightmapMask = Shader.PropertyToID("_LightmapMask");
    private static readonly int idExMotion = Shader.PropertyToID("_Motion");
    private static readonly int idExJitter = Shader.PropertyToID("_Jitter");
    private static readonly int idExPrevious = Shader.PropertyToID("_Previous"), idExPreviousCameraPosition = Shader.PropertyToID("_PreviousCameraPosition");
    private readonly Vector4[] externalPreviousPositions = new Vector4[2];
    public static bool ValidateReprojection = true;
    public static bool ReprojectResult = true;
    private Vector4 externalJitter;
    private Vector2 externalDisplayJitter;
    private const uint FlagMotion = 512;
    private static readonly int idExDepth = Shader.PropertyToID("_Depth"), idExNormals = Shader.PropertyToID("_Normals"), idExSurface = Shader.PropertyToID("_Surface"), idExColor = Shader.PropertyToID("_Color");
    private static readonly int idExTraced = Shader.PropertyToID("_Traced"), idExGBuffer = Shader.PropertyToID("_GBuffer"), idExUpload = Shader.PropertyToID("_Upload"), idExResult = Shader.PropertyToID("_Result");
    private static readonly int idExSource = Shader.PropertyToID("_Source"), idExStream0 = Shader.PropertyToID("_Stream0"), idExStream1 = Shader.PropertyToID("_Stream1"), idExIndices = Shader.PropertyToID("_Indices");
    private static readonly int idExInvViewProj = Shader.PropertyToID("_InvViewProj"), idExCameraPosition = Shader.PropertyToID("_CameraPosition"), idExSize = Shader.PropertyToID("_Size");
    private static readonly int idExFlags = Shader.PropertyToID("_Flags"), idExLayout0 = Shader.PropertyToID("_Layout0"), idExLayout1 = Shader.PropertyToID("_Layout1"), idExJob = Shader.PropertyToID("_Job");
    private readonly Matrix4x4[] externalInverse = new Matrix4x4[2];
    private readonly Vector4[] externalPositions = new Vector4[2];
    [NoAutoStaticsCleanup] private static readonly ProfilingSampler samplerExternal = new ProfilingSampler("Basis GI Path Traced (External)");

    private class ExternalData
    {
        public ComputeShader compute;
        public int kernelGBuffer, kernelVertices, kernelIndices, kernelTexture, kernelUnpack;
        public bool array, submit, hasNormals, hasSurface, reversedZ;
        public ulong frame, fetchFrame;
        public uint rows, parity, fetchParity;
        public bool behind;
        public int context;
        public RenderTexture gbuffer, result;
        public int width, height, views, sourceWidth, sourceHeight;
        public TextureHandle depth, normals, surface, color, traced, lightmapMask, motion;
        public bool hasLightmapMask, hasMotion;
        public Vector4 jitter, displayJitter;
        public Matrix4x4[] inverse;
        public Vector4[] positions;
        public readonly Vector4[] previousPositions = new Vector4[2];
        public BasisExternalRayScene.Job[] jobs;
        public int jobCount;
        public readonly Matrix4x4[] mirrors = new Matrix4x4[BasisExternalRayScene.MaxMirrors];
        public readonly Vector4[] mirrorBounds = new Vector4[BasisExternalRayScene.MaxMirrors * 2];
        public int mirrorCount;
        public bool mirrorView;
    }

    private static readonly int idExMirrorToLocal = Shader.PropertyToID("_MirrorToLocal"), idExMirrorBounds = Shader.PropertyToID("_MirrorBounds");

    private BasisExternalRayScene.Job[] externalJobs = new BasisExternalRayScene.Job[64];

    public void SetExternal(ComputeShader compute)
    {
        externalCompute = compute;
        kernelPackGBuffer = -1;
        if (compute == null) { return; }
        kernelPackGBuffer = compute.FindKernel("PackGBuffer");
        kernelPackVertices = compute.FindKernel("PackVertices");
        kernelPackIndices = compute.FindKernel("PackIndices");
        kernelPackTexture = compute.FindKernel("PackTexture");
        kernelUnpack = compute.FindKernel("Unpack");
    }

    private bool PrepareExternal(BasisGlobalIlluminationSettings settings, UniversalCameraData cameraData, int tracedWidth, int tracedHeight, bool motion)
    {
        externalSubmit = false;
        externalContext = null;
        Camera camera = cameraData.camera;
        if (externalCompute == null || kernelPackGBuffer < 0 || !SystemInfo.supportsComputeShaders || camera == null) { return false; }
        bool mirror = BasisGlobalIlluminationFeature.IsMirrorReflection(camera);
        if (!mirror && camera.cameraType != CameraType.Game) { return false; }
        externalMirror = mirror;
        int views = ViewCountOf(cameraData);
        BasisExternalRayTracer.Context context = BasisExternalRayTracer.Prepare(camera, tracedWidth, tracedHeight, views, settings.externalSameFrame);
        if (context == null) { return false; }
        externalContext = context;
        if (!BasisExternalRayTracer.CanSubmit(out ulong frame)) { return context.ResultValid; }
        BasisExternalRayFrame parameters = default;
        parameters.frame = frame;
        parameters.context = (uint)context.Id;
        parameters.parity = context.Parity;
        parameters.cacheCellSize = Mathf.Clamp(settings.worldVoxelSize, 0.05f, 2f);
        parameters.width = (uint)tracedWidth;
        parameters.height = (uint)tracedHeight;
        parameters.viewCount = (uint)views;
        parameters.samples = (uint)(BasisExternalRayTracer.RayCount > 0 ? Mathf.Clamp(BasisExternalRayTracer.RayCount, 1, 64) : Mathf.Clamp(settings.ResolvedRayCount() * 2, 2, 16));
        parameters.bounces = (uint)Mathf.Clamp(settings.ResolvedBounces(), 0, 8) | ((uint)Mathf.Clamp(BasisExternalRayTracer.AvatarRayCount, 0, 64) << 8);
        parameters.maxDistance = Mathf.Max(1f, settings.maxRayLength * 4f);
        parameters.aoRadius = settings.obscuranceRadius;
        parameters.aoIntensity = settings.obscuranceIntensity;
        parameters.emissionScale = settings.emitterIntensity;
        parameters.time = Time.time;
        parameters.fireflyClamp = settings.fireflyClamp * 4f;
        parameters.temporalResponse = Mathf.Clamp(settings.temporalResponse * 0.35f, 0.02f, 1f);
        parameters.lightIntensity = settings.rayTracedLightIntensity;
        parameters.normalBias = Mathf.Max(0.002f, settings.rayTracedNormalBias * 0.5f);
        parameters.flags = BasisExternalRayTracer.FlagScreenReuse | BasisExternalRayTracer.FlagDenoise | BasisExternalRayTracer.FlagReorder | BasisExternalRayTracer.ConsumeResetFlag(context);
        if (externalEmissionScale != parameters.emissionScale) { if (externalEmissionScale >= 0f) { parameters.flags |= BasisExternalRayTracer.FlagCacheReset; } externalEmissionScale = parameters.emissionScale; }
        if (settings.rayTracedShadows) { parameters.flags |= BasisExternalRayTracer.FlagShadows; }
        if (settings.externalSameFrame) { parameters.flags |= BasisExternalRayTracer.FlagSameFrame; }
        if (settings.externalProbes) { parameters.flags |= BasisExternalRayTracer.FlagProbes; }
        if (settings.IsExternalBaked()) { parameters.flags |= BasisExternalRayTracer.FlagBakedHybrid; }
        if (motion) { parameters.flags |= FlagMotion; }
        for (int v = 0; v < 2; v++)
        {
            int eye = Mathf.Min(v, views - 1);
            Matrix4x4 view = cameraData.GetViewMatrix(eye);
            Matrix4x4 projection = cameraData.GetProjectionMatrix(eye);
            Matrix4x4 viewProjection = projection * view;
            Vector3 position = view.inverse.GetColumn(3);
            BasisExternalRayView ev = new BasisExternalRayView { view = view, projection = projection, inverseViewProjection = viewProjection.inverse, previousViewProjection = v < views ? BasisExternalRayTracer.PreviousViewProjection(context, v, viewProjection) : viewProjection, position = position };
            if (v == 0) { parameters.view0 = ev; } else { parameters.view1 = ev; }
            externalInverse[v] = (GL.GetGPUProjectionMatrix(projection, false) * view).inverse;
            externalPositions[v] = position;
            externalPreviousPositions[v] = context.Position[v];
            externalPreviousPositions[v].w = 2f / (Mathf.Max(Mathf.Abs(projection.m11), 1e-4f) * tracedHeight);
            context.Position[v] = position;
        }
        BasisExternalRayScene scene = BasisExternalRayTracer.Scene;
        if (BasisExternalRayTracer.SceneFilledThisFrame(out ulong source))
        {
            BasisExternalRayTracer.CopyScene(source, frame, ref parameters);
            scene.Jobs.Clear();
            scene.ClearUpload();
        }
        else
        {
            scene.BeginFrame(BasisExternalRayTracer.UploadFree(frame));
            scene.Fill(frame, settings, externalPositions[0], ref parameters);
            BasisExternalRayTracer.MarkSceneFilled(frame);
        }
        unsafe { *BasisExternalRayTracer.Params(frame) = parameters; }
        externalFrame = frame;
        externalSubmit = true;
        uint parity = context.Parity;
        externalBehind = !settings.externalSameFrame && context.LastFrame != 0;
        externalFetchFrame = externalBehind ? context.LastFrame : frame;
        externalFetchParity = externalBehind ? context.LastParity : parity;
        BasisExternalRayTracer.Commit(context, frame);
        context.LastFrame = frame;
        context.LastParity = parity;
        externalParity = parity;
        return true;
    }

    private TextureHandle RecordExternal(RenderGraph renderGraph, UniversalResourceData resourceData, UniversalCameraData cameraData, TextureHandle surface, TextureHandle normals, TextureHandle lightmapMask, TextureHandle motion, int tracedWidth, int tracedHeight, in RenderTextureDescriptor descriptor, RenderTextureDescriptor tracedDescriptor)
    {
        int views = ViewCountOf(cameraData);
        tracedDescriptor.enableRandomWrite = true;
        tracedDescriptor.msaaSamples = 1;
        TextureHandle traced = UniversalRenderer.CreateRenderGraphTexture(renderGraph, tracedDescriptor, "_BasisGIExternal", false, FilterMode.Bilinear, TextureWrapMode.Clamp);
        using (IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass("Basis GI Path Traced (External)", out ExternalData data, samplerExternal))
        {
            data.compute = externalCompute;
            data.kernelGBuffer = kernelPackGBuffer;
            data.kernelVertices = kernelPackVertices;
            data.kernelIndices = kernelPackIndices;
            data.kernelTexture = kernelPackTexture;
            data.kernelUnpack = kernelUnpack;
            data.array = tracedDescriptor.dimension == TextureDimension.Tex2DArray;
            data.submit = externalSubmit;
            data.frame = externalFrame;
            data.parity = externalParity;
            data.fetchFrame = externalFetchFrame;
            data.fetchParity = externalFetchParity;
            data.behind = externalBehind;
            data.context = externalContext.Id;
            data.gbuffer = externalContext.GBuffer;
            data.result = externalContext.Result;
            data.width = tracedWidth;
            data.height = tracedHeight;
            data.views = views;
            data.sourceWidth = descriptor.width;
            data.sourceHeight = descriptor.height;
            data.reversedZ = SystemInfo.usesReversedZBuffer;
            data.depth = resourceData.cameraDepthTexture;
            data.normals = normals;
            data.hasNormals = normals.IsValid();
            data.surface = surface;
            data.hasSurface = surface.IsValid();
            data.color = resourceData.cameraColor;
            data.traced = traced;
            data.inverse = externalInverse;
            data.positions = externalPositions;
            data.previousPositions[0] = externalPreviousPositions[0];
            data.previousPositions[1] = externalPreviousPositions[1];
            BasisExternalRayScene scene = BasisExternalRayTracer.Scene;
            data.jobCount = externalSubmit && scene != null ? scene.Jobs.Count : 0;
            if (externalJobs.Length < data.jobCount) { externalJobs = new BasisExternalRayScene.Job[Mathf.NextPowerOfTwo(data.jobCount)]; }
            for (int i = 0; i < data.jobCount; i++) { externalJobs[i] = scene.Jobs[i]; }
            data.jobs = externalJobs;
            data.rows = externalSubmit && scene != null ? scene.UploadRows : 0;
            data.mirrorCount = scene != null ? scene.MirrorCount : 0;
            data.mirrorView = externalMirror;
            if (data.mirrorCount > 0)
            {
                System.Array.Copy(scene.MirrorToLocal, data.mirrors, data.mirrorCount);
                System.Array.Copy(scene.MirrorBounds, data.mirrorBounds, data.mirrorCount * 2);
            }
            builder.UseTexture(resourceData.cameraDepthTexture);
            builder.UseTexture(resourceData.cameraColor);
            if (data.hasNormals) { builder.UseTexture(normals); }
            if (data.hasSurface) { builder.UseTexture(surface); }
            data.lightmapMask = lightmapMask;
            data.hasLightmapMask = lightmapMask.IsValid();
            if (data.hasLightmapMask) { builder.UseTexture(lightmapMask); }
            data.motion = motion;
            data.hasMotion = motion.IsValid();
            data.jitter = externalJitter;
            data.displayJitter = externalDisplayJitter;
            if (data.hasMotion) { builder.UseTexture(motion); }
            builder.UseTexture(traced, AccessFlags.WriteAll);
            builder.AllowPassCulling(false);
            builder.SetRenderFunc((ExternalData d, UnsafeGraphContext context) => ExecuteExternal(d, context));
        }
        externalSubmit = false;
        return traced;
    }

    private static void ExecuteExternal(ExternalData d, UnsafeGraphContext context)
    {
        CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
        ComputeShader cs = d.compute;
        LocalKeyword arrayKeyword = new LocalKeyword(cs, "BASIS_RT_ARRAY");
        if (d.array) { cmd.EnableKeyword(cs, arrayKeyword); } else { cmd.DisableKeyword(cs, arrayKeyword); }
        RenderTexture upload = BasisExternalRayTracer.Upload, gbuffer = d.gbuffer, result = d.result;
        if (upload == null || gbuffer == null || result == null) { return; }
        bool validate = ValidateReprojection && d.submit && d.behind && d.fetchFrame != 0 && d.hasMotion;
        if (d.submit)
        {
            if (d.behind && d.fetchFrame != 0) { cmd.IssuePluginEventAndData(BasisExternalRayTracer.EventFunc, BasisExternalRayTracer.EventBase + 1, BasisExternalRayTracer.EventData(d.fetchFrame, d.context, d.fetchParity, 0)); }
            if (validate) { DispatchUnpack(cmd, d, gbuffer, result, true); }
            for (int i = 0; i < d.jobCount; i++)
            {
                BasisExternalRayScene.Job job = d.jobs[i];
                switch (job.kind)
                {
                    case BasisExternalRayScene.JobKind.Vertices:
                    case BasisExternalRayScene.JobKind.Positions:
                        if (job.stream0 == null || job.stream1 == null) { break; }
                        cmd.SetComputeBufferParam(cs, d.kernelVertices, idExStream0, job.stream0);
                        cmd.SetComputeBufferParam(cs, d.kernelVertices, idExStream1, job.stream1);
                        cmd.SetComputeTextureParam(cs, d.kernelVertices, idExUpload, upload);
                        cmd.SetComputeIntParams(cs, idExLayout0, job.layout0.x, job.layout0.y, job.layout0.z, job.layout0.w);
                        cmd.SetComputeIntParams(cs, idExLayout1, job.layout1.x, job.layout1.y, job.layout1.z, job.layout1.w);
                        cmd.SetComputeIntParams(cs, idExJob, (int)job.first, (int)job.count, (int)job.texel, job.kind == BasisExternalRayScene.JobKind.Positions ? 1 : 0);
                        cmd.DispatchCompute(cs, d.kernelVertices, (int)((job.count + 63) / 64), 1, 1);
                        break;
                    case BasisExternalRayScene.JobKind.Indices:
                        if (job.indices == null) { break; }
                        cmd.SetComputeBufferParam(cs, d.kernelIndices, idExIndices, job.indices);
                        cmd.SetComputeTextureParam(cs, d.kernelIndices, idExUpload, upload);
                        cmd.SetComputeIntParams(cs, idExLayout0, job.layout0.x, job.layout0.y, 0, 0);
                        cmd.SetComputeIntParams(cs, idExJob, (int)job.first, (int)job.count, (int)job.texel, 0);
                        cmd.DispatchCompute(cs, d.kernelIndices, (int)((job.count + 255) / 256), 1, 1);
                        break;
                    case BasisExternalRayScene.JobKind.Texture:
                        if (job.source == null || job.scratch == null) { break; }
                        cmd.Blit(job.source, job.scratch);
                        cmd.SetComputeTextureParam(cs, d.kernelTexture, idExSource, job.scratch);
                        cmd.SetComputeTextureParam(cs, d.kernelTexture, idExUpload, upload);
                        cmd.SetComputeIntParams(cs, idExFlags, (int)job.hdr, 0, 0, 0);
                        cmd.SetComputeIntParams(cs, idExJob, (int)job.width, (int)job.count, (int)job.texel, (int)job.row);
                        cmd.DispatchCompute(cs, d.kernelTexture, (int)((job.count + 255) / 256), 1, 1);
                        break;
                }
            }
            cmd.SetComputeTextureParam(cs, d.kernelGBuffer, idExDepth, d.depth);
            cmd.SetComputeTextureParam(cs, d.kernelGBuffer, idExColor, d.color);
            cmd.SetComputeTextureParam(cs, d.kernelGBuffer, idExNormals, d.hasNormals ? d.normals : d.color);
            cmd.SetComputeTextureParam(cs, d.kernelGBuffer, idExSurface, d.hasSurface ? d.surface : d.color);
            cmd.SetComputeTextureParam(cs, d.kernelGBuffer, idExLightmapMask, d.hasLightmapMask ? d.lightmapMask : d.color);
            cmd.SetComputeTextureParam(cs, d.kernelGBuffer, idExMotion, d.hasMotion ? d.motion : d.color);
            cmd.SetComputeIntParams(cs, idExLayout1, d.hasLightmapMask ? 1 : 0, d.hasMotion ? 1 : 0, d.mirrorCount, d.mirrorView && MirrorFootprint ? 1 : 0);
            cmd.SetComputeMatrixArrayParam(cs, idExMirrorToLocal, d.mirrors);
            cmd.SetComputeVectorArrayParam(cs, idExMirrorBounds, d.mirrorBounds);
            cmd.SetComputeTextureParam(cs, d.kernelGBuffer, idExGBuffer, gbuffer);
            cmd.SetComputeMatrixArrayParam(cs, idExInvViewProj, d.inverse);
            cmd.SetComputeVectorArrayParam(cs, idExCameraPosition, d.positions);
            cmd.SetComputeIntParams(cs, idExSize, d.width, d.height, d.sourceWidth, d.sourceHeight);
            cmd.SetComputeIntParams(cs, idExFlags, d.hasNormals ? 1 : 0, d.hasSurface ? 1 : 0, d.views, d.reversedZ ? 1 : 0);
            cmd.SetComputeVectorParam(cs, idExJitter, d.jitter);
            cmd.DispatchCompute(cs, d.kernelGBuffer, (d.width * d.views + 7) / 8, (d.height + 7) / 8, 1);
            cmd.IssuePluginEventAndData(BasisExternalRayTracer.EventFunc, BasisExternalRayTracer.EventBase + 0, BasisExternalRayTracer.EventData(d.frame, d.context, d.parity, d.rows));
            if (!d.behind) { cmd.IssuePluginEventAndData(BasisExternalRayTracer.EventFunc, BasisExternalRayTracer.EventBase + 1, BasisExternalRayTracer.EventData(d.frame, d.context, d.parity, 0)); }
        }
        if (!validate) { DispatchUnpack(cmd, d, gbuffer, result, false); }
    }

    private static void DispatchUnpack(CommandBuffer cmd, ExternalData d, RenderTexture gbuffer, RenderTexture result, bool validate)
    {
        ComputeShader cs = d.compute;
        cmd.SetComputeTextureParam(cs, d.kernelUnpack, idExResult, result);
        cmd.SetComputeTextureParam(cs, d.kernelUnpack, idExTraced, d.traced);
        cmd.SetComputeTextureParam(cs, d.kernelUnpack, idExMotion, d.hasMotion ? d.motion : d.color);
        cmd.SetComputeTextureParam(cs, d.kernelUnpack, idExDepth, d.depth);
        cmd.SetComputeTextureParam(cs, d.kernelUnpack, idExNormals, d.hasNormals ? d.normals : d.color);
        cmd.SetComputeTextureParam(cs, d.kernelUnpack, idExPrevious, gbuffer);
        cmd.SetComputeMatrixArrayParam(cs, idExInvViewProj, d.inverse);
        cmd.SetComputeVectorArrayParam(cs, idExCameraPosition, d.positions);
        cmd.SetComputeVectorArrayParam(cs, idExPreviousCameraPosition, d.previousPositions);
        cmd.SetComputeIntParams(cs, idExSize, d.width, d.height, d.sourceWidth, d.sourceHeight);
        cmd.SetComputeIntParams(cs, idExFlags, validate ? 1 : 0, ReprojectResult && d.behind && d.hasMotion ? 1 : 0, d.views, d.reversedZ ? 1 : 0);
        cmd.SetComputeIntParams(cs, idExLayout0, d.hasNormals ? 1 : 0, 0, 0, 0);
        cmd.SetComputeVectorParam(cs, idExJitter, d.displayJitter);
        cmd.DispatchCompute(cs, d.kernelUnpack, (d.width + 7) / 8, (d.height + 7) / 8, d.views);
    }
}
