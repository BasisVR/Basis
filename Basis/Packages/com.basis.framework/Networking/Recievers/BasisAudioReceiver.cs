using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking.NetworkedAvatar;
using OpusSharp.Core;
using OpusSharp.Core.Extensions;
using System;
using UnityEngine;
using UnityEngine.AddressableAssets;

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
        const string AudioSource = "Packages/com.basis.sdk/Prefabs/Players/AudioSource.prefab";
        public BasisNetworkPlayer BasisNetworkPlayer;
        //everything can safely share the same silent data as we only copy it.
        public static float[] silentData;

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
                UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<GameObject> Loadable = Addressables.LoadAssetAsync<GameObject>(AudioSource);
                GameObject LoadableAudioSource = Loadable.WaitForCompletion();
                GameObject ActualAudio = GameObject.Instantiate(LoadableAudioSource,BasisDeviceManagement.Instance.transform);
                AudioSourceTransform = ActualAudio.transform;
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
                GameObject.Destroy(AudioSourceTransform.gameObject);
                AudioSourceTransform = null;
                HasTransform = false;
                BasisRemoteVisemeAudioDriver = null;
            }
            IsPlaying = false;
        }
        public void MoveAudio(BasisCalibratedCoords Coords)
        {
            if (HasTransform)
            {
                AudioSourceTransform.SetPositionAndRotation(Coords.position, Coords.rotation);
            }
        }
        public static int outputSampleRate;
        public void Initalize(BasisNetworkPlayer networkedPlayer)
        {
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
            UnloadAudioSource();
        }
        public void StartAudio()
        {
            if (BasisNetworkPlayer != null)
            {
                LoadAudioSource(BasisNetworkPlayer);
            }
        }
        public void ChangeRemotePlayersVolumeSettings(float Volume = 1.0f, float dopplerLevel = 0, float spatialBlend = 1.0f, bool spatialize = true, bool spatializePostEffects = true)
        {
            // Set spatial and doppler settings
            if (audioSource != null)
            {
                audioSource.spatialize = spatialize;
                audioSource.spatializePostEffects = spatializePostEffects;
                audioSource.spatialBlend = spatialBlend;
                audioSource.dopplerLevel = dopplerLevel;
            }
            short Gain;

            if (Volume <= 0f)
            {
                // Mute audio source and set gain to 0
                audioSource.volume = 0f;
                Gain = 256;
            }
            else if (Volume <= 1f)
            {
                // Set audio volume directly, gain stays at default (1.0 * 1024)
                audioSource.volume = Volume;
                Gain = (short)1024; // Normal gain
            }
            else
            {
                // Max out Unity volume, and use Opus gain for amplification
                audioSource.volume = 1f;
                Gain = (short)(Volume * 1024);
            }
            // BasisDebug.Log("Set Gain To " + Gain);
            OpusDecoderExtensions.SetGain(decoder, Gain);

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
