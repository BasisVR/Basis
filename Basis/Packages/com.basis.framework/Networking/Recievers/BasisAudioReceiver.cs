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

        public float[] pcmBuffer = new float[RemoteOpusSettings.FrameSize];
        public int pcmLength;
        public byte lastReadIndex = 0;
        public Transform AudioSourceTransform;
        public float[] resampledSegment;
        public volatile bool HasAudioSource = false;
        public volatile float DirectionalDampeningMultiplier = 1f;
        public BasisNetworkReceiver BasisNetworkReceiver;
        public static float[] silentData;
        public static int outputSampleRate;

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

        /// <summary>
        /// Maximum consecutive missing slots that trigger Opus PLC.
        /// Beyond this the gap is treated as intentional sender silence.
        /// </summary>
        private const int MaxConsecutivePlc = 2;
        private int _consecutiveMissing;

        /// <summary>
        /// Number of accumulated 20 ms silence units (see <see cref="_silentUnits20ms"/>)
        /// after which we treat the stream as fully idle and rearm decoder + jitter on
        /// resume. Sized above any natural pause (room-noise floor keeps packets flowing
        /// during normal speech gaps) and above network jitter (absorbed by the jitter
        /// buffer), so the realistic trigger is a true sender-side mute.
        /// </summary>
        private const int IdleResetThresholdUnits = 10; // 200 ms

        // Latched so the idle reset fires once per idle cycle, not every drain tick
        // while the jitter buffer is filling back up.
        private bool _idleResetDone;

        // Resampler state — persistent across OnAudioFilterRead callbacks so phase
        // and the last interpolation sample carry over the callback boundary. Without
        // this, interpolation restarts at phase=0 each call and drops a fractional
        // sample per callback = periodic crackle.
        private double _resamplePhase;
        private float _resampleLastSample;

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

        // ==================== Packet arrival ====================

        public void Insert(AudioSegmentDataMessage msg)
        {
            VoiceBuffer.InsertEncoded(msg.SequenceNumber, msg.buffer, msg.LengthUsed, msg.TotalPlayedInSilence);
        }

        // ==================== Decode pipeline ====================

        public void OnDecode(byte[] data, int length)
        {
#if UNITY_SERVER
            return;
#else
            if (HasAudioSource)
            {
                pcmLength = decoder.Decode(data, length, pcmBuffer, RemoteOpusSettings.FrameSize, false);
                VoiceBuffer.PushDecoded(pcmBuffer, pcmLength, true);
            }
#endif
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
                    VoiceBuffer.PushDecoded(pcmBuffer, pcmLength, true);
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
                VoiceBuffer.PushDecoded(pcmBuffer, pcmLength, true);
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
            if (System.Threading.Volatile.Read(ref _silentUnits20ms) >= IdleResetThresholdUnits)
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
                    _idleResetDone = true;
                }
            }
            else
            {
                _idleResetDone = false;
            }

            while (true)
            {
                // Backpressure: don't decode faster than the audio thread can drain.
                // If we did, PushDecoded would silently drop the oldest frame (= click).
                if (VoiceBuffer.DecodedFrameCount >= VoiceBuffer.DecodedFrameCapacity)
                    break;

                if (!VoiceBuffer.TryConsumeEncoded(out byte[] data, out int length, out byte silenceUnits, out bool isMissing))
                    break;

                _lastDrainDecoded = true;
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
                    }
                    else
                    {
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
                    if (silenceUnits > 0)
                    {
                        int localUnits = System.Threading.Interlocked.Exchange(ref _silentUnits20ms, 0);
                        int missing = silenceUnits - localUnits;
                        if (missing > 0)
                            System.Threading.Interlocked.Add(ref SilenceInjectedCount, missing);
                    }
                    OnDecode(data, length);
                }
            }
        }

        // Set by DrainAndDecodeThreadSafe, read by ApplyAudioState on main thread.
        internal volatile bool _lastDrainDecoded;

        /// <summary>
        /// Main-thread only: updates AudioSource enabled/playing state after decode.
        /// </summary>
        public void ApplyAudioState()
        {
            if (_lastDrainDecoded && HasAudioSource)
            {
                if (VoiceBuffer.HasRealAudio)
                {
                    EnableAndEnsurePlaying();
                }
                else if (audioSource.enabled)
                {
                    audioSource.enabled = false;
                }
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
                if (VoiceBuffer.HasRealAudio)
                {
                    EnableAndEnsurePlaying();
                }
                else if (audioSource.enabled)
                {
                    audioSource.enabled = false;
                }
            });
        }

        private void EnableAndEnsurePlaying()
        {
            if (!audioSource.enabled)
                audioSource.enabled = true;
            if (!audioSource.isPlaying)
                audioSource.Play();
        }

        public void ApplyRangeData(float Distance)
        {
            if (HasAudioSource)
                audioSource.maxDistance = Distance;
        }

        public async Task LoadAudioSource(BasisNetworkPlayer networkedPlayer, Transform MouthParent, float MaxDistance)
        {
            if (AudioSourceTransform == null || audioSource == null)
            {
                AudioSourceTransform = BasisAudioRemoteSource.RequestAudio(MouthParent).transform;
                AudioSourceTransform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
                AudioSourceTransform.name = $"[Audio] {BasisNetworkReceiver.Player.DisplayName}";
                audioSource = BasisHelpers.GetOrAddComponent<AudioSource>(AudioSourceTransform.gameObject);
                audioSource.clip = BasisAudioClipPool.Get(networkedPlayer.playerId);
                audioSource.loop = true;
                audioSource.Play();
                audioSource.maxDistance = MaxDistance;
            }
            HasAudioSource = true;
            AvatarChanged(networkedPlayer, false);

            SettingsProviderRemoteAudio.ApplyRemoteAudioTo(this);
            ChangeRemotePlayersVolumeSettings(1);

            try
            {
                var settings = await BasisPlayerSettingsManager.RequestPlayerSettings(networkedPlayer.Player.UUID);
                bool tempBlocked = networkedPlayer.Player is BasisRemotePlayer rp && rp.TempBlocked;
                bool muted = settings.IsBlocked || tempBlocked;
                ChangeRemotePlayersVolumeSettings(muted ? 0f : settings.VolumeLevel);
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"{ex}", BasisDebug.LogTag.Remote);
            }
        }

        public void UnloadAudioSource()
        {
            HasAudioSource = false;
            if (visemeDriver != null) visemeDriver.TrackedAudioSource = null;
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

        public void Initialize(BasisNetworkReceiver networkedPlayer)
        {
#if UNITY_SERVER
            return;
#else
            outputSampleRate = AudioSettings.outputSampleRate;
            silentData ??= new float[RemoteOpusSettings.FrameSize];
            BasisNetworkReceiver = networkedPlayer;

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
            if (decoder != null)
            {
                decoder.Dispose();
                decoder = null;
            }
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

            if (BasisRemoteVisemeAudioDriver == null)
                BasisRemoteVisemeAudioDriver = BasisHelpers.GetOrAddComponent<BasisRemoteAudioDriver>(audioSource.gameObject);
            BasisRemoteVisemeAudioDriver.BasisAudioReceiver = this;
            BasisRemoteVisemeAudioDriver.Initalize(visemeDriver);
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

        public void ChangeRemotePlayersVolumeSettings(float volume = 1.0f, float dopplerLevel = 1f, float spatialBlend = 1.0f, bool spatialize = true, bool spatializePostEffects = true)
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
            audioSource.spatialize = spatialize;
            audioSource.spatializePostEffects = spatializePostEffects;
            audioSource.spatialBlend = Mathf.Clamp01(spatialBlend);
            audioSource.dopplerLevel = Mathf.Max(0f, dopplerLevel);
            // Unity AudioSource stays at unit gain; actual attenuation happens per-sample in OnAudioFilterRead.
            audioSource.volume = 1f;
        }

        // ==================== Audio thread callback ====================

        private void ResetAudioThreadState()
        {
            _resamplePhase = 0.0;
            _resampleLastSample = 0f;
            _lastGain = 0f;
            _gainPrimed = false;
            _fadeEnvelope = 0f;
            _lastOutputSample = 0f;
        }

        public void OnAudioFilterRead(float[] data, int channels, int length)
        {
            int frames = length / channels;
            double msThisCallback = 1000.0 * frames / outputSampleRate;

            if (VoiceBuffer.IsEmpty)
            {
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
                            data[idx++] = sample;
                    }
                    _fadeEnvelope = env;
                    if (env <= 0f) _lastOutputSample = 0f;
                }
                else
                {
                    Array.Clear(data, 0, length);
                }

                _silentUsAccum += (long)(msThisCallback * 1000.0);
                int newUnits = (int)(_silentUsAccum / 20000L);
                if (newUnits > 0)
                {
                    int delta = newUnits - System.Threading.Volatile.Read(ref _silentUnits20ms);
                    if (delta > 0)
                        System.Threading.Interlocked.Add(ref _silentUnits20ms, delta);
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
                ProcessNoResample(data, frames, channels);
            }
            else
            {
                ProcessResample(data, frames, channels);
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

        private void ProcessNoResample(float[] data, int frames, int channels)
        {
            EnsureCapacity(ref _inputScratch, frames);
            int read = VoiceBuffer.ReadPcm(_inputScratch, frames);

            // Zero-fill any shortfall
            if (read < frames)
            {
                Array.Clear(_inputScratch, read, frames - read);
            }

            ApplyGainAndWrite(_inputScratch, data, frames, channels);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float FastClamp(float x)
        {
            if (x > 1f) return 1f;
            if (x < -1f) return -1f;
            return x;
        }

        private void ProcessResample(float[] data, int frames, int channels)
        {
            // Persistent-phase linear interpolation. The virtual input stream is
            //   virtual[0]       = _resampleLastSample (carry from previous callback)
            //   virtual[i>=1]    = _inputScratch[i-1]  (freshly read this callback)
            // and _resamplePhase is kept modulo 1 between callbacks, so there is no
            // restart-at-zero discontinuity and no fractional sample is silently
            // dropped at the callback boundary.
            double step = _resampleRatio;
            if (step <= 0.0) step = 1.0;

            double maxPhase = _resamplePhase + (frames - 1) * step;
            int maxVirtualIndex = (int)Math.Floor(maxPhase) + 1;
            int N = Math.Max(1, maxVirtualIndex); // new input samples needed this callback

            EnsureCapacity(ref _inputScratch, N);
            EnsureCapacity(ref _resampleScratch, frames);

            int read = VoiceBuffer.ReadPcm(_inputScratch, N);
            if (read < N)
            {
                Array.Clear(_inputScratch, read, N - read);
            }

            double phase = _resamplePhase;
            for (int f = 0; f < frames; f++)
            {
                int iLow = (int)Math.Floor(phase);
                double frac = phase - iLow;
                int iHigh = iLow + 1;

                float sLow = iLow <= 0 ? _resampleLastSample
                           : (iLow - 1 < N ? _inputScratch[iLow - 1] : _resampleLastSample);
                float sHigh = iHigh <= 0 ? _resampleLastSample
                            : (iHigh - 1 < N ? _inputScratch[iHigh - 1] : sLow);

                _resampleScratch[f] = (float)(sLow + frac * (sHigh - sLow));
                phase += step;
            }

            // Advance phase and carry over the last consumed sample to the next callback.
            double endPhase = _resamplePhase + frames * step;
            int consumedVirtual = (int)Math.Floor(endPhase);
            int lastIdx = consumedVirtual - 1;
            if (lastIdx >= 0 && lastIdx < N)
            {
                _resampleLastSample = _inputScratch[lastIdx];
            }
            _resamplePhase = endPhase - consumedVirtual;

            ApplyGainAndWrite(_resampleScratch, data, frames, channels);
        }

        /// <summary>
        /// Applies per-sample gain (smoothly ramped over the callback) and the
        /// underrun fade envelope to <paramref name="source"/>, writing the
        /// result into the interleaved Unity output buffer. Also updates
        /// <see cref="_lastOutputSample"/> so a subsequent underrun callback
        /// can fade out from the last real sample instead of stepping to zero.
        /// </summary>
        private void ApplyGainAndWrite(float[] source, float[] data, int frames, int channels)
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

            int idx = 0;
            float lastWritten = 0f;
            for (int f = 0; f < frames; f++)
            {
                if (env < 1f)
                {
                    env += FadeRampPerSample;
                    if (env > 1f) env = 1f;
                }
                float sample = FastClamp(source[f] * gain * env);
                for (int c = 0; c < channels; c++)
                    data[idx++] = sample;
                lastWritten = sample;
                gain += gainStep;
            }

            _lastGain = targetGain;
            _fadeEnvelope = env;
            _lastOutputSample = lastWritten;
        }
    }
}
