using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// The screen camera behind a hand-held camera's Direct To Screen mode.
///
/// <para>
/// In VR nothing reaches the game window but URP's mirror of one eye, drawn at the end of the VR
/// camera's stack. This is a base camera that draws nothing of its own — no layers, a black clear,
/// no post-processing, XR rendering off so it targets the window rather than the headset — at a
/// depth after every other camera. Its one job is to give
/// <see cref="BasisCameraDirectToScreenFeature"/> a frame that lands on the window after the
/// mirror has, so the feed the feature draws into it is what the monitor shows.
/// </para>
///
/// <para>
/// Created on first use, kept disabled while the camera is not presenting, and destroyed with the
/// hand-held camera it is parented to. One presents at a time: the window is a single surface, so
/// an output taking it over stops whichever had it.
/// </para>
/// </summary>
[DisallowMultipleComponent]
public sealed class BasisCameraDirectToScreenOutput : MonoBehaviour
{
    /// <summary>
    /// After every camera that can draw to the window. URP renders the VR camera's mirror at the
    /// end of that camera's own stack, so whatever draws to the window after it is what stays.
    /// </summary>
    public const float ScreenCameraDepth = 100f;

    /// <summary>The output currently drawing to the window, or null while no camera is presenting.</summary>
    public static BasisCameraDirectToScreenOutput Presenting { get; private set; }

    private BasisHandHeldCamera owner;
    private Camera screenCamera;
    private UniversalAdditionalCameraData screenCameraData;

    private RTHandle feedHandle;
    private RenderTexture feedTexture;

    private UniversalRenderPipelineAsset rendererSearchedOn;
    private bool rendererHasFeature;
    private bool fallbackHooked;
    private BasisCameraDirectToScreenPass fallbackPass;
    private static bool warnedAboutMissingFeature;

    /// <summary>True while this output is the one drawing to the window.</summary>
    public bool IsPresenting => ReferenceEquals(Presenting, this);

    /// <summary>The camera the feed is drawn through. Exposed for tests and diagnostics.</summary>
    public Camera ScreenCamera => screenCamera;

    /// <summary>
    /// True when the pass is being enqueued by hand because no renderer on the active pipeline
    /// carries <see cref="BasisCameraDirectToScreenFeature"/>. The mode still works; it is just
    /// running on the default renderer rather than the one built for it.
    /// </summary>
    public bool IsUsingFallbackPass => fallbackHooked;

    public static BasisCameraDirectToScreenOutput Create(BasisHandHeldCamera owner)
    {
        GameObject go = new GameObject("Direct To Screen Output");
        go.transform.SetParent(owner.transform, false);

        BasisCameraDirectToScreenOutput output = go.AddComponent<BasisCameraDirectToScreenOutput>();
        output.owner = owner;
        output.BuildScreenCamera(go);
        return output;
    }

    private void BuildScreenCamera(GameObject go)
    {
        screenCamera = go.AddComponent<Camera>();
        screenCamera.enabled = false;
        screenCamera.clearFlags = CameraClearFlags.SolidColor;
        screenCamera.backgroundColor = Color.black;
        screenCamera.cullingMask = 0;
        screenCamera.depth = ScreenCameraDepth;
        screenCamera.targetDisplay = 0;
        screenCamera.targetTexture = null;
        screenCamera.allowDynamicResolution = false;
        screenCamera.useOcclusionCulling = false;
        screenCamera.orthographic = true;
        screenCamera.orthographicSize = 1f;
        screenCamera.nearClipPlane = 0.01f;
        screenCamera.farClipPlane = 1f;

        // HDR as the main camera has it, so a float feed (an EXR capture frame) keeps its range on
        // the way to the window and an HDR display gets URP's own encoding in the final blit; no
        // MSAA, since the target only ever receives a full-screen blit and samples would be waste.
        screenCamera.allowHDR = true;
        screenCamera.allowMSAA = false;

        screenCameraData = screenCamera.GetUniversalAdditionalCameraData();
        screenCameraData.renderType = CameraRenderType.Base;
        screenCameraData.renderPostProcessing = false;
        screenCameraData.renderShadows = false;
        screenCameraData.requiresColorOption = CameraOverrideOption.Off;
        screenCameraData.requiresDepthOption = CameraOverrideOption.Off;
        screenCameraData.antialiasing = AntialiasingMode.None;
        screenCameraData.stopNaN = false;
        screenCameraData.dithering = false;
        screenCameraData.allowXRRendering = false;
        screenCameraData.allowHDROutput = true;
        screenCameraData.volumeLayerMask = 0;
    }

    /// <summary>Whether <paramref name="camera"/> is this output's screen camera — the only camera the feature draws on.</summary>
    public bool IsScreenCamera(Camera camera) => screenCamera != null && ReferenceEquals(camera, screenCamera);

    /// <summary>Starts, or keeps, drawing <paramref name="feed"/> to the window, taking the window from any other output.</summary>
    public void Present(RenderTexture feed)
    {
        SetFeed(feed);
        if (!IsPresenting)
        {
            if (Presenting != null) Presenting.Stop();
            Presenting = this;
        }
        EnsureRenderer();
        if (screenCamera != null && !screenCamera.enabled) screenCamera.enabled = true;
    }

    /// <summary>Hands the window back. Safe to call when not presenting.</summary>
    public void Stop()
    {
        if (IsPresenting) Presenting = null;
        if (screenCamera != null) screenCamera.enabled = false;
        UnhookFallback();
    }

    /// <summary>
    /// The feed to draw: the camera's live render texture and a handle the render graph can import.
    /// Re-wrapped whenever the camera has rebuilt its texture underneath it — a resize, a capture at
    /// another size — so the frame drawn is always the texture the capture camera is writing now,
    /// never a handle to one it has destroyed.
    /// </summary>
    public bool TryGetFeed(out RTHandle handle, out RenderTexture texture)
    {
        if (owner != null) SetFeed(owner.PreviewTexture);
        handle = feedHandle;
        texture = feedTexture;
        return handle != null && texture != null && texture.IsCreated();
    }

    /// <summary>
    /// Points the output at <paramref name="feed"/>. Wrapped by identifier rather than by texture:
    /// the graph derives a description from a wrapped render texture, and refuses this one for
    /// carrying a depth buffer alongside its colour. An identifier carries nothing, so the pass
    /// describes the colour side itself, the way URP imports its own camera targets — and either
    /// way the texture stays the camera's, since releasing the handle never destroys it.
    /// </summary>
    public void SetFeed(RenderTexture feed)
    {
        if (feedHandle != null && feed != null && ReferenceEquals(feedTexture, feed)) return;

        ReleaseFeedHandle();
        if (feed == null) return;

        feedTexture = feed;
        feedHandle = RTHandles.Alloc(new RenderTargetIdentifier(feed), feed.name);
    }

    private void ReleaseFeedHandle()
    {
        if (feedHandle != null)
        {
            feedHandle.Release();
            feedHandle = null;
        }
        feedTexture = null;
    }

    /// <summary>
    /// Puts the screen camera on the renderer that carries the feature, looked up rather than
    /// assumed by index so a reordered renderer list cannot land it on a renderer meant for the
    /// world. Where no renderer carries it, the pass is enqueued by hand so the mode still works.
    /// </summary>
    private void EnsureRenderer()
    {
        UniversalRenderPipelineAsset asset = UniversalRenderPipeline.asset;
        if (ReferenceEquals(asset, rendererSearchedOn) && (rendererHasFeature || fallbackHooked)) return;
        rendererSearchedOn = asset;

        int index = FindRendererWithFeature(asset);
        rendererHasFeature = index >= 0;
        if (rendererHasFeature)
        {
            screenCameraData.SetRenderer(index);
            UnhookFallback();
            return;
        }

        if (!warnedAboutMissingFeature)
        {
            warnedAboutMissingFeature = true;
            BasisDebug.LogWarning(
                "Direct To Screen: no renderer on the active pipeline asset carries a BasisCameraDirectToScreenFeature, so the pass is being enqueued directly on the default renderer. Add DirectToScreenRenderer to the pipeline asset's renderer list for the intended path.",
                BasisDebug.LogTag.Camera);
        }
        HookFallback();
    }

    /// <summary>The index of the first renderer on <paramref name="asset"/> carrying an active feature, or -1.</summary>
    public static int FindRendererWithFeature(UniversalRenderPipelineAsset asset)
    {
        if (asset == null) return -1;

        ReadOnlySpan<ScriptableRendererData> renderers = asset.rendererDataList;
        for (int Index = 0; Index < renderers.Length; Index++)
        {
            ScriptableRendererData data = renderers[Index];
            if (data == null) continue;

            List<ScriptableRendererFeature> features = data.rendererFeatures;
            for (int Feature = 0; Feature < features.Count; Feature++)
            {
                if (features[Feature] is BasisCameraDirectToScreenFeature feature && feature.isActive) return Index;
            }
        }
        return -1;
    }

    private void HookFallback()
    {
        if (fallbackHooked) return;
        fallbackHooked = true;
        fallbackPass ??= new BasisCameraDirectToScreenPass();
        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
    }

    private void UnhookFallback()
    {
        if (!fallbackHooked) return;
        fallbackHooked = false;
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
    }

    // Before the renderer collects its passes for the camera, which is the moment a pass can be
    // enqueued on it by hand; the queue is not cleared again until the camera has rendered.
    private void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
    {
        if (!IsPresenting || !IsScreenCamera(camera) || screenCameraData == null) return;
        if (!TryGetFeed(out RTHandle handle, out RenderTexture texture)) return;

        ScriptableRenderer renderer = screenCameraData.scriptableRenderer;
        if (renderer == null) return;

        fallbackPass.Setup(handle, texture);
        renderer.EnqueuePass(fallbackPass);
    }

    private void OnDisable() => Stop();

    private void OnDestroy()
    {
        Stop();
        ReleaseFeedHandle();
    }
}
