using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking.NetworkedAvatar;
using OpusSharp.Core;
using OpusSharp.Core.Extensions;
using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using UnityEngine;
namespace Basis.Scripts.Networking.Receivers
{
    /// <summary>
    /// Receives, decodes, buffers, and plays remote voice audio for a networked player.
    /// Manages an <see cref="AudioSource"/>, Opus decoding, ring-buffering, resampling,
    /// and optional viseme (lip-sync) driving.
    /// </summary>
    [Serializable]
    public class BasisAudioReceiver
    {
        /// <summary>
        /// Remote viseme driver component attached to the active audio source.
        /// </summary>
        [SerializeReference] public BasisRemoteAudioDriver BasisRemoteVisemeAudioDriver = null;

        /// <summary>
        /// The active <see cref="AudioSource"/> used for playback.
        /// </summary>
        public AudioSource audioSource;

        /// <summary>
        /// Viseme and audio processing driver used to feed lip-sync.
        /// </summary>
        public BasisAudioAndVisemeDriver visemeDriver = new BasisAudioAndVisemeDriver();

        /// <summary>
        /// Ring buffer used to maintain correct ordering of decoded audio samples.
        /// </summary>
        public BasisVoiceRingBuffer InOrderRead = new BasisVoiceRingBuffer();

        /// <summary>
        /// Decode destination buffer (mono) sized to one network frame.
        /// </summary>
        public float[] pcmBuffer = new float[RemoteOpusSettings.SampleLength];

        /// <summary>
        /// Number of valid samples written to <see cref="pcmBuffer"/> by the decoder.
        /// </summary>
        public int pcmLength;

        /// <summary>
        /// (Optional) last network ring index read (for diagnostics).
        /// </summary>
        public byte lastReadIndex = 0;

        /// <summary>
        /// Transform holding/parenting the audio source in the scene (usually near mouth).
        /// </summary>
        public Transform AudioSourceTransform;

        /// <summary>
        /// Temporary resampling segment (used when output rate != network rate).
        /// </summary>
        public float[] resampledSegment;

        /// <summary>
        /// Indicates whether an audio transform/source has been successfully created.
        /// </summary>
        public volatile bool HasAudioSource = false;

        /// <summary>
        /// Owning network receiver (player/session context).
        /// </summary>
        public BasisNetworkReceiver BasisNetworkReceiver;

        /// <summary>
        /// Shared silence buffer to avoid allocations when no audio is present.
        /// </summary>
        public static float[] silentData;

        /// <summary>
        /// Unity output sample rate cache (AudioSettings.outputSampleRate).
        /// </summary>
        public static int outputSampleRate;

        /// <summary>
        /// Opus decoder used for network voice frames.
        /// </summary>

        public OpusDecoder decoder;

        private float[] _inputScratch;    // big enough for the largest chunk we pull
        private int _cachedOutputRate = -1;
        private float _resampleRatio = 1f;
        private float[] _resampleScratch; // big enough for the largest frames we output
        // Count local silence in 20 ms "units"
        public volatile int _silentUnits20ms;   // thread-safe-ish; prefer Interlocked ops
        public double _silentMsAccum;           // accumulate fractional callback durations

        /// <summary>
        /// Called when an encoded voice packet arrives. Decodes and enqueues PCM.
        /// </summary>
        /// <param name="data">Opus-encoded payload.</param>
        /// <param name="length">Payload length in bytes.</param>
        public void OnDecode(byte[] data, int length)
        {
            if (HasAudioSource)
            {
                pcmLength = decoder.Decode(data, length, pcmBuffer, RemoteOpusSettings.NetworkSampleRate, false);
                InOrderRead.Add(pcmBuffer, pcmLength, true);
                AudioSourceSet();
            }
        }

        /// <summary>
        /// Enables/disables the audio source on the main thread based on buffer state.
        /// </summary>
        public void AudioSourceSet()
        {
            BasisDeviceManagement.EnqueueOnMainThread(() =>
            {
                if (!HasAudioSource)
                {
                    return;
                }
                if (InOrderRead.HasRealAudio)
                {
                    if (audioSource.enabled == false)
                    {
                        audioSource.enabled = true;
                    }
                }
                else
                {
                    if (audioSource.enabled)
                    {
                        audioSource.enabled = false;
                    }
                }
            });
        }

        /// <summary>
        /// Enqueues a frame of silence when no real audio is available.
        /// </summary>
        public void OnDecodeSilence()
        {
            if (HasAudioSource)
            {
                InOrderRead.Add(silentData, RemoteOpusSettings.FrameSize, false);
                AudioSourceSet();
            }
        }

        /// <summary>
        /// Creates/attaches an <see cref="AudioSource"/> and begins playback for the given player.
        /// Also initializes viseme driving and applies per-player volume settings.
        /// </summary>
        /// <param name="networkedPlayer">The networked player whose voice we render.</param>
        /// <param name="MouthParent">Transform to parent the audio source under (e.g., mouth).</param>
        public async Task LoadAudioSource(BasisNetworkPlayer networkedPlayer, Transform MouthParent,float MaxDistance)
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
            AvatarChanged(networkedPlayer,false);

            ChangeRemotePlayersVolumeSettings(1);

            try
            {
                var BasisPlayerSettingsData = await BasisPlayerSettingsManager.RequestPlayerSettings(networkedPlayer.Player.UUID);
                ChangeRemotePlayersVolumeSettings(BasisPlayerSettingsData.VolumeLevel);
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"{ex}", BasisDebug.LogTag.Remote);
            }
        }
        public void ApplyRangeData(float Distance)
        {
            if (HasAudioSource)
            {
                audioSource.maxDistance = Distance;
            }
        }
        /// <summary>
        /// Stops playback, returns pooled resources, and clears references.
        /// </summary>
        public void UnloadAudioSource()
        {
            HasAudioSource = false;

            if (audioSource != null && audioSource.clip != null)
            {
                audioSource.Stop();
                BasisAudioClipPool.Return(audioSource.clip);
            }

            if (AudioSourceTransform != null)
            {
                BasisAudioRemoteSource.Return(AudioSourceTransform.gameObject);
            }

            AudioSourceTransform = null;
            BasisRemoteVisemeAudioDriver = null;
        }

        /// <summary>
        /// Initializes the receiver for a specific networked player/receiver context.
        /// Sets up shared silence buffer and caches output sample rate.
        /// </summary>
        /// <param name="networkedPlayer">Owning network receiver.</param>
        public void Initalize(BasisNetworkReceiver networkedPlayer)
        {
#if UNITY_SERVER
            return;
#endif
            outputSampleRate = AudioSettings.outputSampleRate;

            silentData ??= new float[RemoteOpusSettings.FrameSize];

            BasisNetworkReceiver = networkedPlayer;

#if UNITY_IOS && !UNITY_EDITOR
            // iOS requires statically linked Opus library
            decoder = new OpusDecoder(RemoteOpusSettings.NetworkSampleRate, RemoteOpusSettings.Channels, use_static: true);
#else
            decoder = new OpusDecoder(RemoteOpusSettings.NetworkSampleRate, RemoteOpusSettings.Channels, use_static: false);
#endif
        }

        /// <summary>
        /// Cleans up decoder and audio resources.
        /// </summary>
        public void OnDestroy()
        {
            if (decoder != null)
            {
                decoder.Dispose();
                decoder = null;
            }

            UnloadAudioSource();
        }

        /// <summary>
        /// Called when the remote player's avatar changes; refreshes viseme setup.
        /// </summary>
        /// <param name="networkedPlayer">The player whose avatar changed.</param>
        public void AvatarChanged(BasisNetworkPlayer networkedPlayer,bool WasFromCalibration)
        {
#if UNITY_SERVER
            return;
#endif
            if (audioSource == null)
            {
                //BasisDebug.LogWarning($"Avatar Changed no Audio Source was from calibration? {WasFromCalibration}", BasisDebug.LogTag.Voice);
                return;
            }
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
            if (visemeDriver.TryInitialize(networkedPlayer.Player))
            {

            }
            else
            {
              //  BasisDebug.LogWarning("Cant Setup Viseme Audio Driver Does not meet Critera");
            }

            if (BasisRemoteVisemeAudioDriver == null)
            {
                BasisRemoteVisemeAudioDriver = BasisHelpers.GetOrAddComponent<BasisRemoteAudioDriver>(audioSource.gameObject);
            }

            BasisRemoteVisemeAudioDriver.BasisAudioReceiver = this;
            BasisRemoteVisemeAudioDriver.Initalize(visemeDriver);
        }

        /// <summary>
        /// Stops audio and unloads associated resources.
        /// </summary>
        public void StopAudio()
        {
#if UNITY_SERVER
            return;
#endif
            UnloadAudioSource();
        }

        /// <summary>
        /// Starts audio playback, allocating scratch buffers and creating the source if needed.
        /// </summary>
        public void StartAudio(float MaxDistance)
        {
            // Conservative initial sizes; will grow once and then reuse.
            const int BufferSize = 1024;

            if (_inputScratch == null || _inputScratch.Length != BufferSize)
            {
                _inputScratch = new float[BufferSize];
            }
            else
            {
                _inputScratch.AsSpan().Clear();
            }

            if (_resampleScratch == null || _resampleScratch.Length != BufferSize)
            {
                _resampleScratch = new float[BufferSize];
            }
            else
            {
                _resampleScratch.AsSpan().Clear();
            }

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

        /// <summary>
        /// Applies per-remote-player audio settings (volume, spatialization, doppler, and Opus gain).
        /// </summary>
        /// <param name="volume">Desired volume (0..∞, clamped to 1 for Unity volume; Opus gain scales above 1).</param>
        /// <param name="dopplerLevel">Doppler effect intensity (≥ 0).</param>
        /// <param name="spatialBlend">0 = 2D, 1 = fully 3D.</param>
        /// <param name="spatialize">Enable HRTF/spatializer if available.</param>
        /// <param name="spatializePostEffects">Apply spatialization post-effects.</param>
        public void ChangeRemotePlayersVolumeSettings(float volume = 1.0f, float dopplerLevel = 0, float spatialBlend = 1.0f, bool spatialize = true, bool spatializePostEffects = true)
        {
            if (audioSource == null)
            {
                if (decoder != null)
                {
                    try
                    {
                        OpusDecoderExtensions.SetGain(decoder, 256);
                    }
                    catch (OpusException)
                    {
                        // SetGain may fail on some Opus builds - non-fatal
                    }
                }
                BasisDebug.LogError("AudioSource is null. Cannot apply volume settings.", BasisDebug.LogTag.Remote);
                return;
            }
            audioSource.spatialize = spatialize;
            audioSource.spatializePostEffects = spatializePostEffects;
            audioSource.spatialBlend = Mathf.Clamp01(spatialBlend);
            audioSource.dopplerLevel = Mathf.Max(0f, dopplerLevel);

            int gain;
            if (volume <= 0f)
            {
                // Effectively silence
                gain = (int)(-96f * 256f);
                audioSource.volume = 0f;
            }
            else
            {
                float db = 20f * Mathf.Log10(volume);
                gain = (int)(db * 256f);
                audioSource.volume = 1;
            }
            if (decoder != null)
            {
                try
                {
                    //  BasisDebug.Log($"Gain Set To {gain}");
                    OpusDecoderExtensions.SetGain(decoder, gain);
                }
                catch (OpusException ex)
                {
                    // Some Opus library builds may not support OPUS_SET_GAIN or may fail
                    // if called before any decoding has occurred. This is non-fatal.
                    BasisDebug.LogWarning($"Failed to set decoder gain: {ex.Message}", BasisDebug.LogTag.Voice);
                }
            }
            else
            {
                BasisDebug.LogWarning("Decoder is null. Cannot apply gain.");
            }
        }

        /// <summary>
        /// Unity audio callback. Mixes buffered mono voice into the provided interleaved output buffer.
        /// If output sample rate differs from network rate, performs linear resampling.
        /// </summary>
        /// <param name="data">Interleaved output buffer to write into.</param>
        /// <param name="channels">Number of output channels.</param>
        /// <param name="length">Total sample count in <paramref name="data"/> (interleaved).</param>
        public void OnAudioFilterRead(float[] data, int channels,int length)
        {
            int frames = length / channels;
            double msThisCallback = 1000.0 * frames / outputSampleRate;

            if (InOrderRead.IsEmpty)
            {
                Array.Clear(data, 0, length);

                // accumulate time and convert to 20ms units
                _silentMsAccum += msThisCallback;
                int newUnits = (int)(_silentMsAccum / 20.0); // how many full 20ms chunks fit
                if (newUnits > 0)
                {
                    // make local counter reflect total observed units this silence run
                    // only increment the delta to avoid double counting
                    int delta = newUnits - _silentUnits20ms;
                    if (delta > 0)
                        System.Threading.Interlocked.Add(ref _silentUnits20ms, delta);

                    _silentMsAccum -= newUnits * 20.0; // keep remainder for next callback
                }
                return;
            }

            // got audio: reset local silence tracking
            System.Threading.Interlocked.Exchange(ref _silentUnits20ms, 0);
            _silentMsAccum = 0.0;

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

        /// <summary>
        /// Ensures a scratch buffer has at least <paramref name="needed"/> elements.
        /// Uses power-of-two growth to reduce reallocations.
        /// </summary>
        private void EnsureCapacity(ref float[] buf, int needed)
        {
            if (buf.Length < needed)
            {
                int newSize = 1;
                while (newSize < needed)
                {
                    newSize <<= 1;
                }

                buf = new float[newSize];
            }
        }

        /// <summary>
        /// Processes audio when no resampling is required (network rate == output rate).
        /// </summary>
        private void ProcessNoResample(float[] data, int frames, int channels)
        {
            EnsureCapacity(ref _inputScratch, frames);
            InOrderRead.Remove(frames, out float[] segment);

            // Copy to local scratch for safe reuse of pooled arrays
            Buffer.BlockCopy(segment, 0, _inputScratch, 0, frames * sizeof(float));

            int idx = 0;
            for (int f = 0; f < frames; f++)
            {
                float sample = _inputScratch[f];
                for (int c = 0; c < channels; c++)
                {
                    float v = data[idx] * sample;
                    data[idx++] = FastClamp(v);
                }
            }

            InOrderRead.BufferedReturn.Enqueue(segment);
        }

        /// <summary>
        /// Fast clamp to [-1,1] optimized for tight loops.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float FastClamp(float x)
        {
            if (x > 1f) return 1f;
            if (x < -1f) return -1f;
            return x;
        }

        /// <summary>
        /// Processes audio with linear resampling when output rate differs from network rate.
        /// </summary>
        private void ProcessResample(float[] data, int frames, int channels)
        {
            float ratio = _resampleRatio; // network / output
            int neededFrames = (int)Mathf.Ceil(frames * ratio);

            EnsureCapacity(ref _inputScratch, neededFrames);
            EnsureCapacity(ref _resampleScratch, frames);

            InOrderRead.Remove(neededFrames, out float[] inputSegment);

            Buffer.BlockCopy(inputSegment, 0, _inputScratch, 0, neededFrames * sizeof(float));

            // Phase-accumulator linear interpolation
            double phase = 0.0;
            double step = ratio;
            int maxIndex = neededFrames - 1;

            for (int f = 0; f < frames; f++)
            {
                int iLow = (int)phase;
                double frac = phase - iLow;
                int iHigh = iLow + 1;

                float sLow = _inputScratch[iLow];
                float sHigh = (iHigh <= maxIndex) ? _inputScratch[iHigh] : 0f;

                _resampleScratch[f] = (float)(sLow + frac * (sHigh - sLow));
                phase += step;
            }

            int idx = 0;
            for (int f = 0; f < frames; f++)
            {
                float sample = _resampleScratch[f];
                for (int c = 0; c < channels; c++)
                {
                    float v = data[idx] * sample;
                    data[idx++] = FastClamp(v);
                }
            }

            InOrderRead.BufferedReturn.Enqueue(inputSegment);
        }
    }
}
