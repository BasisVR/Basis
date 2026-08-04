using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking.NetworkedAvatar;
using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using UnityEngine;
using static SerializableBasis;
namespace Basis.Scripts.Networking.Receivers
{
    /// <summary>
    /// Receives, decodes, buffers, and plays remote voice audio for a networked player.
    /// Uses a single <see cref="BasisVoiceBuffer"/> for packet reordering and PCM playback.
    /// </summary>
    [Serializable]
    public class BasisAudioReceiver
    {
        [SerializeReference] public BasisRemoteAudioDriver BasisRemoteVisemeAudioDriver = null;
        public AudioSource audioSource;
        public BasisAudioAndVisemeDriver visemeDriver = new BasisAudioAndVisemeDriver();

        /// <summary>
        /// Single combined buffer: jitter buffer (encoded) + decoded PCM queue.
        /// </summary>
        public BasisVoiceBuffer VoiceBuffer = new BasisVoiceBuffer();

        public float[] pcmBuffer = new float[RemoteOpusSettings.MaxFrameSize];
        public int pcmLength;
        public byte lastReadIndex = 0;
        public Transform AudioSourceTransform;
        public float[] resampledSegment;
        public volatile bool HasAudioSource = false;
        public volatile float DirectionalDampeningMultiplier = 1f;

        /// <summary>
        /// High-shelf depth in dB from the listener cone-of-influence — the part of
        /// the cone that models the listener's own head being in the way, rather
        /// than the broadband part folded into
        /// <see cref="DirectionalDampeningMultiplier"/>. Written by the spatial
        /// voice job on the main thread, consumed on the audio thread.
        /// </summary>
        public volatile float ConeShelfDb;

        /// <summary>
        /// High-shelf depth in dB from the talker's mouth directivity: how far
        /// off-axis this player's head is from pointing at you. This is the cue
        /// Steam Audio's broadband dipole structurally cannot supply — a real mouth
        /// turned away loses roughly 12 dB more at 4 kHz than at 250 Hz.
        /// </summary>
        public volatile float DirectivityShelfDb;

        // Applied AFTER the viseme pass (see BasisRemoteAudioDriver) so lip-sync
        // classifies the talker's own spectrum rather than an orientation-tinted one.
        private readonly BasisVoiceToneShaper _toneShaper = new BasisVoiceToneShaper();

        // ── Debug metering (audio-thread writer, main-thread reader) ──
        // Peak amplitude of the decoded voice, sampled per audio callback for the
        // BasisAudioGizmos level meter. This is the raw signal BEFORE any per-sample
        // attenuation — i.e. how loud this speaker actually is — so the gizmo can show
        // their output against everything that reduces it. Decays toward 0 between
        // utterances so the meter falls back to silence. Single writer + single reader,
        // and a stale frame is harmless for an overlay, so no sync beyond volatile.
        public volatile float SourcePeak;
        private const float MeterReleaseFactor = 0.90f;

        [System.NonSerialized] public BasisNetworkReceiver BasisNetworkReceiver;

        /// <summary>
        /// Optional decoded-PCM tap used by the voice-recording system. Invoked on the
        /// decode thread with the freshly decoded mono frame; the callback MUST copy the
        /// samples out because <see cref="pcmBuffer"/> is reused. Null when nobody records.
        /// </summary>
        public volatile System.Action<float[], int> OnDecodedFrame;

        /// <summary>
        /// Optional encoded-frame tap used to feed a spatialized voice re-emit source.
        /// Invoked with the inbound segment as it is inserted; the handler must consume it
        /// synchronously (the segment buffer is reused). Null when not routed to an object.
        /// </summary>
        public volatile System.Action<AudioSegmentDataMessage> OnEncodedFrame;

        public static float[] silentData;
        public static int outputSampleRate;
        private static bool _loggedOutputRate;

#if !UNITY_SERVER
        public OpusSharp.Core.Interfaces.IOpusDecoder decoder;
#endif

        private float[] _inputScratch;
        private int _cachedOutputRate = -1;
        private float _resampleRatio = 1f;
        private float[] _resampleScratch;
        public volatile int _silentUnits20ms;
        public long _silentUsAccum;

        /// <summary>Packet loss concealment frames generated (diagnostic counter).</summary>
        public volatile int PlcCount;
        /// <summary>Silence gaps skipped (diagnostic counter).</summary>
        public volatile int SilenceInjectedCount;
        /// <summary>Frames reconstructed from Opus FEC data embedded in the NEXT packet (diagnostic counter).</summary>
        public volatile int FecRecoveredCount;
        /// <summary>Times the idle-reset rearmed the initial-fill gate (diagnostic counter).</summary>
        public volatile int RearmCount;
        /// <summary>PLC frames synthesized to bridge a mid-stream starve (nothing newer
        /// buffered) before fading to silence — the expand half of the adaptive
        /// latency loop (diagnostic counter).</summary>
        public volatile int StarvePlcCount;
        /// <summary>Near-silent frames dropped by the catch-up drain (diagnostic counter).</summary>
        public volatile int TrimmedQuietFrames;
        /// <summary>Frames shortened by one pitch period by the catch-up drain (diagnostic counter).</summary>
        public volatile int AcceleratedFrames;
        /// <summary>Total samples reclaimed by acceleration (drain-side, read for diagnostics).</summary>
        public long AcceleratedSavedSamples;
        /// <summary>Packets discarded by the emergency flush when standing latency exceeded the hard bound (diagnostic counter).</summary>
        public volatile int FlushedPackets;

        // ── Catch-up drain state (touched only under the decode gate) ──
        // Standing buffered audio beyond the adaptive target is drained back down:
        // near-silent frames are dropped outright, voiced frames are shortened by
        // one pitch period (BasisVoiceTimeCompress), and a hopeless backlog is
        // flushed. Without this, every network stall/late burst permanently added
        // its duration to the listener's latency until the sender happened to pause.
        private bool _catchupActive;
        private int _starvePlcBudget;
        private bool _starveEpisode;
        private bool _fadeInNextDecodedFrame;
        private int _framesSinceExpand;
        private const float QuietTrimPeak = 0.02f;       // ≈ -34 dBFS (codec noise floors ride above -40)
        private const float QuietTrimSoft = 0.05f;       // background bound when no pitch structure found
        private const int StarvePlcMaxFrames = 6;        // ≈ 120 ms bridge at 20 ms frames
        private const int CatchupSlackFrames = 2;        // engage above target+slack
        private const int FlushSlackFrames = 8;          // hard bound above target...
        private const int FlushMinFrames = 16;           // ...but never below this absolute depth
        private const int ExpandMinSpacingFrames = 25;   // ≥ half a second between inserted frames

        /// <summary>PLC frames inserted between real frames to raise standing depth toward
        /// a grown target — the mid-stream buffer build the pre-roll can't provide on a
        /// continuous talker (diagnostic counter).</summary>
        public volatile int ExpandInsertedFrames;

        /// <summary>
        /// Maximum consecutive missing slots that trigger Opus PLC.
        /// Beyond this the gap is treated as intentional sender silence.
        /// </summary>
        private const int MaxConsecutivePlc = 2;
        private int _consecutiveMissing;

        // Gate: the per-frame network pass and the audio-thread top-up must never decode at
        // the same time (the Opus decoder is not reentrant). CAS 0->1 to enter, write 0 to leave.
        private int _decoding;

        /// <summary>
        /// Number of accumulated 20 ms silence units (see <see cref="_silentUnits20ms"/>)
        /// after which we treat the stream as fully idle and rearm decoder + jitter on
        /// resume. Sized above any natural pause (room-noise floor keeps packets flowing
        /// during normal speech gaps) and above network jitter (absorbed by the jitter
        /// buffer), so the realistic trigger is a true sender-side mute.
        /// </summary>
        private const int IdleResetThresholdUnits = 10; // 200 ms

        /// <summary>
        /// Separate, much longer threshold for disabling the AudioSource. Disable/re-enable
        /// churn restarts Steam Audio's per-source state and clicks on every resume, so the
        /// source must survive whole inter-sentence pauses even though the decoder/jitter
        /// rearm fires at <see cref="IdleResetThresholdUnits"/>.
        /// </summary>
        private const int SourceIdleDisableUnits = 150; // 3 s

        // Latched so the idle reset fires once per idle cycle, not every drain tick
        // while the jitter buffer is filling back up.
        private bool _idleResetDone;

        // Anti-aliased polyphase resampler for the 48 kHz network stream -> output
        // device rate. Replaces a first-order linear interpolation that had no
        // anti-aliasing lowpass: whenever the output device wasn't 48 kHz (44.1 kHz
        // desktop devices, or a Bluetooth headset forced to 16 kHz SCO while the mic
        // is open) the linear path folded high-frequency voice content back into the
        // audible band as continuous grit/crackle. AudioResampler is a windowed-sinc
        // design that lowpasses to the output Nyquist before decimating.
        private OpenLipSync.Inference.Audio.AudioResampler _sincResampler;
        private int _sincResamplerOutRate = -1;

        // Resampler output produced beyond what a callback consumed, carried to the
        // next callback. Bridges the resampler's push-N / pull-M model to the fixed
        // per-callback frame count OnAudioFilterRead must fill.
        private float[] _resampleLeftover = new float[256];
        private int _resampleLeftoverCount;

        // Set on the main thread when the audio-thread resampler should restart
        // (re-enable / new utterance / rate change); consumed on the audio thread so
        // the delay line is only ever mutated there.
        private volatile bool _sincResetRequested;

        // Output gain smoothing — ramps the final per-sample gain across a callback
        // instead of stepping per callback (kills zippering on head movement /
        // DirectionalDampeningMultiplier changes).
        private float _lastGain;
        private bool _gainPrimed;

        // Silence<->audio envelope: short ramp on underrun entry/exit to avoid
        // step-discontinuity clicks when the decoded queue drains or refills.
        private const float FadeRampPerSample = 1f / 96f; // ~2 ms at 48 kHz
        private float _fadeEnvelope;
        private float _lastOutputSample;

        // Per-player volume, applied in the audio thread with smoothing instead of
        // via the Opus decoder's SetGain CTL (which changed state mid-stream).
        private volatile float _perPlayerVolume = 1f;

        /// <summary>Current per-player volume (your slider × block/mute), 0..1. Read by the audio debug gizmos.</summary>
        public float PerPlayerVolume => _perPlayerVolume;

        // Receive-side loudness normalisation. Owned and stepped ENTIRELY on the audio
        // thread inside ApplyGainAndWrite — the main thread only ever flips the volatile
        // enable flag, and the reset request, so nothing here needs a lock and nothing
        // allocates. Equalises talkers whose own client has AGC off or is on an old build.
        private readonly BasisMicrophoneAgc _normalizer = new BasisMicrophoneAgc();
        private BasisMicrophoneAgc.Settings _normalizerSettings = new BasisMicrophoneAgc.Settings
        {
            TargetRms = BasisMicrophoneAgc.DefaultTargetRms,
            MaxBoostDb = 18f,
            Attack01 = 0.75f,
            Release01 = 0.25f,
            Headroom = 0.98f,
        };
        private volatile bool _normalizeLoudness;
        private volatile bool _normalizerResetRequested;
        private float _normalizerAmp = 1f;

        /// <summary>Per-player: normalise this talker's incoming loudness. Set from the main thread.</summary>
        public bool NormalizeLoudness
        {
            get => _normalizeLoudness;
            set
            {
                if (_normalizeLoudness == value) return;
                _normalizeLoudness = value;
                _normalizerResetRequested = true;
            }
        }

        /// <summary>Gain the receive-side normaliser is currently applying, in dB. Read by the audio debug gizmos.</summary>
        public float NormalizerGainDb => _normalizeLoudness ? _normalizer.GainDb : 0f;

        // ==================== Packet arrival ====================

        public void Insert(AudioSegmentDataMessage msg)
        {
            VoiceBuffer.InsertEncoded(msg.SequenceNumber, msg.buffer, msg.LengthUsed, msg.TotalPlayedInSilence);
            OnEncodedFrame?.Invoke(msg);
        }

        // ==================== Decode pipeline ====================

        public void OnDecode(byte[] data, int length)
        {
#if UNITY_SERVER
            return;
#else
            if (HasAudioSource)
            {
                // Pass MaxFrameSize (the buffer's true capacity) as available space so a
                // 40 ms packet still decodes during in-flight transitions where this
                // receiver still thinks FrameSize == 960. Actual length comes back via
                // the return value.
                try
                {
                    pcmLength = decoder.Decode(data, length, pcmBuffer, RemoteOpusSettings.MaxFrameSize, false);
                    PushCurrentFrame(true);
                    OnDecodedFrame?.Invoke(pcmBuffer, pcmLength);
                }
                catch
                {
                    OnDecodePLC();
                }
            }
#endif
        }

        /// <summary>
        /// Decode path while the catch-up drain is active: after decoding (and feeding
        /// the recording tap, which keeps full content), a frame still in excess of the
        /// target either gets dropped entirely when near-silent, or shortened by one
        /// pitch period when voiced. Either way playback slides toward the live edge.
        /// </summary>
        private void OnDecodeCatchup(byte[] data, int length, int targetDepth)
        {
#if UNITY_SERVER
            return;
#else
            if (!HasAudioSource) return;
            try
            {
                pcmLength = decoder.Decode(data, length, pcmBuffer, RemoteOpusSettings.MaxFrameSize, false);
            }
            catch
            {
                OnDecodePLC();
                return;
            }
            OnDecodedFrame?.Invoke(pcmBuffer, pcmLength);

            // +1: the frame in hand is part of the standing audio.
            if (VoiceBuffer.StandingBufferedFrames + 1 > targetDepth + 1)
            {
                float peak = 0f;
                for (int i = 0; i < pcmLength; i++)
                {
                    float a = pcmBuffer[i] < 0f ? -pcmBuffer[i] : pcmBuffer[i];
                    if (a > peak) peak = a;
                }
                if (peak < QuietTrimPeak)
                {
                    // Inaudible content — drop the whole frame, soften both seam sides.
                    System.Threading.Interlocked.Increment(ref TrimmedQuietFrames);
                    VoiceBuffer.FadeOutNewestDecodedTail(48);
                    _fadeInNextDecodedFrame = true;
                    return;
                }
                int newLength = BasisVoiceTimeCompress.AccelerateInPlace(
                    pcmBuffer, pcmLength, RemoteOpusSettings.NetworkSampleRate, out int saved);
                if (saved > 0)
                {
                    pcmLength = newLength;
                    System.Threading.Interlocked.Increment(ref AcceleratedFrames);
                    AcceleratedSavedSamples += saved;
                }
                else if (peak < QuietTrimSoft)
                {
                    // No pitch structure AND quiet: background (room tone, codec
                    // noise floor) rather than speech — safe to drop while behind.
                    System.Threading.Interlocked.Increment(ref TrimmedQuietFrames);
                    VoiceBuffer.FadeOutNewestDecodedTail(48);
                    _fadeInNextDecodedFrame = true;
                    return;
                }
            }
            PushCurrentFrame(true);
#endif
        }

        /// <summary>
        /// Push <see cref="pcmBuffer"/> into the playback queue, applying a short
        /// fade-in first when the previous frame was removed by a trim or flush (the
        /// splice would otherwise step mid-waveform).
        /// </summary>
        private void PushCurrentFrame(bool hasRealAudio)
        {
            if (_fadeInNextDecodedFrame)
            {
                _fadeInNextDecodedFrame = false;
                int fade = pcmLength < 48 ? pcmLength : 48;
                float invFade = fade > 0 ? 1f / fade : 0f;
                for (int i = 0; i < fade; i++)
                    pcmBuffer[i] *= i * invFade;
            }
            VoiceBuffer.PushDecoded(pcmBuffer, pcmLength, hasRealAudio);
        }

        public void OnDecodePLC()
        {
#if UNITY_SERVER
            return;
#else
            if (HasAudioSource)
            {
                try
                {
                    pcmLength = decoder.Decode(null, 0, pcmBuffer, RemoteOpusSettings.FrameSize, false);
                    PushCurrentFrame(true);
                    OnDecodedFrame?.Invoke(pcmBuffer, pcmLength);
                }
                catch
                {
                    VoiceBuffer.PushDecoded(silentData, RemoteOpusSettings.FrameSize, false);
                }
            }
#endif
        }

        /// <summary>
        /// Reconstruct a missing frame using the FEC data embedded in the NEXT
        /// packet (requires the encoder to have OPUS_SET_INBAND_FEC enabled and
        /// <paramref name="data"/> to be the packet that follows the lost one in
        /// sequence). Falls back to PLC on decoder failure.
        /// </summary>
        public void OnDecodeFEC(byte[] data, int length)
        {
#if UNITY_SERVER
            return;
#else
            if (!HasAudioSource) return;
            try
            {
                pcmLength = decoder.Decode(data, length, pcmBuffer, RemoteOpusSettings.FrameSize, true);
                PushCurrentFrame(true);
                OnDecodedFrame?.Invoke(pcmBuffer, pcmLength);
            }
            catch
            {
                OnDecodePLC();
            }
#endif
        }

        /// <summary>
        /// Thread-safe: drains encoded packets and decodes them (Opus).
        /// Does NOT touch Unity AudioSource. Call ApplyAudioState() on main thread after.
        /// </summary>
        public void DrainAndDecodeThreadSafe()
        {
            if (System.Threading.Interlocked.CompareExchange(ref _decoding, 1, 0) != 0)
                return;
            try
            {
                DrainAndDecodeLocked();
            }
            finally
            {
                System.Threading.Volatile.Write(ref _decoding, 0);
            }
        }

        private void DrainAndDecodeLocked()
        {
            _lastDrainDecoded = false;

            // Mute creates a gap that Opus's own loss handling won't catch:
            // sender stops advancing sequence numbers, so the jitter buffer never
            // marks anything missing and the existing _consecutiveMissing reset
            // path doesn't fire. The audio thread, however, accumulates
            // _silentUnits20ms while the decoded queue is empty. Once that crosses
            // IdleResetThresholdUnits, reset the decoder (clears the CELT OLA tail
            // that would otherwise blend pre-mute audio into the first new frame)
            // and rearm the jitter buffer (so playback waits for InitialBufferDepth
            // packets again instead of releasing the first post-mute packet with
            // only 20 ms of audio queued). Latched so we only do this once per
            // idle cycle while packets refill.
#pragma warning disable CS0420 // Volatile.Read provides correct semantics for this volatile field
            if (System.Threading.Volatile.Read(ref _silentUnits20ms) >= IdleResetThresholdUnits
                && VoiceBuffer.EncodedBufferedCount == 0)
#pragma warning restore CS0420
            {
                if (!_idleResetDone)
                {
#if !UNITY_SERVER
                    if (decoder != null)
                    {
                        try { decoder.Ctl(OpusSharp.Core.GenericCTL.OPUS_RESET_STATE); }
                        catch (OpusSharp.Core.OpusException) { }
                    }
#endif
                    VoiceBuffer.RearmInitialBuffer();
                    RearmCount++;
                    _idleResetDone = true;
                }
            }
            else
            {
                _idleResetDone = false;
            }

            // ── Standing-latency control ──
            // Target is the adaptive jitter depth; anything buffered beyond it is
            // latency the listener is carrying for no benefit. Small excess drains
            // via quiet-frame trims and pitch-period compression (in the decode
            // path below); a hopeless backlog — a recovered network stall can pile
            // up 500+ ms — is flushed outright, keeping the newest audio and the
            // decoded runway so playback never gaps while the cursor jumps.
            int targetDepth = VoiceBuffer.InitialBufferDepth;
            int standingNow = VoiceBuffer.StandingBufferedFrames;
            if (!_catchupActive && standingNow > targetDepth + CatchupSlackFrames)
                _catchupActive = true;
            else if (_catchupActive && standingNow <= targetDepth + 1)
                _catchupActive = false;

            int flushBound = targetDepth + FlushSlackFrames;
            if (flushBound < FlushMinFrames) flushBound = FlushMinFrames;
            if (standingNow > flushBound)
            {
                int skipped = VoiceBuffer.SkipForwardEncoded(standingNow - (targetDepth + CatchupSlackFrames));
                if (skipped > 0)
                {
                    FlushedPackets += skipped;
                    // The skipped content is gone from the decoder's history — reset
                    // it, and soften both sides of the seam.
                    VoiceBuffer.FadeOutNewestDecodedTail(96);
                    _fadeInNextDecodedFrame = true;
                    _consecutiveMissing = 0;
                    _starveEpisode = false;
#if !UNITY_SERVER
                    if (decoder != null)
                    {
                        try { decoder.Ctl(OpusSharp.Core.GenericCTL.OPUS_RESET_STATE); }
                        catch (OpusSharp.Core.OpusException) { }
                    }
#endif
                }
            }

            while (true)
            {
                // Backpressure: don't decode faster than the audio thread can drain.
                // If we did, PushDecoded would silently drop the oldest frame (= click).
                if (VoiceBuffer.DecodedFrameCount >= VoiceBuffer.DecodedFrameCapacity)
                    break;

                if (!VoiceBuffer.TryConsumeEncoded(out byte[] data, out int length, out byte silenceUnits, out bool isMissing))
                {
                    // Starve bridge: the stream is live but nothing newer has arrived
                    // and the decoded runway is nearly dry. Bridge with Opus PLC —
                    // waveform extrapolation — instead of fading to silence, up to
                    // ~120 ms. A late burst then resumes seamlessly (the buffered
                    // content plays after the bridge and the catch-up drain reclaims
                    // the added lag); a genuine sender mute just gets a natural
                    // trail-off, since the pre-mute hangover is near-silent anyway.
                    if (VoiceBuffer.Started && !VoiceBuffer.IsHoldingForLatePacket
                        && VoiceBuffer.DecodedFrameCount <= 2)
                    {
                        // Entry needs evidence the stream actually STOPPED: an empty
                        // ring right after an arrival is just the normal cadence of a
                        // shallow queue (e.g. the release moment after a re-preroll),
                        // and bridging there would mis-latch an underrun mid-speech.
                        int frameMs = RemoteOpusSettings.FrameSize * 1000 / RemoteOpusSettings.NetworkSampleRate;
                        if (!_starveEpisode
                            && VoiceBuffer.EncodedBufferedCount == 0
                            && VoiceBuffer.ReceivedSinceStart >= targetDepth
                            && VoiceBuffer.MsSinceLastArrival() > frameMs * 5 / 2)
                        {
                            _starveEpisode = true;
                            _starvePlcBudget = StarvePlcMaxFrames;
                            // Latch the underrun classification now (resolved by the
                            // next arrival's silenceUnits) and require a short refill
                            // before more audio releases, so the resume doesn't ride
                            // a 1-packet queue.
                            VoiceBuffer.NoteUnderrun();
                        }
                        if (_starveEpisode && _starvePlcBudget > 0)
                        {
                            _starvePlcBudget--;
                            System.Threading.Interlocked.Increment(ref StarvePlcCount);
                            OnDecodePLC();
                            _lastDrainDecoded = true;
                        }
                    }
                    break;
                }

                if (isMissing)
                {
                    _consecutiveMissing++;
                    if (_consecutiveMissing <= MaxConsecutivePlc)
                    {
                        // Try Opus FEC first: if the next-in-sequence packet is
                        // already buffered, it carries a redundant copy of the
                        // missing frame. Falls back to PLC when FEC data isn't
                        // available yet (late next packet, or burst loss).
                        if (VoiceBuffer.TryPeekNextEncoded(out byte[] fecData, out int fecLength))
                        {
                            System.Threading.Interlocked.Increment(ref FecRecoveredCount);
                            OnDecodeFEC(fecData, fecLength);
                        }
                        else
                        {
                            System.Threading.Interlocked.Increment(ref PlcCount);
                            OnDecodePLC();
                        }
                        // FEC/PLC push audio into the decoded queue, so this counts
                        // as a meaningful drain that downstream state (audio source
                        // enable/play) may need to react to.
                        _lastDrainDecoded = true;
                    }
                    else
                    {
                        // Pure silence injection: we just bump a counter and don't
                        // push anything to the decoded queue, so there's nothing
                        // for ApplyAudioState to react to. Don't set the flag.
                        System.Threading.Interlocked.Increment(ref SilenceInjectedCount);
                    }
                }
                else
                {
                    if (_consecutiveMissing > MaxConsecutivePlc)
                    {
                        // After a long gap we don't want Opus's PLC history to
                        // influence the next real frame (= transient pop), but we
                        // also don't want to wipe the decoded queue (= loud click
                        // from dropping up to 160 ms of queued audio). Reset the
                        // decoder's internal state instead.
#if !UNITY_SERVER
                        if (decoder != null)
                        {
                            try { decoder.Ctl(OpusSharp.Core.GenericCTL.OPUS_RESET_STATE); }
                            catch (OpusSharp.Core.OpusException) { }
                        }
#endif
                    }
                    _consecutiveMissing = 0;
                    _starveEpisode = false;
                    if (silenceUnits > 0)
                    {
                        int localUnits = System.Threading.Interlocked.Exchange(ref _silentUnits20ms, 0);
                        int missing = silenceUnits - localUnits;
                        if (missing > 0)
                            System.Threading.Interlocked.Add(ref SilenceInjectedCount, missing);
                    }

                    // Preemptive expand: when the adaptive target has grown above the
                    // standing depth, a continuous talker never re-prerolls, so the
                    // extra protection would stay theoretical — the deadline-hold and
                    // gap headroom only exist in audio actually buffered. Slip one PLC
                    // frame in ahead of the real one (rate-limited, ~+40 ms/s) until
                    // standing meets the target. The complement of the catch-up drain.
                    _framesSinceExpand++;
                    int standingWithFrame = VoiceBuffer.StandingBufferedFrames + 1;
                    int expandSpacing = targetDepth - standingWithFrame >= 3
                        ? ExpandMinSpacingFrames / 2   // far behind target: build protection faster
                        : ExpandMinSpacingFrames;
                    if (!_catchupActive && silenceUnits == 0
                        && _framesSinceExpand >= expandSpacing
                        && VoiceBuffer.ReceivedSinceStart >= targetDepth
                        && standingWithFrame < targetDepth - 1
                        && VoiceBuffer.DecodedFrameCount < VoiceBuffer.DecodedFrameCapacity - 1)
                    {
                        _framesSinceExpand = 0;
                        System.Threading.Interlocked.Increment(ref ExpandInsertedFrames);
                        OnDecodePLC();
                    }

                    if (_catchupActive)
                        OnDecodeCatchup(data, length, targetDepth);
                    else
                        OnDecode(data, length);
                    _lastDrainDecoded = true;
                }
            }
        }

        // Set by DrainAndDecodeThreadSafe, read by ApplyAudioState on main thread.
        internal volatile bool _lastDrainDecoded;

        // Cached AudioSource state. Unity's audioSource.enabled / isPlaying getters
        // cross the managed/native boundary (and isPlaying queries the FMOD channel),
        // so polling them every receiver every tick costs ~µs per receiver. We mirror
        // the state here and only touch the Unity API on transitions.
        private volatile bool _audioEnabled;
        private bool _audioPlaying;

        /// <summary>
        /// True when a main-thread <see cref="ApplyAudioState"/> is owed: the source's
        /// enabled state disagrees with whether real audio is queued. Safe to read off-thread.
        /// </summary>
        public bool NeedsAudioStateApply => HasAudioSource && ShouldAudioBeActive() != _audioEnabled;

        /// <summary>
        /// True while the voice source is enabled (and so OnAudioFilterRead is running
        /// the level meter). Read by debug gizmos to zero a meter that would otherwise
        /// freeze at its last value once the source is disabled between utterances.
        /// </summary>
        public bool IsAudioActive => HasAudioSource && _audioEnabled;

        /// <summary>
        /// Whether the AudioSource should currently be enabled. True while real audio is
        /// queued, and for a short hang afterwards so brief inter-word/sentence gaps don't
        /// disable+re-enable the source — that churn restarts Steam Audio's per-source
        /// state and clicks on every resume. We still disable once the stream has been
        /// fully silent for IdleResetThresholdUnits (the same point the decoder resets), so
        /// genuinely idle players don't keep a live spatialized voice source — which is
        /// what matters at 1000+ players.
        /// </summary>
        private bool ShouldAudioBeActive()
        {
            if (VoiceBuffer.HasRealAudio) return true;
#pragma warning disable CS0420 // Volatile.Read provides correct semantics for this volatile field
            return System.Threading.Volatile.Read(ref _silentUnits20ms) < SourceIdleDisableUnits;
#pragma warning restore CS0420
        }

        /// <summary>
        /// Main-thread only: reconciles AudioSource enabled/playing with the decoded queue.
        /// No-op when already in the right state.
        /// </summary>
        public void ApplyAudioState()
        {
            if (!HasAudioSource) return;
            if (ShouldAudioBeActive())
            {
                EnableAndEnsurePlaying();
            }
            else
            {
                DisableAudio();
            }
        }

        public void DrainAndDecode()
        {
            DrainAndDecodeThreadSafe();
            ApplyAudioState();
        }

        // ==================== AudioSource management ====================

        public void AudioSourceSet()
        {
            BasisDeviceManagement.EnqueueOnMainThread(() =>
            {
                if (!HasAudioSource) return;
                if (ShouldAudioBeActive())
                    EnableAndEnsurePlaying();
                else
                    DisableAudio();
            });
        }

        private void EnableAndEnsurePlaying()
        {
            if (!_audioEnabled)
            {
                // Audio-thread state (fade envelope, resampler carry, last
                // output sample) survives the disable cycle. If the IsEmpty
                // fade-down didn't get to run a final callback before
                // DisableAudio fired, _fadeEnvelope can stay at 1, and the
                // first sample of the new utterance would step from silence
                // to full amplitude. Reset before re-enable so each speech
                // onset fades in from 0 with no stale interpolation carry.
                ResetAudioThreadState();
                audioSource.enabled = true;
                _audioEnabled = true;
                visemeDriver.AudioSourceInactive = false;
            }
            if (!_audioPlaying)
            {
                audioSource.Play();
                _audioPlaying = true;
            }
        }

        private void DisableAudio()
        {
            if (_audioEnabled)
            {
                audioSource.enabled = false;
                _audioEnabled = false;
                visemeDriver.AudioSourceInactive = true;
                // Disabling stops audio processing; treat as not-playing so the next
                // enable path calls Play() again.
                _audioPlaying = false;
            }
        }

        public void ApplyRangeData(float Distance)
        {
            if (!HasAudioSource) return;
            audioSource.maxDistance = Distance;
            // Unity evaluates a custom rolloff at distance/maxDistance, so the curve
            // is expressed in units of the hearing range. A range change therefore
            // rescales the whole shape — a 10 m range would move the metre where the
            // inverse law starts to 40 cm — and the model-generated curves have to be
            // rebuilt against the new range rather than stretched with it.
            SettingsProviderRemoteAudio.ApplyDistanceCurves(this);
        }

        public async Task LoadAudioSource(BasisNetworkReceiver networkedPlayer, Transform MouthParent, float MaxDistance)
        {
            if (AudioSourceTransform == null || audioSource == null)
            {
                AudioSourceTransform = BasisAudioRemoteSource.RequestAudio(MouthParent).transform;
                AudioSourceTransform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
#if UNITY_EDITOR
                AudioSourceTransform.name = $"[Audio] {BasisNetworkReceiver.Player.DisplayName}";
#endif
                audioSource = BasisHelpers.GetOrAddComponent<AudioSource>(AudioSourceTransform.gameObject);
                audioSource.clip = BasisAudioClipPool.Get(networkedPlayer.playerId);
                audioSource.loop = true;
                audioSource.spatialize = true;
                audioSource.spatializePostEffects = true;
                audioSource.enabled = true;
                audioSource.Play();
                audioSource.maxDistance = MaxDistance;
            }
            else if (!audioSource.enabled)
            {
                audioSource.enabled = true;
                audioSource.Play();
            }
            _audioEnabled = true;
            _audioPlaying = true;
            HasAudioSource = true;
            AvatarChanged(networkedPlayer, false);

            SettingsProviderRemoteAudio.ApplyRemoteAudioTo(this);
            ChangeRemotePlayersVolumeSettings(1);

            try
            {
                var settings = await BasisPlayerSettingsManager.RequestPlayerSettings(networkedPlayer.Player.UUID);
                // BasisAudioReceiver only ever wraps a remote player; use the pre-cast
                // RemotePlayer field instead of repeating `is BasisRemotePlayer`.
                bool tempBlocked = networkedPlayer.RemotePlayer.TempBlocked;
                bool muted = settings.IsBlocked || tempBlocked;
                NormalizeLoudness = settings.NormalizeLoudness;
                ChangeRemotePlayersVolumeSettings(muted ? 0f : settings.VolumeLevel);
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"{ex}", BasisDebug.LogTag.Remote);
            }
        }

        /// <summary>
        /// Swap the AudioSource's clip for a freshly-pooled one, in-place. The
        /// existing clip is destroyed (not pooled) so a stale-sized clip can't
        /// re-enter circulation when the pool's <see cref="BasisAudioClipPool.ClipBufferScalar"/>
        /// has just changed. AudioSource, transform, viseme driver, and Steam Audio
        /// component are all preserved — only the underlying clip is replaced.
        /// </summary>
        public void ReloadClip()
        {
            if (audioSource == null || BasisNetworkReceiver == null) return;

            audioSource.Stop();

            AudioClip oldClip = audioSource.clip;
            audioSource.clip = null;
            if (oldClip != null) AudioClip.Destroy(oldClip);

            audioSource.clip = BasisAudioClipPool.Get(BasisNetworkReceiver.playerId);
            audioSource.loop = true;
            audioSource.Play();
            _audioPlaying = true;
            _audioEnabled = audioSource.enabled;
            visemeDriver.AudioSourceInactive = !_audioEnabled;
        }

        public void UnloadAudioSource()
        {
            HasAudioSource = false;
            _audioEnabled = false;
            _audioPlaying = false;
            if (visemeDriver != null)
            {
                visemeDriver.TrackedAudioSource = null;
                visemeDriver.AudioSourceInactive = true;
            }
            if (audioSource != null && audioSource.clip != null)
            {
                audioSource.Stop();
                BasisAudioClipPool.Return(audioSource.clip);
            }
            if (AudioSourceTransform != null)
                BasisAudioRemoteSource.Return(AudioSourceTransform.gameObject);
            audioSource = null;
            AudioSourceTransform = null;
            BasisRemoteVisemeAudioDriver = null;
        }

        // ==================== Initialization / lifecycle ====================

        private static void LogOutputRateOnce()
        {
            if (_loggedOutputRate) return;
            _loggedOutputRate = true;
            if (outputSampleRate != RemoteOpusSettings.NetworkSampleRate)
            {
                BasisDebug.Log($"[Voice] Output device runs at {outputSampleRate} Hz; voice streams at {RemoteOpusSettings.NetworkSampleRate} Hz, so playback is resampled (anti-aliased polyphase). A non-48000 device rate — 44100, or 16000 on a Bluetooth headset in headset/SCO mode while the mic is open — is the usual source of resampler-side voice artifacts.", BasisDebug.LogTag.Voice);
            }
            else
            {
                BasisDebug.Log($"[Voice] Output device runs at {outputSampleRate} Hz, matching the voice stream — no resampling.", BasisDebug.LogTag.Voice);
            }
        }

        public void Initialize(BasisNetworkReceiver networkedPlayer)
        {
#if UNITY_SERVER
            return;
#else
            InitializeWithOutputRate(networkedPlayer, AudioSettings.outputSampleRate);
#endif
        }

        /// <summary>
        /// Testability seam: same as <see cref="Initialize"/> but with the output device
        /// rate passed in, so headless harnesses can construct the receiver without
        /// Unity's audio engine (AudioSettings is an engine-internal call).
        /// </summary>
        public void InitializeWithOutputRate(BasisNetworkReceiver networkedPlayer, int deviceOutputRate)
        {
#if UNITY_SERVER
            return;
#else
            outputSampleRate = deviceOutputRate;
            silentData ??= new float[RemoteOpusSettings.MaxFrameSize];
            BasisNetworkReceiver = networkedPlayer;
            LogOutputRateOnce();

#if UNITY_IOS && !UNITY_EDITOR
            // iOS requires statically linked Opus library
            decoder = new OpusSharp.Core.Static.OpusDecoder(RemoteOpusSettings.NetworkSampleRate, RemoteOpusSettings.Channels);
#else
            decoder = new OpusSharp.Core.Dynamic.OpusDecoder(RemoteOpusSettings.NetworkSampleRate, RemoteOpusSettings.Channels);
#endif
#endif
        }

        public void OnDestroy()
        {
#if !UNITY_SERVER
            // Wait out any in-flight audio-thread decode before freeing the native decoder.
            while (System.Threading.Interlocked.CompareExchange(ref _decoding, 1, 0) != 0)
                System.Threading.Thread.SpinWait(64);
            if (decoder != null)
            {
                decoder.Dispose();
                decoder = null;
            }
            System.Threading.Volatile.Write(ref _decoding, 0);
            _sincResampler?.Dispose();
            _sincResampler = null;
            _sincResamplerOutRate = -1;
#endif
            UnloadAudioSource();
        }

        public void AvatarChanged(BasisNetworkPlayer networkedPlayer, bool WasFromCalibration)
        {
#if UNITY_SERVER
            return;
#endif
            if (audioSource == null) return;
            if (networkedPlayer == null)
            {
                BasisDebug.LogError("networkedPlayer did not exist", BasisDebug.LogTag.Voice);
                return;
            }
            if (networkedPlayer.Player == null)
            {
                BasisDebug.LogError("networkedPlayer.Player did not exist", BasisDebug.LogTag.Voice);
                return;
            }
            visemeDriver.TryInitialize(networkedPlayer.Player);
            visemeDriver.TrackedAudioSource = audioSource;
            visemeDriver.AudioSourceInactive = !audioSource.enabled;

            if (BasisRemoteVisemeAudioDriver == null)
                BasisRemoteVisemeAudioDriver = BasisHelpers.GetOrAddComponent<BasisRemoteAudioDriver>(audioSource.gameObject);
            BasisRemoteVisemeAudioDriver.BasisAudioReceiver = this;
            BasisRemoteVisemeAudioDriver.Initialize(visemeDriver);
        }

        public void StopAudio()
        {
#if UNITY_SERVER
            return;
#endif
            UnloadAudioSource();
        }

        public void InitializeForPlayback()
        {
            const int BufferSize = 1024;
            _inputScratch = new float[BufferSize];
            _resampleScratch = new float[BufferSize];
            _cachedOutputRate = outputSampleRate;
            _resampleRatio = (float)RemoteOpusSettings.NetworkSampleRate / _cachedOutputRate;
            WarmResampler();
            ResetAudioThreadState();
        }

        public void StartAudio(float MaxDistance)
        {
            const int BufferSize = 1024;
            if (_inputScratch == null || _inputScratch.Length != BufferSize)
                _inputScratch = new float[BufferSize];
            else
                _inputScratch.AsSpan().Clear();

            if (_resampleScratch == null || _resampleScratch.Length != BufferSize)
                _resampleScratch = new float[BufferSize];
            else
                _resampleScratch.AsSpan().Clear();

            _cachedOutputRate = outputSampleRate;
            _resampleRatio = (float)RemoteOpusSettings.NetworkSampleRate / _cachedOutputRate;
            WarmResampler();
            ResetAudioThreadState();
#if UNITY_SERVER
            return;
#endif
            if (BasisNetworkReceiver == null)
            {
                BasisDebug.LogError("Missing Network Receiver Audio Receiver!", BasisDebug.LogTag.Remote);
                return;
            }
            if (BasisNetworkReceiver.RemotePlayer == null)
            {
                BasisDebug.LogError("RemotePlayer was null in Audio Receiver", BasisDebug.LogTag.Remote);
                return;
            }
            if (BasisNetworkReceiver.RemotePlayer.MouthTransform == null)
            {
                BasisDebug.LogError("Mouth Transform Does not exist in Audio Receiver!", BasisDebug.LogTag.Remote);
                return;
            }
            LoadAudioSource(MaxDistance);
        }

        public async void LoadAudioSource(float MaxDistance)
        {
            await LoadAudioSource(BasisNetworkReceiver, BasisNetworkReceiver.RemotePlayer.MouthTransform, MaxDistance);
        }

        // ==================== Volume ====================

        public void ChangeRemotePlayersVolumeSettings(float volume = 1.0f)
        {
            if (BasisNetworkReceiver != null && BasisNetworkReceiver.RemotePlayer != null && BasisNetworkReceiver.RemotePlayer.IsEffectivelyBlocked)
            {
                volume = 0f;
            }

            // Apply per-player volume in the audio thread (smoothed via _lastGain ramp).
            // Avoids poking the Opus decoder's SetGain CTL, which changes state mid-stream.
            _perPlayerVolume = Mathf.Max(0f, volume);

            if (audioSource == null)
            {
                return;
            }
            // Unity AudioSource stays at unit gain; actual attenuation happens per-sample in OnAudioFilterRead.
            audioSource.volume = 1f;
        }

        // ==================== Audio thread callback ====================

        private void ResetAudioThreadState()
        {
            // Defer the resampler restart to the audio thread (EnsureSincResampler);
            // its delay line must not be mutated from the main thread mid-callback.
            _sincResetRequested = true;
            _lastGain = 0f;
            _gainPrimed = false;
            _fadeEnvelope = 0f;
            _lastOutputSample = 0f;
            _toneShaper.Reset();
        }

        /// <summary>
        /// Applies the orientation-dependent tone shaping — talker mouth directivity
        /// and listener head shadow — to the already-written output buffer.
        ///
        /// Deliberately NOT folded into <see cref="ApplyGainAndWrite"/>: the viseme
        /// driver reads this same buffer straight after the receiver writes it, and
        /// tilting the spectrum before lip-sync sees it would make a talker's mouth
        /// shapes depend on which way their head is pointing. So the driver calls
        /// this after <c>ProcessAudioSamples</c> instead. The shaper only ever cuts,
        /// so running it downstream of the soft limiter cannot reintroduce clipping.
        /// </summary>
        public void ApplySpatialTone(float[] data, int channels, int length)
        {
            if (data == null || channels <= 0) return;
            int rate = outputSampleRate > 0 ? outputSampleRate : RemoteOpusSettings.NetworkSampleRate;
            _toneShaper.Process(data, channels, length / channels, rate,
                                DirectivityShelfDb, ConeShelfDb);
        }

        public void OnAudioFilterRead(float[] data, int channels, int length)
        {
            // Top up the decoded queue on the audio clock (not only the per-frame network pass)
            // so a render-frame hitch can't starve this read and underrun into the fade-to-silence.
            int spins = 0;
            while (System.Threading.Interlocked.CompareExchange(ref _decoding, 1, 0) != 0)
            {
                if (++spins > 256)
                {
                    spins = -1;
                    break;
                }
                System.Threading.Thread.SpinWait(32);
            }
            if (spins >= 0)
            {
                try
                {
                    DrainAndDecodeLocked();
                }
                finally
                {
                    System.Threading.Volatile.Write(ref _decoding, 0);
                }
            }

            int frames = length / channels;
            double msThisCallback = 1000.0 * frames / outputSampleRate;
            Span<float> output = data.AsSpan(0, length);

            if (VoiceBuffer.IsEmpty)
            {
                // No signal this callback — bleed the level meter toward silence.
                SourcePeak *= MeterReleaseFactor;

                if (_fadeEnvelope > 0f)
                {
                    VoiceBuffer.NoteUnderrun();
                }

                // Fade the last produced sample down toward zero over ~2 ms instead
                // of an abrupt step to silence. Once the envelope hits 0 the output
                // is just zero (no click).
                if (_fadeEnvelope > 0f)
                {
                    float env = _fadeEnvelope;
                    float last = _lastOutputSample;
                    int idx = 0;
                    for (int f = 0; f < frames; f++)
                    {
                        env -= FadeRampPerSample;
                        if (env < 0f) env = 0f;
                        float sample = last * env;
                        for (int c = 0; c < channels; c++)
                            output[idx++] = sample;
                    }
                    _fadeEnvelope = env;
                    if (env <= 0f) _lastOutputSample = 0f;
                }
                else
                {
                    output.Clear();
                }

                _silentUsAccum += (long)(msThisCallback * 1000.0);
                int newUnits = (int)(_silentUsAccum / 20000L);
                if (newUnits > 0)
                {
                    System.Threading.Interlocked.Add(ref _silentUnits20ms, newUnits);
                    _silentUsAccum -= newUnits * 20000L;
                }
                return;
            }

            System.Threading.Interlocked.Exchange(ref _silentUnits20ms, 0);
            _silentUsAccum = 0L;

            if (_cachedOutputRate != outputSampleRate)
            {
                _cachedOutputRate = outputSampleRate;
                _resampleRatio = (float)RemoteOpusSettings.NetworkSampleRate / _cachedOutputRate;
            }

            if (RemoteOpusSettings.NetworkSampleRate == _cachedOutputRate)
            {
                ProcessNoResample(output, frames, channels);
            }
            else
            {
                ProcessResample(output, frames, channels);
            }
        }

        private void EnsureCapacity(ref float[] buf, int needed)
        {
            if (buf.Length < needed)
            {
                int newSize = 1;
                while (newSize < needed) newSize <<= 1;
                buf = new float[newSize];
            }
        }

        private void ProcessNoResample(Span<float> data, int frames, int channels)
        {
            EnsureCapacity(ref _inputScratch, frames);
            int read = VoiceBuffer.ReadPcm(_inputScratch, frames);
            bool underrun = read < frames;

            Span<float> source = _inputScratch.AsSpan(0, frames);
            if (underrun)
            {
                FadeFillUnderrun(source, read, frames);
            }

            ApplyGainAndWrite(source, data, frames, channels);

            if (underrun)
            {
                // Faded out mid-callback to silence; restart the envelope so the
                // next callback with real audio fades back in instead of stepping
                // up from 0 to full amplitude.
                VoiceBuffer.NoteUnderrun();
                _fadeEnvelope = 0f;
                _lastOutputSample = 0f;
            }
        }

        /// <summary>
        /// Soft peak limiter: transparent below ±0.8, then a smooth knee that
        /// asymptotically approaches ±1 instead of hard-clamping. Per-player volume can
        /// boost up to 1.5×, which pushes loud syllables past full scale; a hard clamp
        /// there is audible buzz/crackle, so round the peaks instead. C1-continuous at the
        /// knee (slope 1) and never exceeds ±1, so it can't overshoot the output buffer.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float SoftLimit(float x)
        {
            const float t = 0.8f;        // linear region
            const float range = 1f - t;  // headroom to the ±1 ceiling
            float ax = x < 0f ? -x : x;
            if (ax <= t) return x;
            if (ax >= 8f) return x < 0f ? -1f : 1f; // guard Inf/huge -> NaN in the ratio
            float over = ax - t;
            float comp = t + range * (over / (over + range));
            return x < 0f ? -comp : comp;
        }

        /// <summary>
        /// Smooths the boundary of a partial buffer read so the source goes from
        /// the last real sample to 0 over a short ramp instead of stepping. Used
        /// when ReadPcm returns fewer samples than requested (queue ran dry mid
        /// callback) — a hard zero-fill at the boundary would be an audible click.
        /// </summary>
        private static void FadeFillUnderrun(Span<float> buf, int read, int total)
        {
            int fillLen = total - read;
            if (fillLen <= 0) return;
            int fadeLen = fillLen < 96 ? fillLen : 96; // ~2 ms at 48 kHz
            float lastSample = read > 0 ? buf[read - 1] : 0f;
            float invFade = 1f / fadeLen;
            for (int i = 0; i < fadeLen; i++)
            {
                buf[read + i] = lastSample * (1f - i * invFade);
            }
            if (fillLen > fadeLen)
            {
                buf.Slice(read + fadeLen, fillLen - fadeLen).Clear();
            }
        }

        private void ProcessResample(Span<float> data, int frames, int channels)
        {
            EnsureSincResampler();
            EnsureCapacity(ref _resampleScratch, frames);
            Span<float> resampled = _resampleScratch.AsSpan(0, frames);

            if (_sincResampler == null)
            {
                // Output rate not usable yet — emit silence rather than risk a
                // wrong-rate resampler. Effectively never hit post-init.
                resampled.Clear();
                ApplyGainAndWrite(resampled, data, frames, channels);
                return;
            }

            int produced = 0;
            bool underrun = false;

            // 1) Consume anything the resampler over-produced last callback.
            if (_resampleLeftoverCount > 0)
            {
                int take = Math.Min(_resampleLeftoverCount, frames);
                _resampleLeftover.AsSpan(0, take).CopyTo(resampled);
                int rem = _resampleLeftoverCount - take;
                if (rem > 0)
                    _resampleLeftover.AsSpan(take, rem).CopyTo(_resampleLeftover.AsSpan(0, rem));
                _resampleLeftoverCount = rem;
                produced = take;
            }

            // 2) Pull decoded PCM and run it through the polyphase filter until the
            //    callback is full. The resampler keeps its own delay line + phase, so
            //    variable-size feeds give the same continuous output as one big feed.
            double ratio = _resampleRatio > 0f ? _resampleRatio : 1.0;
            int guard = 0;
            while (produced < frames && guard++ < 8)
            {
                int need = frames - produced;
                int inWant = (int)Math.Ceiling(need * ratio) + 2;
                EnsureCapacity(ref _inputScratch, inWant);
                int read = VoiceBuffer.ReadPcm(_inputScratch, inWant);
                if (read <= 0)
                {
                    underrun = true;
                    break;
                }

                ReadOnlySpan<float> emitted = _sincResampler.Resample(_inputScratch.AsSpan(0, read));
                int copy = Math.Min(emitted.Length, frames - produced);
                if (copy > 0)
                {
                    emitted.Slice(0, copy).CopyTo(resampled.Slice(produced));
                    produced += copy;
                }

                int surplus = emitted.Length - copy;
                if (surplus > 0)
                {
                    int needCap = _resampleLeftoverCount + surplus;
                    if (needCap > _resampleLeftover.Length)
                    {
                        int cap = _resampleLeftover.Length;
                        while (cap < needCap) cap <<= 1;
                        Array.Resize(ref _resampleLeftover, cap); // preserves existing samples
                    }
                    emitted.Slice(copy).CopyTo(_resampleLeftover.AsSpan(_resampleLeftoverCount));
                    _resampleLeftoverCount += surplus;
                }

                if (read < inWant)
                {
                    // Decoded queue ran dry mid-callback.
                    underrun = true;
                    break;
                }
            }

            // 3) Smooth the boundary to silence if the queue underran.
            if (produced < frames)
            {
                underrun = true;
                FadeFillUnderrun(resampled, produced, frames);
            }

            ApplyGainAndWrite(resampled, data, frames, channels);

            if (underrun)
            {
                VoiceBuffer.NoteUnderrun();
                _fadeEnvelope = 0f;
                _lastOutputSample = 0f;
            }
        }

        /// <summary>
        /// (Re)creates the polyphase resampler when the output device rate changes and
        /// services a pending delay-line reset. Runs on the audio thread so the
        /// resampler is only ever mutated there; the main thread just sets
        /// <see cref="_sincResetRequested"/>.
        /// </summary>
        private void EnsureSincResampler()
        {
            int outRate = _cachedOutputRate;
            if (outRate <= 0)
            {
                return;
            }

            if (_sincResampler == null || _sincResamplerOutRate != outRate)
            {
                _sincResampler?.Dispose();
                // 32 taps / 512 phases: anti-aliased quality at a fraction of the
                // per-sample cost of the lipsync default (48/1024). This runs on the
                // audio thread for every audible remote speaker, so tap count is the
                // dominant cost; 32 still gives strong image rejection across the
                // voice band.
                _sincResampler = new OpenLipSync.Inference.Audio.AudioResampler(
                    RemoteOpusSettings.NetworkSampleRate, outRate, filterTaps: 32, numPhases: 512);
                _sincResamplerOutRate = outRate;
                _resampleLeftoverCount = 0;
                _sincResetRequested = false;
                return;
            }

            if (_sincResetRequested)
            {
                _sincResetRequested = false;
                _sincResampler.Reset();
                _resampleLeftoverCount = 0;
            }
        }

        /// <summary>
        /// Main-thread warm-up: builds and caches the polyphase coefficient table for
        /// the current output rate so the first audio-thread resample doesn't pay the
        /// table build. Uses a throwaway instance (no shared state with the audio
        /// thread's resampler). No-op when the device already runs at the network rate.
        /// </summary>
        private void WarmResampler()
        {
            int outRate = _cachedOutputRate;
            if (outRate <= 0 || outRate == RemoteOpusSettings.NetworkSampleRate)
            {
                return;
            }
            try
            {
                using (new OpenLipSync.Inference.Audio.AudioResampler(
                    RemoteOpusSettings.NetworkSampleRate, outRate, filterTaps: 32, numPhases: 512))
                {
                }
            }
            catch (Exception ex)
            {
                BasisDebug.LogWarning($"[Voice] resampler warm-up failed: {ex.Message}", BasisDebug.LogTag.Voice);
            }
        }

        /// <summary>
        /// Applies per-sample gain (smoothly ramped over the callback) and the
        /// underrun fade envelope to <paramref name="source"/>, writing the
        /// result into the interleaved Unity output buffer. Also updates
        /// <see cref="_lastOutputSample"/> so a subsequent underrun callback
        /// can fade out from the last real sample instead of stepping to zero.
        /// </summary>
        private void ApplyGainAndWrite(ReadOnlySpan<float> source, Span<float> data, int frames, int channels)
        {
            float targetGain = DirectionalDampeningMultiplier * SMModuleAudio.ActiveMainVolume * _perPlayerVolume;
            if (!_gainPrimed)
            {
                _lastGain = targetGain;
                _gainPrimed = true;
            }
            float gainStep = (targetGain - _lastGain) / Mathf.Max(1, frames);
            float gain = _lastGain;
            float env = _fadeEnvelope;

            float normStart = _normalizerAmp;
            float normEnd = StepNormalizer(source, frames);
            float normStep = (normEnd - normStart) / Mathf.Max(1, frames);
            float norm = normStart;

            int idx = 0;
            float lastWritten = 0f;
            float blockPeak = 0f;
            for (int f = 0; f < frames; f++)
            {
                if (env < 1f)
                {
                    env += FadeRampPerSample;
                    if (env > 1f) env = 1f;
                }
                float raw = source[f];
                float absRaw = raw < 0f ? -raw : raw;
                if (absRaw > blockPeak) blockPeak = absRaw;
                float sample = SoftLimit(raw * norm * gain * env);
                for (int c = 0; c < channels; c++)
                    data[idx++] = sample;
                lastWritten = sample;
                gain += gainStep;
                norm += normStep;
            }

            // Peak meter: instant attack to this callback's loudest sample, otherwise
            // decay. Captures the raw signal level (pre-gain) so the gizmo shows how
            // loud the speaker is independent of the attenuation applied to them.
            SourcePeak = blockPeak > SourcePeak ? blockPeak : SourcePeak * MeterReleaseFactor;

            _lastGain = targetGain;
            _normalizerAmp = normEnd;
            _fadeEnvelope = env;
            _lastOutputSample = lastWritten;
        }

        /// <summary>
        /// Advances the receive-side normaliser by one audio callback and returns the gain
        /// to ramp to. Audio thread only. Measures the decoded signal BEFORE per-player
        /// volume and distance dampening, so the estimate tracks how loud the talker
        /// actually is rather than how loud they are to you right now.
        /// </summary>
        private float StepNormalizer(ReadOnlySpan<float> source, int frames)
        {
            if (_normalizerResetRequested)
            {
                _normalizerResetRequested = false;
                _normalizer.Reset();
                _normalizerAmp = 1f;
            }

            if (!_normalizeLoudness || frames <= 0)
            {
                return 1f;
            }

            double sumSq = 0.0;
            float peak = 0f;
            for (int f = 0; f < frames; f++)
            {
                float v = source[f];
                sumSq += (double)v * v;
                float magnitude = v < 0f ? -v : v;
                if (magnitude > peak) peak = magnitude;
            }

            float frameRms = Mathf.Sqrt((float)(sumSq / frames));
            int rate = outputSampleRate > 0 ? outputSampleRate : RemoteOpusSettings.NetworkSampleRate;

            return _normalizer.Process(frameRms, peak, _normalizerSettings, frames / (float)rate);
        }
    }
}
