using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Basis.Rendering.RTAO
{
    /// <summary>
    /// Multiplies the finished opaque image by the occlusion, the way URP's own SSAO does in its After Opaque
    /// mode. This is the path that reaches surfaces the lighting path cannot: shaders that never sample
    /// _ScreenSpaceOcclusionTexture, materials whose own occlusion map would otherwise clamp the result, and
    /// anything lit almost entirely by a direct light.
    /// </summary>
    public sealed class BasisRTAOAfterOpaquePass : ScriptableRenderPass
    {
        public const int ShaderPass = 1;

        private static readonly ProfilingSampler samplerAfterOpaque = new ProfilingSampler("BasisRTAO After Opaque");
        public static float GpuMs => samplerAfterOpaque.gpuElapsedTime;
        public static void SetProfilingEnabled(bool enabled) => samplerAfterOpaque.enableRecording = enabled;

        private Material material;
        private MaterialPropertyBlock block;

        public BasisRTAOAfterOpaquePass()
        {
            profilingSampler = samplerAfterOpaque;
            renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
        }

        public void Setup(Material compositeMaterial)
        {
            material = compositeMaterial;
            block ??= new MaterialPropertyBlock();
        }

        private class PassData
        {
            public Material material;
            public MaterialPropertyBlock block;
            public TextureHandle resolved;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (material == null || !frameData.Contains<BasisRTAOResolvedTexture>())
                return;

            BasisRTAOResolvedTexture resolved = frameData.Get<BasisRTAOResolvedTexture>();
            if (!resolved.handle.IsValid())
                return;

            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            if (!resourceData.activeColorTexture.IsValid())
                return;

            using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass<PassData>("BasisRTAO After Opaque", out PassData data, profilingSampler))
            {
                data.material = material;
                data.block = block;
                data.resolved = resolved.handle;

                builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.ReadWrite);
                builder.UseTexture(resolved.handle, AccessFlags.Read);
                builder.AllowPassCulling(false);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
                {
                    data.block.Clear();
                    data.block.SetTexture(BasisRTAOShaderIds.ResolvedAfterOpaqueTex, data.resolved);
                    CoreUtils.DrawFullScreen(ctx.cmd, data.material, data.block, ShaderPass);
                });
            }
        }
    }
}
