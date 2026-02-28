using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;

public class CopyCameraColorToStaticRTFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public class Settings
    {
        // Run late so URP's lighting/binning jobs are done.
        public RenderPassEvent when = RenderPassEvent.AfterRenderingPostProcessing;

        [Header("Output format")]
        public bool useHDR = false; // ARGBHalf if true
        public RenderTextureFormat ldrFormat = RenderTextureFormat.ARGB32;

        [Header("Blit fallback / MSAA resolve (required for MSAA cameras)")]
        public Material srpBlitMaterial; // Use Unity's "SRP Blit" shader

        [Header("RT sampling")]
        public FilterMode filterMode = FilterMode.Bilinear;
        public TextureWrapMode wrapMode = TextureWrapMode.Clamp;
    }

    public Settings settings = new Settings();

    public static RenderTexture OutputRT { get; private set; }

    class Pass : ScriptableRenderPass
    {
        public Settings settings;
        static RTHandle s_OutputHandle;

        public Pass()
        {
            // In many URP versions this is still the safest way to guarantee activeColorTexture is valid.
            requiresIntermediateTexture = true;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameContext)
        {
            var resourceData = frameContext.Get<UniversalResourceData>();

            if (s_OutputHandle == null || OutputRT == null || !OutputRT.IsCreated())
                return;

            TextureHandle src = resourceData.activeColorTexture;
            TextureHandle dst = renderGraph.ImportTexture(s_OutputHandle);

            // Decide whether copy is legal. Copy requires identical MSAA sample counts.
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
                // This path is what makes MSAA cameras work (resolve).
                if (settings.srpBlitMaterial == null)
                    return; // Without a blit material, you will get black in MSAA mismatch cases.

                var blitParams = new RenderGraphUtils.BlitMaterialParameters(src, dst, settings.srpBlitMaterial, 0);
                renderGraph.AddBlitPass(blitParams, passName: "Blit/Resolve CameraColor -> StaticRT");
            }
        }

        public static void EnsureRT(in RenderingData renderingData, Settings settings)
        {
            var camDesc = renderingData.cameraData.cameraTargetDescriptor;

            int w = Mathf.Max(1, camDesc.width);
            int h = Mathf.Max(1, camDesc.height);

            var fmt = settings.useHDR ? RenderTextureFormat.ARGBHalf : settings.ldrFormat;

            bool needsRebuild =
                OutputRT == null ||
                !OutputRT.IsCreated() ||
                OutputRT.width != w ||
                OutputRT.height != h ||
                OutputRT.format != fmt;

            if (!needsRebuild)
                return;

            CleanupStatic();

            OutputRT = new RenderTexture(w, h, 0, fmt)
            {
                name = "StaticCameraColorCopy",
                filterMode = settings.filterMode,
                wrapMode = settings.wrapMode,
                useMipMap = false,
                autoGenerateMips = false,

                // Keep destination single-sampled; blit path resolves MSAA into it.
                antiAliasing = 1
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
                if (OutputRT.IsCreated()) OutputRT.Release();
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
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (renderingData.cameraData.isSceneViewCamera || renderingData.cameraData.isPreviewCamera)
            return;

        Pass.EnsureRT(renderingData, settings);

        _pass.settings = settings;

        // Extra nudge later to dodge some URP job timing weirdness in certain versions:
        _pass.renderPassEvent = settings.when + 1;

        renderer.EnqueuePass(_pass);
    }

    protected override void Dispose(bool disposing)
    {
        Pass.CleanupStatic();
    }
}
