using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

/// <summary>
/// The renderer feature behind the hand-held camera's Direct To Screen mode.
///
/// <para>
/// While a camera is presenting, its screen camera (<see cref="BasisCameraDirectToScreenOutput"/>)
/// renders an otherwise empty frame to the game window after every other camera, and this feature
/// draws the camera's feed into that frame, letterboxed to the shot's aspect — so the monitor shows
/// the shot instead of the headset mirror. It enqueues nothing on any other camera, so it is free
/// wherever else it happens to run and can sit on a renderer permanently.
/// </para>
///
/// <para>
/// URP draws the XR mirror view at the end of the VR camera's stack, and the screen camera renders
/// after that stack, so this covers the mirror rather than fighting it — and nothing here touches
/// the mirror settings, which is what makes the hand-back on a switch to desktop nothing more than
/// the screen camera going quiet.
/// </para>
/// </summary>
[DisallowMultipleRendererFeature("Basis Direct To Screen")]
public sealed class BasisCameraDirectToScreenFeature : ScriptableRendererFeature
{
    private BasisCameraDirectToScreenPass pass;

    public override void Create()
    {
        pass = new BasisCameraDirectToScreenPass();
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (pass == null) return;

        BasisCameraDirectToScreenOutput output = BasisCameraDirectToScreenOutput.Presenting;
        if (output == null || !output.IsScreenCamera(renderingData.cameraData.camera)) return;
        if (!output.TryGetFeed(out RTHandle handle, out RenderTexture texture)) return;

        pass.Setup(handle, texture);
        renderer.EnqueuePass(pass);
    }
}

/// <summary>
/// What the pass last drew. The editor test window reads it to say whether Direct To Screen is
/// really reaching the window, and with what.
/// </summary>
public struct BasisCameraDirectToScreenPassInfo
{
    /// <summary>The <see cref="Time.frameCount"/> the pass was last recorded on, or -1 if never.</summary>
    public int Frame;
    public Rect Viewport;
    public int SourceWidth;
    public int SourceHeight;
    public int SourceSamples;
    public GraphicsFormat SourceFormat;
    public int TargetWidth;
    public int TargetHeight;
    public int TargetSamples;
    public GraphicsFormat TargetFormat;
}

/// <summary>
/// Draws a feed into the colour target of the camera it is enqueued on, fitted inside it with the
/// shot's aspect kept: bars rather than a stretched or cropped picture, since the point of the mode
/// is to see the frame that is being shot. Shared by <see cref="BasisCameraDirectToScreenFeature"/>
/// and the fallback that enqueues it by hand when no renderer on the pipeline carries the feature.
///
/// <para>
/// It draws into the camera's own colour target rather than straight onto the window, and before
/// URP's final blit. That blit is what knows how to put a picture on this display — the sRGB or
/// HDR encoding the display needs, its colour gamut, the flip between a texture and the window —
/// so the feed rides it exactly as any camera's frame does. Asking for the intermediate texture is
/// what guarantees there is a final blit to ride.
/// </para>
/// </summary>
public sealed class BasisCameraDirectToScreenPass : ScriptableRenderPass
{
    private static readonly ProfilingSampler PassSampler = new ProfilingSampler("Basis Direct To Screen");
    public static float GpuMs => PassSampler.gpuElapsedTime;
    public static void SetProfilingEnabled(bool enabled) => PassSampler.enableRecording = enabled;

    /// <summary>The last recording, for the editor test window. Plain values, so it costs nothing per frame.</summary>
    public static BasisCameraDirectToScreenPassInfo LastRecorded = new BasisCameraDirectToScreenPassInfo { Frame = -1 };

    private RTHandle feedHandle;
    private RenderTexture feedTexture;

    public BasisCameraDirectToScreenPass()
    {
        renderPassEvent = RenderPassEvent.AfterRenderingTransparents;
        requiresIntermediateTexture = true;
        profilingSampler = PassSampler;
    }

    /// <summary>
    /// The feed to draw on the next execution: the camera's live render texture, and a handle
    /// wrapping it by identifier (see <see cref="BasisCameraDirectToScreenOutput.SetFeed"/>).
    /// </summary>
    public void Setup(RTHandle handle, RenderTexture texture)
    {
        feedHandle = handle;
        feedTexture = texture;
    }

    private sealed class PassData
    {
        public TextureHandle Source;
        public RenderTargetIdentifier SourceId;
        public TextureHandle Destination;
        public Rect Viewport;
    }

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        if (feedHandle == null || feedTexture == null || !feedTexture.IsCreated()) return;

        UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
        TextureHandle destination = resourceData.activeColorTexture;
        if (!destination.IsValid()) return;

        // Worked out in the target's own pixels — with a render scale that is not the window's
        // size — and carried to the window by URP's final blit, which scales the whole target.
        RenderTargetInfo target = renderGraph.GetRenderTargetInfo(destination);
        Rect viewport = FitViewport(feedTexture.width, feedTexture.height, new Rect(0f, 0f, target.width, target.height));
        if (viewport.width < 1f || viewport.height < 1f) return;

        // The feed is the capture camera's target: a render texture with a depth buffer of its own,
        // and multisampled when the camera is. The graph refuses to derive a description from a
        // texture that is both colour and depth, so it is told what it is being handed — the colour
        // side only, at its true sample count — the way URP imports its own camera targets. A
        // sampled read of a multisampled texture takes the resolved picture, which is what a blit
        // wants; and a float (HDR) feed goes through untouched for the final blit to encode.
        RenderTargetInfo source = new RenderTargetInfo
        {
            width = feedTexture.width,
            height = feedTexture.height,
            volumeDepth = 1,
            msaaSamples = Mathf.Max(1, feedTexture.antiAliasing),
            format = feedTexture.graphicsFormat,
            bindMS = false,
        };
        ImportResourceParams importParams = new ImportResourceParams
        {
            clearOnFirstUse = false,
            discardOnLastUse = false,
            textureUVOrigin = TextureUVOrigin.BottomLeft,
        };

        using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Basis Direct To Screen", out PassData passData, PassSampler))
        {
            passData.Source = renderGraph.ImportTexture(feedHandle, source, importParams);
            passData.SourceId = new RenderTargetIdentifier(feedTexture);
            passData.Destination = destination;
            passData.Viewport = viewport;

            builder.UseTexture(passData.Source, AccessFlags.Read);
            builder.SetRenderAttachment(destination, 0, AccessFlags.Write);
            builder.AllowPassCulling(false);
            builder.SetRenderFunc(static (PassData data, RasterGraphContext context) =>
            {
                // Texture to texture, so ordinarily no flip — but asked rather than assumed, the way
                // URP's own blits ask, so a target that turns out to be the window is still right.
                bool flip = context.GetTextureUVOrigin(in data.Source) != context.GetTextureUVOrigin(in data.Destination);
                Vector4 scaleBias = flip ? new Vector4(1f, -1f, 0f, 1f) : new Vector4(1f, 1f, 0f, 0f);

                // Bound by identifier rather than through the handle. The handle wraps an identifier
                // (see BasisCameraDirectToScreenOutput.SetFeed), so the handle overload would give the
                // property block a texture it does not have; the graph already knows the dependency
                // from UseTexture above, and this is only how the shader reaches the same texture.
                Material material = Blitter.GetBlitMaterial(TextureDimension.Tex2D);
                context.cmd.SetViewport(data.Viewport);
                Blitter.BlitTexture(context.cmd, data.SourceId, scaleBias, material, BilinearPass(material));
            });
        }

        LastRecorded = new BasisCameraDirectToScreenPassInfo
        {
            Frame = Time.frameCount,
            Viewport = viewport,
            SourceWidth = source.width,
            SourceHeight = source.height,
            SourceSamples = source.msaaSamples,
            SourceFormat = source.format,
            TargetWidth = target.width,
            TargetHeight = target.height,
            TargetSamples = target.msaaSamples,
            TargetFormat = target.format,
        };
    }

    private static int bilinearPass = -1;

    /// <summary>
    /// The bilinear pass of the core blit shader, found by name once. Pass 1 is where it has
    /// always been, and is the fallback should the name ever go missing.
    /// </summary>
    private static int BilinearPass(Material material)
    {
        if (bilinearPass < 0)
        {
            int found = material != null ? material.FindPass("Bilinear") : -1;
            bilinearPass = found >= 0 ? found : 1;
        }
        return bilinearPass;
    }

    /// <summary>
    /// The largest rectangle of the feed's aspect that fits inside <paramref name="window"/>, centred:
    /// the letterbox or pillarbox the picture is drawn in. Snapped to whole pixels so the bars have a
    /// hard edge rather than a filtered one.
    /// </summary>
    public static Rect FitViewport(int feedWidth, int feedHeight, Rect window)
    {
        if (feedWidth <= 0 || feedHeight <= 0 || window.width <= 0f || window.height <= 0f) return Rect.zero;

        float feedAspect = (float)feedWidth / feedHeight;
        float windowAspect = window.width / window.height;

        float width = window.width;
        float height = window.height;
        if (feedAspect > windowAspect) height = window.width / feedAspect;
        else if (feedAspect < windowAspect) width = window.height * feedAspect;

        width = Mathf.Round(width);
        height = Mathf.Round(height);
        float x = Mathf.Round(window.x + (window.width - width) * 0.5f);
        float y = Mathf.Round(window.y + (window.height - height) * 0.5f);
        return new Rect(x, y, width, height);
    }
}
