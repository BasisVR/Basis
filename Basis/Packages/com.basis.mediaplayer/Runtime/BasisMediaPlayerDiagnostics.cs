using System;
using System.IO;
using System.Text;
using UnityEngine;

[RequireComponent(typeof(BasisMediaPlayer))]
public sealed class BasisMediaPlayerDiagnostics : MonoBehaviour
{
    [Header("Logging")]
    [Tooltip("If true, begin writing diagnostics CSV on Enable. Disable to gate logging behind a manual StartLogging() call.")]
    public bool AutoStart = true;

    [Tooltip("Snapshot rate in samples per second. 50 Hz (20 ms) is dense enough to see audio-callback chunking.")]
    [Min(1f)] public float SnapshotsPerSecond = 50f;

    [Tooltip("Maximum snapshot rows kept in the in-memory buffer before forcing a disk flush.")]
    [Min(64)] public int FlushEveryNSnapshots = 200;

    [Tooltip("If true, the CSV is appended to between Play sessions; if false, the file is truncated on each StartLogging() call.")]
    public bool AppendBetweenSessions = false;

    [Tooltip("Override for the output path. Sandboxed to Application.persistentDataPath; absolute paths outside that root are rejected. Leave empty to use Application.persistentDataPath/BasisMediaPlayerDiag.csv.")]
    public string LogPathOverride = "";

    public string ResolvedLogPath { get; private set; }
    public bool IsLogging { get; private set; }
    public long SnapshotsWritten { get; private set; }

    private BasisMediaPlayer player;
    private BasisMediaPlayerAudio audioComponent;
    private StreamWriter writer;
    private StringBuilder lineBuilder;
    private float nextSnapshotTime;
    private float lastSnapshotTime;
    private int rowsSinceFlush;
    private readonly NativeEngineDebug engineDebug = new NativeEngineDebug();

    private void Awake()
    {
        TryGetComponent(out player);
        TryGetComponent(out audioComponent);
        lineBuilder = new StringBuilder(512);
        ResolvedLogPath = ResolvePath();
    }

    private void OnEnable()
    {
        if (AutoStart) StartLogging();
    }

    private void OnDisable()
    {
        StopLogging();
    }

    public void StartLogging()
    {
        if (IsLogging) return;
        ResolvedLogPath = ResolvePath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ResolvedLogPath) ?? ".");
            bool fileExists = File.Exists(ResolvedLogPath);
            var fs = new FileStream(ResolvedLogPath,
                AppendBetweenSessions ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.Read);
            writer = new StreamWriter(fs, new UTF8Encoding(false));
            if (!fileExists || !AppendBetweenSessions)
            {
                writer.WriteLine(BuildHeader());
            }
            else
            {
                writer.WriteLine("# --- session " + DateTime.UtcNow.ToString("o") + " ---");
            }
            writer.Flush();
            IsLogging = true;
            SnapshotsWritten = 0;
            rowsSinceFlush = 0;
            nextSnapshotTime = Time.realtimeSinceStartup;
            lastSnapshotTime = -1f;
            BasisDebug.Log($"BasisMediaPlayerDiagnostics: logging to {ResolvedLogPath}", BasisDebug.LogTag.Video);
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"BasisMediaPlayerDiagnostics: failed to open log: {ex.Message}", BasisDebug.LogTag.Video);
            writer = null;
            IsLogging = false;
        }
    }

    public void StopLogging()
    {
        if (!IsLogging) return;
        IsLogging = false;
        try
        {
            writer?.Flush();
            writer?.Dispose();
        }
        catch { }
        writer = null;
    }

    public void Flush()
    {
        try { writer?.Flush(); rowsSinceFlush = 0; } catch { }
    }

    private void Update()
    {
        if (!IsLogging || writer == null) return;
        float now = Time.realtimeSinceStartup;
        if (now < nextSnapshotTime) return;
        float interval = 1f / Mathf.Max(1f, SnapshotsPerSecond);
        nextSnapshotTime = now + interval;

        WriteSnapshotRow(now);

        rowsSinceFlush++;
        if (rowsSinceFlush >= FlushEveryNSnapshots)
        {
            try { writer.Flush(); rowsSinceFlush = 0; } catch { }
        }
    }

    private string ResolvePath()
    {
        if (string.IsNullOrEmpty(LogPathOverride))
            return Path.Combine(Application.persistentDataPath, "BasisMediaPlayerDiag.csv");
        if (!BasisMediaPlayerSecurity.TrySandboxLogPath(LogPathOverride, out string sandboxed, out string reason))
        {
            BasisDebug.LogWarning($"BasisMediaPlayerDiagnostics: LogPathOverride rejected ({reason}); falling back to default.", BasisDebug.LogTag.Video);
            return Path.Combine(Application.persistentDataPath, "BasisMediaPlayerDiag.csv");
        }
        return sandboxed;
    }

    private string BuildHeader()
    {
        return string.Join(",",
            "unity_time_s",
            "is_playing",
            "is_paused",
            "prepared",
            "backend",
            "clock_now_us",
            "video_w",
            "video_h",
            "engine_state",
            "engine_pos_us",
            "engine_has_texture",
            "cpu_queue_depth",
            "cpu_presented",
            "cpu_overflow_drops",
            "cpu_catchup_skips",
            "cpu_late_skips",
            "cpu_format_errors",
            "audio_present",
            "audio_source_playing",
            "audio_consumed_samples",
            "audio_queue_depth",
            "audio_dropped",
            "audio_mute",
            "audio_gain",
            "audio_volume",
            "audio_spatial",
            "pcm_peak",
            "pcm_rms",
            "listener_paused",
            "wall_dt_ms",
            "eng_decoded_frames",
            "eng_blit_count",
            "eng_copy_count",
            "eng_render_count",
            "eng_nodue_count",
            "eng_acq_fails",
            "eng_lag_ms",
            "eng_buf_ms",
            "eng_buf_mode",
            "eng_audio_queue_ms",
            "eng_audio_trims",
            "eng_video_queue",
            "eng_audio_latency_ms",
            "eng_ttff_ms",
            "eng_audio_cfg",
            "eng_aout_count",
            "eng_audio_sr",
            "eng_audio_ch",
            "eng_bitrate_idx",
            "eng_audio_track_idx",
            "audio_expected_sr",
            "audio_expected_ch",
            "audio_latency_us",
            "audio_has_media_time",
            "audio_media_time_us"
        );
    }

    private void WriteSnapshotRow(float unityTime)
    {
        lineBuilder.Length = 0;

        var clock = player.Clock;
        var eng = player.NativeEngine;
        var src = player.Source;
        var snap = ResolveAudio();

        float wallDtMs = lastSnapshotTime >= 0f ? (unityTime - lastSnapshotTime) * 1000f : 0f;
        lastSnapshotTime = unityTime;

        engineDebug.Reset();
        if (eng != null)
        {
            try { engineDebug.ParseFrom(eng.DebugInfo); }
            catch (Exception ex) { BasisDebug.LogWarning($"BasisMediaPlayerDiagnostics: debug parse failed: {ex.Message}", BasisDebug.LogTag.Video); }
        }

        AppendF(unityTime);
        AppendB(player.IsPlaying);
        AppendB(player.IsPaused);
        AppendB(player.IsPrepared);
        AppendStr(eng != null ? "native" : (src != null ? src.GetType().Name : "none"));
        AppendL(clock.CurrentMediaTimeUs);
        AppendI(player.VideoSize.x);
        AppendI(player.VideoSize.y);
        AppendStr(eng != null ? eng.State.ToString() : "");
        AppendL(eng != null ? eng.PositionUs : 0);
        AppendB(eng != null && eng.OutputTexture != null);
        AppendI(player.QueuedFrameCount);
        AppendL(player.PresentedFrameCount);
        AppendL(player.OverflowDropCount);
        AppendL(player.CatchUpSkipCount);
        AppendL(player.LateSkipCount);
        AppendL(player.FormatErrorCount);
        AppendB(snap.Present);
        AppendB(snap.Playing);
        AppendL(snap.Consumed);
        AppendI(snap.Queue);
        AppendL(snap.Dropped);
        AppendB(snap.Mute);
        AppendF(snap.Gain);
        AppendF(snap.Volume);
        AppendF(snap.Spatial);
        AppendF(snap.Peak);
        AppendF(snap.Rms);
        AppendB(AudioListener.pause);

        AppendF(wallDtMs);
        AppendL(eng != null ? (long)eng.DecodedFrameCount : 0);
        AppendL(engineDebug.Blit);
        AppendL(engineDebug.Copy);
        AppendL(engineDebug.Render);
        AppendL(engineDebug.NoDue);
        AppendL(engineDebug.AcqFails);
        AppendL(engineDebug.LagMs);
        AppendL(engineDebug.BufMs);
        AppendI(engineDebug.BufMode);
        AppendL(engineDebug.AudioQMs);
        AppendL(engineDebug.Trims);
        AppendL(engineDebug.VideoQ);
        AppendL(engineDebug.AudioLatencyMs);
        AppendL(engineDebug.TtffMs);
        AppendI(engineDebug.AudioCfg);
        AppendL(engineDebug.AOutCount);
        AppendI(engineDebug.AudioSr);
        AppendI(eng != null && eng.TryGetPcmFormat(out _, out int engCh) ? engCh : -1);
        AppendI(eng != null ? eng.SelectedBitrateIndex : -1);
        AppendI(eng != null ? eng.SelectedAudioTrackIndex : -1);

        AppendI(snap.SampleRate);
        AppendI(snap.Channels);
        AppendL(snap.LatencyUs);
        AppendB(snap.HasMedia);
        AppendL(snap.MediaUs, last: true);

        try
        {
            writer.WriteLine(lineBuilder.ToString());
            SnapshotsWritten++;
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"BasisMediaPlayerDiagnostics: write failed: {ex.Message}", BasisDebug.LogTag.Video);
            IsLogging = false;
        }
    }

    private void AppendF(float v, bool last = false) { lineBuilder.Append(v.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)); if (!last) lineBuilder.Append(','); }
    private void AppendI(int v, bool last = false) { lineBuilder.Append(v); if (!last) lineBuilder.Append(','); }
    private void AppendL(long v, bool last = false) { lineBuilder.Append(v); if (!last) lineBuilder.Append(','); }
    private void AppendB(bool v, bool last = false) { lineBuilder.Append(v ? "1" : "0"); if (!last) lineBuilder.Append(','); }
    private void AppendStr(string v, bool last = false) { if (v != null) lineBuilder.Append(v); if (!last) lineBuilder.Append(','); }

    private struct AudioSnapshot
    {
        public bool Present, Playing, Mute, HasMedia;
        public long Consumed, Dropped, LatencyUs, MediaUs;
        public int Queue, SampleRate, Channels;
        public float Gain, Volume, Spatial, Peak, Rms;
    }

    // Populates the audio columns from the BasisMediaPlayerAudio sink.
    // Queue/Dropped/LatencyUs/HasMedia/MediaUs stay defaulted: the sink reads the
    // native ring directly (no frame queue) and the engine owns the media clock.
    private AudioSnapshot ResolveAudio()
    {
        var s = new AudioSnapshot();
        var aud = audioComponent != null ? audioComponent : (player != null ? player.AudioComponent : null);
        if (aud != null)
        {
            s.Present = true;
            s.Playing = aud.IsAnyOutputPlaying;
            s.Consumed = aud.ConsumedSampleCount;
            s.Mute = aud.Mute;
            s.Gain = aud.VolumeGain;
            s.Volume = aud.RepresentativeVolume;
            s.Spatial = aud.RepresentativeSpatialBlend;
            s.Peak = aud.LastPcmPeak;
            s.Rms = aud.LastPcmRms;
            s.SampleRate = aud.SampleRate;
            s.Channels = aud.ChannelCount;
        }
        return s;
    }

    private sealed class NativeEngineDebug
    {
        public long Blit, Copy, Render, NoDue, AcqFails;
        public long LagMs, BufMs, TtffMs, AOutCount;
        public int BufMode, AudioCfg, AudioSr;
        // Audio-pacing counters (native backends that gate audio release against
        // the present clock): audio currently queued in ms, clock-gated trims
        // fired, and video frames held in the present ring.
        public long AudioQMs, Trims, VideoQ, AudioLatencyMs;

        public void Reset()
        {
            Blit = Copy = Render = NoDue = AcqFails = 0;
            LagMs = BufMs = TtffMs = AOutCount = 0;
            BufMode = AudioCfg = AudioSr = 0;
            AudioQMs = Trims = VideoQ = AudioLatencyMs = 0;
        }

        public void ParseFrom(string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            int i = 0;
            int n = s.Length;
            while (i < n)
            {
                while (i < n && (s[i] == ' ' || s[i] == '\t' || s[i] == '|' || s[i] == ',')) i++;
                if (i >= n) break;
                int keyStart = i;
                while (i < n && s[i] != '=' && s[i] != ' ' && s[i] != '\t' && s[i] != '|' && s[i] != ',') i++;
                if (i >= n || s[i] != '=') continue;
                int keyLen = i - keyStart;
                i++;
                int valStart = i;
                while (i < n && s[i] != ' ' && s[i] != '\t' && s[i] != '|' && s[i] != ',') i++;
                int valLen = i - valStart;
                if (keyLen <= 0 || valLen <= 0) continue;
                Assign(s, keyStart, keyLen, valStart, valLen);
            }
        }

        private void Assign(string s, int kStart, int kLen, int vStart, int vLen)
        {
            if (vLen >= 2 && s[vStart + vLen - 2] == 'm' && s[vStart + vLen - 1] == 's') vLen -= 2;
            if (vLen <= 0) return;
            long v = 0;
            int sign = 1;
            int vi = vStart;
            int vEnd = vStart + vLen;
            if (s[vi] == '-') { sign = -1; vi++; }
            else if (s[vi] == '+') { vi++; }
            for (; vi < vEnd; vi++)
            {
                char c = s[vi];
                if (c < '0' || c > '9') return;
                v = v * 10 + (c - '0');
            }
            v *= sign;

            switch (kLen)
            {
                case 2:
                    if (s[kStart] == 'a' && s[kStart + 1] == 'q') { AudioQMs = v; return; }
                    if (s[kStart] == 'v' && s[kStart + 1] == 'q') { VideoQ = v; return; }
                    return;
                case 3:
                    if (s[kStart] == 'a' && s[kStart + 1] == 'c' && s[kStart + 2] == 'q') { AcqFails = v; return; }
                    if (s[kStart] == 'a' && s[kStart + 1] == 's' && s[kStart + 2] == 'r') { AudioSr = (int)v; return; }
                    if (s[kStart] == 'b' && s[kStart + 1] == 'u' && s[kStart + 2] == 'f') { BufMs = v; return; }
                    if (s[kStart] == 'l' && s[kStart + 1] == 'a' && s[kStart + 2] == 'g') { LagMs = v; return; }
                    return;
                case 4:
                    if (s[kStart] == 'b' && s[kStart + 1] == 'l' && s[kStart + 2] == 'i' && s[kStart + 3] == 't') { Blit = v; return; }
                    if (s[kStart] == 'c' && s[kStart + 1] == 'o' && s[kStart + 2] == 'p' && s[kStart + 3] == 'y') { Copy = v; return; }
                    if (s[kStart] == 'm' && s[kStart + 1] == 'o' && s[kStart + 2] == 'd' && s[kStart + 3] == 'e') { BufMode = (int)v; return; }
                    if (s[kStart] == 'a' && s[kStart + 1] == 'c' && s[kStart + 2] == 'f' && s[kStart + 3] == 'g') { AudioCfg = (int)v; return; }
                    if (s[kStart] == 'a' && s[kStart + 1] == 'o' && s[kStart + 2] == 'u' && s[kStart + 3] == 't') { AOutCount = v; return; }
                    if (s[kStart] == 'a' && s[kStart + 1] == 'l' && s[kStart + 3] == 't') { AudioLatencyMs = v; return; } // alat
                    if (s[kStart] == 't' && s[kStart + 1] == 't' && s[kStart + 2] == 'f' && s[kStart + 3] == 'f') { TtffMs = v; return; }
                    return;
                case 5:
                    if (s[kStart] == 'n' && s[kStart + 4] == 'e') { NoDue = v; return; }
                    if (s[kStart] == 'a' && s[kStart + 4] == 'm') { Trims = v; return; } // atrim
                    return;
                case 6:
                    if (s[kStart] == 'r' && s[kStart + 5] == 'r') { Render = v; return; }
                    return;
            }
        }
    }
}
