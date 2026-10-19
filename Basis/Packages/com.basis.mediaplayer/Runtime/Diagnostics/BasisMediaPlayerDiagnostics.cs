using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Records what the engine cannot see: one row per Unity frame, on the
/// main thread.
///
/// The engine writes its own capture CSV (the player's
/// <see cref="BasisMediaPlayer.engineCapture"/>), sampled on its own
/// thread, and that is the authority on everything inside the pipeline:
/// bank depth, stage counters, decode and release. What it cannot observe
/// is the other side of the boundary: how often Unity actually rendered,
/// how long each presented frame was held on screen, whether the audio
/// pull kept up with the stream rate, and what the device's DSP chain was
/// doing. Judder and A/V drift show up in that layer, which this file
/// captures.
///
/// The two files use different clocks (the engine's stops while paused),
/// so line them up by event, not by timestamp.
/// </summary>
[RequireComponent(typeof(BasisMediaPlayer))]
public sealed class BasisMediaPlayerDiagnostics : MonoBehaviour, IBasisMediaTickConsumer
{
    [Header("Logging")]
    [Tooltip("Begin writing on enable. Turn off to gate logging behind a manual StartLogging() call.")]
    public bool AutoStart = true;

    [Tooltip("Rows are built once per Unity frame and handed to a writer thread. This is how many queue up before the thread is woken to write and flush them; the frame never touches the file.")]
    [Min(16)] public int FlushEveryNRows = 200;

    [Tooltip("Append to the file across sessions instead of truncating it at every StartLogging().")]
    public bool AppendBetweenSessions;

    [Tooltip("Output path. Sandboxed to Application.persistentDataPath; anything resolving outside that root is refused. Empty uses persistentDataPath/BasisMediaFrames.csv.")]
    public string LogPathOverride = "";

    public string ResolvedLogPath { get; private set; }
    public bool IsLogging { get; private set; }
    public long RowsWritten { get; private set; }

    /// <summary>Why logging is not running, when it isn't. Empty while
    /// healthy.</summary>
    public string LastError { get; private set; } = "";

    BasisMediaPlayer _player;
    BasisMediaPlayerAudio _audio;
    StreamWriter _writer;
    StringBuilder _row;
    int _rowsSinceFlush;

    // Rows are built on the main thread (they read live Unity state) but written and
    // flushed on this thread, so the frame never pays for a FileStream syscall.
    readonly System.Collections.Concurrent.ConcurrentQueue<string> _pendingRows = new System.Collections.Concurrent.ConcurrentQueue<string>();
    System.Threading.Thread _writeThread;
    System.Threading.AutoResetEvent _writeSignal;
    volatile bool _writeRunning;

    // Previous-frame values, so each row can carry deltas rather than
    // leaving the reader to difference the totals.
    ulong _lastPresented;
    ulong _lastDecoded;
    long _lastAudioPulled;
    long _lastOutputConsumed;
    int _framesSincePresent;

    void Awake()
    {
        TryGetComponent(out _player);
        TryGetComponent(out _audio);
        _row = new StringBuilder(256);
        ResolvedLogPath = ResolvePath(out _);
    }

    void OnEnable()
    {
        // Last in the player's tick, so a row carries the counters this frame
        // produced. Left to its own Update it could just as well be reading
        // the previous frame's, which smears every hold interval by one.
        if (_player != null) _player.AddTickConsumer(this);
        if (AutoStart) StartLogging();
    }

    void OnDisable()
    {
        if (_player != null) _player.RemoveTickConsumer(this);
        StopLogging();
    }

    public void StartLogging()
    {
        if (IsLogging) return;
        ResolvedLogPath = ResolvePath(out string refusal);
        if (refusal != null)
        {
            LastError = refusal;
            BasisDebug.LogError($"[BasisMedia] diagnostics: {refusal}", BasisDebug.LogTag.Video);
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ResolvedLogPath) ?? ".");
            bool exists = File.Exists(ResolvedLogPath);
            string header = Header();
            // A file from an older build carries different columns, so
            // appending to it would produce a CSV nothing can parse.
            bool schemaChanged = exists && AppendBetweenSessions && !HeaderMatches(header);
            var stream = new FileStream(
                ResolvedLogPath,
                AppendBetweenSessions && !schemaChanged ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.Read);
            _writer = new StreamWriter(stream, new UTF8Encoding(false));
            if (!exists || !AppendBetweenSessions || schemaChanged)
                _writer.WriteLine(header);
        }
        catch (Exception e)
        {
            LastError = e.Message;
            BasisDebug.LogError($"[BasisMedia] diagnostics could not open '{ResolvedLogPath}': {e.Message}", BasisDebug.LogTag.Video);
            _writer = null;
            return;
        }

        LastError = "";
        RowsWritten = 0;
        _rowsSinceFlush = 0;
        _writeSignal = new System.Threading.AutoResetEvent(false);
        _writeRunning = true;
        _writeThread = new System.Threading.Thread(WriteLoop) { IsBackground = true, Name = "BasisMediaPlayerDiag" };
        _writeThread.Start();
        _lastPresented = 0;
        _lastDecoded = 0;
        _lastAudioPulled = 0;
        _lastOutputConsumed = 0;
        _framesSincePresent = 0;
        IsLogging = true;
    }

    public void StopLogging()
    {
        if (!IsLogging && _writeThread == null) return;
        IsLogging = false;
        // Stop the writer, then drain whatever it had not reached: the last rows of
        // a session are the ones a reader wants most.
        _writeRunning = false;
        _writeSignal?.Set();
        try { _writeThread?.Join(1000); } catch { }
        _writeThread = null;
        try
        {
            while (_pendingRows.TryDequeue(out string row))
            {
                _writer?.WriteLine(row);
                RowsWritten++;
            }
            _writer?.Flush();
        }
        catch (Exception e) { BasisDebug.LogWarning($"[BasisMedia] diagnostics flush failed: {e.Message}", BasisDebug.LogTag.Video); }
        try { _writer?.Dispose(); }
        catch (Exception e) { BasisDebug.LogWarning($"[BasisMedia] diagnostics close failed: {e.Message}", BasisDebug.LogTag.Video); }
        _writer = null;
        _writeSignal?.Dispose();
        _writeSignal = null;
        _rowsSinceFlush = 0;
    }

    /// <summary>Wake the writer thread so queued rows reach the file now.</summary>
    public void Flush()
    {
        _rowsSinceFlush = 0;
        _writeSignal?.Set();
    }

    // Drains queued rows and flushes each time it is signalled, so the file stays
    // current without the main thread ever touching the stream.
    void WriteLoop()
    {
        while (_writeRunning)
        {
            _writeSignal.WaitOne();
            try
            {
                bool wroteAny = false;
                while (_pendingRows.TryDequeue(out string row))
                {
                    _writer.WriteLine(row);
                    RowsWritten++;
                    wroteAny = true;
                }
                if (wroteAny) _writer.Flush();
            }
            catch (Exception e)
            {
                LastError = e.Message;
                BasisDebug.LogError($"[BasisMedia] diagnostics write failed: {e.Message}", BasisDebug.LogTag.Video);
                IsLogging = false;
                return;
            }
        }
    }

    // Column contract. Additions go on the end; a reader keyed on position
    // stays valid. Kept in step with Row().
    static string Header() =>
        "unity_time,frame,frame_dt_ms," +
        "state,position_us,duration_us,banked_ms," +
        "decoded,decoded_delta,presented,presented_delta,frames_held," +
        "video_w,video_h,has_texture," +
        "audio_pulled,audio_pulled_delta,stream_rate,stream_channels," +
        "dsp_rate,dsp_buffer,dsp_buffers,listener_paused," +
        "sync_ppm,subtitle_track,caption_len," +
        "out_bound,out_playing,out_consumed,out_consumed_delta,out_peak,out_rms," +
        "out_latency_us,av_offset_us";

    BasisMediaTickStage IBasisMediaTickConsumer.TickStage => BasisMediaTickStage.Diagnostics;

    void IBasisMediaTickConsumer.MediaTick()
    {
        if (!IsLogging || _writer == null || _player == null) return;

        ulong presented = (ulong)_player.PresentedFrameCount;
        ulong decoded = _player.FramesDecoded;
        long pulled = _player.AudioFramesPulled;

        // How many Unity frames the last presented frame stayed on screen.
        // On a 72 Hz display a 24 fps source should read 3, 3, 3; a stray
        // 2 or 4 is judder.
        ulong presentedDelta = presented - _lastPresented;
        int held = _framesSincePresent;
        if (presentedDelta > 0) _framesSincePresent = 1;
        else _framesSincePresent++;

        AudioSettings.GetDSPBufferSize(out int dspLength, out int dspCount);

        _row.Clear();
        Append(Time.unscaledTimeAsDouble);
        Append(Time.frameCount);
        Append(Time.unscaledDeltaTime * 1000f);
        Append((int)_player.State);
        Append((long)(_player.PositionSeconds * 1_000_000.0));
        Append((long)(_player.DurationSeconds * 1_000_000.0));
        Append(_player.BankedMilliseconds);
        Append((long)decoded);
        Append((long)(decoded - _lastDecoded));
        Append((long)presented);
        Append((long)presentedDelta);
        Append(presentedDelta > 0 ? held : 0);
        Append(_player.VideoSize.x);
        Append(_player.VideoSize.y);
        Append(_player.Texture != null ? 1 : 0);
        Append(pulled);
        Append(pulled - _lastAudioPulled);
        Append(_player.AudioSampleRate);
        Append(_player.AudioChannels);
        Append(AudioSettings.outputSampleRate);
        Append(dspLength);
        Append(dspCount);
        Append(AudioListener.pause ? 1 : 0);
        Append(_player.SyncRatePpm);
        Append(_player.SelectedSubtitleTrackIndex);
        Append(_player.CurrentCaption?.Length ?? 0);

        // The other side of the ring: what the per-speaker outputs actually
        // consumed and how loud it was. The pull columns above say the engine
        // was being drained at the right rate; these say a speaker was fed.
        // Counted in output frames at the device rate, from the primary
        // output's mixed block.
        long consumed = _audio != null ? _audio.ConsumedSampleCount : 0;
        Append(_audio != null ? _audio.BoundOutputCount : 0);
        Append(_audio != null && _audio.IsAnyOutputPlaying ? 1 : 0);
        Append(consumed);
        Append(consumed - _lastOutputConsumed);
        Append(_audio != null ? _audio.LastPcmPeak : 0f);
        Append(_audio != null ? _audio.LastPcmRms : 0f);
        Append(_audio != null ? _audio.EstimatedOutputLatencyUs : 0);
        // The engine's own A/V figure, beside the host's view of the same
        // moment. out_latency_us is what the engine was told to compensate
        // for; this is what it believes it achieved. A disagreement between
        // the two is what to look for.
        Append(_player.AvOffsetUs, last: true);

        _lastPresented = presented;
        _lastDecoded = decoded;
        _lastAudioPulled = pulled;
        _lastOutputConsumed = consumed;

        try
        {
            _pendingRows.Enqueue(_row.ToString());
            if (++_rowsSinceFlush >= FlushEveryNRows) Flush();
        }
        catch (Exception e)
        {
            LastError = e.Message;
            BasisDebug.LogError($"[BasisMedia] diagnostics write failed: {e.Message}", BasisDebug.LogTag.Video);
            StopLogging();
        }
    }

    void Append(double value, bool last = false)
    {
        _row.Append(value.ToString("0.###", CultureInfo.InvariantCulture));
        if (!last) _row.Append(',');
    }

    void Append(long value, bool last = false)
    {
        _row.Append(value.ToString(CultureInfo.InvariantCulture));
        if (!last) _row.Append(',');
    }

    /// <summary>
    /// The output path, refused if it resolves outside
    /// <c>Application.persistentDataPath</c>. A world-authored or
    /// networked field could otherwise name any file the process can
    /// write.
    /// </summary>
    string ResolvePath(out string refusal) =>
        BasisMediaCapturePath.Resolve(LogPathOverride, "BasisMediaFrames.csv", out refusal);

    bool HeaderMatches(string header)
    {
        try
        {
            using var reader = new StreamReader(ResolvedLogPath);
            return string.Equals(reader.ReadLine(), header, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
}
