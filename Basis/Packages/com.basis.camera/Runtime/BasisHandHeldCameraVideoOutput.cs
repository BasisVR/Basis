using Basis;
using System;
using UnityEngine;
public partial class BasisHandHeldCamera
{
    private static readonly int TransparentMaskTexId = Shader.PropertyToID("_MaskTex"), TransparentScaleOffsetId = Shader.PropertyToID("_ScaleOffset");
    [NonSerialized] public BasisVideoOutputSettings VideoOutputSettings = new BasisVideoOutputSettings();
    [NonSerialized] public bool IsVideoOutputActive, IsWebStreamActive;
    [NonSerialized] public string LiveOutputFailure = string.Empty;
    [NonSerialized] public BasisVideoTransport VideoTransport = BasisCameraVideoPlatform.Supported ? BasisVideoTransport.Platform : BasisVideoTransport.Web;
    public Shader TransparentVideoOutputShader;
    private IBasisVideoOutputSink videoSink;
    private BasisWebVideoOutputSink webSink;
    private string activeSenderName = string.Empty;
    private RenderTexture videoStreamTexture, transparentVideoMaskTexture, webStreamTexture;
    private Material transparentVideoOutputMaterial;
    private BasisRenderRateLimiter videoPacing;
    private BasisStreamFramePacer webStreamPacer;
    private bool streamFrameIsFresh;
    public string WebStreamUrl => webSink != null ? webSink.Url : string.Empty;
    public bool IsAnyVideoOutputActive => IsVideoOutputActive || IsWebStreamActive;
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
            started = false;
            LiveOutputFailure = $"{BasisCameraVideoPlatform.TransportName(VideoTransport)} threw on start ({e.GetType().Name}: {e.Message}).";
            BasisDebug.LogError($"{LiveOutputFailure} {e}", BasisDebug.LogTag.Camera);
            StopWebStream();
            StopVideoOutput();
        }
        if (started) return true;

        if (string.IsNullOrEmpty(LiveOutputFailure)) LiveOutputFailure = $"{BasisCameraVideoPlatform.TransportName(VideoTransport)} refused to start and gave no reason.";
        BasisDebug.LogError($"Live output refused: {LiveOutputFailure}", BasisDebug.LogTag.Camera);
        return false;
    }
    public void StopLiveOutput()
    {
        LiveOutputFailure = string.Empty;
        StopWebStream();
        StopVideoOutput();
    }
    public void SetVideoTransport(BasisVideoTransport transport)
    {
        if (VideoTransport == transport) return;
        bool wasActive = IsAnyVideoOutputActive;
        StopLiveOutput();
        VideoTransport = transport;
        if (wasActive) StartLiveOutput();
    }
    public bool MatchesStreamPreset(in BasisCameraStreamPreset preset) => preset.Matches(VideoTransport, VideoOutputSettings);
    public void ApplyStreamPreset(in BasisCameraStreamPreset preset)
    {
        BasisVideoOutputSettings settings = VideoOutputSettings;
        int quality = preset.Transport == BasisVideoTransport.Web ? preset.WebQuality : settings.WebQuality;
        ApplyStreamSettings(preset.Transport, preset.Width, preset.Height, preset.FrameRate, quality, settings.WebPort, settings.SenderName);
    }
    public void ApplyStreamSettings(BasisVideoTransport transport, int width, int height, float frameRate, int webQuality, int webPort, string senderName)
    {
        if (!BasisCameraVideoPlatform.IsAvailable(transport)) transport = BasisVideoTransport.Web;
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
    public bool StartWebStream()
    {
        StopWebStream();
        if (captureCamera == null)
        {
            LiveOutputFailure = "This camera has no capture camera to publish.";
            return false;
        }

        BasisVideoOutputSettings settings = VideoOutputSettings;
        settings.ClampSize();

        int port = BasisCameraVideoPlatform.FirstUnclaimedPort(Mathf.Clamp(settings.WebPort, 1024, 65500));

        webSink = new BasisWebVideoOutputSink();
        if (!webSink.Start(port))
        {
            LiveOutputFailure = webSink.FailureMessage ?? $"No free port from {port} upwards to serve the stream on.";
            webSink = null;
            return false;
        }
        BasisCameraVideoPlatform.ClaimPort(webSink.Port);
        settings.WebPort = webSink.Port;

        ResizeWebStreamTexture();

        webStreamPacer.Reset();
        streamFrameIsFresh = true;
        IsWebStreamActive = true;
        UpdateRenderGate();
        BasisDebug.Log($"Web stream started at {webSink.Url} — open it in a browser, or add it to OBS as a Browser source.", BasisDebug.LogTag.Camera);
        return true;
    }
    public void SetWebStreamPort(int port)
    {
        int clamped = Mathf.Clamp(port, 1024, 65500);
        if (VideoOutputSettings.WebPort == clamped) return;
        VideoOutputSettings.WebPort = clamped;
        if (IsWebStreamActive) StartWebStream();
    }
    public void SetWebStreamQuality(int quality) => VideoOutputSettings.WebQuality = Mathf.Clamp(quality, 1, 100);
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
    public void StopWebStream()
    {
        bool wasActive = IsWebStreamActive;
        IsWebStreamActive = false;
        if (webSink != null)
        {
            BasisCameraVideoPlatform.ReleasePort(webSink.Port);
            webSink.Stop();
            webSink = null;
        }
        BasisCameraRenderTargets.Release(ref webStreamTexture);
        if (wasActive) UpdateRenderGate();
    }
    public bool StartVideoOutput()
    {
        if (!BasisCameraVideoPlatform.Supported)
        {
            LiveOutputFailure = "This build carries no shared-texture video backend — use the web stream instead.";
            return false;
        }

        StopVideoOutput();
        if (captureCamera == null)
        {
            LiveOutputFailure = "This camera has no capture camera to publish.";
            return false;
        }

        if (!BodyAllowsLiveFeed)
        {
            LiveOutputFailure = $"A {BodyTraits.Kind} body has no output socket — it only shows its own viewfinder. Switch the body on the Presets tab.";
            return false;
        }
        BasisVideoOutputSettings settings = VideoOutputSettings;
        settings.ClampSize();

        activeSenderName = BasisCameraVideoPlatform.ClaimSenderName(settings.SenderName);
        videoSink = BasisCameraVideoPlatform.CreateSink();
        videoStreamTexture = BasisCameraRenderTargets.Create(settings.Width, settings.Height, BasisCameraVideoPlatform.FrameFormat, 0, 1, true, "BasisVideoOutput");

        string requestedName = settings.SenderName;
        settings.SenderName = activeSenderName;
        bool sinkStarted = videoSink.Start(settings, captureCamera.gameObject);
        settings.SenderName = requestedName;
        if (!sinkStarted)
        {
            LiveOutputFailure = videoSink.FailureMessage ?? $"{BasisCameraVideoPlatform.BackendName} would not start.";
            StopVideoOutput();
            return false;
        }
        videoPacing = default;
        IsVideoOutputActive = true;
        if (backgroundMode == BasisCameraBackgroundMode.Transparent && videoSink.SupportsAlpha) PrepareTransparentVideoOutputResources(renderTexture);
        UpdateRenderGate();
        BasisDebug.Log($"{BasisCameraVideoPlatform.BackendName} output started as '{activeSenderName}': {settings.Width}x{settings.Height} @ {settings.FrameRate}fps", BasisDebug.LogTag.Camera);
        return true;
    }
    public void StopVideoOutput()
    {
        bool wasActive = IsVideoOutputActive;
        IsVideoOutputActive = false;
        BasisCameraVideoPlatform.ReleaseSenderName(activeSenderName);
        activeSenderName = string.Empty;
        if (videoSink != null)
        {
            videoSink.Stop();
            videoSink = null;
        }
        BasisCameraRenderTargets.Release(ref videoStreamTexture);
        ReleaseTransparentVideoOutputResources();
        if (wasActive) UpdateRenderGate();
    }
    public void SetVideoOutputResolution(int width, int height)
    {
        VideoOutputSettings.Width = width;
        VideoOutputSettings.Height = height;
        if (IsVideoOutputActive) StartVideoOutput();
        else if (IsWebStreamActive) ResizeWebStreamTexture();
    }
    public void SetVideoOutputFrameRate(float frameRate) => VideoOutputSettings.FrameRate = frameRate;
    private void MarkStreamFrameFresh() => streamFrameIsFresh = true;
    private bool CanPreserveVideoOutputAlpha() => IsVideoOutputActive && videoSink != null && videoSink.SupportsAlpha;
    private void ResizeWebStreamTexture()
    {
        BasisVideoOutputSettings settings = VideoOutputSettings;
        settings.ClampSize();
        if (webStreamTexture != null && webStreamTexture.width == settings.Width && webStreamTexture.height == settings.Height) return;
        BasisCameraRenderTargets.Release(ref webStreamTexture);
        webStreamTexture = BasisCameraRenderTargets.Create(settings.Width, settings.Height, RenderTextureFormat.ARGB32, 0, 1, true, "BasisWebVideoOutput");
        streamFrameIsFresh = true;
    }
    private void TickVideoOutput()
    {
        TickWebStream();
        if (!IsVideoOutputActive) return;
        if (videoSink.FailureMessage != null)
        {
            BasisDebug.LogError($"Video output stopped: {videoSink.FailureMessage}", BasisDebug.LogTag.Camera);
            StopVideoOutput();
            return;
        }
        Texture source = renderTexture;
        if (source == null || !videoPacing.AllowThisFrame(Time.unscaledDeltaTime, VideoOutputSettings.FrameRate, true)) return;

        BasisCameraRenderTargets.GetBlitCrop(source, videoStreamTexture, out Vector2 scale, out Vector2 offset);
        if (BasisCameraVideoPlatform.FlipsRows)
        {
            scale.y = -scale.y;
            offset.y += -scale.y;
        }

        bool outputHasAlpha = false;
        if (backgroundMode == BasisCameraBackgroundMode.Transparent && videoSink.SupportsAlpha)
        {
            try
            {
                captureCamera.Render();
                captureCamera.enabled = false;
                outputHasAlpha = BlitTransparentVideoOutput(source, scale, offset);
            }
            catch (Exception ex)
            {
                BasisDebug.LogErrorOnce($"Transparent video output failed: {ex}", BasisDebug.LogTag.Camera);
            }
        }

        if (!outputHasAlpha) Graphics.Blit(source, videoStreamTexture, scale, offset);
        videoSink.PushFrame(videoStreamTexture, outputHasAlpha);
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
        if (!webSink.HasClients)
        {
            webStreamPacer.Reset();
            return;
        }

        Texture source = renderTexture;
        if (!webStreamPacer.AllowThisFrame(Time.unscaledDeltaTime, VideoOutputSettings.FrameRate, streamFrameIsFresh, source != null && webSink.CanAcceptFrame)) return;
        streamFrameIsFresh = false;

        BasisCameraRenderTargets.GetBlitCrop(source, webStreamTexture, out Vector2 scale, out Vector2 offset);
        Graphics.Blit(source, webStreamTexture, scale, offset);
        webSink.PushFrame(webStreamTexture, VideoOutputSettings.WebQuality);
    }
    private bool BlitTransparentVideoOutput(Texture source, Vector2 scale, Vector2 offset)
    {
        if (!TransparentVideoOutputResourcesReady(source) || !RenderTransparentVideoMask(source)) return false;

        transparentVideoOutputMaterial.SetTexture(TransparentMaskTexId, transparentVideoMaskTexture);
        transparentVideoOutputMaterial.SetVector(TransparentScaleOffsetId, new Vector4(scale.x, scale.y, offset.x, offset.y));
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

        int samples = SamplesOf(source);
        if (transparentVideoMaskTexture != null && (transparentVideoMaskTexture.width != source.width || transparentVideoMaskTexture.height != source.height || transparentVideoMaskTexture.antiAliasing != samples)) BasisCameraRenderTargets.Release(ref transparentVideoMaskTexture);
        if (transparentVideoMaskTexture == null) transparentVideoMaskTexture = BasisCameraRenderTargets.Create(source.width, source.height, RenderTextureFormat.ARGB32, 24, samples, false, "BasisTransparentVideoMask");
        return true;
    }
    private bool TransparentVideoOutputResourcesReady(Texture source)
    {
        if (source == null || transparentVideoOutputMaterial == null || transparentVideoMaskTexture == null) return false;
        return transparentVideoMaskTexture.width == source.width && transparentVideoMaskTexture.height == source.height && transparentVideoMaskTexture.antiAliasing == SamplesOf(source);
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
        BasisCameraRenderTargets.Release(ref transparentVideoMaskTexture);
        BasisCameraRenderTargets.DestroyAndClear(ref transparentVideoOutputMaterial);
    }
    private static int SamplesOf(Texture source) => source is RenderTexture renderTarget ? renderTarget.antiAliasing : 1;
}
