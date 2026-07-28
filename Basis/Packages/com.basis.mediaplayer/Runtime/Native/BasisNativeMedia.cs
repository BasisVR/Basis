using System;
using System.Runtime.InteropServices;

// P/Invoke surface for basis_media_native — the OS-codec live media engine.
// See Native~/basis_media_native.h for the C ABI and threading contract.
//
// The native library is loaded lazily; if it is missing (not yet built for the
// current platform), Open() throws a descriptive DllNotFoundException-wrapped
// exception instead of crashing the editor.
//
// Calling convention is __stdcall on Windows (BASIS_CALL); Mono/IL2CPP treats
// CallingConvention.StdCall as the platform default elsewhere, which is correct
// for Android's single ARM convention.
internal static class BasisNativeMedia
{
    private const string Lib = "basis_media_native";

    public enum State
    {
        Idle = 0,
        Connecting = 1,
        Buffering = 2,
        Playing = 3,
        Paused = 4,
        Ended = 5,
        Error = 6,
    }

    // Must match basis_render_op_t in basis_media_native.h.
    public const int RenderUpdate = 1;
    public const int RenderRelease = 2;

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private static extern IntPtr basis_media_open([MarshalAs(UnmanagedType.LPStr)] string url);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private static extern IntPtr basis_media_open_dual(
        [MarshalAs(UnmanagedType.LPStr)] string videoUrl,
        [MarshalAs(UnmanagedType.LPStr)] string audioUrl,
        int deliveryHint);

    // Wide string: the path can contain non-ANSI characters (a project under a
    // localized directory), which LPStr/LoadLibraryA would mangle.
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private static extern void basis_decoder_set_opus_library_path([MarshalAs(UnmanagedType.LPWStr)] string path);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    private static extern void basis_media_close(IntPtr engine);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    private static extern void basis_media_play(IntPtr engine);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    private static extern void basis_media_pause(IntPtr engine);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    private static extern void basis_media_stop(IntPtr engine);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    private static extern int basis_media_get_state(IntPtr engine);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    private static extern int basis_media_get_video_size(IntPtr engine, out int w, out int h);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    private static extern int basis_media_get_frame_origin(IntPtr engine);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    private static extern long basis_media_get_position_us(IntPtr engine);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    private static extern long basis_media_get_duration_us(IntPtr engine);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    private static extern int basis_media_seek_us(IntPtr engine, long targetUs);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    private static extern int basis_media_get_last_error(IntPtr engine, byte[] buf, int bufSize);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    private static extern int basis_media_get_debug(IntPtr engine, byte[] buf, int bufSize);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    private static extern int basis_media_get_transport(IntPtr engine, byte[] buf, int bufSize);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    private static extern void basis_media_set_buffer(IntPtr engine, int mode, int bufferMs);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    private static extern void basis_media_set_audio_latency(IntPtr engine, int latencyUs);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    private static extern void basis_media_set_output_texture(IntPtr engine, IntPtr nativeTexture, int w, int h);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr basis_media_get_texture(IntPtr engine, out int w, out int h);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    private static extern ulong basis_media_get_frame_counter(IntPtr engine);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    private static extern int basis_media_get_audio_format(IntPtr engine, out int sampleRate, out int channels);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    private static extern int basis_media_read_audio(IntPtr engine, float[] outBuffer, int maxFloats);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr basis_media_get_render_event_func();

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    private static extern int basis_media_get_bitrate_track_count(IntPtr engine);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    private static extern int basis_media_get_bitrate_track(IntPtr engine, int index, out int bps, out int width, out int height, byte[] codecBuf, int codecBufSize, byte[] labelBuf, int labelBufSize);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    private static extern int basis_media_select_bitrate(IntPtr engine, int index);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    private static extern int basis_media_get_selected_bitrate(IntPtr engine);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    private static extern int basis_media_get_audio_track_count(IntPtr engine);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    private static extern int basis_media_get_audio_track(IntPtr engine, int index, out int channels, out int sampleRate, out int bps, out int dualMono, byte[] langBuf, int langBufSize, byte[] codecBuf, int codecBufSize, byte[] labelBuf, int labelBufSize);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    private static extern int basis_media_select_audio_track(IntPtr engine, int index);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    private static extern int basis_media_get_selected_audio_track(IntPtr engine);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    private static extern int basis_media_seek_back_us(IntPtr engine, long backUs);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    private static extern int basis_media_poll_caption(IntPtr engine, byte[] buf, int bufSize, out long startUs, out long endUs);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    private static extern int basis_media_probe_video_codec(int codec);

    // ---- Managed wrappers (translate the flat ABI into friendlier types) ----

    private static bool _opusLibrarySet;
    private static readonly object OpusLibraryLock = new object();

    // Opus decode (WebM A_OPUS) runtime-loads the libopus that
    // com.avionblock.opussharp ships. In the Windows Editor that DLL lives under
    // Packages/…; a standalone build flattens it next to this plugin, where the
    // native side finds it by name, so only the Editor needs the explicit path.
    // Resolved once before the first open; a no-op if opussharp is absent. Locked
    // and the flag set only after the setter runs, so a concurrent Open can't race
    // past configuration and strand the native loader on its by-name fallback.
    private static void EnsureOpusLibrary()
    {
        lock (OpusLibraryLock)
        {
            if (_opusLibrarySet) return;
#if UNITY_EDITOR_WIN
            try
            {
                string rid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                    ? "win-arm64" : "win-x64";
                string path = System.IO.Path.GetFullPath(
                    $"Packages/com.avionblock.opussharp/Plugins/{rid}/native/opus.dll");
                if (System.IO.File.Exists(path))
                    basis_decoder_set_opus_library_path(path);
            }
            catch { /* opussharp absent / path unresolved: native falls back to opus.dll */ }
#endif
            _opusLibrarySet = true;
        }
    }

    public static IntPtr Open(string url)
    {
        EnsureOpusLibrary();
        try
        {
            return basis_media_open(url);
        }
        catch (DllNotFoundException ex)
        {
            throw new InvalidOperationException(
                "basis_media_native shared library not found. Build it from " +
                "com.basis.mediaplayer/Native~/ (CMake on desktop, NDK on Android) and place the " +
                "result under com.basis.mediaplayer/Plugins/<platform>/.", ex);
        }
    }

    // Opens a video stream plus an optional separate audio-only stream, synced by
    // the engine onto one clock. delivery selects the live-vs-on-demand clock; Auto
    // lets the engine detect it at open. A single muxed stream with Auto delivery is
    // exactly Open(videoUrl) (which opens with the Auto hint); everything else routes
    // through basis_media_open_dual, so the load path can always call here. A native
    // lib that predates split-stream has no basis_media_open_dual export; surface that
    // as an actionable rebuild message rather than a hard crash.
    public static IntPtr OpenWithAudio(string videoUrl, string audioUrl, BasisMediaDelivery delivery)
    {
        if (string.IsNullOrEmpty(audioUrl) && delivery == BasisMediaDelivery.Auto) return Open(videoUrl);
        EnsureOpusLibrary();
        try
        {
            return basis_media_open_dual(videoUrl, audioUrl, (int)delivery);
        }
        catch (DllNotFoundException ex)
        {
            throw new InvalidOperationException(
                "basis_media_native shared library not found. Build it from " +
                "com.basis.mediaplayer/Native~/ and place the result under " +
                "com.basis.mediaplayer/Plugins/<platform>/.", ex);
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new InvalidOperationException(
                "basis_media_native is present but predates split-stream playback " +
                "(no basis_media_open_dual). Rebuild it from com.basis.mediaplayer/Native~/.", ex);
        }
    }

    public static void Close(IntPtr e) { if (e != IntPtr.Zero) basis_media_close(e); }
    public static void Play(IntPtr e) { if (e != IntPtr.Zero) basis_media_play(e); }
    public static void Pause(IntPtr e) { if (e != IntPtr.Zero) basis_media_pause(e); }
    public static void Stop(IntPtr e) { if (e != IntPtr.Zero) basis_media_stop(e); }

    public static State GetState(IntPtr e) => e == IntPtr.Zero ? State.Idle : (State)basis_media_get_state(e);

    public static bool TryGetVideoSize(IntPtr e, out int w, out int h)
    {
        w = 0; h = 0;
        return e != IntPtr.Zero && basis_media_get_video_size(e, out w, out h) == 0;
    }

    // 0 = bottom-left origin (frame upright; sample as-is), 1 = top-left origin
    // (frame upside-down; the consumer must flip vertically). Native libraries that
    // predate the export default to 0 (upright) — identical to the prior behavior.
    public static int GetFrameOrigin(IntPtr e)
    {
        if (e == IntPtr.Zero) return 0;
        try { return basis_media_get_frame_origin(e); }
        catch (EntryPointNotFoundException) { return 0; }
    }

    public static long GetPositionUs(IntPtr e) => e == IntPtr.Zero ? -1 : basis_media_get_position_us(e);

    // 0 = unknown/live (also the "no seekable timeline" signal). Old native
    // libraries without the export read as 0 — same as a live source.
    public static long GetDurationUs(IntPtr e)
    {
        if (e == IntPtr.Zero) return 0;
        try { return basis_media_get_duration_us(e); }
        catch (EntryPointNotFoundException) { return 0; }
    }

    // Asynchronous: the demuxer repositions at its next sample boundary and
    // playback resumes from the preceding keyframe / segment boundary.
    public static bool SeekUs(IntPtr e, long targetUs)
    {
        if (e == IntPtr.Zero) return false;
        try { return basis_media_seek_us(e, targetUs) == 0; }
        catch (EntryPointNotFoundException) { return false; }
    }

    // 1 when this platform decodes the codec, verified as far as the platform
    // allows: Windows checks the decoder MFT AND the GPU's decode profile;
    // Android checks decoder presence (hardware-universal for these codecs on
    // the Quest target). Engine-less and thread-safe; the native side caches
    // the verdict per process. Binaries that predate the export answer as they
    // behaved before the probe existed: H.264/H.265 decode, VP9/AV1 don't.
    public static bool ProbeVideoCodec(int codec)
    {
        try { return basis_media_probe_video_codec(codec) != 0; }
        catch (EntryPointNotFoundException) { return codec == 1 || codec == 2; }
        // Missing plugin: answer false rather than aborting the caller (a
        // resolver probes before playback; Open() is where the descriptive
        // build-instructions error surfaces).
        catch (DllNotFoundException) { return false; }
    }

    public static string GetLastError(IntPtr e)
    {
        if (e == IntPtr.Zero) return null;
        var buf = new byte[512];
        int n = basis_media_get_last_error(e, buf, buf.Length);
        if (n <= 0) return null;
        return System.Text.Encoding.UTF8.GetString(buf, 0, Math.Min(n, buf.Length));
    }

    public static void SetBuffer(IntPtr e, int mode, int bufferMs)
    {
        if (e != IntPtr.Zero) basis_media_set_buffer(e, mode, bufferMs);
    }

    // Reports the managed audio sink's measured output latency (µs) so the backend
    // paces video to match. Old libraries without the export (the desktop DLL,
    // which self-times) no-op — they keep their own A/V timing.
    public static void SetAudioLatencyUs(IntPtr e, int latencyUs)
    {
        if (e == IntPtr.Zero) return;
        try { basis_media_set_audio_latency(e, latencyUs); }
        catch (EntryPointNotFoundException) { }
    }

    // Register a Unity-allocated RenderTexture as the engine's render target.
    // Used on the Android/Vulkan path to dodge the Mali libGLES_mali.so crash in
    // vkCreateImageView triggered by Texture2D.CreateExternalTexture wrapping a
    // plugin-owned VkImage. On Windows this is accepted but ignored (the D3D
    // CreateExternalTexture path doesn't hit the same driver bug). Older .so
    // builds without the symbol get silently skipped — older bindings stay
    // functional through the existing GetTexture path.
    public static void SetOutputTexture(IntPtr e, IntPtr nativeTexture, int w, int h)
    {
        if (e == IntPtr.Zero) return;
        try { basis_media_set_output_texture(e, nativeTexture, w, h); }
        catch (EntryPointNotFoundException) { /* old .so: caller falls back to GetTexture */ }
    }

    public static string GetDebug(IntPtr e)
    {
        if (e == IntPtr.Zero) return null;
        var buf = new byte[256];
        int n = basis_media_get_debug(e, buf, buf.Length);
        if (n <= 0) return null;
        return System.Text.Encoding.UTF8.GetString(buf, 0, Math.Min(n, buf.Length));
    }

    public static string GetTransport(IntPtr e)
    {
        if (e == IntPtr.Zero) return null;
        var buf = new byte[64];
        try
        {
            int n = basis_media_get_transport(e, buf, buf.Length);
            if (n <= 0) return null;
            return System.Text.Encoding.UTF8.GetString(buf, 0, Math.Min(n, buf.Length));
        }
        catch (EntryPointNotFoundException) { return null; } // binary predates the transport API (stub platforms)
    }

    public static IntPtr GetTexture(IntPtr e, out int w, out int h)
    {
        w = 0; h = 0;
        return e == IntPtr.Zero ? IntPtr.Zero : basis_media_get_texture(e, out w, out h);
    }

    public static ulong GetFrameCounter(IntPtr e) => e == IntPtr.Zero ? 0UL : basis_media_get_frame_counter(e);

    public static bool TryGetAudioFormat(IntPtr e, out int sampleRate, out int channels)
    {
        sampleRate = 0; channels = 0;
        return e != IntPtr.Zero && basis_media_get_audio_format(e, out sampleRate, out channels) == 0;
    }

    public static int ReadAudio(IntPtr e, float[] outBuffer, int maxFloats)
        => e == IntPtr.Zero ? 0 : basis_media_read_audio(e, outBuffer, maxFloats);

    public static IntPtr GetRenderEventFunc()
    {
        try { return basis_media_get_render_event_func(); }
        catch (DllNotFoundException) { return IntPtr.Zero; }
    }

    public static int GetBitrateTrackCount(IntPtr e)
    {
        if (e == IntPtr.Zero) return 0;
        try { return basis_media_get_bitrate_track_count(e); }
        catch (EntryPointNotFoundException) { return 0; }
    }

    public static bool TryGetBitrateTrack(IntPtr e, int index, out int bps, out int w, out int h, out string codec, out string label)
    {
        bps = 0; w = 0; h = 0; codec = null; label = null;
        if (e == IntPtr.Zero) return false;
        var codecBuf = new byte[64];
        var labelBuf = new byte[128];
        try
        {
            int r = basis_media_get_bitrate_track(e, index, out bps, out w, out h, codecBuf, codecBuf.Length, labelBuf, labelBuf.Length);
            if (r != 0) return false;
            codec = ReadCString(codecBuf);
            label = ReadCString(labelBuf);
            return true;
        }
        catch (EntryPointNotFoundException) { return false; }
    }

    public static bool SelectBitrate(IntPtr e, int index)
    {
        if (e == IntPtr.Zero) return false;
        try { return basis_media_select_bitrate(e, index) == 0; }
        catch (EntryPointNotFoundException) { return false; }
    }

    public static int GetSelectedBitrate(IntPtr e)
    {
        if (e == IntPtr.Zero) return -1;
        try { return basis_media_get_selected_bitrate(e); }
        catch (EntryPointNotFoundException) { return -1; }
    }

    public static int GetAudioTrackCount(IntPtr e)
    {
        if (e == IntPtr.Zero) return 0;
        try { return basis_media_get_audio_track_count(e); }
        catch (EntryPointNotFoundException) { return 0; }
    }

    public static bool TryGetAudioTrack(IntPtr e, int index, out int channels, out int sampleRate, out int bps, out bool dualMono, out string lang, out string codec, out string label)
    {
        channels = 0; sampleRate = 0; bps = 0; dualMono = false; lang = null; codec = null; label = null;
        if (e == IntPtr.Zero) return false;
        var langBuf = new byte[16];
        var codecBuf = new byte[64];
        var labelBuf = new byte[128];
        try
        {
            int dual;
            int r = basis_media_get_audio_track(e, index, out channels, out sampleRate, out bps, out dual, langBuf, langBuf.Length, codecBuf, codecBuf.Length, labelBuf, labelBuf.Length);
            if (r != 0) return false;
            dualMono = dual != 0;
            lang = ReadCString(langBuf);
            codec = ReadCString(codecBuf);
            label = ReadCString(labelBuf);
            return true;
        }
        catch (EntryPointNotFoundException) { return false; }
    }

    public static bool SelectAudioTrack(IntPtr e, int index)
    {
        if (e == IntPtr.Zero) return false;
        try { return basis_media_select_audio_track(e, index) == 0; }
        catch (EntryPointNotFoundException) { return false; }
    }

    public static int GetSelectedAudioTrack(IntPtr e)
    {
        if (e == IntPtr.Zero) return -1;
        try { return basis_media_get_selected_audio_track(e); }
        catch (EntryPointNotFoundException) { return -1; }
    }

    public static bool SeekBackUs(IntPtr e, long backUs)
    {
        if (e == IntPtr.Zero) return false;
        try { return basis_media_seek_back_us(e, backUs) == 0; }
        catch (EntryPointNotFoundException) { return false; }
    }

    // Polls the in-band caption cue active at the current presentation position into
    // a caller-owned buffer (avoids per-frame allocation on the poll path). Returns
    // the number of UTF-8 bytes written (0 = no active cue), or -1 when the engine is
    // gone or the native lib predates captions (older build without the export).
    public static int PollCaption(IntPtr e, byte[] buf, out long startUs, out long endUs)
    {
        startUs = 0; endUs = 0;
        if (e == IntPtr.Zero || buf == null || buf.Length == 0) return -1;
        try { return basis_media_poll_caption(e, buf, buf.Length, out startUs, out endUs); }
        catch (EntryPointNotFoundException) { return -1; }
    }

    private static string ReadCString(byte[] buf)
    {
        int n = 0;
        while (n < buf.Length && buf[n] != 0) n++;
        return n == 0 ? null : System.Text.Encoding.UTF8.GetString(buf, 0, n);
    }
}
