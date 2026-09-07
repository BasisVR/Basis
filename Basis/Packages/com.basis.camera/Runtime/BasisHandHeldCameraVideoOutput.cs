#if BASIS_HAS_SPOUT && (UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_EDITOR))
#define BASIS_VIDEO_OUTPUT_SPOUT
#elif BASIS_HAS_SYPHON && (UNITY_EDITOR_OSX || (UNITY_STANDALONE_OSX && !UNITY_EDITOR))
#define BASIS_VIDEO_OUTPUT_SYPHON
#elif UNITY_EDITOR_LINUX || (UNITY_STANDALONE_LINUX && !UNITY_EDITOR)
#define BASIS_VIDEO_OUTPUT_V4L2
#endif
#if BASIS_VIDEO_OUTPUT_SPOUT || BASIS_VIDEO_OUTPUT_SYPHON || BASIS_VIDEO_OUTPUT_V4L2
#define BASIS_VIDEO_OUTPUT_SUPPORTED
#endif
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Basis;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
#if BASIS_VIDEO_OUTPUT_SPOUT
using Klak.Spout;
#endif
#if BASIS_VIDEO_OUTPUT_SYPHON
using Klak.Syphon;
#endif
#if BASIS_VIDEO_OUTPUT_V4L2
using System.IO;
using System.Runtime.InteropServices;
using Unity.Collections.LowLevel.Unsafe;
#endif

/// <summary>
/// Live video output for the handheld camera: Spout sender on Windows, Syphon server
/// on macOS, v4l2loopback virtual camera on Linux, plus an MJPEG web stream everywhere.
/// Streams whatever the preview shows at a picked resolution and framerate.
/// </summary>
public partial class BasisHandHeldCamera
{
    /// <summary>Stream settings. Width/Height/FrameRate persist via <see cref="BasisHandHeldCameraUI.CameraSettings"/>.</summary>
    [NonSerialized]
    public BasisVideoOutputSettings VideoOutputSettings = new BasisVideoOutputSettings();

    /// <summary>True while streaming.</summary>
    [NonSerialized]
    public bool IsVideoOutputActive;

    /// <summary>True on platforms with a video output backend (Windows/Spout, macOS/Syphon, Linux/v4l2loopback).</summary>
    public static bool IsVideoOutputSupported =>
#if BASIS_VIDEO_OUTPUT_SUPPORTED
        true;
#else
        false;
#endif

    /// <summary>Name of the compiled-in backend, for UI labels and logs.</summary>
    public static string VideoOutputBackendName =>
#if BASIS_VIDEO_OUTPUT_SPOUT
        "Spout";
#elif BASIS_VIDEO_OUTPUT_SYPHON
        "Syphon";
#elif BASIS_VIDEO_OUTPUT_V4L2
        "Virtual Camera";
#else
        "Video";
#endif

    /// <summary>
    /// What the user has to set up on the receiving side. None of these backends work out of
    /// the box: a stream that publishes correctly still shows up nowhere without them, which
    /// is indistinguishable from the feature being broken.
    /// </summary>
    public static string VideoOutputRequirement =>
#if BASIS_VIDEO_OUTPUT_SPOUT
        "Publishes as \"Basis Camera\". Needs the Spout2 plugin installed in OBS — stock OBS has no Spout source.";
#elif BASIS_VIDEO_OUTPUT_SYPHON
        "Publishes as \"Basis Camera\". Needs a Syphon-capable receiver, such as OBS with the Syphon plugin.";
#elif BASIS_VIDEO_OUTPUT_V4L2
        "Appears as a webcam. Needs the loopback module loaded: sudo modprobe v4l2loopback exclusive_caps=1";
#else
        "Not supported on this platform.";
#endif

#if BASIS_VIDEO_OUTPUT_SPOUT
    private BasisSpoutVideoOutputSink videoSink;
#elif BASIS_VIDEO_OUTPUT_SYPHON
    private BasisSyphonVideoOutputSink videoSink;
#elif BASIS_VIDEO_OUTPUT_V4L2
    private BasisV4L2VideoOutputSink videoSink;
#endif
#if BASIS_VIDEO_OUTPUT_SUPPORTED
    /// <summary>Sender names in use process-wide, so simultaneous cameras publish separate outputs (#854).</summary>
    private static readonly HashSet<string> ActiveVideoOutputNames = new HashSet<string>();

    /// <summary>The name actually published, which carries the duplicate suffix the setting must not.</summary>
    private string activeSenderName = string.Empty;
#endif
    public Shader TransparentVideoOutputShader;
    private static readonly int TransparentMaskTexId = Shader.PropertyToID("_MaskTex");
    private static readonly int TransparentScaleOffsetId = Shader.PropertyToID("_ScaleOffset");

    private RenderTexture videoStreamTexture;
    private RenderTexture transparentVideoMaskTexture;
    private Material transparentVideoOutputMaterial;
    private BasisRenderRateLimiter videoPacing;

    private bool CanPreserveVideoOutputAlpha()
    {
#if BASIS_VIDEO_OUTPUT_SUPPORTED
        return IsVideoOutputActive && videoSink != null && videoSink.SupportsAlpha;
#else
        return false;
#endif
    }

    // ---- MJPEG web stream ----------------------------------------------------------
    // Kept independent of the platform sink above rather than folded in beside it: the two
    // have different lifetimes, and the shared-texture path is the one that must not break.

    /// <summary>True while the MJPEG web stream is serving.</summary>
    [NonSerialized]
    public bool IsWebStreamActive;

    /// <summary>Ports already bound in this process, so a second camera lands on the next one.</summary>
    private static readonly HashSet<int> ClaimedWebPorts = new HashSet<int>();

    private BasisWebVideoOutputSink webSink;
    private RenderTexture webStreamTexture;

    /// <summary>
    /// Paces what the stream publishes. Not a <see cref="BasisRenderRateLimiter"/>: that one is
    /// asked "may this frame run" and answers off its own clock, which is exactly what a stream
    /// hanging off another rate-limited camera must not do.
    /// </summary>
    private BasisStreamFramePacer webStreamPacer;

    /// <summary>
    /// Set when the capture camera finishes a render, cleared when that render has been handed
    /// to the stream. The one signal that a newer picture exists.
    /// </summary>
    private bool streamFrameIsFresh;

    /// <summary>Called from the render pipeline when the capture camera has finished drawing.</summary>
    private void MarkStreamFrameFresh() => streamFrameIsFresh = true;

    /// <summary>Address to open, or empty when not serving.</summary>
    public string WebStreamUrl => webSink != null ? webSink.Url : string.Empty;

    /// <summary>True when any live output is consuming the render texture.</summary>
    public bool IsAnyVideoOutputActive => IsVideoOutputActive || IsWebStreamActive;

    /// <summary>
    /// Why the last attempt to start a live output refused, or empty when none did. Every refusal
    /// below returned a bare false, so the toggle sprang back with nothing said anywhere the
    /// operator was looking — and the two that do log are silenced by the Disable Logging setting
    /// and by a tag filter. The panel reads this so a refusal always says which one it was.
    /// </summary>
    [NonSerialized]
    public string LiveOutputFailure = string.Empty;

    // ---- transport selection -------------------------------------------------------

    /// <summary>Which transport the live output uses. One at a time — they cost the same frame twice.</summary>
    [NonSerialized]
    public BasisVideoTransport VideoTransport = IsVideoOutputSupported
        ? BasisVideoTransport.Platform
        : BasisVideoTransport.Web;

    /// <summary>Transports this build can actually offer. The web stream is always one of them.</summary>
    public static List<BasisVideoTransport> AvailableVideoTransports()
    {
        List<BasisVideoTransport> transports = new List<BasisVideoTransport>();
        if (IsVideoOutputSupported) transports.Add(BasisVideoTransport.Platform);
        transports.Add(BasisVideoTransport.Web);
        return transports;
    }

    /// <summary>Display name for a transport: the platform one is named after its backend.</summary>
    public static string GetVideoTransportName(BasisVideoTransport transport) =>
        transport == BasisVideoTransport.Web ? "Web Stream (MJPEG)" : VideoOutputBackendName;

    /// <summary>What the user has to do on the receiving side for this transport.</summary>
    public static string GetVideoTransportRequirement(BasisVideoTransport transport) =>
        transport == BasisVideoTransport.Web
            ? "Needs nothing installed — add the address to OBS as a Browser source, or open it in a browser."
            : VideoOutputRequirement;

    /// <summary>Starts whichever transport is selected.</summary>
    public bool StartLiveOutput()
    {
        StopLiveOutput();
        LiveOutputFailure = string.Empty;

        bool started;
        try
        {
            started = VideoTransport == BasisVideoTransport.Web ? StartWebStream() : StartVideoOutput();
        }
        catch (Exception e)
        {
            // An exception on the way up left the toggle the only thing that had moved: the sink
            // was half-built, the render texture and the claimed sender name were still held, and
            // the panel never reached the line that puts the switch back. Unwound and reported as
            // a refusal like any other.
            started = false;
            LiveOutputFailure = $"{GetVideoTransportName(VideoTransport)} threw on start ({e.GetType().Name}: {e.Message}).";
            BasisDebug.LogError($"{LiveOutputFailure} {e}", BasisDebug.LogTag.Camera);
            StopWebStream();
            StopVideoOutput();
        }
        if (started) return true;

        if (string.IsNullOrEmpty(LiveOutputFailure))
        {
            LiveOutputFailure = $"{GetVideoTransportName(VideoTransport)} refused to start and gave no reason.";
        }
        BasisDebug.LogError($"Live output refused: {LiveOutputFailure}", BasisDebug.LogTag.Camera);
        return false;
    }

    /// <summary>Stops every transport, whichever one happens to be up.</summary>
    public void StopLiveOutput()
    {
        LiveOutputFailure = string.Empty;
        StopWebStream();
        StopVideoOutput();
    }

    /// <summary>Switches transport, carrying the running state across.</summary>
    public void SetVideoTransport(BasisVideoTransport transport)
    {
        if (VideoTransport == transport) return;
        bool wasActive = IsAnyVideoOutputActive;
        StopLiveOutput();
        VideoTransport = transport;
        if (wasActive) StartLiveOutput();
    }

    public static bool IsVideoTransportAvailable(BasisVideoTransport transport) =>
        transport == BasisVideoTransport.Web || (transport == BasisVideoTransport.Platform && IsVideoOutputSupported);

    public bool MatchesStreamPreset(in BasisCameraStreamPreset preset) => preset.Matches(VideoTransport, VideoOutputSettings);

    public void ApplyStreamPreset(in BasisCameraStreamPreset preset)
    {
        BasisVideoOutputSettings settings = VideoOutputSettings;
        int quality = preset.Transport == BasisVideoTransport.Web ? preset.WebQuality : settings.WebQuality;
        ApplyStreamSettings(preset.Transport, preset.Width, preset.Height, preset.FrameRate, quality, settings.WebPort, settings.SenderName);
    }

    public void ApplyStreamSettings(BasisVideoTransport transport, int width, int height, float frameRate, int webQuality, int webPort, string senderName)
    {
        if (!IsVideoTransportAvailable(transport)) transport = BasisVideoTransport.Web;
        width = Mathf.Clamp(width, 16, 8192);
        height = Mathf.Clamp(height, 16, 8192);
        webQuality = Mathf.Clamp(webQuality, 1, 100);
        webPort = Mathf.Clamp(webPort, 1024, 65500);

        BasisVideoOutputSettings settings = VideoOutputSettings;
        bool sizeChanged = settings.Width != width || settings.Height != height;
        bool restart = IsAnyVideoOutputActive && (VideoTransport != transport || (IsVideoOutputActive && sizeChanged) || (IsWebStreamActive && settings.WebPort != webPort));
        if (restart) StopLiveOutput();

        VideoTransport = transport;
        settings.Width = width;
        settings.Height = height;
        settings.FrameRate = frameRate;
        settings.WebQuality = webQuality;
        settings.WebPort = webPort;
        if (!string.IsNullOrWhiteSpace(senderName)) settings.SenderName = senderName;

        if (restart) StartLiveOutput();
        else if (IsWebStreamActive && sizeChanged) ResizeWebStreamTexture();
    }

    /// <summary>
    /// Starts an MJPEG stream on loopback. Unlike Spout/Syphon this needs nothing installed
    /// on the receiving side — OBS reads it with a stock Browser or Media source — but it
    /// costs a readback and a JPEG encode per frame, so it only pays that while someone is
    /// connected.
    /// </summary>
    public bool StartWebStream()
    {
        StopWebStream();
        if (captureCamera == null)
        {
            LiveOutputFailure = "This camera has no capture camera to publish.";
            return false;
        }

        BasisVideoOutputSettings settings = VideoOutputSettings;
        settings.Width = Mathf.Clamp(settings.Width, 16, 8192);
        settings.Height = Mathf.Clamp(settings.Height, 16, 8192);

        int port = Mathf.Clamp(settings.WebPort, 1024, 65500);
        while (ClaimedWebPorts.Contains(port) && port < 65500) port++;

        webSink = new BasisWebVideoOutputSink();
        if (!webSink.Start(port))
        {
            LiveOutputFailure = webSink.FailureMessage ?? $"No free port from {port} upwards to serve the stream on.";
            webSink = null;
            return false;
        }
        ClaimedWebPorts.Add(webSink.Port);
        settings.WebPort = webSink.Port;

        ResizeWebStreamTexture();

        webStreamPacer.Reset();
        // The render texture already holds a frame, so the first viewer gets a picture without
        // waiting for the capture camera to come round again.
        streamFrameIsFresh = true;
        IsWebStreamActive = true;
        UpdateRenderGate();
        BasisDebug.Log($"Web stream started at {webSink.Url} — open it in a browser, or add it to OBS as a Browser source.", BasisDebug.LogTag.Camera);
        return true;
    }

    /// <summary>Sets the loopback port, rebinding when the stream is already serving.</summary>
    public void SetWebStreamPort(int port)
    {
        int clamped = Mathf.Clamp(port, 1024, 65500);
        if (VideoOutputSettings.WebPort == clamped) return;
        VideoOutputSettings.WebPort = clamped;
        if (IsWebStreamActive) StartWebStream();
    }

    /// <summary>Sets JPEG quality, 1-100. Takes effect on the next frame; no rebind needed.</summary>
    public void SetWebStreamQuality(int quality)
    {
        VideoOutputSettings.WebQuality = Mathf.Clamp(quality, 1, 100);
    }

    /// <summary>
    /// Opens the stream in the system browser. The address is rebuilt from the bound port and
    /// re-validated rather than trusting a stored string: Application.OpenURL hands whatever
    /// it is given straight to the OS, which will launch any registered protocol handler, so
    /// nothing user-typed is ever allowed to reach it.
    /// </summary>
    public bool OpenWebStreamInBrowser()
    {
        if (!IsWebStreamActive || webSink == null) return false;

        int port = webSink.Port;
        if (port < 1024 || port > 65535) return false;

        if (!Uri.TryCreate($"http://127.0.0.1:{port}/", UriKind.Absolute, out Uri uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp || !uri.IsLoopback || uri.Port != port) return false;

        Application.OpenURL(uri.AbsoluteUri);
        return true;
    }

    /// <summary>Stops the MJPEG stream and drops any connected viewers.</summary>
    public void StopWebStream()
    {
        bool wasActive = IsWebStreamActive;
        IsWebStreamActive = false;
        if (webSink != null)
        {
            ClaimedWebPorts.Remove(webSink.Port);
            webSink.Stop();
            webSink = null;
        }
        if (webStreamTexture != null)
        {
            webStreamTexture.Release();
            Destroy(webStreamTexture);
            webStreamTexture = null;
        }
        if (wasActive) UpdateRenderGate();
    }

    private void ResizeWebStreamTexture()
    {
        BasisVideoOutputSettings settings = VideoOutputSettings;
        settings.Width = Mathf.Clamp(settings.Width, 16, 8192);
        settings.Height = Mathf.Clamp(settings.Height, 16, 8192);
        if (webStreamTexture != null && webStreamTexture.width == settings.Width && webStreamTexture.height == settings.Height) return;
        if (webStreamTexture != null)
        {
            webStreamTexture.Release();
            Destroy(webStreamTexture);
        }
        webStreamTexture = new RenderTexture(new RenderTextureDescriptor(settings.Width, settings.Height, RenderTextureFormat.ARGB32, 0) { sRGB = true }) { name = "BasisWebVideoOutput" };
        webStreamTexture.Create();
        streamFrameIsFresh = true;
    }

    private void TickWebStream()
    {
        if (!IsWebStreamActive || webSink == null) return;
        if (webSink.FailureMessage != null)
        {
            BasisDebug.LogError($"Web stream stopped: {webSink.FailureMessage}", BasisDebug.LogTag.Camera);
            StopWebStream();
            return;
        }
        // Nobody watching: skip the blit as well as the encode, so an idle stream is free.
        if (!webSink.HasClients)
        {
            webStreamPacer.Reset();
            return;
        }

        Texture source = renderTexture;

        // Publish renders, not clock ticks — see BasisStreamFramePacer. The sink is asked whether
        // it would take a frame before the slot is spent, so a tick that arrives mid-readback
        // costs nothing and the next one sends the newest picture rather than waiting out another
        // whole interval.
        bool ready = source != null && webSink.CanAcceptFrame;
        if (!webStreamPacer.AllowThisFrame(Time.unscaledDeltaTime, VideoOutputSettings.FrameRate, streamFrameIsFresh, ready)) return;
        streamFrameIsFresh = false;

        GetStreamBlitCrop(source, webStreamTexture, out Vector2 scale, out Vector2 offset);
        Graphics.Blit(source, webStreamTexture, scale, offset);
        webSink.PushFrame(webStreamTexture, VideoOutputSettings.WebQuality);
    }

    /// <summary>Starts streaming with <see cref="VideoOutputSettings"/>. The capture camera keeps rendering while active, even when the preview is hidden.</summary>
    public bool StartVideoOutput()
    {
#if BASIS_VIDEO_OUTPUT_SUPPORTED
        StopVideoOutput();
        if (captureCamera == null)
        {
            LiveOutputFailure = "This camera has no capture camera to publish.";
            return false;
        }

        // A film body has no output to stream. Refused rather than started silently and dropped,
        // so the panel's own failure line is what says so.
        if (!BodyAllowsLiveFeed)
        {
            LiveOutputFailure = $"A {BodyTraits.Kind} body has no output socket — it only shows its own viewfinder. Switch the body on the Presets tab.";
            return false;
        }
        BasisVideoOutputSettings settings = VideoOutputSettings;
        settings.Width = Mathf.Clamp(settings.Width, 16, 8192);
        settings.Height = Mathf.Clamp(settings.Height, 16, 8192);

        // The suffix is kept off settings.SenderName. Written back, a second camera turned the
        // operator's own name into "Basis Camera 2" permanently, and the start after that read
        // that as the base and published "Basis Camera 2 2".
        string baseName = string.IsNullOrEmpty(settings.SenderName) ? "Basis Camera" : settings.SenderName;
        activeSenderName = baseName;
        for (int Suffix = 2; ActiveVideoOutputNames.Contains(activeSenderName); Suffix++)
        {
            activeSenderName = $"{baseName} {Suffix}";
        }
        ActiveVideoOutputNames.Add(activeSenderName);

#if BASIS_VIDEO_OUTPUT_V4L2
        RenderTextureFormat format = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.BGRA32) ? RenderTextureFormat.BGRA32 : RenderTextureFormat.ARGB32;
        videoSink = new BasisV4L2VideoOutputSink();
#elif BASIS_VIDEO_OUTPUT_SYPHON
        RenderTextureFormat format = RenderTextureFormat.ARGB32;
        videoSink = new BasisSyphonVideoOutputSink();
#else
        RenderTextureFormat format = RenderTextureFormat.ARGB32;
        videoSink = new BasisSpoutVideoOutputSink();
#endif
        videoStreamTexture = new RenderTexture(new RenderTextureDescriptor(settings.Width, settings.Height, format, 0) { sRGB = true }) { name = "BasisVideoOutput" };
        videoStreamTexture.Create();

        string requestedName = settings.SenderName;
        settings.SenderName = activeSenderName;
        bool sinkStarted = videoSink.Start(settings, captureCamera.gameObject);
        settings.SenderName = requestedName;
        if (!sinkStarted)
        {
            // Read before the stop: that clears the sink, and its own account of why it would not
            // start is the only thing that separates a missing shader from the wrong graphics API.
            LiveOutputFailure = videoSink.FailureMessage ?? $"{VideoOutputBackendName} would not start.";
            StopVideoOutput();
            return false;
        }
        videoPacing = default;
        IsVideoOutputActive = true;
        if (backgroundMode == BasisCameraBackgroundMode.Transparent && videoSink.SupportsAlpha)
        {
            PrepareTransparentVideoOutputResources(renderTexture);
        }
        UpdateRenderGate();
        BasisDebug.Log($"{VideoOutputBackendName} output started as '{activeSenderName}': {settings.Width}x{settings.Height} @ {settings.FrameRate}fps", BasisDebug.LogTag.Camera);
        return true;
#else
        LiveOutputFailure = "This build carries no shared-texture video backend — use the web stream instead.";
        return false;
#endif
    }

    /// <summary>Stops streaming.</summary>
    public void StopVideoOutput()
    {
#if BASIS_VIDEO_OUTPUT_SUPPORTED
        bool wasActive = IsVideoOutputActive;
        IsVideoOutputActive = false;
        if (!string.IsNullOrEmpty(activeSenderName))
        {
            ActiveVideoOutputNames.Remove(activeSenderName);
            activeSenderName = string.Empty;
        }
        if (videoSink != null)
        {
            videoSink.Stop();
            videoSink = null;
        }
        if (videoStreamTexture != null)
        {
            videoStreamTexture.Release();
            Destroy(videoStreamTexture);
            videoStreamTexture = null;
        }
        ReleaseTransparentVideoOutputResources();
        if (wasActive) UpdateRenderGate();
#endif
    }

    /// <summary>Sets the stream resolution, restarting the stream when active.</summary>
    public void SetVideoOutputResolution(int width, int height)
    {
        VideoOutputSettings.Width = width;
        VideoOutputSettings.Height = height;
        if (IsVideoOutputActive) StartVideoOutput();
        else if (IsWebStreamActive) ResizeWebStreamTexture();
    }

    /// <summary>Sets the stream framerate. Applies live; 0 or below streams at the render rate.</summary>
    public void SetVideoOutputFrameRate(float frameRate)
    {
        VideoOutputSettings.FrameRate = frameRate;
    }

    private void TickVideoOutput()
    {
        TickWebStream();
#if BASIS_VIDEO_OUTPUT_SUPPORTED
        if (!IsVideoOutputActive) return;
        if (videoSink.FailureMessage != null)
        {
            BasisDebug.LogError($"Video output stopped: {videoSink.FailureMessage}", BasisDebug.LogTag.Camera);
            StopVideoOutput();
            return;
        }
        Texture source = renderTexture;
        if (source == null) return;
        if (!videoPacing.AllowThisFrame(Time.unscaledDeltaTime, VideoOutputSettings.FrameRate, true)) return;

        GetStreamBlitCrop(source, videoStreamTexture, out Vector2 scale, out Vector2 offset);
#if BASIS_VIDEO_OUTPUT_V4L2
        // Readback of Unity RTs is bottom-up; V4L2 wants rows top-down. Flipping the crop means
        // negating its scale and moving the offset to the far edge of the same band.
        scale.y = -scale.y;
        offset.y += -scale.y;
#endif

        bool transparent = backgroundMode == BasisCameraBackgroundMode.Transparent && videoSink.SupportsAlpha;
        bool outputHasAlpha = false;
        if (transparent)
        {
            try
            {
                // Render the RGB and alpha mask as a matched pair. This callback runs before Unity's
                // normal camera render, so render explicitly here and suppress the duplicate automatic
                // render for this frame.
                captureCamera.Render();
                captureCamera.enabled = false;
                outputHasAlpha = BlitTransparentVideoOutput(source, scale, offset);
            }
            catch (Exception ex)
            {
                BasisDebug.LogErrorOnce($"Transparent video output failed: {ex}", BasisDebug.LogTag.Camera);
            }
        }

        if (!outputHasAlpha)
        {
            Graphics.Blit(source, videoStreamTexture, scale, offset);
        }

        videoSink.PushFrame(videoStreamTexture, outputHasAlpha);
#endif
    }

    private bool BlitTransparentVideoOutput(Texture source, Vector2 scale, Vector2 offset)
    {
        if (!TransparentVideoOutputResourcesReady(source)) return false;
        if (!RenderTransparentVideoMask(source)) return false;

        transparentVideoOutputMaterial.SetTexture(TransparentMaskTexId, transparentVideoMaskTexture);
        transparentVideoOutputMaterial.SetVector(
            TransparentScaleOffsetId,
            new Vector4(scale.x, scale.y, offset.x, offset.y));
        Graphics.Blit(source, videoStreamTexture, transparentVideoOutputMaterial);
        return true;
    }

    private bool PrepareTransparentVideoOutputResources(Texture source)
    {
        if (source == null) return false;

        if (transparentVideoOutputMaterial == null)
        {
            if (TransparentVideoOutputShader == null)
            {
                BasisDebug.LogErrorOnce("Transparent video output shader is unavailable.", BasisDebug.LogTag.Camera);
                return false;
            }
            transparentVideoOutputMaterial = new Material(TransparentVideoOutputShader) { name = "Basis Transparent Video Output" };
        }

        int samples = source is RenderTexture sourceRenderTexture ? sourceRenderTexture.antiAliasing : 1;
        if (transparentVideoMaskTexture != null &&
            (transparentVideoMaskTexture.width != source.width ||
             transparentVideoMaskTexture.height != source.height ||
             transparentVideoMaskTexture.antiAliasing != samples))
        {
            transparentVideoMaskTexture.Release();
            Destroy(transparentVideoMaskTexture);
            transparentVideoMaskTexture = null;
        }

        if (transparentVideoMaskTexture == null)
        {
            var descriptor = new RenderTextureDescriptor(source.width, source.height, RenderTextureFormat.ARGB32, 24)
            {
                msaaSamples = samples,
                useMipMap = false,
                autoGenerateMips = false,
                sRGB = false
            };
            transparentVideoMaskTexture = new RenderTexture(descriptor) { name = "BasisTransparentVideoMask" };
            transparentVideoMaskTexture.Create();
        }

        return true;
    }

    private bool TransparentVideoOutputResourcesReady(Texture source)
    {
        if (source == null || transparentVideoOutputMaterial == null || transparentVideoMaskTexture == null) return false;

        int samples = source is RenderTexture sourceRenderTexture ? sourceRenderTexture.antiAliasing : 1;
        return transparentVideoMaskTexture.width == source.width
            && transparentVideoMaskTexture.height == source.height
            && transparentVideoMaskTexture.antiAliasing == samples;
    }

    private bool RenderTransparentVideoMask(Texture source)
    {
        if (captureCamera == null || CameraData == null || !TransparentVideoOutputResourcesReady(source)) return false;

        RenderTexture previousTarget = captureCamera.targetTexture;
        bool previousPostProcessing = CameraData.renderPostProcessing;
        CameraClearFlags previousClearFlags = captureCamera.clearFlags;
        Color previousBackgroundColor = captureCamera.backgroundColor;
        try
        {
            captureCamera.targetTexture = transparentVideoMaskTexture;
            CameraData.renderPostProcessing = false;
            captureCamera.clearFlags = CameraClearFlags.SolidColor;
            captureCamera.backgroundColor = Color.clear;
            captureCamera.Render();
            return true;
        }
        finally
        {
            captureCamera.targetTexture = previousTarget;
            CameraData.renderPostProcessing = previousPostProcessing;
            captureCamera.clearFlags = previousClearFlags;
            captureCamera.backgroundColor = previousBackgroundColor;
        }
    }

    private void ReleaseTransparentVideoOutputResources()
    {
        if (transparentVideoMaskTexture != null)
        {
            transparentVideoMaskTexture.Release();
            Destroy(transparentVideoMaskTexture);
            transparentVideoMaskTexture = null;
        }
        if (transparentVideoOutputMaterial != null)
        {
            Destroy(transparentVideoOutputMaterial);
            transparentVideoOutputMaterial = null;
        }
    }

    /// <summary>
    /// Source-to-target UV crop for the stream blits. The feed's aspect is no longer fixed — Direct
    /// To Screen sizes it to the screen — while the stream's is whatever resolution the user set,
    /// and a plain blit stretches one onto the other, which a receiver has no way to undo. Meeting
    /// in the middle of the frame keeps the stream undistorted at the size receivers were told to
    /// expect. Identity whenever the two already agree, which is the usual case.
    /// </summary>
    internal static void GetStreamBlitCrop(Texture source, RenderTexture destination, out Vector2 scale, out Vector2 offset)
    {
        scale = Vector2.one;
        offset = Vector2.zero;

        if (source == null || destination == null) return;
        if (source.width <= 0 || source.height <= 0 || destination.width <= 0 || destination.height <= 0) return;

        float sourceAspect = (float)source.width / source.height;
        float destinationAspect = (float)destination.width / destination.height;

        if (sourceAspect > destinationAspect)
        {
            scale.x = destinationAspect / sourceAspect;
            offset.x = (1f - scale.x) * 0.5f;
        }
        else if (sourceAspect < destinationAspect)
        {
            scale.y = sourceAspect / destinationAspect;
            offset.y = (1f - scale.y) * 0.5f;
        }
    }
}

namespace Basis
{
    /// <summary>How a camera's live output leaves the process.</summary>
    public enum BasisVideoTransport
    {
        /// <summary>The platform's shared-texture backend: Spout, Syphon or v4l2loopback. Zero-copy, needs a receiver.</summary>
        Platform,

        /// <summary>MJPEG over loopback HTTP. Costs a readback and an encode, but needs nothing installed.</summary>
        Web,
    }

    /// <summary>
    /// Decides when a live output publishes a frame.
    /// <para>
    /// A stream hanging off a rate-limited camera has two clocks available and only one of them
    /// is real. Pacing on its own accumulator — which is what this replaced — puts a second clock
    /// next to the camera's, and two accumulators at the same nominal rate sampled once a frame
    /// beat against each other: some slots land on a frame the camera has not redrawn, and the
    /// same picture is encoded and sent twice; some fresh renders fall between slots and are
    /// thrown away. The average frame rate comes out right, which is why this reads as working
    /// code, and the picture arrives unevenly, which is the part a viewer sees.
    /// </para>
    /// <para>
    /// So the fresh render is what drives it, and the frame rate is only a ceiling. The slack is
    /// the other half of that: a source running at the stream rate delivers frames a hair early
    /// as often as a hair late, and holding those back for a full interval is what would halve a
    /// 30fps source to 15.
    /// </para>
    /// </summary>
    public struct BasisStreamFramePacer
    {
        /// <summary>How early a fresh frame may be published, as a fraction of the frame interval.</summary>
        public const float IntervalSlack = 0.9f;

        /// <summary>Credit is capped at this many intervals, so a stall is not paid back as a burst.</summary>
        public const float MaxBankedIntervals = 2f;

        private float elapsed;

        /// <summary>Drops banked time. For a stream that has just started or has no viewers.</summary>
        public void Reset() => elapsed = 0f;

        /// <summary>
        /// Advances the clock and answers whether this tick should publish a frame.
        /// </summary>
        /// <param name="deltaTime">Time since the last tick.</param>
        /// <param name="frameRate">Ceiling in frames per second. 0 or below publishes every fresh frame.</param>
        /// <param name="frameIsFresh">Whether the source has drawn a picture that has not been published.</param>
        /// <param name="sinkIsReady">Whether the sink would take a frame right now. A sink that would
        /// not is never charged for the slot, so the next tick publishes instead of the next interval.</param>
        public bool AllowThisFrame(float deltaTime, float frameRate, bool frameIsFresh, bool sinkIsReady)
        {
            float interval = frameRate > 0f ? 1f / frameRate : 0f;
            elapsed += deltaTime;
            if (interval > 0f && elapsed > interval * MaxBankedIntervals) elapsed = interval * MaxBankedIntervals;

            if (!frameIsFresh || !sinkIsReady) return false;
            if (interval > 0f && elapsed < interval * IntervalSlack) return false;

            // Carry the remainder rather than zeroing, so a source running well above the stream
            // rate is held to the stream rate exactly instead of drifting above it.
            elapsed = interval > 0f ? Mathf.Max(0f, elapsed - interval) : 0f;
            return true;
        }
    }

    public class BasisVideoOutputSettings
    {
        public const int DefaultWidth = 1920, DefaultHeight = 1080, DefaultWebPort = 8787, DefaultWebQuality = 70;
        public const float DefaultFrameRate = 30f;
        public const string DefaultSenderName = "Basis Camera";
        public int Width = DefaultWidth;
        public int Height = DefaultHeight;
        /// <summary>Target framerate. 0 or below streams at the render rate.</summary>
        public float FrameRate = DefaultFrameRate;
        /// <summary>Sender/server name shown to Spout (Windows) and Syphon (macOS) receivers.</summary>
        public string SenderName = DefaultSenderName;
        /// <summary>Optional explicit /dev/videoN path (Linux only). Empty auto-detects the first v4l2loopback device.</summary>
        public string DevicePath = string.Empty;

        /// <summary>Loopback port for the MJPEG web stream. Taken ports roll forward to the next free one.</summary>
        public int WebPort = DefaultWebPort;

        /// <summary>JPEG quality for the web stream, 1-100. Lower is cheaper to encode and to send.</summary>
        public int WebQuality = DefaultWebQuality;
    }

    /// <summary>
    /// MJPEG over HTTP, bound to loopback. The only backend that needs nothing installed on
    /// the receiving side: OBS reads it with a stock Browser or Media source, and so does any
    /// browser or VLC. Costs a GPU readback and a JPEG encode per frame, so it does that work
    /// only while a client is connected.
    /// </summary>
    public sealed class BasisWebVideoOutputSink
    {
        private const string Boundary = "basisframe";

        /// <summary>Path serving the raw multipart stream; everything else gets the viewer page.</summary>
        private const string StreamPath = "/stream";

        /// <summary>Every extra viewer is another full-frame write off the same encode.</summary>
        private const int MaxClients = 4;

        /// <summary>Consecutive ports tried before giving up.</summary>
        private const int PortAttempts = 16;

        /// <summary>Longest the worker waits with nothing to do before it looks for new viewers.</summary>
        private const int IdleWaitMs = 15;

        /// <summary>Shorter wait while a connection is still mid-handshake, so it lands promptly.</summary>
        private const int HandshakeWaitMs = 5;

        /// <summary>How long a connection may sit without sending a request before it is closed.</summary>
        private const int HandshakeTimeoutMs = 2000;

        /// <summary>How long one frame may stay on the wire before the viewer is written off.</summary>
        private const int WriteStallTimeoutMs = 4000;

        private static readonly byte[] FrameTrailer = Encoding.ASCII.GetBytes("\r\n");

        /// <summary>A connected viewer. One frame is on the wire at a time; the rest are dropped.</summary>
        private sealed class Viewer
        {
            public TcpClient Client;
            public NetworkStream Stream;

            /// <summary>Header, frame and trailer as one write, so a frame cannot be interleaved.</summary>
            public byte[] Scratch = Array.Empty<byte>();

            /// <summary>Set while a frame is on the wire. Written by the worker, cleared by the write callback.</summary>
            public volatile bool Writing;

            /// <summary>Set by the write callback. The worker owns the actual removal.</summary>
            public volatile bool Dead;
            public string Failure;
            public int WriteStartedTick;
        }

        /// <summary>A connection that has been accepted but has not sent its request line yet.</summary>
        private sealed class Handshake
        {
            public TcpClient Client;
            public int DeadlineTick;
        }

        private TcpListener listener;
        private Thread worker;
        private volatile bool running;
        private volatile int clientCount;

        /// <summary>
        /// Wakes the worker the moment a frame lands instead of leaving it to poll. Never disposed:
        /// nothing here touches its <see cref="ManualResetEventSlim.WaitHandle"/>, so it holds no
        /// kernel object, and disposing it would race the worker's own wait on the way out.
        /// </summary>
        private readonly ManualResetEventSlim frameSignal = new ManualResetEventSlim(false);

        /// <summary>Both touched only by the worker thread while running.</summary>
        private readonly List<Viewer> clients = new List<Viewer>();
        private readonly List<Handshake> handshakes = new List<Handshake>();

        /// <summary>
        /// Raw readback bytes on their way from the main thread to the worker, plus the one spare
        /// buffer they pass back and forth. The worker takes <see cref="rawFrame"/> out under the
        /// lock and returns it as <see cref="rawSpare"/> once it has encoded it, so the main thread
        /// always has somewhere to write and an encode in progress never blocks the next readback.
        /// Nothing is allocated per frame once both buffers exist.
        /// </summary>
        private byte[] rawFrame;
        private byte[] rawSpare;
        private readonly object rawLock = new object();
        private volatile bool rawReady;
        private int rawWidth;
        private int rawHeight;
        private int rawQuality;

        /// <summary>
        /// Set if Unity refuses to encode away from the main thread, so a wrong guess costs
        /// performance instead of the whole stream.
        /// </summary>
        private volatile bool encodeOnMainThread;

        private readonly object frameLock = new object();
        private byte[] pendingFrame;

        private Action<AsyncGPUReadbackRequest> readbackCallback;
        private volatile bool readbackInFlight;
        private int frameWidth;
        private int frameHeight;
        private int frameQuality;

        public string FailureMessage { get; private set; }
        public int Port { get; private set; }
        public string Url => $"http://127.0.0.1:{Port}/";
        public bool HasClients => clientCount > 0;

        /// <summary>
        /// True when a frame pushed now would actually be taken. Asked before the blit so a tick
        /// that cannot publish costs nothing and, more importantly, does not spend the caller's
        /// pacing slot on a frame that is about to be thrown away.
        /// </summary>
        public bool CanAcceptFrame => running && clientCount > 0 && !readbackInFlight && !rawReady;

        public bool Start(int port)
        {
            // Loopback only, never IPAddress.Any: binding every interface would put the
            // player's camera on the local network the moment the toggle is flipped.
            for (int Attempt = 0; Attempt < PortAttempts; Attempt++)
            {
                try
                {
                    listener = new TcpListener(IPAddress.Loopback, port + Attempt);
                    listener.Start();
                    Port = port + Attempt;
                    break;
                }
                catch (SocketException)
                {
                    listener = null;
                }
            }

            if (listener == null)
            {
                BasisDebug.LogError($"Web stream could not bind any port in {port}-{port + PortAttempts - 1}.", BasisDebug.LogTag.Camera);
                return false;
            }

            running = true;
            readbackCallback = OnReadbackComplete;
            worker = new Thread(WorkerLoop) { IsBackground = true, Name = "BasisWebVideoOutput" };
            worker.Start();
            return true;
        }

        /// <summary>Main thread. Requests a readback of the frame; the encode happens on the worker.</summary>
        public void PushFrame(RenderTexture frame, int quality)
        {
            // One readback in flight at a time: queueing them would just build latency, and
            // the newest frame is the only one a live stream cares about.
            if (!CanAcceptFrame) return;

            frameWidth = frame.width;
            frameHeight = frame.height;
            frameQuality = Mathf.Clamp(quality, 1, 100);
            readbackInFlight = true;
            AsyncGPUReadback.Request(frame, 0, TextureFormat.RGBA32, readbackCallback);
        }

        public void Stop()
        {
            running = false;
            frameSignal.Set();
            try { listener?.Stop(); } catch (Exception) { }
            listener = null;

            if (worker != null)
            {
                worker.Join(500);
                worker = null;
            }

            for (int Index = 0; Index < clients.Count; Index++)
            {
                try { clients[Index].Client.Close(); } catch (Exception) { }
            }
            clients.Clear();
            for (int Index = 0; Index < handshakes.Count; Index++)
            {
                try { handshakes[Index].Client.Close(); } catch (Exception) { }
            }
            handshakes.Clear();
            clientCount = 0;
            lock (rawLock)
            {
                rawReady = false;
                rawFrame = null;
                rawSpare = null;
            }
            lock (frameLock) pendingFrame = null;
        }

        /// <summary>
        /// Main thread. Hands the raw bytes to the worker rather than encoding here — a 1080p
        /// JPEG encode is milliseconds, and paying that inside the render loop stutters the
        /// game as well as the stream.
        /// </summary>
        private void OnReadbackComplete(AsyncGPUReadbackRequest request)
        {
            readbackInFlight = false;
            if (!running || request.hasError) return;

            if (encodeOnMainThread)
            {
                byte[] jpeg = EncodeJpeg(request.GetData<byte>(), frameWidth, frameHeight, frameQuality);
                if (jpeg != null)
                {
                    lock (frameLock) pendingFrame = jpeg;
                    frameSignal.Set();
                }
                return;
            }

            NativeArray<byte> data = request.GetData<byte>();

            byte[] target;
            lock (rawLock)
            {
                target = rawSpare;
                rawSpare = null;
            }
            if (target == null || target.Length != data.Length) target = new byte[data.Length];
            data.CopyTo(target);

            lock (rawLock)
            {
                // A frame still sitting here means the worker has not started on it. This one is
                // newer, so it takes its place instead of queueing behind it.
                if (rawFrame != null && rawSpare == null) rawSpare = rawFrame;
                rawFrame = target;
                rawWidth = frameWidth;
                rawHeight = frameHeight;
                rawQuality = frameQuality;
                rawReady = true;
            }
            frameSignal.Set();
        }

        /// <summary>
        /// Worker thread. Takes the waiting frame out from under the lock <em>before</em> encoding
        /// it and hands the buffer straight back afterwards, so the readback that lands during the
        /// encode has somewhere to go. Encoding in place is what used to make every second frame
        /// arrive to find the slot still busy and be dropped.
        /// </summary>
        private byte[] TakeRawAndEncode()
        {
            if (encodeOnMainThread) return null;

            byte[] raw;
            int width;
            int height;
            int quality;
            lock (rawLock)
            {
                if (!rawReady) return null;
                raw = rawFrame;
                rawFrame = null;
                rawReady = false;
                width = rawWidth;
                height = rawHeight;
                quality = rawQuality;
            }

            try
            {
                return EncodeJpeg(raw, width, height, quality);
            }
            finally
            {
                lock (rawLock)
                {
                    if (rawSpare == null) rawSpare = raw;
                }
            }
        }

        /// <summary>
        /// Readback and the JPEG encoders both treat row 0 as the bottom, so the result comes
        /// out upright without a flip blit.
        /// </summary>
        private byte[] EncodeJpeg(byte[] raw, int width, int height, int quality)
        {
            try
            {
                return ImageConversion.EncodeArrayToJPG(
                    raw, GraphicsFormat.R8G8B8A8_SRGB, (uint)width, (uint)height, 0, quality);
            }
            catch (Exception e)
            {
                if (!encodeOnMainThread)
                {
                    // Unity would not encode off the main thread. Degrade instead of losing
                    // the stream; the cost lands in the render loop from here on.
                    encodeOnMainThread = true;
                    BasisDebug.LogWarning($"Web stream falling back to main-thread JPEG encoding ({e.GetType().Name}).", BasisDebug.LogTag.Camera);
                }
                else
                {
                    FailureMessage = $"JPEG encode failed ({e.GetType().Name}: {e.Message})";
                }
                return null;
            }
        }

        private byte[] EncodeJpeg(NativeArray<byte> raw, int width, int height, int quality)
        {
            NativeArray<byte> encoded = default;
            try
            {
                encoded = ImageConversion.EncodeNativeArrayToJPG(
                    raw, GraphicsFormat.R8G8B8A8_SRGB, (uint)width, (uint)height, 0, quality);
                return encoded.ToArray();
            }
            catch (Exception e)
            {
                FailureMessage = $"JPEG encode failed ({e.GetType().Name}: {e.Message})";
                return null;
            }
            finally
            {
                if (encoded.IsCreated) encoded.Dispose();
            }
        }

        private void WorkerLoop()
        {
            while (running)
            {
                bool didWork = false;
                try
                {
                    while (listener != null && listener.Pending())
                    {
                        QueueHandshake(listener.AcceptTcpClient());
                        didWork = true;
                    }

                    if (ReapViewers()) didWork = true;
                    if (handshakes.Count > 0 && ServiceHandshakes()) didWork = true;

                    byte[] jpeg = TakeRawAndEncode();
                    if (jpeg != null)
                    {
                        Broadcast(jpeg);
                        didWork = true;
                    }

                    byte[] pending = TakeFrame();
                    if (pending != null)
                    {
                        Broadcast(pending);
                        didWork = true;
                    }
                }
                catch (Exception e)
                {
                    // Never return: one bad accept or write used to kill this thread outright,
                    // which silently ended the stream for the rest of the session.
                    BasisDebug.LogError($"Web stream worker error: {e.GetType().Name}: {e.Message}", BasisDebug.LogTag.Camera);
                }

                if (didWork) continue;

                // Woken by the main thread the instant a frame lands, so the encode starts with the
                // readback rather than up to a scheduler tick after it. Polling on Thread.Sleep(1)
                // was worth as much as 15ms of jitter a frame on Windows, where the sleep rounds up
                // to whatever timer resolution the process happens to be running at.
                frameSignal.Wait(handshakes.Count > 0 ? HandshakeWaitMs : IdleWaitMs);
                frameSignal.Reset();
            }
        }

        /// <summary>Parks a fresh connection until it says what it wants. See <see cref="ServiceHandshakes"/>.</summary>
        private void QueueHandshake(TcpClient client)
        {
            // Budgeted separately from the viewers. A connection that has not asked for anything
            // yet is usually a browser's speculative preconnect, and counting those against the
            // viewer cap lets them keep the real request out.
            if (handshakes.Count >= MaxClients)
            {
                try { client.Close(); } catch (Exception) { }
                return;
            }

            client.NoDelay = true;
            // Only the handshake writes synchronously, and it is a few hundred bytes; frames go
            // out asynchronously, where this timeout does not apply.
            client.SendTimeout = 2000;
            client.ReceiveTimeout = 250;
            handshakes.Add(new Handshake { Client = client, DeadlineTick = Environment.TickCount + HandshakeTimeoutMs });
        }

        /// <summary>
        /// Reads the request line off every connection that has actually sent one. Deferred rather
        /// than read inline off the accept because browsers open speculative connections and then
        /// send nothing on them: a blocking read on one of those parked this thread — and with it
        /// the encode, and every other viewer's frames — for the whole receive timeout.
        /// </summary>
        private bool ServiceHandshakes()
        {
            bool progressed = false;
            for (int Index = handshakes.Count - 1; Index >= 0; Index--)
            {
                Handshake handshake = handshakes[Index];
                bool ready;
                try
                {
                    ready = handshake.Client.Available > 0;
                }
                catch (Exception)
                {
                    ready = false;
                    handshake.DeadlineTick = Environment.TickCount - 1;
                }

                if (!ready && Environment.TickCount - handshake.DeadlineTick < 0) continue;

                handshakes.RemoveAt(Index);
                progressed = true;
                try
                {
                    if (ready) AdmitViewer(handshake.Client);
                    else handshake.Client.Close();
                }
                catch (Exception e)
                {
                    BasisDebug.Log($"Web stream handshake dropped: {e.GetType().Name}: {e.Message}", BasisDebug.LogTag.Camera);
                    try { handshake.Client.Close(); } catch (Exception) { }
                }
            }
            return progressed;
        }

        private void AdmitViewer(TcpClient client)
        {
            NetworkStream stream = client.GetStream();
            string path = ReadRequestPath(stream);

            // Chrome dropped multipart/x-mixed-replace for top-level documents, so browsing
            // straight to the stream renders the first part and stops — "one frame until you
            // reload". The same stream still animates inside an <img>, so / serves a page that
            // wraps it and the raw stream lives at /stream for players that want it directly.
            if (!path.StartsWith(StreamPath, StringComparison.Ordinal))
            {
                WriteViewerPage(stream);
                client.Close();
                return;
            }

            // The cap is on viewers of the stream itself, since that is what costs a write per
            // frame each. Serving the page above is a few hundred bytes and is always allowed.
            if (clients.Count >= MaxClients)
            {
                client.Close();
                return;
            }

            byte[] header = Encoding.ASCII.GetBytes(
                "HTTP/1.0 200 OK\r\n" +
                "Connection: close\r\n" +
                "Cache-Control: no-store, no-cache, must-revalidate\r\n" +
                "Pragma: no-cache\r\n" +
                $"Content-Type: multipart/x-mixed-replace; boundary={Boundary}\r\n\r\n");
            stream.Write(header, 0, header.Length);

            clients.Add(new Viewer { Client = client, Stream = stream });
            clientCount = clients.Count;
            BasisDebug.Log($"Web stream viewer connected ({clients.Count} total).", BasisDebug.LogTag.Camera);
        }

        /// <summary>
        /// Pulls the path out of the request line. Also clears the request from the receive
        /// buffer, which browsers never read back.
        /// </summary>
        private static string ReadRequestPath(NetworkStream stream)
        {
            try
            {
                byte[] scratch = new byte[1024];
                int read = stream.Read(scratch, 0, scratch.Length);
                if (read <= 0) return "/";

                string request = Encoding.ASCII.GetString(scratch, 0, read);
                int start = request.IndexOf(' ');
                if (start < 0) return "/";
                int end = request.IndexOf(' ', start + 1);
                return end < 0 ? "/" : request.Substring(start + 1, end - start - 1);
            }
            catch (Exception)
            {
                return "/";
            }
        }

        /// <summary>Minimal page whose only job is to hold the stream in an img that browsers will animate.</summary>
        private static void WriteViewerPage(NetworkStream stream)
        {
            byte[] body = Encoding.UTF8.GetBytes(
                "<!doctype html><meta charset=\"utf-8\"><title>Basis Camera</title>" +
                "<style>html,body{margin:0;height:100%;background:#000}" +
                "img{width:100%;height:100%;object-fit:contain;display:block}</style>" +
                $"<img src=\"{StreamPath}\" alt=\"Basis Camera\">");

            byte[] header = Encoding.ASCII.GetBytes(
                "HTTP/1.0 200 OK\r\n" +
                "Connection: close\r\n" +
                "Cache-Control: no-store, no-cache\r\n" +
                "Content-Type: text/html; charset=utf-8\r\n" +
                $"Content-Length: {body.Length}\r\n\r\n");

            stream.Write(header, 0, header.Length);
            stream.Write(body, 0, body.Length);
        }

        /// <summary>
        /// Worker thread. Hands each viewer the frame as a single asynchronous write and moves on.
        /// <para>
        /// This used to poll each socket for writability and then write the frame synchronously,
        /// which reads as safe and is not: the poll only says the send buffer has room for
        /// <em>something</em>, so a frame several times that size still blocked until the viewer
        /// drained it. One browser tab slow to decode therefore stalled the encode, the other
        /// viewers, and the next readback along with it — for up to the send timeout — and the
        /// stream came back as a burst. Now a viewer that is still draining simply misses this
        /// frame.
        /// </para>
        /// </summary>
        private void Broadcast(byte[] frame)
        {
            if (clients.Count == 0) return;

            byte[] part = Encoding.ASCII.GetBytes(
                $"--{Boundary}\r\nContent-Type: image/jpeg\r\nContent-Length: {frame.Length}\r\n\r\n");
            int total = part.Length + frame.Length + FrameTrailer.Length;

            for (int Index = clients.Count - 1; Index >= 0; Index--)
            {
                Viewer viewer = clients[Index];

                // Still draining the last frame: skip this one for this viewer. Queueing instead
                // is what turns a live stream into an ever-growing delay.
                if (viewer.Writing) continue;

                if (viewer.Scratch.Length < total) viewer.Scratch = new byte[total + (total >> 2)];
                Buffer.BlockCopy(part, 0, viewer.Scratch, 0, part.Length);
                Buffer.BlockCopy(frame, 0, viewer.Scratch, part.Length, frame.Length);
                Buffer.BlockCopy(FrameTrailer, 0, viewer.Scratch, part.Length + frame.Length, FrameTrailer.Length);

                viewer.WriteStartedTick = Environment.TickCount;
                viewer.Writing = true;
                try
                {
                    viewer.Stream.BeginWrite(viewer.Scratch, 0, total, WriteCompleted, viewer);
                }
                catch (Exception e)
                {
                    viewer.Writing = false;
                    DropViewer(Index, $"{e.GetType().Name}: {e.Message}");
                }
            }
            clientCount = clients.Count;
        }

        /// <summary>Thread pool. Only ever touches its own viewer, never the list the worker owns.</summary>
        private void WriteCompleted(IAsyncResult result)
        {
            Viewer viewer = (Viewer)result.AsyncState;
            try
            {
                viewer.Stream.EndWrite(result);
            }
            catch (Exception e)
            {
                viewer.Failure = $"{e.GetType().Name}: {e.Message}";
                viewer.Dead = true;
            }
            finally
            {
                viewer.Writing = false;
            }
        }

        /// <summary>
        /// Worker thread. Clears out viewers the write callback marked dead, and any whose frame
        /// has been on the wire long enough that the connection is plainly not coming back: an
        /// asynchronous write has no send timeout behind it, so nothing else would free that slot.
        /// </summary>
        private bool ReapViewers()
        {
            bool removed = false;
            for (int Index = clients.Count - 1; Index >= 0; Index--)
            {
                Viewer viewer = clients[Index];
                if (viewer.Dead)
                {
                    DropViewer(Index, viewer.Failure);
                    removed = true;
                    continue;
                }

                if (viewer.Writing && Environment.TickCount - viewer.WriteStartedTick > WriteStallTimeoutMs)
                {
                    DropViewer(Index, $"no progress for {WriteStallTimeoutMs}ms");
                    removed = true;
                }
            }
            return removed;
        }

        /// <summary>Worker thread. Closes a viewer and takes it off the list.</summary>
        private void DropViewer(int index, string reason)
        {
            Viewer viewer = clients[index];
            clients.RemoveAt(index);
            clientCount = clients.Count;
            try { viewer.Client.Close(); } catch (Exception) { }

            // Closing the tab is normal, but a write that fails for any other reason used to end
            // the stream with no trace of why.
            BasisDebug.Log($"Web stream client dropped: {reason ?? "closed"}.", BasisDebug.LogTag.Camera);
        }

        private byte[] TakeFrame()
        {
            lock (frameLock)
            {
                byte[] frame = pendingFrame;
                pendingFrame = null;
                return frame;
            }
        }
    }

#if BASIS_VIDEO_OUTPUT_SPOUT
    public sealed class BasisSpoutVideoOutputSink
    {
        // In Always Included Shaders so the runtime-created SpoutResources survives build stripping.
        private const string BlitShaderPath = "Hidden/Klak/Spout/Blit";

        /// <summary>Grace period before the sender is expected to appear in Spout's shared sender list.</summary>
        private const float RegistrationGraceSeconds = 2f;

        private SpoutSender sender;
        private SpoutResources resources;
        private string senderName;
        private float registrationDeadline;
        private bool registrationChecked;

        public string FailureMessage { get; private set; }
        public bool SupportsAlpha = true;

        public bool Start(BasisVideoOutputSettings settings, GameObject host)
        {
            FailureMessage = null;

            // Spout shares a D3D texture handle; there is no path through any other API,
            // and the plugin fails silently rather than telling us. Say so up front.
            GraphicsDeviceType device = SystemInfo.graphicsDeviceType;
            if (device != GraphicsDeviceType.Direct3D11 && device != GraphicsDeviceType.Direct3D12)
            {
                FailureMessage = $"Spout needs Direct3D11 or Direct3D12; this player is running on {device}.";
                BasisDebug.LogError(FailureMessage, BasisDebug.LogTag.Camera);
                return false;
            }

            Shader blitShader = Shader.Find(BlitShaderPath);
            if (blitShader == null)
            {
                FailureMessage = $"Spout blit shader '{BlitShaderPath}' was stripped from the build — keep it in Always Included Shaders.";
                BasisDebug.LogError(FailureMessage, BasisDebug.LogTag.Camera);
                return false;
            }
            resources = ScriptableObject.CreateInstance<SpoutResources>();
            resources.blitShader = blitShader;
            sender = host.AddComponent<SpoutSender>();
            sender.SetResources(resources);
            sender.spoutName = settings.SenderName;
            sender.keepAlpha = false;
            sender.captureMethod = CaptureMethod.Texture;

            senderName = settings.SenderName;
            registrationDeadline = Time.unscaledTime + RegistrationGraceSeconds;
            registrationChecked = false;
            return true;
        }

        public void PushFrame(RenderTexture frame, bool keepAlpha)
        {
            if (sender == null) return;
            sender.keepAlpha = keepAlpha;
            sender.sourceTexture = frame;
            VerifyRegistration();
        }

        /// <summary>
        /// The Spout sender is created lazily on the render thread and reports nothing back
        /// when it fails, so a dead stream is otherwise indistinguishable from a live one.
        /// Once the grace period is up, ask Spout whether the sender is actually published.
        /// This only logs — a false negative here must not tear down a stream that works.
        /// </summary>
        private void VerifyRegistration()
        {
            if (registrationChecked || Time.unscaledTime < registrationDeadline) return;
            registrationChecked = true;

            string[] names;
            try
            {
                names = SpoutManager.GetSourceNames();
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"Spout plugin unreachable ({e.GetType().Name}: {e.Message}). KlakSpout.dll may be missing from the build.", BasisDebug.LogTag.Camera);
                return;
            }

            for (int Index = 0; Index < names.Length; Index++)
            {
                if (!string.Equals(names[Index], senderName, StringComparison.Ordinal)) continue;
                BasisDebug.Log($"Spout sender '{senderName}' is published — select it as a Spout2 Capture source.", BasisDebug.LogTag.Camera);
                return;
            }

            BasisDebug.LogError(
                $"Spout sender '{senderName}' never registered (Spout reports: {(names.Length == 0 ? "no senders" : string.Join(", ", names))}). " +
                "Spout needs the player on Direct3D11 or Direct3D12 — it cannot work on Vulkan.",
                BasisDebug.LogTag.Camera);
        }

        public void Stop()
        {
            if (sender != null)
            {
                // Drop the texture first: the sender's end-of-frame coroutine can still run
                // after this, and the stream RT is released as soon as we return.
                sender.sourceTexture = null;
                UnityEngine.Object.Destroy(sender);
            }
            if (resources != null) UnityEngine.Object.Destroy(resources);
            sender = null;
            resources = null;
            senderName = null;
        }
    }
#endif

#if BASIS_VIDEO_OUTPUT_SYPHON
    public sealed class BasisSyphonVideoOutputSink
    {
        // In Always Included Shaders so the runtime-created SyphonResources survives build stripping.
        private const string BlitShaderPath = "Hidden/Klak/Syphon/Blit";

        /// <summary>Grace period before the server is expected to appear in Syphon's server directory.</summary>
        private const float RegistrationGraceSeconds = 2f;

        private SyphonServer server;
        private SyphonResources resources;
        private string serverName;
        private float registrationDeadline;
        private bool registrationChecked;

        public string FailureMessage { get; private set; }
        public bool SupportsAlpha = true;

        public bool Start(BasisVideoOutputSettings settings, GameObject host)
        {
            FailureMessage = null;

            // KlakSyphon publishes an IOSurface backed by a Metal texture; there is no path
            // through any other graphics API, and the plugin hands back a null server rather
            // than telling us. Say so up front.
            GraphicsDeviceType device = SystemInfo.graphicsDeviceType;
            if (device != GraphicsDeviceType.Metal)
            {
                FailureMessage = $"Syphon needs Metal; this player is running on {device}.";
                BasisDebug.LogError(FailureMessage, BasisDebug.LogTag.Camera);
                return false;
            }

            Shader blitShader = Shader.Find(BlitShaderPath);
            if (blitShader == null)
            {
                FailureMessage = $"Syphon blit shader '{BlitShaderPath}' was stripped from the build — keep it in Always Included Shaders.";
                BasisDebug.LogError(FailureMessage, BasisDebug.LogTag.Camera);
                return false;
            }
            resources = ScriptableObject.CreateInstance<SyphonResources>();
            resources.blitShader = blitShader;
            server = host.AddComponent<SyphonServer>();
            server.Resources = resources;
            server.KeepAlpha = false;
            server.CaptureMethod = CaptureMethod.Texture;
            server.ServerName = settings.SenderName;

            serverName = settings.SenderName;
            registrationDeadline = Time.unscaledTime + RegistrationGraceSeconds;
            registrationChecked = false;
            return true;
        }

        public void PushFrame(RenderTexture frame, bool keepAlpha)
        {
            if (server == null) return;
            server.KeepAlpha = keepAlpha;

            // Changing the source texture tears the server down, so only assign when it changes.
            if (server.SourceTexture != frame)
            {
                server.SourceTexture = frame;
            }
            VerifyRegistration();
        }

        /// <summary>
        /// The server is only created on the first frame after a source texture exists, and
        /// Plugin_CreateServer returns a null instance on failure without raising anything, so a
        /// dead stream is otherwise indistinguishable from a live one. Once the grace period is
        /// up, ask Syphon whether the server is actually published. This only logs — a false
        /// negative here must not tear down a stream that works.
        /// </summary>
        private void VerifyRegistration()
        {
            if (registrationChecked || Time.unscaledTime < registrationDeadline) return;
            registrationChecked = true;

            List<string> names = new List<string>();
            try
            {
                names.AddRange(SyphonServerDirectory.EnumerateServerNames());
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"Syphon plugin unreachable ({e.GetType().Name}: {e.Message}). KlakSyphon.bundle may be missing from the build.", BasisDebug.LogTag.Camera);
                return;
            }

            for (int Index = 0; Index < names.Count; Index++)
            {
                // The directory reports servers as "<app>/<server>", so compare the server half.
                string entry = names[Index];
                if (string.IsNullOrEmpty(entry)) continue;
                int slash = entry.LastIndexOf('/');
                string published = slash >= 0 ? entry.Substring(slash + 1) : entry;
                if (!string.Equals(published, serverName, StringComparison.Ordinal)) continue;
                BasisDebug.Log($"Syphon server '{serverName}' is published — select it as a Syphon source.", BasisDebug.LogTag.Camera);
                return;
            }

            BasisDebug.LogError(
                $"Syphon server '{serverName}' never registered (Syphon reports: {(names.Count == 0 ? "no servers" : string.Join(", ", names))}). " +
                "Syphon needs the player on Metal, with KlakSyphon.bundle present in the build.",
                BasisDebug.LogTag.Camera);
        }

        public void Stop()
        {
            if (server != null)
            {
                // Drop the texture first: assigning null tears the plugin down and stops the
                // end-of-frame capture coroutine, which would otherwise still blit from the
                // stream RT after this returns — the caller releases it immediately.
                server.SourceTexture = null;
                UnityEngine.Object.Destroy(server);
            }
            if (resources != null) UnityEngine.Object.Destroy(resources);
            server = null;
            resources = null;
            serverName = null;
        }
    }
#endif

#if BASIS_VIDEO_OUTPUT_V4L2
    /// <summary>
    /// Writes frames into a v4l2loopback virtual camera purely through libc so Linux apps
    /// (OBS, ffmpeg, browsers) consume the stream like a webcam. Requires the kernel
    /// module: sudo modprobe v4l2loopback exclusive_caps=1
    /// </summary>
    public sealed unsafe class BasisV4L2VideoOutputSink
    {
        private const int O_RDWR = 0x0002;
        private const int EINTR = 4;
        private const uint FourccXBGR32 = 0x34325258;              // 'XR24' — B,G,R,X in memory, matches BGRA32 readback
        private const ulong VIDIOC_QUERYCAP = 0x80685600;
        private const ulong VIDIOC_S_FMT = 0xC0D05605;
        private const uint V4L2_BUF_TYPE_VIDEO_OUTPUT = 2;
        private const uint V4L2_FIELD_NONE = 1;
        private const uint V4L2_COLORSPACE_SRGB = 8;
        private const uint V4L2_CAP_VIDEO_OUTPUT = 0x00000002;
        private const uint V4L2_CAP_DEVICE_CAPS = 0x80000000;
        private const string LoopbackDriverName = "v4l2 loopback";

        private int fd = -1;
        private string devicePath;
        private Action<AsyncGPUReadbackRequest> readbackCallback;

        /// <summary>Devices already streaming from this process, so simultaneous cameras each claim their own (#854).</summary>
        private static readonly HashSet<string> ClaimedDevices = new HashSet<string>();

        public string FailureMessage { get; private set; }
        public bool SupportsAlpha = false;

        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        private static extern int Open(string path, int flags);

        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        private static extern int Close(int fd);

        [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
        private static extern int Ioctl(int fd, ulong request, void* argp);

        [DllImport("libc", EntryPoint = "write", SetLastError = true)]
        private static extern IntPtr Write(int fd, void* buffer, UIntPtr count);

        [StructLayout(LayoutKind.Sequential)]
        private struct V4L2Capability                              // struct v4l2_capability, 104 bytes
        {
            public fixed byte Driver[16];
            public fixed byte Card[32];
            public fixed byte BusInfo[32];
            public uint Version;
            public uint Capabilities;
            public uint DeviceCaps;
            public fixed uint Reserved[3];
        }

        [StructLayout(LayoutKind.Explicit, Size = 208)]
        private struct V4L2Format                                  // struct v4l2_format, union at offset 8 (pointer-aligned)
        {
            [FieldOffset(0)] public uint Type;
            [FieldOffset(8)] public uint Width;
            [FieldOffset(12)] public uint Height;
            [FieldOffset(16)] public uint PixelFormat;
            [FieldOffset(20)] public uint Field;
            [FieldOffset(24)] public uint BytesPerLine;
            [FieldOffset(28)] public uint SizeImage;
            [FieldOffset(32)] public uint Colorspace;
        }

        public bool Start(BasisVideoOutputSettings settings, GameObject host)
        {
            fd = OpenDevice(settings.DevicePath, out devicePath);
            if (fd < 0)
            {
                BasisDebug.LogError("No v4l2loopback output device found. Load the module first: sudo modprobe v4l2loopback exclusive_caps=1", BasisDebug.LogTag.Camera);
                return false;
            }

            var format = new V4L2Format
            {
                Type = V4L2_BUF_TYPE_VIDEO_OUTPUT,
                Width = (uint)settings.Width,
                Height = (uint)settings.Height,
                PixelFormat = FourccXBGR32,
                Field = V4L2_FIELD_NONE,
                BytesPerLine = (uint)settings.Width * 4,
                SizeImage = (uint)(settings.Width * settings.Height * 4),
                Colorspace = V4L2_COLORSPACE_SRGB
            };
            if (Ioctl(fd, VIDIOC_S_FMT, &format) != 0)
            {
                BasisDebug.LogError($"VIDIOC_S_FMT on {devicePath} failed (errno {Marshal.GetLastWin32Error()})", BasisDebug.LogTag.Camera);
                Stop();
                return false;
            }

            readbackCallback = OnReadbackComplete;
            BasisDebug.Log($"V4L2 video output writing to {devicePath}", BasisDebug.LogTag.Camera);
            return true;
        }

        public void PushFrame(RenderTexture frame, bool keepAlpha)
        {
            if (fd < 0 || FailureMessage != null) return;
            AsyncGPUReadback.Request(frame, 0, TextureFormat.BGRA32, readbackCallback);
        }

        public void Stop()
        {
            if (fd >= 0)
            {
                Close(fd);
                fd = -1;
            }
            if (devicePath != null)
            {
                ClaimedDevices.Remove(devicePath);
                devicePath = null;
            }
        }

        private void OnReadbackComplete(AsyncGPUReadbackRequest request)
        {
            if (fd < 0 || FailureMessage != null) return;
            if (request.hasError)
            {
                FailureMessage = "GPU readback for the video output failed.";
                return;
            }

            NativeArray<byte> data = request.GetData<byte>();
            byte* source = (byte*)data.GetUnsafeReadOnlyPtr();
            long total = data.Length;
            long offset = 0;
            while (offset < total)
            {
                long written = Write(fd, source + offset, (UIntPtr)(total - offset)).ToInt64();
                if (written < 0)
                {
                    int errno = Marshal.GetLastWin32Error();
                    if (errno == EINTR) continue;
                    FailureMessage = $"Writing to {devicePath} failed (errno {errno}).";
                    return;
                }
                offset += written;
            }
        }

        private static int OpenDevice(string overridePath, out string chosenPath)
        {
            if (!string.IsNullOrEmpty(overridePath))
            {
                chosenPath = overridePath;
                if (!ClaimedDevices.Add(overridePath)) return -1;
                int overrideFd = Open(overridePath, O_RDWR);
                if (overrideFd < 0) ClaimedDevices.Remove(overridePath);
                return overrideFd;
            }

            string[] candidates;
            try
            {
                candidates = Directory.GetFiles("/dev", "video*");
            }
            catch (Exception)
            {
                chosenPath = null;
                return -1;
            }
            Array.Sort(candidates);

            foreach (string candidate in candidates)
            {
                if (ClaimedDevices.Contains(candidate)) continue;
                int candidateFd = Open(candidate, O_RDWR);
                if (candidateFd < 0) continue;

                var capability = default(V4L2Capability);
                if (Ioctl(candidateFd, VIDIOC_QUERYCAP, &capability) == 0)
                {
                    uint caps = (capability.Capabilities & V4L2_CAP_DEVICE_CAPS) != 0 ? capability.DeviceCaps : capability.Capabilities;
                    if ((caps & V4L2_CAP_VIDEO_OUTPUT) != 0 && DriverNameMatches(capability.Driver))
                    {
                        chosenPath = candidate;
                        ClaimedDevices.Add(candidate);
                        return candidateFd;
                    }
                }
                Close(candidateFd);
            }

            chosenPath = null;
            return -1;
        }

        private static bool DriverNameMatches(byte* driver)
        {
            for (int Index = 0; Index < LoopbackDriverName.Length; Index++)
            {
                if (driver[Index] != (byte)LoopbackDriverName[Index]) return false;
            }
            return driver[LoopbackDriverName.Length] == 0;
        }
    }
#endif
}
