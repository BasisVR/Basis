using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.Common;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking.NetworkedAvatar;
using OpusSharp.Core;
using OpusSharp.Core.Extensions;
using System;
using UnityEngine;
namespace Basis.Scripts.Networking.Receivers
{
    [System.Serializable]
    public class BasisAudioReceiver
    {
        public BasisRemoteAudioDriver BasisRemoteVisemeAudioDriver = null;
        [SerializeField]
        public AudioSource audioSource;
        [SerializeField]
        public BasisAudioAndVisemeDriver visemeDriver = new BasisAudioAndVisemeDriver();
        public BasisVoiceRingBuffer InOrderRead = new BasisVoiceRingBuffer();
        public bool IsPlaying = false;
        public float[] pcmBuffer = new float[RemoteOpusSettings.SampleLength];
        public int pcmLength;
        public byte lastReadIndex = 0;
        public Transform AudioSourceTransform;
        public float[] resampledSegment;
        public bool HasTransform = false;
        public BasisNetworkPlayer BasisNetworkPlayer;
        //everything can safely share the same silent data as we only copy it.
        public static float[] silentData;
        public static int outputSampleRate;
        public OpusDecoder decoder = new OpusDecoder(RemoteOpusSettings.NetworkSampleRate, RemoteOpusSettings.Channels);
        public void OnDecode(byte[] data, int length)
        {
            if (HasTransform)//only process the audio if we actually need it!
            {
                pcmLength = decoder.Decode(data, length, pcmBuffer, RemoteOpusSettings.NetworkSampleRate, false);
                InOrderRead.Add(pcmBuffer, pcmLength);
            }
        }
        public void OnDecodeSilence()
        {
            if (HasTransform)//only process the audio if we actually need it!
            {
                InOrderRead.Add(silentData, RemoteOpusSettings.FrameSize);
            }
        }
        public async void LoadAudioSource(BasisNetworkPlayer networkedPlayer)
        {
            if (AudioSourceTransform == null)
            {
                AudioSourceTransform = BasisAudioRemoteSource.RequestAudio().transform;
                AudioSourceTransform.name = $"[Audio] {BasisNetworkPlayer.Player.DisplayName}";
                HasTransform = true;
                if (audioSource == null)
                {
                    audioSource = BasisHelpers.GetOrAddComponent<AudioSource>(AudioSourceTransform.gameObject);
                    audioSource.loop = true;
                    // Initialize settings and audio source
                    audioSource.clip = BasisAudioClipPool.Get(networkedPlayer.playerId);
                }
                audioSource.Play();
            }
            IsPlaying = true;
            AvatarChanged(networkedPlayer);
            BasisPlayerSettingsData BasisPlayerSettingsData = await BasisPlayerSettingsManager.RequestPlayerSettings(networkedPlayer.Player.UUID);
            ChangeRemotePlayersVolumeSettings(BasisPlayerSettingsData.VolumeLevel);
        }
        public void UnloadAudioSource()
        {
            if (audioSource != null && audioSource.clip != null)
            {
                audioSource.Stop();
                BasisAudioClipPool.Return(audioSource.clip);
            }
            if (AudioSourceTransform != null)
            {
                BasisAudioTransformDriver.RequestRemove(AudioSourceTransform);
                BasisAudioRemoteSource.Return(AudioSourceTransform.gameObject);
                AudioSourceTransform = null;
                HasTransform = false;
                BasisRemoteVisemeAudioDriver = null;
            }
            IsPlaying = false;
        }
        public void Initalize(BasisNetworkPlayer networkedPlayer)
        {
#if UNITY_SERVER
       return;
#endif
            outputSampleRate = UnityEngine.AudioSettings.outputSampleRate;
            if (silentData == null)
            {
                silentData = new float[RemoteOpusSettings.FrameSize];
            }
            BasisNetworkPlayer = networkedPlayer;
        }
        public void OnDestroy()
        {
            // Unsubscribe from events on destroy
            if (decoder != null)
            {
                decoder.Dispose();
                decoder = null;
            }
            UnloadAudioSource();
        }
        public void AvatarChanged(BasisNetworkPlayer networkedPlayer)
        {
#if UNITY_SERVER
       return;
#endif
            if (audioSource != null)
            {
                // Ensure viseme driver is initialized for audio processing
                visemeDriver.TryInitialize(networkedPlayer.Player);
                if (BasisRemoteVisemeAudioDriver == null)
                {
                    BasisRemoteVisemeAudioDriver = BasisHelpers.GetOrAddComponent<BasisRemoteAudioDriver>(audioSource.gameObject);
                }
                BasisRemoteVisemeAudioDriver.BasisAudioReceiver = this;
                BasisRemoteVisemeAudioDriver.Initalize(visemeDriver);
            }
        }
        public void StopAudio()
        {
#if UNITY_SERVER
       return;
#endif
            UnloadAudioSource();
        }
        public void StartAudio()
        {
#if UNITY_SERVER
       return;
#endif
            if (BasisNetworkPlayer != null)
            {
                LoadAudioSource(BasisNetworkPlayer);
            }
        }
        public void ChangeRemotePlayersVolumeSettings(float volume = 1.0f,float dopplerLevel = 0,float spatialBlend = 1.0f,bool spatialize = true,bool spatializePostEffects = true)
        {
            // Safety check for audio source
            if (audioSource == null)
            {
                Debug.LogWarning("AudioSource is null. Cannot apply volume settings.");
                return;
            }

            // Apply spatial audio settings
            audioSource.spatialize = spatialize;
            audioSource.spatializePostEffects = spatializePostEffects;
            audioSource.spatialBlend = Mathf.Clamp01(spatialBlend);
            audioSource.dopplerLevel = Mathf.Max(0f, dopplerLevel); // Doppler should not be negative

            // Determine gain value
            short gain;

            if (volume <= 0f)
            {
                audioSource.volume = 0f;
                gain = 256; // Mute gain for Opus (e.g., 256 = silence)
            }
            else if (volume <= 1f)
            {
                audioSource.volume = volume;
                gain = 1024; // Normal gain
            }
            else
            {
                audioSource.volume = 1f;
                gain = (short)Mathf.Clamp(volume * 1024f, 1024f, short.MaxValue); // Prevent overflow
            }

            // Log for debugging if needed
            //Debug.Log($"[AudioDebug] Set Volume: {volume}, UnityVolume: {audioSource.volume}, Gain: {gain}, Spatialize: {spatialize}");

            // Apply gain to decoder
            if (decoder != null)
            {
                OpusDecoderExtensions.SetGain(decoder, gain);
            }
            else
            {
                Debug.LogWarning("Decoder is null. Cannot apply gain.");
            }
        }
        public void OnAudioFilterRead(float[] data, int channels, int length)
        {
            int frames = length / channels; // Number of audio frames
            if (InOrderRead.IsEmpty)
            {
                // No voice data, fill with silence
                //  BasisDebug.Log("Missing Audio Data! filling with Silence");
                Array.Fill(data, 0);
                return;
            }

            if (RemoteOpusSettings.NetworkSampleRate == outputSampleRate)
            {
                ProcessAudioWithoutResampling(data, frames, channels);
            }
            else
            {
                ProcessAudioWithResampling(data, frames, channels, outputSampleRate);
            }
        }
        private void ProcessAudioWithResampling(float[] data, int frames, int channels, int outputSampleRate)
        {
            float resampleRatio = (float)RemoteOpusSettings.NetworkSampleRate / outputSampleRate;
            int neededFrames = Mathf.CeilToInt(frames * resampleRatio);

            InOrderRead.Remove(neededFrames, out float[] inputSegment);

            float[] resampledSegment = new float[frames];

            // Resampling using linear interpolation
            for (int FrameIndex = 0; FrameIndex < frames; FrameIndex++)
            {
                float srcIndex = FrameIndex * resampleRatio;
                int indexLow = Mathf.FloorToInt(srcIndex);
                int indexHigh = Mathf.CeilToInt(srcIndex);
                float frac = srcIndex - indexLow;

                float sampleLow = (indexLow < inputSegment.Length) ? inputSegment[indexLow] : 0;
                float sampleHigh = (indexHigh < inputSegment.Length) ? inputSegment[indexHigh] : 0;

                resampledSegment[FrameIndex] = Mathf.Lerp(sampleLow, sampleHigh, frac);
            }

            // Apply resampled audio to output buffer
            for (int FrameIndex = 0; FrameIndex < frames; FrameIndex++)
            {
                float sample = resampledSegment[FrameIndex];
                for (int c = 0; c < channels; c++)
                {
                    int index = FrameIndex * channels + c;
                    data[index] *= sample;
                    data[index] = Math.Clamp(data[index], -1, 1);
                }
            }

            InOrderRead.BufferedReturn.Enqueue(inputSegment);
        }
        private void ProcessAudioWithoutResampling(float[] data, int frames, int channels)
        {
            InOrderRead.Remove(frames, out float[] segment);

            for (int FrameIndex = 0; FrameIndex < frames; FrameIndex++)
            {
                float sample = segment[FrameIndex]; // Single-channel sample from the RingBuffer
                for (int ChannelIndex = 0; ChannelIndex < channels; ChannelIndex++)
                {
                    int index = FrameIndex * channels + ChannelIndex;
                    data[index] *= sample;
                    data[index] = Math.Clamp(data[index], -1, 1);
                }
            }
            InOrderRead.BufferedReturn.Enqueue(segment);
        }
    }
}
