using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Basis.Scripts.BasisSdk.Highlight
{
    /// <summary>
    /// RenderGraph pass implementation for BasisHighlight:
    ///   1. Mask     — draw every active renderer with its override material into
    ///                 a half-res R8 silhouette texture.
    ///   2. Dilate   — separable H/V max filter that grows the silhouette by N
    ///                 pixels. (dilated − raw) is the outline ring; because max
    ///                 filtering produces a constant-thickness band, the ring
    ///                 exactly follows the contour, including concave corners
    ///                 (no gaussian iso-line "skipping" across nub junctions).
    ///   3. Glow     — optional separable gaussian on the dilated mask, used as
    ///                 the soft halo trailing outward from the silhouette.
    ///   4. Composite — combine raw, dilated, and glow into a ring + halo and
    ///                 alpha-blend over camera color.
    /// </summary>
    internal class BasisHighlightPass : ScriptableRenderPass
    {
        private static readonly int PropMask = Shader.PropertyToID("_BasisHighlightMask");
        private static readonly int PropDilated = Shader.PropertyToID("_BasisHighlightDilated");
        private static readonly int PropColor = Shader.PropertyToID("_BasisHighlightColor");
        private static readonly int PropSoftness = Shader.PropertyToID("_BasisHighlightSoftness");
        private static readonly int PropGlow = Shader.PropertyToID("_BasisHighlightGlow");
        private static readonly int PropInteriorFill = Shader.PropertyToID("_BasisHighlightInteriorFill");
        private static readonly int PropBlurRadius = Shader.PropertyToID("_BasisHighlightBlurRadius");
        private static readonly int PropDilateRadius = Shader.PropertyToID("_BasisHighlightDilateRadius");
        private static readonly int PropDebugMode = Shader.PropertyToID("_BasisHighlightDebugMode");

        // Pass indices in BasisHighlightBlur.shader
        private const int PassBlurH = 0;
        private const int PassBlurV = 1;
        private const int PassDilateH = 2;
        private const int PassDilateV = 3;

        internal static readonly List<BasisHighlightPass> Live = new();

        private readonly BasisHighlightSettings _settings;
        private Color _outlineColor;

        public BasisHighlightPass(BasisHighlightSettings settings)
        {
            _settings = settings;
            _outlineColor = settings.outlineColor;
            profilingSampler = new ProfilingSampler("BasisHighlight");

            // The composite hardware-blends a full-screen pass over the camera
            // color. On untethered VR (Quest/Adreno) URP renders the camera
            // straight to the backbuffer; our mask/dilate/blur passes split the
            // render pass, so the composite would have to reload the backbuffer
            // to blend over it — backbuffers aren't reloadable across that split,
            // so the scene is lost (black) and only the freshly-drawn ring shows.
            // Forcing an intermediate color texture gives a reloadable target so
            // the prior scene survives. Desktop already uses an intermediate
            // texture, which is why the bug was Quest-only. Same flag URP's own
            // FullScreenPassRendererFeature sets for its blend-over-color case.
            requiresIntermediateTexture = true;
        }

        internal void SetOutlineColor(Color color)
        {
            _outlineColor = color;
        }

        internal void ResetOutlineColor()
        {
            _outlineColor = _settings.outlineColor;
        }

        // ------------- Pass data records (per render-graph pass) -------------

        private class MaskPassData
        {
            public Material fallbackMaterial;
        }

        private class FilterPassData
        {
            public Material material;
            public TextureHandle source;
            public int passIndex;
            public float blurRadius;
            public float dilateRadius;
        }

        private class CompositePassData
        {
            public Material compositeMaterial;
            public TextureHandle glow;
            public TextureHandle dilated;
            public TextureHandle rawMask;
            public Color color;
            public float softness;
            public float glowIntensity;
            public float interiorFill;
            public int debugMode;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (BasisHighlightManager.ActiveCount == 0)
            {
                return;
            }

            if (_settings.defaultMaskMaterial == null
                || _settings.compositeMaterial == null
                || _settings.blurMaterial == null)
            {
                return;
            }

            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

            RenderTextureDescriptor camDesc = cameraData.cameraTargetDescriptor;
            int downsample = Mathf.Clamp(_settings.maskDownsample, 1, 4);
            int w = Mathf.Max(1, camDesc.width / downsample);
            int h = Mathf.Max(1, camDesc.height / downsample);

            RenderTextureDescriptor maskDesc = new RenderTextureDescriptor(w, h, GraphicsFormat.R8_UNorm, GraphicsFormat.None, 0)
            {
                msaaSamples = 1, // intermediates (dilate/soften/glow) stay 1x
                useMipMap = false,
                autoGenerateMips = false,
                // Preserve XR setup so single-pass instanced (Quest) sees the
                // same texture-array layout the camera target uses.
                volumeDepth = camDesc.volumeDepth,
                dimension = camDesc.dimension,
                vrUsage = camDesc.vrUsage,
            };

            // MSAA-only descriptor for the silhouette. Geometry rasterized with N
            // samples gives true coverage AA on the contour; RenderGraph auto-
            // resolves to a single-sample texture when DilateH / Composite sample it.
            RenderTextureDescriptor maskDescMS = maskDesc;
            maskDescMS.msaaSamples = (int)_settings.maskMsaa;
            // Clamp to what the platform actually supports for this format.
            maskDescMS.msaaSamples = SystemInfo.GetRenderTextureSupportedMSAASampleCount(maskDescMS);

            TextureHandle rawMask = UniversalRenderer.CreateRenderGraphTexture(
                renderGraph, maskDescMS, "_BasisHighlightMask", clear: true);

            // --- 1. Mask pass ---
            using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass<MaskPassData>(
                       "BasisHighlight Mask", out MaskPassData data, profilingSampler))
            {
                data.fallbackMaterial = _settings.defaultMaskMaterial;
                builder.SetRenderAttachment(rawMask, 0, AccessFlags.Write);
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(false);

                builder.SetRenderFunc((MaskPassData d, RasterGraphContext ctx) => { BasisHighlightManager.DrawAll(ctx.cmd, d.fallbackMaterial); });
            }

            // --- 2. Dilation (H then V) into dedicated buffers ---
            // Use two separate buffers — we never sample-and-write the same one
            // in a single pass, and the final dilatedV needs to live until the
            // composite reads it.
            TextureHandle dilateH = UniversalRenderer.CreateRenderGraphTexture(
                renderGraph, maskDesc, "_BasisHighlightDilateH", clear: false);
            TextureHandle dilated = UniversalRenderer.CreateRenderGraphTexture(
                renderGraph, maskDesc, "_BasisHighlightDilated", clear: false);

            float dilateRadius = Mathf.Clamp(_settings.outlineWidth, 0, 8);
            Blit1D(renderGraph, rawMask, dilateH, _settings.blurMaterial, PassDilateH,
                blurRadius: 0f, dilateRadius: dilateRadius, "BasisHighlight DilateH");
            Blit1D(renderGraph, dilateH, dilated, _settings.blurMaterial, PassDilateV,
                blurRadius: 0f, dilateRadius: dilateRadius, "BasisHighlight DilateV");

            // --- 2b. Ring-soften: small gaussian on the dilated mask so the
            // composite's smoothstep has a real 0..1 gradient to work with.
            // Without this, the dilated mask is essentially binary and any
            // value of ringSoftness collapses to a hard step. Kept separate
            // from the glow blur so it doesn't grow with glowRadius. Skipped
            // when ringSoftenRadius is ~0 to save two passes.
            TextureHandle dilatedSoft = dilated;
            if (_settings.ringSoftenRadius > 0.01f)
            {
                TextureHandle softH = UniversalRenderer.CreateRenderGraphTexture(
                    renderGraph, maskDesc, "_BasisHighlightDilatedSoftH", clear: false);
                TextureHandle softV = UniversalRenderer.CreateRenderGraphTexture(
                    renderGraph, maskDesc, "_BasisHighlightDilatedSoft", clear: false);
                Blit1D(renderGraph, dilated, softH, _settings.blurMaterial, PassBlurH,
                    _settings.ringSoftenRadius, 0f, "BasisHighlight RingSoftenH");
                Blit1D(renderGraph, softH, softV, _settings.blurMaterial, PassBlurV,
                    _settings.ringSoftenRadius, 0f, "BasisHighlight RingSoftenV");
                dilatedSoft = softV;
            }

            // --- 2c. Inner-edge soften: optional gaussian on the RAW mask so the
            // composite's inner smoothstep (rawAA) has a multi-pixel gradient and
            // the inner edge feathers. Radius is independent from RingSoften, so
            // the inner edge (touching the object) can be softened separately from
            // the outer edge. Crucially this blurs a COPY — the dilation chain
            // above still reads the sharp rawMask, so ring width and concave-corner
            // following are unaffected. When innerSoftenRadius ~0 we skip the two
            // passes and the composite samples the crisp mask (MSAA AA only).
            TextureHandle rawSoft = rawMask;
            if (_settings.innerSoftenRadius > 0.01f)
            {
                TextureHandle rawSoftH = UniversalRenderer.CreateRenderGraphTexture(
                    renderGraph, maskDesc, "_BasisHighlightRawSoftH", clear: false);
                TextureHandle rawSoftV = UniversalRenderer.CreateRenderGraphTexture(
                    renderGraph, maskDesc, "_BasisHighlightRawSoft", clear: false);
                Blit1D(renderGraph, rawMask, rawSoftH, _settings.blurMaterial, PassBlurH,
                    _settings.innerSoftenRadius, 0f, "BasisHighlight InnerSoftenH");
                Blit1D(renderGraph, rawSoftH, rawSoftV, _settings.blurMaterial, PassBlurV,
                    _settings.innerSoftenRadius, 0f, "BasisHighlight InnerSoftenV");
                rawSoft = rawSoftV;
            }

            // --- 3. Glow: gaussian on the dilated mask (optional) ---
            TextureHandle glow = dilated; // default: skip blur, use dilated as glow source
            int glowIterations = Mathf.Max(0, _settings.glowIterations);
            if (glowIterations > 0 && _settings.glowIntensity > 0f)
            {
                // The halo carries nothing the mask resolution is protecting - the composite feathers it
                // against the ring's outer edge and never reads a hard boundary out of it - so the gaussian
                // runs at half the mask's resolution and the composite's bilinear read puts it back. That is
                // a quarter of the fragments across every glow blit, and at the shipped desktop settings
                // (two iterations, four blits) the glow is the largest single cost in the chain.
                //
                // The kernel offsets come from the SOURCE texel size, so the extent has to be kept by hand:
                // the first blit still reads the mask-resolution dilated texture and keeps the authored
                // radius, every blit after it reads a texture with twice the texel size and halves it. All
                // nine taps then land on the screen positions they occupied before, so the halo keeps its
                // width rather than doubling it.
                RenderTextureDescriptor glowDesc = maskDesc;
                glowDesc.width = Mathf.Max(1, maskDesc.width / 2);
                glowDesc.height = Mathf.Max(1, maskDesc.height / 2);

                TextureHandle bufA = UniversalRenderer.CreateRenderGraphTexture(
                    renderGraph, glowDesc, "_BasisHighlightBlurA", clear: false);
                TextureHandle bufB = UniversalRenderer.CreateRenderGraphTexture(
                    renderGraph, glowDesc, "_BasisHighlightBlurB", clear: false);

                TextureHandle src = dilated;
                TextureHandle dst = bufA;
                float maskResRadius = _settings.glowRadius;
                float glowResRadius = _settings.glowRadius * 0.5f;
                bool readingMaskRes = true;

                for (int i = 0; i < glowIterations; i++)
                {
                    Blit1D(renderGraph, src, dst, _settings.blurMaterial, PassBlurH,
                        readingMaskRes ? maskResRadius : glowResRadius, 0f, "BasisHighlight BlurH");
                    readingMaskRes = false;
                    src = dst;
                    dst = src.Equals(bufA) ? bufB : bufA;

                    Blit1D(renderGraph, src, dst, _settings.blurMaterial, PassBlurV,
                        glowResRadius, 0f, "BasisHighlight BlurV");
                    src = dst;
                    dst = src.Equals(bufA) ? bufB : bufA;
                }

                glow = src;
            }

            // --- 4. Composite ---
            using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass<CompositePassData>(
                       "BasisHighlight Composite", out CompositePassData data, profilingSampler))
            {
                data.compositeMaterial = _settings.compositeMaterial;
                data.glow = glow;
                data.dilated = dilatedSoft; // softened mask drives ring AA
                data.rawMask = rawSoft; // inner-softened mask drives inner-edge AA
                data.color = _outlineColor;
                data.softness = _settings.ringSoftness;
                data.glowIntensity = _settings.glowIntensity;
                data.interiorFill = _settings.interiorFill;
                data.debugMode = (int)_settings.debugMode;

                builder.UseTexture(glow, AccessFlags.Read);
                if (!dilatedSoft.Equals(glow))
                {
                    builder.UseTexture(dilatedSoft, AccessFlags.Read);
                }

                builder.UseTexture(rawSoft, AccessFlags.Read);

                builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.Write);
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc((CompositePassData d, RasterGraphContext ctx) =>
                {
                    Material mat = d.compositeMaterial;
                    mat.SetTexture(PropMask, d.rawMask);
                    mat.SetTexture(PropDilated, d.dilated);

                    mat.SetColor(PropColor, d.color);
                    mat.SetFloat(PropSoftness, d.softness);
                    mat.SetFloat(PropGlow, d.glowIntensity);
                    mat.SetFloat(PropInteriorFill, d.interiorFill);
                    mat.SetFloat(PropDebugMode, (float)d.debugMode);

                    // glow goes through _BlitTexture
                    Blitter.BlitTexture(ctx.cmd, d.glow, new Vector4(1, 1, 0, 0), mat, 0);
                });
            }
        }

        private static void Blit1D(
            RenderGraph rg, TextureHandle src, TextureHandle dst,
            Material mat, int passIndex,
            float blurRadius, float dilateRadius, string passName)
        {
            using IRasterRenderGraphBuilder builder = rg.AddRasterRenderPass<FilterPassData>(passName, out FilterPassData data);

            data.material = mat;
            data.source = src;
            data.passIndex = passIndex;
            data.blurRadius = blurRadius;
            data.dilateRadius = dilateRadius;

            builder.UseTexture(src, AccessFlags.Read);
            builder.SetRenderAttachment(dst, 0, AccessFlags.Write);
            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);

            builder.SetRenderFunc((FilterPassData d, RasterGraphContext ctx) =>
            {
                // Use queued cmd.SetGlobalFloat (NOT mat.SetFloat). All four
                // blur-shader passes share one material instance; mat.SetFloat
                // mutates CPU state immediately, but Blitter.BlitTexture queues
                // a GPU draw that runs later. By GPU execution time the material
                // sees whichever SetFloat ran LAST during recording, so the
                // dilate passes would silently use the blur passes' radius=0
                // and produce no dilation. Global-float commands are queued in
                // order with the draws, so each draw gets the right value.
                ctx.cmd.SetGlobalFloat(PropBlurRadius, d.blurRadius);
                ctx.cmd.SetGlobalFloat(PropDilateRadius, d.dilateRadius);
                Blitter.BlitTexture(ctx.cmd, d.source, new Vector4(1, 1, 0, 0), d.material, d.passIndex);
            });
        }
    }
}
