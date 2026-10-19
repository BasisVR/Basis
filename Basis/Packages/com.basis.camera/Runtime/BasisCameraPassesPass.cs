using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
[NoAutoStaticsCleanup]
public sealed class BasisCameraPassesPass : ScriptableRenderPass
{
    public const int DepthShaderPass = 0, NormalsShaderPass = 1, MotionShaderPass = 2, MaskResolveShaderPass = 3;
    private static readonly int DepthRangeId = Shader.PropertyToID("_BasisPassDepthRange"), DataId = Shader.PropertyToID("_BasisPassData"), TargetSizeId = Shader.PropertyToID("_BasisPassTargetSize");
    private static readonly int WorldToViewId = Shader.PropertyToID("_BasisPassWorldToView"), MaskTexelId = Shader.PropertyToID("_BasisPassMaskTexel");
    private static readonly ProfilingSampler Sampler = new ProfilingSampler("Basis Camera Passes");
    private static readonly string[] PassNames = { "Basis Camera Depth", "Basis Camera Normals", "Basis Camera Mask", "Basis Camera Motion" };
    private static readonly Vector4 Identity = new Vector4(1f, 1f, 0f, 0f);
    private static ShaderTagId[] maskTags;
    private static int maskSamples = -1;
    private readonly RTHandle[] handles = new RTHandle[BasisCameraPasses.Count];
    private readonly RenderTexture[] wrapped = new RenderTexture[BasisCameraPasses.Count];
    private Material material, maskMaterial;
    private BasisCameraPassSet passes;
    private int subjectLayers;
    private float depthRange = BasisCameraPasses.DefaultDepthRange;
    private bool data;
    private Matrix4x4 worldToView = Matrix4x4.identity;
    public BasisCameraPassesPass()
    {
        renderPassEvent = RenderPassEvent.AfterRenderingTransparents;
        profilingSampler = Sampler;
    }
    public BasisCameraPassSet Setup(Material material, Material maskMaterial, RenderTexture reference, RenderTexture[] targets, BasisCameraPassSet wanted, int subjectLayers, float depthRange, bool data, Quaternion cameraRotation)
    {
        this.material = material;
        this.maskMaterial = maskMaterial;
        this.subjectLayers = subjectLayers;
        this.depthRange = BasisCameraPasses.SanitizeDepthRange(depthRange);
        this.data = data;
        worldToView = BasisCameraPasses.ViewRotation(cameraRotation);

        BasisCameraPassSet ready = BasisCameraPassSet.None;
        for (int Index = 0; Index < BasisCameraPasses.Count; Index++)
        {
            BasisCameraPass pass = (BasisCameraPass)Index;
            RenderTexture target = targets != null && Index < targets.Length ? targets[Index] : null;
            bool usable = material != null && BasisCameraPasses.Contains(wanted, pass) && BasisCameraPasses.Matches(target, reference, data) && (pass != BasisCameraPass.Mask || maskMaterial != null);
            if (!usable) continue;
            Wrap(Index, target);
            ready |= BasisCameraPasses.Flag(pass);
        }

        passes = ready;
        ConfigureInput(BasisCameraPasses.InputFor(ready));
        return ready;
    }
    public void Release()
    {
        for (int Index = 0; Index < handles.Length; Index++)
        {
            handles[Index]?.Release();
            handles[Index] = null;
            wrapped[Index] = null;
        }
        passes = BasisCameraPassSet.None;
    }
    private void Wrap(int index, RenderTexture target)
    {
        if (handles[index] != null && ReferenceEquals(wrapped[index], target)) return;
        handles[index]?.Release();
        handles[index] = RTHandles.Alloc(target);
        wrapped[index] = target;
    }
    private bool Has(BasisCameraPass pass) => BasisCameraPasses.Contains(passes, pass) && handles[(int)pass] != null;
    private sealed class BlitData
    {
        public Material Material;
        public int Pass;
    }
    private sealed class MaskData
    {
        public RendererListHandle Renderers;
        public Vector4 Texel;
    }
    private sealed class ResolveData
    {
        public TextureHandle Coverage;
        public Material Material;
    }
    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        if (material == null || passes == BasisCameraPassSet.None) return;

        UniversalResourceData resources = frameData.Get<UniversalResourceData>();
        RenderTexture sized = FirstTarget();
        if (sized == null) return;

        material.SetFloat(DepthRangeId, depthRange);
        material.SetFloat(DataId, data ? 1f : 0f);
        material.SetVector(TargetSizeId, new Vector4(sized.width, sized.height, 1f / sized.width, 1f / sized.height));
        material.SetMatrix(WorldToViewId, worldToView);

        if (Has(BasisCameraPass.Depth)) RecordBlit(renderGraph, BasisCameraPass.Depth, DepthShaderPass, resources.cameraDepthTexture, TextureHandle.nullHandle);
        if (Has(BasisCameraPass.Normals)) RecordBlit(renderGraph, BasisCameraPass.Normals, NormalsShaderPass, resources.cameraDepthTexture, resources.cameraNormalsTexture);
        if (Has(BasisCameraPass.Motion)) RecordBlit(renderGraph, BasisCameraPass.Motion, MotionShaderPass, resources.motionVectorColor, TextureHandle.nullHandle);
        if (Has(BasisCameraPass.Mask)) RecordMask(renderGraph, frameData, resources);
    }
    private RenderTexture FirstTarget()
    {
        for (int Index = 0; Index < wrapped.Length; Index++)
        {
            if (wrapped[Index] != null && BasisCameraPasses.Contains(passes, (BasisCameraPass)Index)) return wrapped[Index];
        }
        return null;
    }
    private void RecordBlit(RenderGraph renderGraph, BasisCameraPass pass, int shaderPass, TextureHandle first, TextureHandle second)
    {
        TextureHandle target = renderGraph.ImportTexture(handles[(int)pass]);
        using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass(PassNames[(int)pass], out BlitData passData, Sampler))
        {
            passData.Material = material;
            passData.Pass = shaderPass;
            if (first.IsValid()) builder.UseTexture(first, AccessFlags.Read);
            if (second.IsValid()) builder.UseTexture(second, AccessFlags.Read);
            builder.SetRenderAttachment(target, 0, AccessFlags.WriteAll);
            builder.AllowPassCulling(false);
            builder.SetRenderFunc(static (BlitData blit, RasterGraphContext context) => Blitter.BlitTexture(context.cmd, Identity, blit.Material, blit.Pass));
        }
    }
    private void RecordMask(RenderGraph renderGraph, ContextContainer frameData, UniversalResourceData resources)
    {
        RenderTexture output = wrapped[(int)BasisCameraPass.Mask];
        UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
        UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

        EnsureMaskTags();
        SortingSettings sorting = new SortingSettings(cameraData.camera) { criteria = SortingCriteria.CommonOpaque };
        DrawingSettings drawing = new DrawingSettings(maskTags[0], sorting) { overrideMaterial = maskMaterial, overrideMaterialPassIndex = 0, perObjectData = PerObjectData.None, enableInstancing = true };
        for (int Index = 1; Index < maskTags.Length; Index++) drawing.SetShaderPassName(Index, maskTags[Index]);
        FilteringSettings filtering = new FilteringSettings(RenderQueueRange.all, subjectLayers);
        RendererListHandle renderers = renderGraph.CreateRendererList(new RendererListParams(renderingData.cullResults, drawing, filtering));

        TextureDesc coverageDescriptor = new TextureDesc(output.width, output.height)
        {
            format = GraphicsFormat.R8_UNorm,
            msaaSamples = MaskSamples(),
            bindTextureMS = false,
            clearBuffer = true,
            clearColor = Color.clear,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            name = "_BasisCameraMaskCoverage",
        };
        TextureHandle coverage = renderGraph.CreateTexture(coverageDescriptor);

        using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass(PassNames[(int)BasisCameraPass.Mask], out MaskData passData, Sampler))
        {
            passData.Renderers = renderers;
            passData.Texel = new Vector4(1f / output.width, 1f / output.height, output.width, output.height);
            builder.SetRenderAttachment(coverage, 0, AccessFlags.Write);
            builder.UseRendererList(renderers);
            builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);
            builder.SetRenderFunc(static (MaskData mask, RasterGraphContext context) =>
            {
                context.cmd.SetGlobalVector(MaskTexelId, mask.Texel);
                context.cmd.DrawRendererList(mask.Renderers);
            });
        }

        TextureHandle target = renderGraph.ImportTexture(handles[(int)BasisCameraPass.Mask]);
        using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Basis Camera Mask Resolve", out ResolveData passData, Sampler))
        {
            passData.Coverage = coverage;
            passData.Material = material;
            builder.UseTexture(coverage, AccessFlags.Read);
            builder.SetRenderAttachment(target, 0, AccessFlags.WriteAll);
            builder.AllowPassCulling(false);
            builder.SetRenderFunc(static (ResolveData resolve, RasterGraphContext context) => Blitter.BlitTexture(context.cmd, resolve.Coverage, Identity, resolve.Material, MaskResolveShaderPass));
        }
    }
    private static void EnsureMaskTags()
    {
        if (maskTags != null) return;
        maskTags = new[] { new ShaderTagId("UniversalForward"), new ShaderTagId("UniversalForwardOnly"), new ShaderTagId("SRPDefaultUnlit"), new ShaderTagId("UniversalGBuffer") };
    }
    public static MSAASamples MaskSamples()
    {
        if (maskSamples < 0)
        {
            if (SystemInfo.IsFormatSupported(GraphicsFormat.R8_UNorm, GraphicsFormatUsage.MSAA4x)) maskSamples = 4;
            else if (SystemInfo.IsFormatSupported(GraphicsFormat.R8_UNorm, GraphicsFormatUsage.MSAA2x)) maskSamples = 2;
            else maskSamples = 1;
        }
        return (MSAASamples)maskSamples;
    }
}
