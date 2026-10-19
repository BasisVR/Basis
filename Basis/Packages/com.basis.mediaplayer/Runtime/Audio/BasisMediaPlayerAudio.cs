using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Routes the decoded audio stream to one or more Unity AudioSources, so each
/// channel can be positioned independently in the world.
///
/// List the AudioSources in Outputs, each carrying a
/// <see cref="BasisMediaAudioChannel"/> that declares which decoded channel(s)
/// it plays: a single channel 1-8, or a stereo downmix of the whole stream.
/// Stereo content uses one Output set to Stereo; a 5.1 / 7.1 mix uses one
/// Output per channel, positioned speaker-by-speaker.
///
/// Decoded audio arrives interleaved from the engine's PCM ring
/// (<see cref="NativePcmSource"/>); a <see cref="BasisMultiChannelPcmSplitter"/>
/// broadcasts it so every output reads independently, and the same channel can
/// feed two AudioSources in different places. The package owns playback; the
/// consumer owns positioning.
/// </summary>
[AddComponentMenu("Basis/Basis Media Audio")]
public sealed class BasisMediaPlayerAudio : MonoBehaviour, IBasisMediaTickConsumer
{
    /// <summary>Ceiling on <see cref="ClipLengthSeconds"/>. The field is
    /// authored content, so a world can set it; the window is a per-session
    /// allocation of rate × channels floats and wants a bound.</summary>
    public const float MaxClipLengthSeconds = 30f;

    [Header("Output")]
    [Tooltip("AudioSources that play content audio. Each needs a BasisMediaAudioChannel selecting its channel(s) — a single channel, or a stereo downmix. Position each where its speaker should sit. This is the path for both stereo and surround setups.")]
    public AudioSource[] Outputs = Array.Empty<AudioSource>();

    [Header("Format")]
    [Tooltip("Sample rate of the active stream. Auto-updated from the engine; the value here is the guess used before the format is known.")]
    public int SampleRate = 48000;

    [Tooltip("Channel count of the active stream. Auto-updated from the engine.")]
    [Range(1, 8)] public int ChannelCount = 6;

    [Header("Buffering")]
    [Tooltip("Depth of the splitter's broadcast window in seconds — how much decoded audio is retained for the per-output taps. Larger is steadier under jitter; it does not add output latency (the taps read the live edge each DSP block).")]
    [Min(0.1f)] public float ClipLengthSeconds = 0.5f;

    [Header("Playback")]
    [Tooltip("If true, the output AudioSources are played automatically when this component is enabled.")]
    public bool AutoPlayOnEnable = true;

    [Tooltip("If true, the output AudioSources are stopped when this component is disabled.")]
    public bool StopOnDisable = true;

    [Tooltip("Sample-domain volume multiplier applied after decode. Use each AudioSource.volume for spatial mixing; this compensates for quiet/loud streams. Hard-capped at 2.0 at runtime.")]
    [Range(0f, 2f)] public float VolumeGain = 1f;

    [Tooltip("If true, decoded samples are zeroed before write. Mutes without stopping the AudioSources.")]
    public bool Mute = false;

    /// <summary>Gain the taps apply. Deliberately does NOT fold in the host's
    /// main volume: these outputs are ordinary AudioSources with no mixer
    /// group, so they reach the AudioListener, and the main volume slider is
    /// applied there already (SMModuleAudio.ApplyMainVolume sets
    /// AudioListener.volume). Multiplying here as well would attenuate media
    /// by the square of it.</summary>
    public float EffectiveVolumeGain => Mute ? 0f : Mathf.Clamp(VolumeGain, 0f, 2f);

    // Read-only metrics for BasisMediaPlayerDiagnostics, so the capture covers
    // what actually reached a speaker. Tracked on the audio thread from the
    // primary output (the first valid entry in Outputs).
    private long consumedSamples;
    private float lastPcmPeak;
    private float lastPcmRms;
    public long ConsumedSampleCount => System.Threading.Interlocked.Read(ref consumedSamples);
    public float LastPcmPeak => lastPcmPeak;
    public float LastPcmRms => lastPcmRms;

    /// <summary>How many outputs the last build actually bound. Zero with
    /// nothing wired, which is why a bare player is silent.</summary>
    public int BoundOutputCount => bindings != null ? bindings.Length : 0;

    public bool IsAnyOutputPlaying
    {
        get
        {
            if (bindings == null) return false;
            foreach (var b in bindings) if (b.Source != null && b.Source.isPlaying) return true;
            return false;
        }
    }
    public float RepresentativeVolume => bindings != null && bindings.Length > 0 && bindings[0].Source != null ? bindings[0].Source.volume : 0f;

    /// <summary>Whether the player beside this sink has a position to report:
    /// its load has reached playback.</summary>
    public bool HasMediaTime => tickOwner != null && tickOwner.IsPrepared;

    /// <summary>The player's playback position in microseconds, 0 before
    /// playback: the engine clock's, the same number
    /// <see cref="BasisMediaPlayer.Position"/> reports.</summary>
    public long CurrentMediaTimeUs => HasMediaTime ? (long)(tickOwner.PositionSeconds * 1_000_000.0) : 0L;
    public float RepresentativeSpatialBlend => bindings != null && bindings.Length > 0 && bindings[0].Source != null ? bindings[0].Source.spatialBlend : 0f;

    /// <summary>
    /// End-to-end audio output latency (µs): the delay between a sample being
    /// pulled from the ring and leaving the speaker. The tap delivers audio per
    /// DSP block, and the estimate is the DSP output buffer plus 20 ms of
    /// headroom.
    ///
    /// Cached because it's read per frame: the figure only changes with the DSP
    /// configuration, and is recomputed in OnEnable, on an output rebuild and
    /// when Unity reports a new audio configuration.
    /// </summary>
    public long EstimatedOutputLatencyUs => estimatedOutputLatencyUs > 0 ? estimatedOutputLatencyUs : RecomputeOutputLatencyUs();
    private long estimatedOutputLatencyUs;

    private long RecomputeOutputLatencyUs()
    {
        AudioSettings.GetDSPBufferSize(out int dspLen, out int dspCount);
        int outRate = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 48000;
        double bufSecs = dspLen > 0 ? (double)dspLen * Mathf.Max(1, dspCount) / outRate : 0.02;
        double filterSecs = splitter != null && builtRate > 0 ? (double)splitter.FilterDelayFrames / builtRate : 0;
        estimatedOutputLatencyUs = (long)((bufSecs + filterSecs + 0.02) * 1_000_000.0);
        return estimatedOutputLatencyUs;
    }

    private IBasisPcmSource nativePcmSource;

    /// <summary>The ring this sink pulls from. Setting it (including to null)
    /// drops the built outputs and waits for the new source's format.</summary>
    public IBasisPcmSource NativePcmSource
    {
        get => nativePcmSource;
        set { if (!ReferenceEquals(nativePcmSource, value)) { nativePcmSource = value; formatKnown = false; announcedRate = 0; announcedChannels = 0; rebuildRequested = true; } }
    }

    private sealed class Binding
    {
        public AudioSource Source;
        public AudioClip Clip;
        public BasisMultiChannelPcmSplitter Splitter;
        public BasisMultiChannelPcmSplitter.Tap[] Taps;
        public int OutChannels;
        public bool Primary;
        public BasisMediaPlayerAudioTap FilterTap;   // per-output OnAudioFilterRead tap
        public AnalysisFeed Feed;                    // set instead of FilterTap on an analysis output
    }

    // Feeds an analysis output by writing the splitter's audio into a looping
    // AudioClip a little ahead of that AudioSource's own playhead, topped up
    // from Update.
    //
    // A streaming clip (AudioClip.Create with a PCM callback) does not work
    // here: Unity keeps its own buffer between the callback and the speaker,
    // it runs about a second whatever length the clip is declared, and it
    // isn't reachable from the callback. That would put an analyser a second
    // behind the audio everyone can hear. Writing the clip's samples directly
    // keeps the delay under our control: the lead we write at, plus the
    // output buffer.
    //
    // Chunks are a fixed size and the clip an exact multiple of it, so a write
    // never straddles the loop point and never needs to allocate.
    private sealed class AnalysisFeed
    {
        private const int ChunkFrames = 1024;

        private readonly AudioSource source;
        private readonly BasisMultiChannelPcmSplitter splitter;
        private readonly BasisMultiChannelPcmSplitter.Reader reader;
        private readonly BasisMultiChannelPcmSplitter.Tap[] taps;
        private readonly int channels;
        private readonly int lengthFrames;
        private readonly int leadFrames;
        private readonly float[] scratch;
        private readonly Func<float> gainProvider;
        private int writeCursor;
        private bool active;

        public readonly AudioClip Clip;

        public AnalysisFeed(AudioSource src, BasisMultiChannelPcmSplitter s, BasisMultiChannelPcmSplitter.Tap[] t,
                            int outChannels, int rate, float leadSeconds, string clipName, Func<float> gain)
        {
            source = src;
            splitter = s;
            reader = s?.CreateReader();
            taps = t;
            channels = Mathf.Max(1, outChannels);
            gainProvider = gain;

            // The lead is the delay. Floored at two chunks so a frame hitch
            // doesn't let the playhead overtake the writer, and kept to a
            // quarter of the clip so the overtake check below can tell a large
            // gap from a wrapped one.
            int wanted = Mathf.Max(Mathf.RoundToInt(rate * leadSeconds), ChunkFrames * 2);
            int chunks = Mathf.Max(4, Mathf.CeilToInt(wanted / (float)ChunkFrames) * 4);
            lengthFrames = chunks * ChunkFrames;
            leadFrames = Mathf.Min(wanted, lengthFrames / 4);

            scratch = new float[ChunkFrames * channels];
            Clip = AudioClip.Create(clipName, lengthFrames, channels, rate, false);
            active = s != null && t != null && reader != null && src != null;
        }

        public void Release() => active = false;

        // Main thread, once a frame. Tops the clip back up to `leadFrames`
        // ahead of the playhead, which is what holds the delay steady.
        public void Pump()
        {
            if (!active || source == null || Clip == null || !source.isPlaying) return;

            int play = source.timeSamples;
            int gap = writeCursor - play;
            if (gap < 0) gap += lengthFrames;

            // A gap past halfway means the playhead has overtaken the writer (a
            // hitch, or playback restarting), not that we are miles ahead. Drop
            // back into position rather than waiting for it to lap us.
            if (gap > lengthFrames / 2)
            {
                // Kept on a chunk boundary, so a write still can't straddle the
                // loop.
                int target = (play + leadFrames) % lengthFrames;
                writeCursor = target - (target % ChunkFrames);
                gap = writeCursor - play;
                if (gap < 0) gap += lengthFrames;
            }

            for (int guard = 0; gap < leadFrames && guard < lengthFrames / ChunkFrames; guard++)
            {
                Array.Clear(scratch, 0, scratch.Length);
                // Never block behind the DSP thread's hold of the splitter gate:
                // a contended frame retries next Pump and the overtake check above
                // absorbs the worst case.
                if (!splitter.TryReadMixed(reader, scratch, ChunkFrames, channels, taps,
                                           gainProvider != null ? gainProvider() : 1f, out _))
                {
                    break;
                }
                Clip.SetData(scratch, writeCursor);
                writeCursor = (writeCursor + ChunkFrames) % lengthFrames;
                gap += ChunkFrames;
            }
        }
    }

    private BasisMultiChannelPcmSplitter splitter;
    private Binding[] bindings;
    private int builtChannels;
    private int builtRate;
    private bool rebuildRequested;
    private volatile bool formatKnown;
    private volatile int pendingFormatRate;
    private volatile int pendingFormatChannels;
    // Last format announced to SetExpectedFormat. Deduped here rather than
    // against the built format so an aborted rebuild (e.g. no Outputs wired)
    // isn't re-requested every frame when the engine re-reports the same
    // format.
    private int announcedRate;
    private int announcedChannels;

    // Main-thread mirrors the audio thread reads. Unity's audio settings and
    // the source's trim are both main-thread reads, and doing either inside a
    // DSP callback under IL2CPP can fail silently.
    private volatile int dspOutputRate = 48000;
    private volatile int pullRateOffsetPpm;

    /// <summary>Announce the stream's format. Rebuilds the outputs when it
    /// differs from what they were built for.</summary>
    public void SetExpectedFormat(int sampleRate, int channels)
    {
        if (sampleRate <= 0 || channels <= 0) return;
        formatKnown = true;
        channels = Mathf.Clamp(channels, 1, 8);
        if (sampleRate == announcedRate && channels == announcedChannels) return;
        announcedRate = sampleRate;
        announcedChannels = channels;
        pendingFormatRate = sampleRate;
        pendingFormatChannels = channels;
    }

    /// <summary>Drop everything the window is holding, when the audio behind
    /// it belongs to a timeline that no longer exists (a seek, a restart).
    /// </summary>
    public void ResetSyncAnchor()
    {
        splitter?.Clear();
    }

    // The player on this GameObject, when there is one. It calls this component
    // back inside its own tick, after the poll that announced the format and
    // the rate trim read below.
    private BasisMediaPlayer tickOwner;

    private void OnEnable()
    {
        RecomputeOutputLatencyUs();
        if (AutoPlayOnEnable) PlayAll();
        if (TryGetComponent(out tickOwner)) tickOwner.AddTickConsumer(this);
        AudioSettings.OnAudioConfigurationChanged += OnAudioConfigurationChanged;
    }

    // Unity resets the audio system on a configuration change (a Bluetooth
    // headset connecting, the default device changing), and its own example
    // plays the AudioSources again in this handler. An output left stopped
    // pulls nothing, and the audio-mastered clock stalls with it. The outputs
    // are rebuilt at the new DSP rate and started as they were on enable.
    private void OnAudioConfigurationChanged(bool deviceWasChanged)
    {
        RecomputeOutputLatencyUs();
        rebuildRequested = true;
    }

    private void OnDisable()
    {
        AudioSettings.OnAudioConfigurationChanged -= OnAudioConfigurationChanged;
        if (tickOwner != null)
        {
            tickOwner.RemoveTickConsumer(this);
            tickOwner = null;
        }

        if (StopOnDisable) StopAll();
    }

    /// <summary>
    /// Refresh the main-thread mirrors the audio thread reads.
    ///
    /// Called from <see cref="PlayAll"/> as well as per frame: the outputs are
    /// started from OnEnable, and the first Update is a frame too late. Until
    /// then the rate holds an optimistic 48 kHz default that Quest does not
    /// have, so the first callbacks would resample against a rate the device
    /// is not running.
    /// </summary>
    private void SampleHostAudioSettings()
    {
        int outRate = AudioSettings.outputSampleRate;
        if (outRate <= 0 || outRate == dspOutputRate) return;
        dspOutputRate = outRate;
        if (splitter != null && builtRate > 0)
        {
            splitter.SetDecimation((double)builtRate / outRate);
            RecomputeOutputLatencyUs();
        }
    }

    BasisMediaTickStage IBasisMediaTickConsumer.TickStage => BasisMediaTickStage.Audio;

    void IBasisMediaTickConsumer.MediaTick()
    {
        SampleHostAudioSettings();
        IBasisPcmSource source = nativePcmSource;
        pullRateOffsetPpm = source != null ? source.PullRateOffsetPpm : 0;

        int rate = pendingFormatRate;
        int ch = pendingFormatChannels;
        if (rate > 0 && ch > 0 && (rate != builtRate || ch != builtChannels))
        {
            SampleRate = rate;
            ChannelCount = ch;
            pendingFormatRate = 0;
            pendingFormatChannels = 0;
            rebuildRequested = true;
        }

        if (rebuildRequested)
        {
            rebuildRequested = false;
            Rebuild();
        }

        // Analysis outputs are written from here rather than an audio callback,
        // so they have to be topped up every frame to stay ahead of their
        // playhead.
        if (bindings != null)
        {
            foreach (var b in bindings) b.Feed?.Pump();
        }
    }

    private void OnDestroy()
    {
        TeardownClips();
    }

    // Source frames per output frame, asked per DSP block. The device
    // conversion is the constant part (Quest runs the DSP at 24 kHz against
    // 48 kHz sources); the trim is the shared-playback ladder asking the pull
    // to run slightly fast or slow, which is how a viewer converges on the
    // owner's position without a seek. Audio masters the clock, so consuming
    // at anything other than this rate drags the whole session.
    private double SourceStep()
    {
        int rate = builtRate;
        int dsp = dspOutputRate;
        if (rate <= 0 || dsp <= 0) return 1.0;
        return rate * (1.0 + pullRateOffsetPpm / 1_000_000.0) / dsp;
    }

    private void Rebuild()
    {
        TeardownClips();

        AudioSource[] outputs = Outputs;
        if (nativePcmSource == null || outputs == null || outputs.Length == 0) { splitter = null; return; }

        // Don't build from the serialised format guess; wait for the engine's
        // real format. SetExpectedFormat flips formatKnown and queues the
        // rebuild once it reports.
        if (!formatKnown) { splitter = null; return; }

        int rate = Mathf.Max(8000, SampleRate);
        int channels = Mathf.Clamp(ChannelCount, 1, 8);
        int windowSamples = Mathf.RoundToInt(rate * Mathf.Min(ClipLengthSeconds, MaxClipLengthSeconds));

        splitter = new BasisMultiChannelPcmSplitter(nativePcmSource, channels, windowSamples);
        splitter.SetDecimation((double)rate / dspOutputRate);
        builtRate = rate;
        builtChannels = channels;
        RecomputeOutputLatencyUs();
        System.Threading.Interlocked.Exchange(ref consumedSamples, 0);
        lastPcmPeak = 0f;
        lastPcmRms = 0f;

        var built = new List<Binding>(outputs.Length);
        bool primaryAssigned = false;
        for (int i = 0; i < outputs.Length; i++)
        {
            AudioSource src = outputs[i];
            if (src == null) continue;

            src.TryGetComponent(out BasisMediaAudioChannel sel);
            int outChannels;
            BasisMultiChannelPcmSplitter.Tap[] taps;
            // A BasisMediaAudioChannel set to Stereo folds the whole stream to
            // 2 channels; any other selection plays a single decoded channel.
            if (sel != null && sel.IsStereo)
            {
                outChannels = 2;
                taps = BuildDownmixTaps(channels);
            }
            else
            {
                if (sel == null)
                {
                    BasisDebug.LogWarning(
                        $"[BasisMedia] '{src.name}' has no BasisMediaAudioChannel; defaulting it to Channel {i + 1} (decoded channel index {i}).", BasisDebug.LogTag.Video);
                }
                int monoChannel = sel != null ? sel.PrimaryChannel : i;
                if (monoChannel < 0 || monoChannel >= channels)
                {
                    // Selected channel isn't present in this stream (e.g. a 5.1
                    // output on a stereo stream). Leave this AudioSource
                    // silent rather than doubling another channel onto it.
                    src.Stop();
                    src.clip = null;
                    continue;
                }
                outChannels = 1;
                taps = new[] { new BasisMultiChannelPcmSplitter.Tap(monoChannel, 0, 1f) };
            }

            var b = new Binding { Source = src, Splitter = splitter, OutChannels = outChannels, Taps = taps };
            bool analysis = sel != null && sel.AnalysisFeed;
            // The metrics are audio-thread figures from the primary output.
            // An analysis feed is written from Update, ahead of its own
            // playhead, so it would report bursts of audio that hasn't played
            // yet. A set with nothing but analysis outputs reports no metrics,
            // since nothing is consuming on the audio thread.
            b.Primary = !primaryAssigned && !analysis;
            if (b.Primary) primaryAssigned = true;
            bool primary = b.Primary;
            src.loop = true;
            src.spatializePostEffects = true;

            if (analysis)
            {
                // Unity's per-source readback (AudioSource.GetOutputData, and
                // the spectrum calls behind it) only reflects clip playback:
                // audio a script writes in OnAudioFilterRead reaches the
                // listener but never enters the buffer those read. Analysers
                // that sample an AudioSource (AudioLink among them) therefore
                // see silence from the tap. An output flagged for
                // analysis plays a clip this component writes instead, which
                // Unity does read back. It runs its configured delay behind the
                // tap-driven outputs, which is why it isn't the default.
                //
                // Unlike the tap, Unity applies this AudioSource's volume and
                // mute to clip playback itself, so neither is folded into the
                // gain here or it would land twice.
                var feed = new AnalysisFeed(src, splitter, taps, outChannels, rate,
                                            Mathf.Clamp(sel.AnalysisFeedLatency,
                                                        BasisMediaAudioChannel.MinAnalysisFeedLatency,
                                                        BasisMediaAudioChannel.MaxAnalysisFeedLatency),
                                            $"BasisMediaPlayerAudio_{i}_analysis",
                                            () => EffectiveVolumeGain);
                b.Feed = feed;
                b.Clip = feed.Clip;
                src.clip = b.Clip;
                // A tap left over from a previous build (or authored on the
                // prefab) would clear the block and overwrite the clip, so drop
                // its binding.
                if (src.TryGetComponent(out BasisMediaPlayerAudioTap stale)) stale.Unbind();
                built.Add(b);
                continue;
            }

            // A short silent looping clip keeps the source's DSP chain active;
            // the tap overwrites each block from the splitter each DSP block
            // (~tens of ms of latency vs a streaming clip's ~half-second
            // buffer). SpatializePostEffects makes any spatialiser (Steam
            // Audio) run AFTER the tap, so the generated audio is spatialised /
            // occluded / transmitted normally.
            b.Clip = AudioClip.Create($"BasisMediaPlayerAudio_{i}_keepalive", Mathf.Max(256, rate / 10), 1, rate, false);
            src.clip = b.Clip;
            if (!src.TryGetComponent(out BasisMediaPlayerAudioTap tap)) tap = src.gameObject.AddComponent<BasisMediaPlayerAudioTap>();
            b.FilterTap = tap;
            // A tap added here appends, so it lands below anything already on
            // the object. Component order is fixed at runtime, so this is a
            // warning rather than something we can put right; the editor offers
            // the reorder.
            Component bypassed = BasisMediaPlayerAudioTap.FirstBypassedFilter(src);
            if (bypassed != null)
            {
                BasisDebug.LogWarning(
                    $"[BasisMedia] '{src.name}' has a {bypassed.GetType().Name} above its BasisMediaPlayerAudioTap, so that filter never hears the stream. Move it below the tap.", BasisDebug.LogTag.Video);
            }
            tap.Bind(splitter, taps, spreadMonoAcrossChannels: outChannels == 1,
                     gain: () => EffectiveVolumeGain,
                     metrics: primary ? (Action<float[], int>)TrackPrimaryMetrics : null,
                     sourceFramesPerOutputFrame: SourceStep);
            built.Add(b);
        }
        bindings = built.ToArray();
        if (isActiveAndEnabled && AutoPlayOnEnable) PlayAll();
    }

    // Stereo fold-down of the available channels, assuming the engine's WAVE
    // channel order: the front pair passes straight through, the centre folds
    // into both sides at -3 dB, the LFE (index 3 in 6+ channel layouts) is
    // dropped, and the remaining channels alternate left/right at -3 dB. Mono
    // duplicates to both sides.
    //
    // Wherever channels are summed the taps are then scaled to a row sum of
    // 1/sqrt(2), not 1.0: decoded float PCM measures peaks past +/-1.0 on hot
    // mixes, so rows that are merely full-scale-safe still clip audibly, and
    // the extra -3 dB absorbs it. A 1- or 2-channel stream is copied rather
    // than summed and passes through at unity.
    private static BasisMultiChannelPcmSplitter.Tap[] BuildDownmixTaps(int channels)
    {
        const float att = 0.70710678f;
        if (channels <= 1)
        {
            return new[]
            {
                new BasisMultiChannelPcmSplitter.Tap(0, 0, 1f),
                new BasisMultiChannelPcmSplitter.Tap(0, 1, 1f),
            };
        }

        var taps = new List<BasisMultiChannelPcmSplitter.Tap>(channels + 2)
        {
            new BasisMultiChannelPcmSplitter.Tap(0, 0, 1f),
            new BasisMultiChannelPcmSplitter.Tap(1, 1, 1f),
        };
        if (channels >= 3)
        {
            taps.Add(new BasisMultiChannelPcmSplitter.Tap(2, 0, att));
            taps.Add(new BasisMultiChannelPcmSplitter.Tap(2, 1, att));
        }
        int next = 3;
        if (channels == 4)
        {
            // 4.0's fourth channel is the back centre; fold it into both sides.
            taps.Add(new BasisMultiChannelPcmSplitter.Tap(3, 0, att));
            taps.Add(new BasisMultiChannelPcmSplitter.Tap(3, 1, att));
            next = 4;
        }
        else if (channels >= 6)
        {
            next = 4;
        }
        bool intoLeft = true;
        for (int c = next; c < channels; c++)
        {
            taps.Add(new BasisMultiChannelPcmSplitter.Tap(c, intoLeft ? 0 : 1, att));
            intoLeft = !intoLeft;
        }

        float sumL = 0f, sumR = 0f;
        int tapCount = taps.Count;
        for (int i = 0; i < tapCount; i++)
        {
            if (taps[i].Out == 0) sumL += taps[i].Coeff;
            else sumR += taps[i].Coeff;
        }
        float headroom = channels >= 3 ? att : 1f;
        float norm = headroom / Mathf.Max(1f, Mathf.Max(sumL, sumR));
        var result = new BasisMultiChannelPcmSplitter.Tap[tapCount];
        for (int i = 0; i < tapCount; i++)
        {
            result[i] = new BasisMultiChannelPcmSplitter.Tap(taps[i].Source, taps[i].Out, taps[i].Coeff * norm);
        }
        return result;
    }

    private void TeardownClips()
    {
        if (bindings != null)
        {
            foreach (var b in bindings)
            {
                if (b.FilterTap != null) b.FilterTap.Unbind();
                if (b.Feed != null) b.Feed.Release();
                if (b.Source != null && b.Source.clip == b.Clip) { b.Source.Stop(); b.Source.clip = null; }
                if (b.Clip != null) Destroy(b.Clip);
            }
            bindings = null;
        }
        builtChannels = 0;
        builtRate = 0;
    }

    private void PlayAll()
    {
        if (bindings == null) return;
        // Before a single callback runs, not a frame after.
        SampleHostAudioSettings();
        // One shared DSP start time keeps the channels sample-aligned;
        // sequential Play() calls can land on different DSP ticks and leave a
        // constant inter-channel offset.
        double start = AudioSettings.dspTime + 0.05;
        foreach (var b in bindings)
            if (b.Source != null && b.Clip != null && !b.Source.isPlaying) b.Source.PlayScheduled(start);
    }

    private void StopAll()
    {
        if (bindings == null) return;
        foreach (var b in bindings)
            if (b.Source != null) b.Source.Stop();
    }

    // Peak / RMS / consumed-frame metrics from the primary output's mixed
    // block, invoked by the primary tap. Runs on the audio thread. Counts
    // sample-frames, not interleaved floats, so the metric is the same whether
    // the primary output is mono or stereo. It counts OUTPUT frames at the
    // device rate, because what it measures is whether a speaker kept being
    // fed.
    private void TrackPrimaryMetrics(float[] data, int outChannels)
    {
        int n = data.Length;
        float peak = 0f; double sumSq = 0;
        for (int i = 0; i < n; i++) { float v = data[i]; float a = v < 0f ? -v : v; if (a > peak) peak = a; sumSq += v * v; }
        lastPcmPeak = peak;
        lastPcmRms = n > 0 ? (float)Math.Sqrt(sumSq / n) : 0f;
        System.Threading.Interlocked.Add(ref consumedSamples, n / Mathf.Max(1, outChannels));
    }
}
