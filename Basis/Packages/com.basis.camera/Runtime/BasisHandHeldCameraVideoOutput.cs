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
#endif
    private RenderTexture videoStreamTexture;
    private BasisRenderRateLimiter videoPacing;

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
    private BasisRenderRateLimiter webPacing;

    /// <summary>Address to open, or empty when not serving.</summary>
    public string WebStreamUrl => webSink != null ? webSink.Url : string.Empty;

    /// <summary>Whether anything is currently reading the stream.</summary>
    public bool WebStreamHasViewers => webSink != null && webSink.HasClients;

    /// <summary>True when any live output is consuming the render texture.</summary>
    public bool IsAnyVideoOutputActive => IsVideoOutputActive || IsWebStreamActive;

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
        return VideoTransport == BasisVideoTransport.Web ? StartWebStream() : StartVideoOutput();
    }

    /// <summary>Stops every transport, whichever one happens to be up.</summary>
    public void StopLiveOutput()
    {
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

    /// <summary>
    /// Starts an MJPEG stream on loopback. Unlike Spout/Syphon this needs nothing installed
    /// on the receiving side — OBS reads it with a stock Browser or Media source — but it
    /// costs a readback and a JPEG encode per frame, so it only pays that while someone is
    /// connected.
    /// </summary>
    public bool StartWebStream()
    {
        StopWebStream();
        if (captureCamera == null) return false;

        BasisVideoOutputSettings settings = VideoOutputSettings;
        settings.Width = Mathf.Clamp(settings.Width, 16, 8192);
        settings.Height = Mathf.Clamp(settings.Height, 16, 8192);

        int port = Mathf.Clamp(settings.WebPort, 1024, 65500);
        while (ClaimedWebPorts.Contains(port) && port < 65500) port++;

        webSink = new BasisWebVideoOutputSink();
        if (!webSink.Start(port))
        {
            webSink = null;
            return false;
        }
        ClaimedWebPorts.Add(webSink.Port);
        settings.WebPort = webSink.Port;

        webStreamTexture = new RenderTexture(new RenderTextureDescriptor(settings.Width, settings.Height, RenderTextureFormat.ARGB32, 0) { sRGB = true })
        {
            name = "BasisWebVideoOutput"
        };
        webStreamTexture.Create();

        webPacing = default;
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
        if (!webSink.HasClients) return;

        Texture source = renderTexture;
        if (source == null) return;
        if (!webPacing.AllowThisFrame(Time.unscaledDeltaTime, VideoOutputSettings.FrameRate, true)) return;

        GetStreamBlitCrop(source, webStreamTexture, out Vector2 scale, out Vector2 offset);
        Graphics.Blit(source, webStreamTexture, scale, offset);
        webSink.PushFrame(webStreamTexture, VideoOutputSettings.WebQuality);
    }

    /// <summary>Starts streaming with <see cref="VideoOutputSettings"/>. The capture camera keeps rendering while active, even when the preview is hidden.</summary>
    public bool StartVideoOutput()
    {
#if BASIS_VIDEO_OUTPUT_SUPPORTED
        StopVideoOutput();
        if (captureCamera == null) return false;
        BasisVideoOutputSettings settings = VideoOutputSettings;
        settings.Width = Mathf.Clamp(settings.Width, 16, 8192);
        settings.Height = Mathf.Clamp(settings.Height, 16, 8192);

        string baseName = string.IsNullOrEmpty(settings.SenderName) ? "Basis Camera" : settings.SenderName;
        string senderName = baseName;
        for (int Suffix = 2; ActiveVideoOutputNames.Contains(senderName); Suffix++)
        {
            senderName = $"{baseName} {Suffix}";
        }
        settings.SenderName = senderName;
        ActiveVideoOutputNames.Add(senderName);

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

        if (!videoSink.Start(settings, captureCamera.gameObject))
        {
            StopVideoOutput();
            return false;
        }
        videoPacing = default;
        IsVideoOutputActive = true;
        UpdateRenderGate();
        BasisDebug.Log($"{VideoOutputBackendName} output started as '{settings.SenderName}': {settings.Width}x{settings.Height} @ {settings.FrameRate}fps", BasisDebug.LogTag.Camera);
        return true;
#else
        return false;
#endif
    }

    /// <summary>Stops streaming.</summary>
    public void StopVideoOutput()
    {
#if BASIS_VIDEO_OUTPUT_SUPPORTED
        bool wasActive = IsVideoOutputActive;
        IsVideoOutputActive = false;
        ActiveVideoOutputNames.Remove(VideoOutputSettings.SenderName);
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
        if (wasActive) UpdateRenderGate();
#endif
    }

    /// <summary>Sets the stream resolution, restarting the stream when active.</summary>
    public void SetVideoOutputResolution(int width, int height)
    {
        VideoOutputSettings.Width = width;
        VideoOutputSettings.Height = height;
        if (IsVideoOutputActive) StartVideoOutput();
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
        Graphics.Blit(source, videoStreamTexture, new Vector2(scale.x, -scale.y), new Vector2(offset.x, offset.y + scale.y));
#else
        Graphics.Blit(source, videoStreamTexture, scale, offset);
#endif
        videoSink.PushFrame(videoStreamTexture);
#endif
    }

    /// <summary>
    /// Source-to-target UV crop for the stream blits. The feed's aspect is no longer fixed — Direct
    /// To Screen sizes it to the screen — while the stream's is whatever resolution the user set,
    /// and a plain blit stretches one onto the other, which a receiver has no way to undo. Meeting
    /// in the middle of the frame keeps the stream undistorted at the size receivers were told to
    /// expect. Identity whenever the two already agree, which is the usual case.
    /// </summary>
    private static void GetStreamBlitCrop(Texture source, RenderTexture destination, out Vector2 scale, out Vector2 offset)
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

    public class BasisVideoOutputSettings
    {
        public int Width = 1920;
        public int Height = 1080;
        /// <summary>Target framerate. 0 or below streams at the render rate.</summary>
        public float FrameRate = 30f;
        /// <summary>Sender/server name shown to Spout (Windows) and Syphon (macOS) receivers.</summary>
        public string SenderName = "Basis Camera";
        /// <summary>Optional explicit /dev/videoN path (Linux only). Empty auto-detects the first v4l2loopback device.</summary>
        public string DevicePath = string.Empty;

        /// <summary>Loopback port for the MJPEG web stream. Taken ports roll forward to the next free one.</summary>
        public int WebPort = 8787;

        /// <summary>JPEG quality for the web stream, 1-100. Lower is cheaper to encode and to send.</summary>
        public int WebQuality = 70;
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

        private static readonly byte[] FrameTrailer = Encoding.ASCII.GetBytes("\r\n");

        private TcpListener listener;
        private Thread worker;
        private volatile bool running;
        private volatile int clientCount;

        /// <summary>Touched only by the worker thread while running.</summary>
        private readonly List<TcpClient> clients = new List<TcpClient>();

        /// <summary>
        /// Raw readback bytes handed from the main thread to the worker for encoding.
        /// Ownership flips on <see cref="rawReady"/>: false means the main thread may fill it,
        /// true means the worker is encoding it. Neither side touches it out of turn, so one
        /// buffer is enough and nothing is allocated per frame.
        /// </summary>
        private byte[] rawFrame;
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

        /// <summary>Main thread. Requests a readback of the frame; the encode happens in the callback.</summary>
        public void PushFrame(RenderTexture frame, int quality)
        {
            // One readback in flight at a time: queueing them would just build latency, and
            // the newest frame is the only one a live stream cares about.
            if (!running || readbackInFlight || clientCount == 0) return;

            frameWidth = frame.width;
            frameHeight = frame.height;
            frameQuality = Mathf.Clamp(quality, 1, 100);
            readbackInFlight = true;
            AsyncGPUReadback.Request(frame, 0, TextureFormat.RGBA32, readbackCallback);
        }

        public void Stop()
        {
            running = false;
            try { listener?.Stop(); } catch (Exception) { }
            listener = null;

            if (worker != null)
            {
                worker.Join(250);
                worker = null;
            }

            for (int Index = 0; Index < clients.Count; Index++)
            {
                try { clients[Index].Close(); } catch (Exception) { }
            }
            clients.Clear();
            clientCount = 0;
            rawReady = false;
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
                byte[] jpeg = EncodeJpeg(request.GetData<byte>().ToArray(), frameWidth, frameHeight, frameQuality);
                if (jpeg != null) lock (frameLock) pendingFrame = jpeg;
                return;
            }

            // Worker still busy with the previous frame: drop this one. Handing over anyway
            // would mean encoding frames nobody can keep up with.
            if (rawReady) return;

            NativeArray<byte> data = request.GetData<byte>();
            if (rawFrame == null || rawFrame.Length != data.Length) rawFrame = new byte[data.Length];
            data.CopyTo(rawFrame);
            rawWidth = frameWidth;
            rawHeight = frameHeight;
            rawQuality = frameQuality;
            rawReady = true;
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

        private void WorkerLoop()
        {
            while (running)
            {
                bool didWork = false;
                try
                {
                    while (listener != null && listener.Pending())
                    {
                        AcceptClient();
                        didWork = true;
                    }

                    if (rawReady && !encodeOnMainThread)
                    {
                        byte[] jpeg = EncodeJpeg(rawFrame, rawWidth, rawHeight, rawQuality);
                        rawReady = false;
                        if (jpeg != null)
                        {
                            Broadcast(jpeg);
                            didWork = true;
                        }
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

                if (!didWork) Thread.Sleep(1);
            }
        }

        private void AcceptClient()
        {
            TcpClient client = listener.AcceptTcpClient();
            if (clients.Count >= MaxClients)
            {
                client.Close();
                return;
            }

            client.NoDelay = true;
            // A viewer that stops reading must not wedge the worker forever, but the timeout
            // has to outlast an ordinary hitch or we would drop healthy clients.
            client.SendTimeout = 2000;
            client.ReceiveTimeout = 250;

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

            byte[] header = Encoding.ASCII.GetBytes(
                "HTTP/1.0 200 OK\r\n" +
                "Connection: close\r\n" +
                "Cache-Control: no-store, no-cache, must-revalidate\r\n" +
                "Pragma: no-cache\r\n" +
                $"Content-Type: multipart/x-mixed-replace; boundary={Boundary}\r\n\r\n");
            stream.Write(header, 0, header.Length);

            clients.Add(client);
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

        private void Broadcast(byte[] frame)
        {
            byte[] part = Encoding.ASCII.GetBytes(
                $"--{Boundary}\r\nContent-Type: image/jpeg\r\nContent-Length: {frame.Length}\r\n\r\n");

            for (int Index = clients.Count - 1; Index >= 0; Index--)
            {
                TcpClient client = clients[Index];
                try
                {
                    // Still draining the last frame: skip this one for this viewer. Queueing
                    // instead is what turns a live stream into an ever-growing delay.
                    if (!client.Client.Poll(0, SelectMode.SelectWrite)) continue;

                    NetworkStream stream = client.GetStream();
                    stream.Write(part, 0, part.Length);
                    stream.Write(frame, 0, frame.Length);
                    stream.Write(FrameTrailer, 0, FrameTrailer.Length);
                }
                catch (Exception e)
                {
                    // Closing the tab is normal, but a write that fails for any other reason
                    // used to end the stream with no trace of why.
                    BasisDebug.Log($"Web stream client dropped: {e.GetType().Name}: {e.Message}", BasisDebug.LogTag.Camera);
                    try { client.Close(); } catch (Exception) { }
                    clients.RemoveAt(Index);
                }
            }
            clientCount = clients.Count;
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

        public string FailureMessage => null;

        public bool Start(BasisVideoOutputSettings settings, GameObject host)
        {
            // Spout shares a D3D texture handle; there is no path through any other API,
            // and the plugin fails silently rather than telling us. Say so up front.
            GraphicsDeviceType device = SystemInfo.graphicsDeviceType;
            if (device != GraphicsDeviceType.Direct3D11 && device != GraphicsDeviceType.Direct3D12)
            {
                BasisDebug.LogError($"Spout output needs Direct3D11 or Direct3D12, but this player is running on {device}.", BasisDebug.LogTag.Camera);
                return false;
            }

            Shader blitShader = Shader.Find(BlitShaderPath);
            if (blitShader == null)
            {
                BasisDebug.LogError($"Spout blit shader '{BlitShaderPath}' was stripped from the build — keep it in Always Included Shaders.", BasisDebug.LogTag.Camera);
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

        public void PushFrame(RenderTexture frame)
        {
            if (sender == null) return;
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

        private SyphonServer server;
        private SyphonResources resources;

        public string FailureMessage => null;

        public bool Start(BasisVideoOutputSettings settings, GameObject host)
        {
            Shader blitShader = Shader.Find(BlitShaderPath);
            if (blitShader == null)
            {
                BasisDebug.LogError($"Syphon blit shader '{BlitShaderPath}' was stripped from the build — keep it in Always Included Shaders.", BasisDebug.LogTag.Camera);
                return false;
            }
            resources = ScriptableObject.CreateInstance<SyphonResources>();
            resources.blitShader = blitShader;
            server = host.AddComponent<SyphonServer>();
            server.Resources = resources;
            server.KeepAlpha = false;
            server.CaptureMethod = CaptureMethod.Texture;
            server.ServerName = settings.SenderName;
            return true;
        }

        public void PushFrame(RenderTexture frame)
        {
            // Property setters tear the server down, so only assign when the texture changes.
            if (server != null && server.SourceTexture != frame)
            {
                server.SourceTexture = frame;
            }
        }

        public void Stop()
        {
            if (server != null) UnityEngine.Object.Destroy(server);
            if (resources != null) UnityEngine.Object.Destroy(resources);
            server = null;
            resources = null;
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

        public void PushFrame(RenderTexture frame)
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
