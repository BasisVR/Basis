using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Helpers;
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

        /// <summary>
        /// Maximum consecutive missing slots that trigger Opus PLC.
        /// Beyond this the gap is treated as intentional sender silence.
        /// </summary>
        private const int MaxConsecutivePlc = 2;
        private int _consecutiveMissing;

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
        /// Thread-safe: drains encoded packets and decodes them (Opus).
        /// Does NOT touch Unity AudioSource. Call ApplyAudioState() on main thread after.
        /// </summary>
        public void DrainAndDecodeThreadSafe()
        {
            _lastDrainDecoded = false;
            while (VoiceBuffer.TryConsumeEncoded(out byte[] data, out int length, out byte silenceUnits, out bool isMissing))
            {
                _lastDrainDecoded = true;
                if (isMissing)
                {
                    _consecutiveMissing++;
                    if (_consecutiveMissing <= MaxConsecutivePlc)
                    {
                        System.Threading.Interlocked.Increment(ref PlcCount);
                        OnDecodePLC();
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
                        VoiceBuffer.ClearDecoded();
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
                ChangeRemotePlayersVolumeSettings(settings.VolumeLevel);
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

        public void ChangeRemotePlayersVolumeSettings(float volume = 1.0f, float dopplerLevel = 0, float spatialBlend = 1.0f, bool spatialize = true, bool spatializePostEffects = true)
        {
            if (audioSource == null)
            {
#if !UNITY_SERVER
                if (decoder != null)
                {
                    try { OpusSharp.Core.Extensions.OpusDecoderExtensions.SetGain(decoder, 256); }
                    catch (OpusSharp.Core.OpusException) { }
                }
#endif
              //  BasisDebug.LogError("AudioSource is null. Cannot apply volume settings.", BasisDebug.LogTag.Remote);
                return;
            }
            audioSource.spatialize = spatialize;
            audioSource.spatializePostEffects = spatializePostEffects;
            audioSource.spatialBlend = Mathf.Clamp01(spatialBlend);
            audioSource.dopplerLevel = Mathf.Max(0f, dopplerLevel);

            short gain;
            if (volume <= 0f)
            {
                gain = (short)(-96f * 256f);
                audioSource.volume = 0f;
            }
            else
            {
                float db = 20f * Mathf.Log10(volume);
                gain = (short)(db * 256f);
                audioSource.volume = 1;
            }
#if !UNITY_SERVER
            if (decoder != null)
            {
                try
                {
                    OpusSharp.Core.Extensions.OpusDecoderExtensions.SetGain(decoder, gain);
                }
                catch (OpusSharp.Core.OpusException ex)
                {
                    BasisDebug.LogWarning($"Failed to set decoder gain: {ex.Message}", BasisDebug.LogTag.Voice);
                }
            }
            else
            {
                BasisDebug.LogWarning("Decoder is null. Cannot apply gain.");
            }
#endif
        }

        // ==================== Audio thread callback ====================

        public void OnAudioFilterRead(float[] data, int channels, int length)
        {
            int frames = length / channels;
            double msThisCallback = 1000.0 * frames / outputSampleRate;

            if (VoiceBuffer.IsEmpty)
            {
                Array.Clear(data, 0, length);

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

            float dampen = DirectionalDampeningMultiplier;
            int idx = 0;
            for (int f = 0; f < frames; f++)
            {
                float sample = FastClamp(_inputScratch[f] * dampen);
                for (int c = 0; c < channels; c++)
                {
                    data[idx++] = sample;
                }
            }
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
            float ratio = _resampleRatio;
            int neededFrames = (int)Mathf.Ceil(frames * ratio);

            EnsureCapacity(ref _inputScratch, neededFrames);
            EnsureCapacity(ref _resampleScratch, frames);

            int read = VoiceBuffer.ReadPcm(_inputScratch, neededFrames);
            if (read < neededFrames)
            {
                Array.Clear(_inputScratch, read, neededFrames - read);
            }

            double phase = 0.0;
            double step = ratio;
            int maxIndex = neededFrames - 1;

            for (int f = 0; f < frames; f++)
            {
                int iLow = (int)phase;
                if (iLow > maxIndex) iLow = maxIndex;
                double frac = phase - (int)phase;
                int iHigh = iLow + 1;

                float sLow = _inputScratch[iLow];
                float sHigh = (iHigh <= maxIndex) ? _inputScratch[iHigh] : 0f;

                _resampleScratch[f] = (float)(sLow + frac * (sHigh - sLow));
                phase += step;
            }

            float dampen = DirectionalDampeningMultiplier;
            int idx = 0;
            for (int f = 0; f < frames; f++)
            {
                float sample = FastClamp(_resampleScratch[f] * dampen);
                for (int c = 0; c < channels; c++)
                {
                    data[idx++] = sample;
                }
            }
        }
    }
}
