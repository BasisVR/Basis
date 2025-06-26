using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking.NetworkedAvatar;
using OpusSharp.Core;
using OpusSharp.Core.Extensions;
using SteamAudio;
using System;
using UnityEngine;

namespace Basis.Scripts.Networking.Receivers
{
    [System.Serializable]
    public class BasisAudioReceiver
    {
        public BasisRemoteAudioDriver BasisRemoteVisemeAudioDriver;
        public OpusDecoder decoder;
        [SerializeField]
        public AudioSource audioSource;
        [SerializeField]
        public BasisAudioAndVisemeDriver visemeDriver = new BasisAudioAndVisemeDriver();
        public BasisVoiceRingBuffer InOrderRead;
        public SteamAudioSource SteamAudioSource;
        public bool IsPlaying = false;
        public float[] pcmBuffer;
        public int pcmLength;
        public bool IsReady;
        public float[] silentData;
        public byte lastReadIndex = 0;
        public Transform AudioSourceTransform;

        public float[] resampledSegment;
        public void OnDecode(byte[] data, int length)
        {
            pcmLength = decoder.Decode(data, length, pcmBuffer, RemoteOpusSettings.NetworkSampleRate, false);
            InOrderRead.Add(pcmBuffer, pcmLength);
        }
        public void OnDecodeSilence()
        {
            InOrderRead.Add(silentData, RemoteOpusSettings.FrameSize);
        }
        public async void OnEnable(BasisNetworkPlayer networkedPlayer)
        {
            if (silentData == null || silentData.Length != RemoteOpusSettings.FrameSize)
            {
                silentData = new float[RemoteOpusSettings.FrameSize];
                Array.Fill(silentData, 0f);
            }
            // Initialize settings and audio source
            if (audioSource == null)
            {
                BasisRemotePlayer remotePlayer = (BasisRemotePlayer)networkedPlayer.Player;
                AudioSourceTransform = remotePlayer.NetworkedVoice;
                audioSource = BasisHelpers.GetOrAddComponent<AudioSource>(AudioSourceTransform.gameObject);
            }
            audioSource.loop = true;
            InOrderRead = new BasisVoiceRingBuffer();
            // Create AudioClip
            audioSource.clip = AudioClip.Create($"player [{networkedPlayer.NetId}]", RemoteOpusSettings.FrameSize * (2 * 2), RemoteOpusSettings.Channels, RemoteOpusSettings.PlayBackSampleRate, false, (buf) =>
            {
                Array.Fill(buf, 1.0f);
            });
            // Ensure decoder is initialized and subscribe to events
            pcmLength = RemoteOpusSettings.FrameSize;
            pcmBuffer = new float[RemoteOpusSettings.SampleLength];
            decoder = new OpusDecoder(RemoteOpusSettings.NetworkSampleRate, RemoteOpusSettings.Channels);
            StartAudio();
            // Perform calibration
            OnCalibration(networkedPlayer);

            BasisPlayerSettingsData BasisPlayerSettingsData = await BasisPlayerSettingsManager.RequestPlayerSettings(networkedPlayer.Player.UUID);
            ChangeRemotePlayersVolumeSettings(BasisPlayerSettingsData.VolumeLevel);
            IsReady = true;
        }
        public void OnDestroy()
        {
            // Unsubscribe from events on destroy
            if (decoder != null)
            {
                decoder.Dispose();
                decoder = null;
            }
            if(SteamAudioSource != null)
            {
                GameObject.Destroy(SteamAudioSource);
            }
            if (audioSource != null)
            {
                audioSource.Stop();
                GameObject.Destroy(audioSource);
            }
        }
        public void OnCalibration(BasisNetworkPlayer networkedPlayer)
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
        public void StopAudio()
        {
            IsPlaying = false;
            audioSource.Stop();
        }
        public void StartAudio()
        {
            IsPlaying = true;
            audioSource.Play();
        }
        public void ChangeRemotePlayersVolumeSettings(float Volume = 1.0f, float dopplerLevel = 0, float spatialBlend = 1.0f, bool spatialize = true, bool spatializePostEffects = true)
        {
            // Set spatial and doppler settings
            audioSource.spatialize = spatialize;
            audioSource.spatializePostEffects = spatializePostEffects;
            audioSource.spatialBlend = spatialBlend;
            audioSource.dopplerLevel = dopplerLevel;

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

            int outputSampleRate = RemoteOpusSettings.PlayBackSampleRate;

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
