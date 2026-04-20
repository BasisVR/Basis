using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Experimental.Rendering;

public class CopyCameraColorToStaticRTFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public class Settings
    {
        // Default: after PP (so you get the final tonemapped image).
        // If you want HDR / pre-tonemap, use BeforeRenderingPostProcessing instead.
        public RenderPassEvent when = RenderPassEvent.AfterRenderingPostProcessing;

        [Header("Destination format")]
        [Tooltip("If true, forces an HDR graphics format when possible (R16G16B16A16_SFloat). Otherwise matches the camera target format.")]
        public bool forceHDR = false;

        [Header("Blit fallback / MSAA resolve (required for MSAA cameras)")]
        [Tooltip("Use Unity's URP 'SRP Blit' material (or your own pure-copy blit). Required when MSAA mismatch prevents Copy.")]
        public Material srpBlitMaterial;

        [Header("RT sampling")]
        public FilterMode filterMode = FilterMode.Bilinear;
        public TextureWrapMode wrapMode = TextureWrapMode.Clamp;

        [Header("Safety")]
        [Tooltip("Disables SRP Blit color conversion keywords to avoid double sRGB/Linear conversion (common 'blown out' build issue).")]
        public bool disableSrpBlitColorConversionKeywords = true;

        [Tooltip("If true, forces OutputRT to use the camera descriptor's sRGB flag (recommended).")]
        public bool matchCameraSRGB = true;
    }

    public Settings settings = new Settings();

    public static RenderTexture OutputRT { get; private set; }

    class Pass : ScriptableRenderPass
    {
        public Settings settings;
        static RTHandle s_OutputHandle;

        public Pass()
        {
            // Keeps activeColorTexture valid across URP versions.
            requiresIntermediateTexture = true;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameContext)
        {
            var resourceData = frameContext.Get<UniversalResourceData>();

            if (s_OutputHandle == null || OutputRT == null || !OutputRT.IsCreated())
                return;

            var src = resourceData.activeColorTexture;
            var dst = renderGraph.ImportTexture(s_OutputHandle);

            // Copy is only legal if MSAA sample counts match.
            var srcDesc = renderGraph.GetTextureDesc(src);
            var dstDesc = renderGraph.GetTextureDesc(dst);

            bool msaaMatches = srcDesc.msaaSamples == dstDesc.msaaSamples;
            bool canCopy = msaaMatches && RenderGraphUtils.CanAddCopyPassMSAA();

            if (canCopy)
            {
                renderGraph.AddCopyPass(src, dst, passName: "Copy CameraColor -> StaticRT");
            }
            else
            {
                // Needed for MSAA resolve (src MSAA -> dst single-sample).
                if (settings.srpBlitMaterial == null)
                    return;

                if (settings.disableSrpBlitColorConversionKeywords)
                {
                    // Prevent “blown out / washed out” builds caused by double linear<->sRGB conversion.
                    settings.srpBlitMaterial.DisableKeyword("_LINEAR_TO_SRGB_CONVERSION");
                    settings.srpBlitMaterial.DisableKeyword("_SRGB_TO_LINEAR_CONVERSION");
                }

                var blitParams = new RenderGraphUtils.BlitMaterialParameters(src, dst, settings.srpBlitMaterial, 0);
                renderGraph.AddBlitPass(blitParams, passName: "Blit/Resolve CameraColor -> StaticRT");
            }
        }

        public static void EnsureRT(in RenderingData renderingData, Settings settings)
        {
            var camDesc = renderingData.cameraData.cameraTargetDescriptor;

            int w = Mathf.Max(1, camDesc.width);
            int h = Mathf.Max(1, camDesc.height);

            // Build a descriptor that MATCHES the camera’s real target (including sRGB),
            // then make it single-sampled because we resolve MSAA via blit path.
            var desc = camDesc;
            desc.width = w;
            desc.height = h;
            desc.depthBufferBits = 0;
            desc.msaaSamples = 1;
            desc.useMipMap = false;
            desc.autoGenerateMips = false;

            if (settings.matchCameraSRGB)
                desc.sRGB = camDesc.sRGB;

            // Optional: force HDR (but pick a compatible format on the platform/GPU).
            if (settings.forceHDR)
            {
                var requested = GraphicsFormat.R16G16B16A16_SFloat;
                desc.graphicsFormat = SystemInfo.GetCompatibleFormat(requested, FormatUsage.Render);
            }
            else
            {
                // Keep camera's format, but ensure it's actually renderable on the platform.
                desc.graphicsFormat = SystemInfo.GetCompatibleFormat(camDesc.graphicsFormat, FormatUsage.Render);
            }

            bool needsRebuild =
                OutputRT == null ||
                !OutputRT.IsCreated() ||
                OutputRT.width != desc.width ||
                OutputRT.height != desc.height ||
                OutputRT.descriptor.graphicsFormat != desc.graphicsFormat ||
                OutputRT.descriptor.sRGB != desc.sRGB;

            if (!needsRebuild)
                return;

            CleanupStatic();

            OutputRT = new RenderTexture(desc)
            {
                name = "StaticCameraColorCopy",
                filterMode = settings.filterMode,
                wrapMode = settings.wrapMode,
            };

            OutputRT.Create();
            s_OutputHandle = RTHandles.Alloc(OutputRT);
        }

        public static void CleanupStatic()
        {
            if (s_OutputHandle != null)
            {
                s_OutputHandle.Release();
                s_OutputHandle = null;
            }

            if (OutputRT != null)
            {
                if (OutputRT.IsCreated())
                    OutputRT.Release();

                Object.Destroy(OutputRT);
                OutputRT = null;
            }
        }
    }

    Pass _pass;

    public override void Create()
    {
        _pass = new Pass
        {
            settings = settings,
            renderPassEvent = settings.when
        };

        // Pre-emptively nuke conversion keywords (helps even if they get toggled later).
        if (settings.srpBlitMaterial && settings.disableSrpBlitColorConversionKeywords)
        {
            settings.srpBlitMaterial.DisableKeyword("_LINEAR_TO_SRGB_CONVERSION");
            settings.srpBlitMaterial.DisableKeyword("_SRGB_TO_LINEAR_CONVERSION");
        }
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (renderingData.cameraData.isSceneViewCamera || renderingData.cameraData.isPreviewCamera)
            return;

        Pass.EnsureRT(renderingData, settings);

        _pass.settings = settings;

        // Don’t “+ 1” the pass event; that can push you into weird territory across URP versions.
        // If you need a different timing, change Settings.when explicitly.
        _pass.renderPassEvent = settings.when;

        renderer.EnqueuePass(_pass);
    }

    protected override void Dispose(bool disposing)
    {
        Pass.CleanupStatic();
    }
}
