using System;
using System.Collections.Generic;
using UnityEngine;

// Routes the decoded audio stream to one or more Unity AudioSources, so each
// channel can be positioned independently in the world.
//
// List the AudioSources in Outputs, each carrying a BasisMediaAudioChannel that
// declares which decoded channel(s) it plays — a single channel 1-8, or a stereo
// downmix of the whole stream. Stereo content uses one Output set to Stereo (the
// MediaPlayerStreaming prefab); a 5.1 / 7.1 mix uses one Output per channel (the
// MediaPlayerMultiChannelStreaming prefab), positioned speaker-by-speaker.
//
// Decoded audio arrives interleaved from the native engine's PCM ring
// (NativePcmSource); a BasisMultiChannelPcmSplitter broadcasts it so every output
// reads independently — the same channel can feed two AudioSources in different
// places. The package owns playback; the consumer owns positioning.
public sealed class BasisMediaPlayerAudio : MonoBehaviour, IBasisMediaClockSource
{
    [Header("Output")]
    [Tooltip("AudioSources that play content audio. Each needs a BasisMediaAudioChannel selecting its channel(s) — a single channel, or a stereo downmix. Position each where its speaker should sit. This is the path for both stereo and surround setups.")]
    public AudioSource[] Outputs = Array.Empty<AudioSource>();

    [Header("Format")]
    [Tooltip("Sample rate of the active stream. Auto-updated from the decoder; the value here is the guess used before the format is known.")]
    public int SampleRate = 48000;

    [Tooltip("Channel count of the active stream. Auto-updated from the decoder.")]
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

    public float EffectiveVolumeGain => Mute ? 0f : Mathf.Clamp(VolumeGain, 0f, 2f) * Mathf.Clamp01(SMModuleAudio.ActiveMainVolume);

    // Native-engine path only: this component is fed by the OS-codec engine's
    // PCM ring. The engine owns the media clock (BasisMediaPlayer syncs off its
    // PositionUs), so this clock source stays inert.
    public bool HasMediaTime => false;
    public long CurrentMediaTimeUs => 0;

    // Read-only metrics for BasisMediaPlayerDiagnostics, so the CSV works for
    // this sink too. Tracked on the audio thread from the primary output (the
    // first valid entry in Outputs).
    private long consumedSamples;
    private float lastPcmPeak;
    private float lastPcmRms;
    public long ConsumedSampleCount => System.Threading.Interlocked.Read(ref consumedSamples);
    public float LastPcmPeak => lastPcmPeak;
    public float LastPcmRms => lastPcmRms;
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
    public float RepresentativeSpatialBlend => bindings != null && bindings.Length > 0 && bindings[0].Source != null ? bindings[0].Source.spatialBlend : 0f;

    // End-to-end audio output latency (µs) reported to the native backend so it
    // paces video presentation to match. The tap delivers audio per DSP block, so
    // latency is ~the DSP output buffer plus a block of headroom — a fraction of the
    // old streaming-clip buffer, which is what keeps A/V sync tight at low latency.
    // Cached because it's read per frame: the figure only changes with the DSP
    // configuration, which triggers an output rebuild that recomputes it.
    public long EstimatedOutputLatencyUs => estimatedOutputLatencyUs > 0 ? estimatedOutputLatencyUs : RecomputeOutputLatencyUs();
    private long estimatedOutputLatencyUs;

    private long RecomputeOutputLatencyUs()
    {
        AudioSettings.GetDSPBufferSize(out int dspLen, out int dspCount);
        int outRate = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 48000;
        double bufSecs = dspLen > 0 ? (double)dspLen * Mathf.Max(1, dspCount) / outRate : 0.02;
        estimatedOutputLatencyUs = (long)((bufSecs + 0.02) * 1_000_000.0);
        return estimatedOutputLatencyUs;
    }

    private IBasisPcmSource nativePcmSource;
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
    // isn't re-requested every frame when the decoder re-reports the same format.
    private int announcedRate;
    private int announcedChannels;

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

    public void ResetSyncAnchor()
    {
        splitter?.Clear();
    }

    private void OnEnable()
    {
        if (AutoPlayOnEnable) PlayAll();
    }

    private void OnDisable()
    {
        if (StopOnDisable) StopAll();
    }

    private void Update()
    {
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
    }

    private void OnDestroy()
    {
        TeardownClips();
    }

    private void Rebuild()
    {
        TeardownClips();

        AudioSource[] outputs = Outputs;
        if (nativePcmSource == null || outputs == null || outputs.Length == 0) { splitter = null; return; }

        // Don't build clips from the serialized format guess — wait for the
        // decoder's real format. SetExpectedFormat flips formatKnown and queues
        // the rebuild once the decoder reports.
        if (!formatKnown) { splitter = null; return; }

        int rate = Mathf.Max(8000, SampleRate);
        int channels = Mathf.Clamp(ChannelCount, 1, 8);
        int windowSamples = Mathf.RoundToInt(rate * Mathf.Min(ClipLengthSeconds, BasisMediaPlayerSecurity.ClipLengthSecondsCap));

        splitter = new BasisMultiChannelPcmSplitter(nativePcmSource, channels, windowSamples);
        RecomputeOutputLatencyUs();
        builtRate = rate;
        builtChannels = channels;
        System.Threading.Interlocked.Exchange(ref consumedSamples, 0);
        lastPcmPeak = 0f;
        lastPcmRms = 0f;

        var built = new List<Binding>(outputs.Length);
        for (int i = 0; i < outputs.Length; i++)
        {
            AudioSource src = outputs[i];
            if (src == null) continue;

            src.TryGetComponent(out BasisMediaAudioChannel sel);
            int outChannels;
            BasisMultiChannelPcmSplitter.Tap[] taps;
            // A BasisMediaAudioChannel set to Stereo folds the whole stream to 2
            // channels; any other selection plays a single decoded channel.
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
                        $"BasisMediaPlayerAudio: '{src.name}' has no BasisMediaAudioChannel; defaulting it to Channel {i + 1} (decoded channel index {i}).",
                        BasisDebug.LogTag.Video);
                }
                int monoChannel = sel != null ? sel.PrimaryChannel : i;
                if (monoChannel < 0 || monoChannel >= channels)
                {
                    // Selected channel isn't present in this stream (e.g. a 5.1
                    // output on a stereo stream) — leave this AudioSource silent
                    // rather than doubling another channel onto it.
                    src.Stop();
                    src.clip = null;
                    continue;
                }
                outChannels = 1;
                taps = new[] { new BasisMultiChannelPcmSplitter.Tap(monoChannel, 0, 1f) };
            }

            var b = new Binding { Source = src, Splitter = splitter, OutChannels = outChannels, Taps = taps };
            b.Primary = built.Count == 0;
            // A short silent looping clip keeps the source's DSP chain active; the tap
            // overwrites each block from the splitter each DSP block (~tens of ms of
            // latency vs a streaming clip's ~half-second buffer). SpatializePostEffects
            // makes any spatialiser (Steam Audio) run AFTER the tap, so the generated
            // audio is spatialised / occluded / transmitted normally.
            b.Clip = AudioClip.Create($"BasisMediaPlayerAudio_{i}_keepalive", Mathf.Max(256, rate / 10), 1, rate, false);
            src.clip = b.Clip;
            src.loop = true;
            src.spatializePostEffects = true;
            if (!src.TryGetComponent(out BasisMediaPlayerAudioTap tap)) tap = src.gameObject.AddComponent<BasisMediaPlayerAudioTap>();
            b.FilterTap = tap;
            bool primary = b.Primary;
            // Source frames per output frame: the tap renders straight into DSP
            // blocks, so rate conversion happens in the splitter read (Quest runs
            // the DSP at 24kHz against 48kHz sources; desktop is typically 1:1).
            int dspRate = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : rate;
            tap.Bind(splitter, taps, spreadMonoAcrossChannels: outChannels == 1,
                     gain: () => EffectiveVolumeGain,
                     metrics: primary ? (Action<float[], int>)TrackPrimaryMetrics : null,
                     sourceFramesPerOutputFrame: (double)rate / dspRate);
            built.Add(b);
        }
        bindings = built.ToArray();
        if (isActiveAndEnabled && AutoPlayOnEnable) PlayAll();
    }

    // Stereo fold-down of the available channels, assuming the decoder's WAVE
    // channel order: the front pair passes straight through, the centre folds
    // into both sides at -3 dB, the LFE (index 3 in 6+ channel layouts) is
    // dropped, and the remaining channels alternate left/right at -3 dB. The
    // taps are then scaled so a full-scale input can't exceed +/-1.0 on either
    // output. Mono duplicates to both sides.
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
        float norm = 1f / Mathf.Max(1f, Mathf.Max(sumL, sumR));
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

    // Peak / RMS / consumed-frame metrics from the primary output's mixed block,
    // invoked by the primary tap. Runs on the audio thread. Counts sample-frames,
    // not interleaved floats, so the metric is the same whether the primary output
    // is mono or stereo.
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
