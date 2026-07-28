using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using GfxAPI = UnityEngine.Rendering.GraphicsDeviceType;

// Public mirror of the native engine state (basis_media_state_t), usable from the
// editor assembly. Values match BasisNativeMedia.State.
public enum BasisMediaEngineState
{
    Idle = 0,
    Connecting = 1,
    Buffering = 2,
    Playing = 3,
    Paused = 4,
    Ended = 5,
    Error = 6,
}

// How the engine sizes its jitter buffer (how far behind live video is presented).
// Values match the native ABI: Fixed=0, Dynamic=1.
public enum BasisVideoBufferMode
{
    Fixed = 0,    // hold BufferMilliseconds exactly
    Dynamic = 1,  // auto-tune: grow on underrun risk, shrink when over-buffered
}

// Video codecs the native engine can be asked about. Values match the native
// probe ABI (basis_media_probe_video_codec).
public enum BasisVideoCodec
{
    H264 = 1,
    H265 = 2,
    VP9 = 3,
    AV1 = 4,
}

// Zero-copy live media source backed by the OS-codec engine in basis_media_native.
//
// Unlike the CPU IBasisFrameSource path (which decodes into managed byte[] frames
// and uploads them through a renderer), this source never sees pixels on the CPU:
// the native engine decodes with the OS hardware decoder straight into a GPU
// texture, and we wrap that texture's native handle with CreateExternalTexture.
// Each frame the player calls Pump() on the main thread, which issues a render-
// thread plugin event so the engine can publish its newest decoded frame into the
// Unity-visible texture, then polls the frame counter / size / state.
//
// Audio is decoded natively too and exposed as an IBasisPcmSource the audio sink
// pulls on the audio thread.
public sealed class BasisNativeVideoSource : IBasisPcmSource, IDisposable
{
    public event Action OnReady;
    public event Action<int, int> OnVideoSizeChanged;
    public event Action OnEndOfStream;
    public event Action<Exception> OnError;
    public event Action<BasisBitrateTrack> OnBitrateTrackChanged;
    public event Action<BasisAudioTrack> OnAudioTrackChanged;
    public event Action<BasisCaptionCue> OnCaptionCueChanged;

    public string Url { get; }
    public string AudioUrl { get; }
    public BasisMediaDelivery Delivery { get; }
    public Texture OutputTexture => unityOwnedRT != null ? (Texture)unityOwnedRT : externalTexture;
    public Vector2Int VideoSize => new Vector2Int(texW, texH);

    // True when the active backend publishes the frame top-left origin (upside-down
    // versus Unity's bottom-left UV convention) and the consumer must flip it
    // vertically. Windows reports this when the GPU's D3D11 video processor can't
    // mirror; the Vulkan path always normalizes to upright (false). Known by the
    // time the first frame's texture is published, so it's safe to read live.
    public bool FrameIsTopLeftOrigin => BasisNativeMedia.GetFrameOrigin(handle) == 1;
    public bool IsRunning => handle != IntPtr.Zero && started && !disposed;
    public BasisMediaEngineState State => (BasisMediaEngineState)(int)BasisNativeMedia.GetState(handle);

    // On Vulkan we flip the texture handoff direction: instead of the plugin
    // allocating a VkImage and Unity wrapping it via Texture2D.CreateExternalTexture
    // (Mali crashes inside vkCreateImageView on Pixel 7/8/9 + Android 16), we
    // allocate a RenderTexture here and pass its native pointer to the plugin via
    // basis_media_set_output_texture. The plugin then renders into Unity's image
    // through IUnityGraphicsVulkan::AccessTexture — Unity's own view already
    // exists from the RT allocation, so no fragile view-create happens at runtime.
    private static bool UsesAccessTexturePath =>
        SystemInfo.graphicsDeviceType == GfxAPI.Vulkan;

    // PTS (microseconds) of the most recently published frame; -1 until known.
    public long PositionUs => BasisNativeMedia.GetPositionUs(handle);

    // Total media duration (microseconds) once the container index has been
    // parsed; 0 for live/unknown — 0 also means the source can't seek.
    public long DurationUs => BasisNativeMedia.GetDurationUs(handle);

    // Requests an asynchronous absolute seek; lands at or shortly before the
    // target (preceding keyframe / segment boundary). False when the source
    // has no seekable timeline.
    public bool SeekUs(long targetUs) => BasisNativeMedia.SeekUs(handle, targetUs);

    // One-line native counter string (blit/copy/lag/buf/nodue/acq/audio). For the
    // debug window / diagnostics.
    public string DebugInfo => handle != IntPtr.Zero ? BasisNativeMedia.GetDebug(handle) : null;

    // Human-readable transport: negotiated detail where the protocol reports one
    // ("RTSP over UDP", "RTSP over TCP (UDP unavailable)"), the URL scheme
    // otherwise. Settles once playback starts; null on binaries without the export.
    public string Transport => handle != IntPtr.Zero ? BasisNativeMedia.GetTransport(handle) : null;

    // Monotonic count of frames decoded + written to the ring (for fps readouts).
    public ulong DecodedFrameCount => BasisNativeMedia.GetFrameCounter(handle);

    private static IntPtr renderEventFunc;
    private static bool renderEventResolved;

    private IntPtr handle;
    private CommandBuffer renderCmd;
    private Texture2D externalTexture;        // Windows path: wraps plugin VkImage / D3D texture
    private RenderTexture unityOwnedRT;       // Vulkan path: Unity-allocated, plugin renders into it
    private IntPtr lastTexturePtr;
    private ulong lastFrameCounter;
    private int texW, texH;
    private bool outputRegistered;            // true once SetOutputTexture has been called
    private bool started;
    private bool disposed;
    private bool readyFired;
    private bool eosRaised;
    private bool errorRaised;
    private string loggedTransport;

    // Desired jitter buffer; applied to the native engine on Start and on change.
    private int bufferMode = (int)BasisVideoBufferMode.Dynamic;
    private int bufferMs = 120;

    private readonly List<BasisBitrateTrack> bitrateTracks = new List<BasisBitrateTrack>();
    private readonly List<BasisAudioTrack> audioTracks = new List<BasisAudioTrack>();
    private int lastReportedBitrateIndex = -2;
    private int lastReportedAudioTrackIndex = -2;

    // Caption poll state. The scratch buffer is reused each frame; a string is only
    // built (and the event fired) when the active cue's bytes actually change.
    private readonly byte[] captionScratch = new byte[512];
    private byte[] lastCaptionBytes = Array.Empty<byte>();
    private int lastCaptionLen;          // 0 = "no caption" (matches an empty poll)
    private bool captionsSupported = true;

    public IReadOnlyList<BasisBitrateTrack> BitrateTracks => bitrateTracks;
    public IReadOnlyList<BasisAudioTrack> AudioTracks => audioTracks;
    public int SelectedBitrateIndex => BasisNativeMedia.GetSelectedBitrate(handle);
    public int SelectedAudioTrackIndex => BasisNativeMedia.GetSelectedAudioTrack(handle);

    public bool SelectBitrate(int index)
    {
        bool ok = BasisNativeMedia.SelectBitrate(handle, index);
        if (ok) PollTrackStateOnce();
        return ok;
    }

    public bool SelectAudioTrack(int index)
    {
        bool ok = BasisNativeMedia.SelectAudioTrack(handle, index);
        if (ok) PollTrackStateOnce();
        return ok;
    }

    public bool SeekBackUs(long backUs) => BasisNativeMedia.SeekBackUs(handle, backUs);

    // True when this platform decodes the codec, verified as far as the platform
    // allows (Windows: decoder MFT + GPU decode profile; Android: decoder
    // presence — hardware-universal for these codecs on Quest). Engine-less —
    // callable before any source exists, from any thread (resolvers run on
    // worker threads); the verdict is cached natively for the process lifetime.
    // Use it to gate format selection so streams are only offered where they
    // will actually play.
    public static bool IsVideoCodecSupported(BasisVideoCodec codec)
        => BasisNativeMedia.ProbeVideoCodec((int)codec);

    // Diagnostics: heartbeat so we can see whether frames keep flowing.
    private int pumpCount;
    private BasisMediaEngineState lastLoggedState = (BasisMediaEngineState)(-1);

    public BasisNativeVideoSource(string url, string audioUrl = null, BasisMediaDelivery delivery = BasisMediaDelivery.Auto)
    {
        if (string.IsNullOrEmpty(url)) throw new ArgumentNullException(nameof(url));
        Url = url;
        AudioUrl = audioUrl;
        Delivery = delivery;
    }

    public void Start()
    {
        if (disposed) throw new ObjectDisposedException(nameof(BasisNativeVideoSource));
        if (started) return;
        ResolveRenderEventFunc();
        handle = BasisNativeMedia.OpenWithAudio(Url, AudioUrl, Delivery); // throws with build instructions if the lib is missing; AudioUrl null ⇒ single stream
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException($"basis_media_open returned null for '{Url}' (unsupported scheme or out of memory).");
        BasisNativeMedia.Play(handle);
        BasisNativeMedia.SetBuffer(handle, bufferMode, bufferMs);
        started = true;
        readyFired = eosRaised = errorRaised = false;
        loggedTransport = null;
        lastFrameCounter = 0;
        lastTexturePtr = IntPtr.Zero;
        // Re-arm caption polling for the new native handle: clear the dedup state so
        // the first cue fires, and retry support in case a prior handle lacked it.
        captionsSupported = true;
        lastCaptionLen = 0;
        lastCaptionBytes = Array.Empty<byte>();
    }

    // Selects the jitter buffer length and mode. Applied live if running, and
    // remembered for (re)Start. ms <= 0 keeps the current length.
    public void SetBuffer(BasisVideoBufferMode mode, int ms)
    {
        bufferMode = (int)mode;
        if (ms > 0) bufferMs = ms;
        if (handle != IntPtr.Zero) BasisNativeMedia.SetBuffer(handle, bufferMode, bufferMs);
    }

    // Reports the audio sink's measured output latency so the native backend paces
    // video to match it (the low-latency A/V sync path; desktop self-times and
    // ignores it). Called each frame by the player from the audio component.
    public void SetAudioLatencyUs(long latencyUs)
    {
        if (handle != IntPtr.Zero) BasisNativeMedia.SetAudioLatencyUs(handle, (int)latencyUs);
    }

    public void Play() { if (handle != IntPtr.Zero) BasisNativeMedia.Play(handle); }
    public void Pause() { if (handle != IntPtr.Zero) BasisNativeMedia.Pause(handle); }

    public void Stop()
    {
        if (!started) return;
        started = false;
        if (handle != IntPtr.Zero) BasisNativeMedia.Stop(handle);
    }

    private void PollTrackStateOnce()
    {
        if (handle == IntPtr.Zero) return;

        int brCount = BasisNativeMedia.GetBitrateTrackCount(handle);
        if (brCount != bitrateTracks.Count)
        {
            bitrateTracks.Clear();
            for (int i = 0; i < brCount; i++)
            {
                if (BasisNativeMedia.TryGetBitrateTrack(handle, i, out int bps, out int w, out int h, out string codec, out string label))
                {
                    bitrateTracks.Add(new BasisBitrateTrack { Index = i, BitsPerSecond = bps, Width = w, Height = h, Codec = codec, Label = label });
                }
            }
        }
        int brSel = BasisNativeMedia.GetSelectedBitrate(handle);
        if (brSel != lastReportedBitrateIndex)
        {
            lastReportedBitrateIndex = brSel;
            if (brSel >= 0 && brSel < bitrateTracks.Count) OnBitrateTrackChanged?.Invoke(bitrateTracks[brSel]);
        }

        int atCount = BasisNativeMedia.GetAudioTrackCount(handle);
        if (atCount != audioTracks.Count)
        {
            audioTracks.Clear();
            for (int i = 0; i < atCount; i++)
            {
                if (BasisNativeMedia.TryGetAudioTrack(handle, i, out int ch, out int sr, out int bps, out bool dm, out string lang, out string codec, out string label))
                {
                    audioTracks.Add(new BasisAudioTrack { Index = i, ChannelCount = ch, SampleRate = sr, BitsPerSecond = bps, IsDualMono = dm, Language = lang, Codec = codec, Label = label });
                }
            }
        }
        int atSel = BasisNativeMedia.GetSelectedAudioTrack(handle);
        if (atSel != lastReportedAudioTrackIndex)
        {
            lastReportedAudioTrackIndex = atSel;
            if (atSel >= 0 && atSel < audioTracks.Count) OnAudioTrackChanged?.Invoke(audioTracks[atSel]);
        }
    }

    // Main-thread tick: drive the render-thread texture update and surface events.
    public void Pump(bool verboseLogging = false)
    {
        if (handle == IntPtr.Zero || disposed) return;

        var engineState = BasisNativeMedia.GetState(handle);
        bool trackStateCanChange =
            engineState == BasisNativeMedia.State.Connecting ||
            engineState == BasisNativeMedia.State.Buffering ||
            engineState == BasisNativeMedia.State.Playing;
        if (trackStateCanChange && (pumpCount % 30) == 0) PollTrackStateOnce();

        // On the Vulkan path: as soon as the engine has detected the video size,
        // allocate a RenderTexture and hand its native pointer to the plugin.
        // The plugin will then render into THIS image via AccessTexture instead
        // of allocating its own VkImage (the latter crashes Mali on Pixel 7/8/9
        // when Unity wraps it with Texture2D.CreateExternalTexture).
        if (UsesAccessTexturePath && !outputRegistered && BasisNativeMedia.TryGetVideoSize(handle, out int detW, out int detH) && detW > 0 && detH > 0)
        {
            EnsureUnityOwnedRT(detW, detH);
            BasisNativeMedia.SetOutputTexture(handle, unityOwnedRT.GetNativeTexturePtr(), detW, detH);
            outputRegistered = true;
            texW = detW; texH = detH;
            OnVideoSizeChanged?.Invoke(texW, texH);
        }

        // Ask the engine to publish its newest decoded frame into the Unity
        // texture on the render thread. IssuePluginEventAndData lives on
        // CommandBuffer (not GL); run it via a reused buffer. Cheap no-op
        // natively when no new frame is ready.
        if (renderEventFunc != IntPtr.Zero)
        {
            renderCmd ??= new CommandBuffer { name = "BasisNativeMedia.Update" };
            renderCmd.Clear();
            renderCmd.IssuePluginEventAndData(renderEventFunc, BasisNativeMedia.RenderUpdate, handle);
            Graphics.ExecuteCommandBuffer(renderCmd);
        }

        ulong fc = BasisNativeMedia.GetFrameCounter(handle);
        if (fc != lastFrameCounter)
        {
            lastFrameCounter = fc;

            if (!UsesAccessTexturePath)
            {
                // Windows / D3D path: the plugin owns the texture; wrap it once
                // per (handle,size) change with Texture2D.CreateExternalTexture.
                IntPtr tex = BasisNativeMedia.GetTexture(handle, out int w, out int h);
                if (tex != IntPtr.Zero && (tex != lastTexturePtr || w != texW || h != texH || externalTexture == null))
                {
                    RebindTexture(tex, w, h);
                    OnVideoSizeChanged?.Invoke(texW, texH);
                }
            }

            if (!readyFired && OutputTexture != null)
            {
                readyFired = true;
                OnReady?.Invoke();
            }
        }

        // Audio-only sources never tick the video frame counter and never produce a
        // texture, so the readiness check above can't fire for them. The engine only
        // flips to Playing with no video size once the source has announced no video
        // track at all, so that pairing is the audio-only ready signal.
        if (!readyFired && engineState == BasisNativeMedia.State.Playing &&
            !(BasisNativeMedia.TryGetVideoSize(handle, out int vw, out int vh) && vw > 0 && vh > 0) &&
            BasisNativeMedia.TryGetAudioFormat(handle, out _, out int audioChannels) && audioChannels > 0)
        {
            readyFired = true;
            OnReady?.Invoke();
        }

        switch (engineState)
        {
            case BasisNativeMedia.State.Error when !errorRaised:
                errorRaised = true;
                OnError?.Invoke(new Exception(BasisNativeMedia.GetLastError(handle) ?? "basis_media_native reported an error."));
                break;
            case BasisNativeMedia.State.Ended when !eosRaised:
                eosRaised = true;
                OnEndOfStream?.Invoke();
                break;
            case BasisNativeMedia.State.Playing when (pumpCount % 30) == 0:
                // Logs which transport the connection settled on (RTSP
                // negotiates UDP vs TCP), and again if it changes (a mid-play
                // fallback restarts the session over TCP). Checked on a ~0.5 s
                // cadence rather than every pump so the marshalling cost stays
                // off the per-frame path regardless of what the native binary
                // returns; stub-platform binaries return null and never log.
                string transport = Transport;
                if (!string.IsNullOrEmpty(transport) && transport != loggedTransport)
                {
                    loggedTransport = transport;
                    BasisDebug.Log($"[NativeMedia] transport: {transport}", BasisDebug.LogTag.Video);
                }
                break;
        }

        PollCaptions();

        // Heartbeat: log on every state change and ~every 120 pumps so the Editor
        // log shows whether frames keep flowing (frames climbing), the stream
        // ended (state=Ended), or the texture froze (frames stuck but no error).
        pumpCount++;
        if (!verboseLogging) return;
        BasisMediaEngineState hb = (BasisMediaEngineState)(int)engineState;
        if (hb != lastLoggedState || (pumpCount % 120) == 0)
        {
            lastLoggedState = hb;
            string texDesc = OutputTexture != null ? $"{texW}x{texH}" : "none";
            BasisDebug.Log(
                $"[NativeMedia] state={hb} frames={fc} posUs={PositionUs} tex={texDesc} [{BasisNativeMedia.GetDebug(handle)}]",
                BasisDebug.LogTag.Video);
        }
    }

    // Polls the active caption cue and fires OnCaptionCueChanged only when the text
    // actually changes (including clearing to null). Timing authority is native — the
    // engine selects the cue active at the current presentation position.
    private void PollCaptions()
    {
        if (!captionsSupported || handle == IntPtr.Zero) return;

        int n = BasisNativeMedia.PollCaption(handle, captionScratch, out long startUs, out long endUs);
        if (n < 0) { captionsSupported = false; return; } // native lib predates captions: stop polling
        if (n > captionScratch.Length) n = captionScratch.Length;
        if (n == lastCaptionLen && BytesEqual(captionScratch, lastCaptionBytes, n)) return;

        lastCaptionLen = n;
        if (lastCaptionBytes.Length < n) lastCaptionBytes = new byte[n];
        Array.Copy(captionScratch, lastCaptionBytes, n);

        string text = n > 0 ? System.Text.Encoding.UTF8.GetString(captionScratch, 0, n) : null;
        OnCaptionCueChanged?.Invoke(new BasisCaptionCue(text, startUs, endUs));
    }

    private static bool BytesEqual(byte[] a, byte[] b, int n)
    {
        for (int i = 0; i < n; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private void RebindTexture(IntPtr nativeTex, int w, int h)
    {
        if (externalTexture != null)
        {
            // Destroys only the managed wrapper; the native texture is owned by
            // the engine and freed on the render thread at Dispose.
            UnityEngine.Object.Destroy(externalTexture);
            externalTexture = null;
        }
        texW = w;
        texH = h;
        lastTexturePtr = nativeTex;
        TextureFormat fmt = NativeTextureFormat();
        externalTexture = Texture2D.CreateExternalTexture(w, h, fmt, false, false, nativeTex);
        externalTexture.wrapMode = TextureWrapMode.Clamp;
        externalTexture.filterMode = FilterMode.Bilinear;
    }

    // Vulkan path: allocate (or reallocate on size change) the Unity-owned
    // RenderTexture the plugin will render into. Calling Create() forces the
    // VkImage to exist before GetNativeTexturePtr() — we need a valid handle
    // BEFORE basis_media_set_output_texture, so the plugin can call AccessTexture
    // on the first render frame.
    private void EnsureUnityOwnedRT(int w, int h)
    {
        if (unityOwnedRT != null && unityOwnedRT.width == w && unityOwnedRT.height == h) return;

        if (unityOwnedRT != null)
        {
            // Detach from the plugin first so the next render_update finds no
            // output and skips its work, then dispose the RT itself.
            BasisNativeMedia.SetOutputTexture(handle, IntPtr.Zero, 0, 0);
            unityOwnedRT.Release();
            UnityEngine.Object.Destroy(unityOwnedRT);
            unityOwnedRT = null;
            outputRegistered = false;
        }

        unityOwnedRT = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32)
        {
            name = "BasisNativeMedia.RT",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            useMipMap = false,
            autoGenerateMips = false,
        };
        unityOwnedRT.Create();
    }

    private static TextureFormat NativeTextureFormat()
    {
        switch (SystemInfo.graphicsDeviceType)
        {
            case GraphicsDeviceType.Direct3D11:
            case GraphicsDeviceType.Direct3D12:
                return TextureFormat.BGRA32; // MF VideoProcessor output is BGRA
            default:
                return TextureFormat.RGBA32; // GLES external blit target
        }
    }

    // ---- IBasisPcmSource (audio thread) ----

    public bool TryGetPcmFormat(out int sampleRate, out int channels)
        => BasisNativeMedia.TryGetAudioFormat(handle, out sampleRate, out channels);

    public int ReadPcm(float[] buffer)
    {
        if (handle == IntPtr.Zero || buffer == null) return 0;
        return BasisNativeMedia.ReadAudio(handle, buffer, buffer.Length);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Stop();

        // Drop the managed wrapper first so Unity stops sampling the native
        // texture, then close. We deliberately do NOT issue a deferred
        // RenderRelease plugin event: it would run at end-of-frame, after
        // basis_media_close has already freed the engine (use-after-free).
        // basis_media_close joins the decode threads and frees GPU resources
        // inside basis_decoder_destroy — D3D11/D3D12 COM Release is thread-safe,
        // and the joined threads guarantee nothing is mid-decode.
        if (externalTexture != null)
        {
            UnityEngine.Object.Destroy(externalTexture);
            externalTexture = null;
        }

        // Vulkan path: detach the plugin's output reference BEFORE Close so the
        // render thread sees a NULL handle and skips its render_update, then
        // dispose the RenderTexture itself. Close() joins the decode threads
        // afterwards, by which point nothing is poking the VkImage.
        if (handle != IntPtr.Zero && outputRegistered)
        {
            BasisNativeMedia.SetOutputTexture(handle, IntPtr.Zero, 0, 0);
            outputRegistered = false;
        }
        if (unityOwnedRT != null)
        {
            unityOwnedRT.Release();
            UnityEngine.Object.Destroy(unityOwnedRT);
            unityOwnedRT = null;
        }

        if (handle != IntPtr.Zero)
        {
            BasisNativeMedia.Close(handle);
            handle = IntPtr.Zero;
        }

        if (renderCmd != null)
        {
            renderCmd.Dispose();
            renderCmd = null;
        }
    }

    private static void ResolveRenderEventFunc()
    {
        if (renderEventResolved) return;
        renderEventResolved = true;
        renderEventFunc = BasisNativeMedia.GetRenderEventFunc();
        if (renderEventFunc == IntPtr.Zero)
        {
            BasisDebug.LogWarning(
                "basis_media_native render-event function is null; the native library may be missing. " +
                "Video texture updates will not run until it is built and present in Plugins/.",
                BasisDebug.LogTag.Video);
        }
    }
}
