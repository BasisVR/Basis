using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Basis.Rendering.RTAO
{
    /// <summary>
    /// Draws one of the occlusion buffers over the frame. Which one is the point: an artifact looks identical
    /// in the final picture whichever stage produced it, so the only way to say where it comes from is to look
    /// at each stage in turn and find the first one that has it.
    /// </summary>
    internal sealed class BasisRTAODebugPass : ScriptableRenderPass
    {
        // Inserting the After Opaque pass shifted this; the debug view is the third pass on the shader.
        public const int ShaderPass = 2;

        private static readonly ProfilingSampler samplerDebug = new ProfilingSampler("BasisRTAO Debug");
        public static float GpuMs => samplerDebug.gpuElapsedTime;
        public static void SetProfilingEnabled(bool enabled) => samplerDebug.enableRecording = enabled;

        private Material material;
        private BasisRTAODebugStage stage;
        private MaterialPropertyBlock block;

        public BasisRTAODebugPass()
        {
            profilingSampler = samplerDebug;
            renderPassEvent = RenderPassEvent.AfterRenderingTransparents;
        }

        public void Setup(Material compositeMaterial, BasisRTAODebugStage debugStage)
        {
            material = compositeMaterial;
            stage = debugStage;
            block ??= new MaterialPropertyBlock();
        }

        /// <summary>How the shader should read the channels of whichever buffer it was handed.</summary>
        internal enum Interpretation
        {
            Visibility = 0,
            Position = 1,
            Normal = 2
        }

        /// <summary>
        /// Picks the buffer for a stage, falling back to the composited result when that stage did not run
        /// this frame - with no denoise passes there is no denoised buffer, and asking for it should show the
        /// truth rather than nothing.
        /// </summary>
        /// <summary>
        /// How a stage should be read, with no reference to whether it exists this frame. Kept separate from
        /// the pick because a TextureHandle can only be made valid by a recording render graph, so this half
        /// is the half a test can actually reach.
        /// </summary>
        /// <returns>True when the stage is one of the trace resolution buffers.</returns>
        internal static bool MapStage(BasisRTAODebugStage stage, int traceScale,
            out Interpretation interpretation, out int scale)
        {
            interpretation = Interpretation.Visibility;
            scale = traceScale;

            switch (stage)
            {
                case BasisRTAODebugStage.Raw:
                case BasisRTAODebugStage.Temporal:
                case BasisRTAODebugStage.Denoised:
                    return true;
                case BasisRTAODebugStage.Position:
                    interpretation = Interpretation.Position;
                    return true;
                case BasisRTAODebugStage.Normal:
                    interpretation = Interpretation.Normal;
                    return true;
                default:
                    scale = 1;
                    return false;
            }
        }

        private static TextureHandle HandleFor(BasisRTAODebugStage stage, BasisRTAOResolvedTexture textures)
        {
            switch (stage)
            {
                case BasisRTAODebugStage.Raw: return textures.raw;
                case BasisRTAODebugStage.Temporal: return textures.temporal;
                case BasisRTAODebugStage.Denoised: return textures.denoised;
                case BasisRTAODebugStage.Position: return textures.position;
                case BasisRTAODebugStage.Normal: return textures.normal;
                default: return textures.handle;
            }
        }

        /// <returns>The stage actually shown, which differs from the one asked for when it did not run.</returns>
        internal static BasisRTAODebugStage SelectStage(BasisRTAODebugStage stage, BasisRTAOResolvedTexture textures,
            out TextureHandle handle, out Interpretation interpretation, out int scale, out bool fromStageArray)
        {
            fromStageArray = MapStage(stage, textures.scale, out interpretation, out scale);
            handle = HandleFor(stage, textures);

            if (!fromStageArray || handle.IsValid())
                return fromStageArray ? stage : BasisRTAODebugStage.Final;

            // The stage did not run this frame - no denoise passes means no denoised buffer - so show the
            // composited result rather than nothing at all.
            handle = textures.handle;
            interpretation = Interpretation.Visibility;
            scale = 1;
            fromStageArray = false;
            return BasisRTAODebugStage.Final;
        }

        private class DebugData
        {
            public Material material;
            public MaterialPropertyBlock block;
            public TextureHandle source;
            public TextureHandle resolved;
            public int interpretation;
            public int scale;
            public int fromStageArray;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (material == null || !frameData.Contains<BasisRTAOResolvedTexture>())
                return;

            BasisRTAOResolvedTexture textures = frameData.Get<BasisRTAOResolvedTexture>();
            if (!textures.handle.IsValid())
                return;

            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            if (!resourceData.activeColorTexture.IsValid())
                return;

            SelectStage(stage, textures, out TextureHandle source, out Interpretation interpretation,
                out int scale, out bool fromStageArray);
            if (!source.IsValid())
                return;

            using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass<DebugData>("BasisRTAO Debug View", out DebugData data, profilingSampler))
            {
                data.material = material;
                data.block = block;
                data.source = source;
                data.resolved = textures.handle;
                data.interpretation = (int)interpretation;
                data.scale = Mathf.Max(1, scale);
                data.fromStageArray = fromStageArray ? 1 : 0;

                builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.Write);
                builder.UseTexture(source, AccessFlags.Read);
                if (fromStageArray)
                    builder.UseTexture(textures.handle, AccessFlags.Read);
                builder.AllowPassCulling(false);

                builder.SetRenderFunc(static (DebugData data, RasterGraphContext ctx) =>
                {
                    data.block.Clear();
                    // Both are bound every time. A declared but unbound texture still has to resolve to
                    // something, and the branch that skips it is a runtime branch, not a compile time one.
                    data.block.SetTexture(BasisRTAOShaderIds.DebugStageTex, data.source);
                    data.block.SetTexture(BasisRTAOShaderIds.DebugResolvedTex, data.resolved);
                    data.material.SetInteger(BasisRTAOShaderIds.DebugInterpretation, data.interpretation);
                    data.material.SetInteger(BasisRTAOShaderIds.DebugStageScale, data.scale);
                    data.material.SetInteger(BasisRTAOShaderIds.DebugFromStageArray, data.fromStageArray);
                    CoreUtils.DrawFullScreen(ctx.cmd, data.material, data.block, ShaderPass);
                });
            }
        }
    }
}
