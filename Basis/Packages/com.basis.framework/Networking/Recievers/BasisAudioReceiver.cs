using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking.NetworkedAvatar;
using OpusSharp.Core;
using System;
using UnityEngine;

namespace Basis.Scripts.Networking.Recievers
{
    [System.Serializable]
    public class BasisAudioReceiver
    {
        public BasisRemoteAudioDriver BasisRemoteVisemeAudioDriver;
        public OpusDecoder decoder;
        [SerializeField]
        public AudioSource audioSource;
        [SerializeField]
        public BasisAudioAndVisemeDriver visemeDriver;
        public BasisVoiceRingBuffer InOrderRead;
        public bool IsPlaying = false;
        public float[] pcmBuffer;
        public int pcmLength;
        public bool IsReady;
        public float[] silentData;
        public byte lastReadIndex = 0;
        public void OnDecode(byte[] data, int length)
        {
            pcmLength = decoder.Decode(data, length, pcmBuffer, RemoteOpusSettings.NetworkSampleRate, false);
            InOrderRead.Add(pcmBuffer, pcmLength);
        }
        public void OnDecodeSilence()
        {
            InOrderRead.Add(silentData, RemoteOpusSettings.FrameSize);
        }
        public void OnEnable(BasisNetworkPlayer networkedPlayer)
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
                audioSource = BasisHelpers.GetOrAddComponent<AudioSource>(remotePlayer.AudioSourceTransform.gameObject);
            }
            audioSource.spatialize = true;
            audioSource.spatializePostEffects = true; //revist later!
            audioSource.spatialBlend = 1.0f;
            audioSource.dopplerLevel = 0;
            audioSource.volume = 1.0f;
            audioSource.loop = true;
            InOrderRead = new BasisVoiceRingBuffer();
            // Create AudioClip
            audioSource.clip = AudioClip.Create($"player [{networkedPlayer.NetId}]", RemoteOpusSettings.FrameSize *4, RemoteOpusSettings.Channels, RemoteOpusSettings.PlayBackSampleRate, false, (buf) =>
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
            if (audioSource != null)
            {
                audioSource.Stop();
                GameObject.Destroy(audioSource);
            }
            if (visemeDriver != null)
            {
                GameObject.Destroy(visemeDriver);
            }
        }
        public void OnCalibration(BasisNetworkPlayer networkedPlayer)
        {
            // Ensure viseme driver is initialized for audio processing
            if (visemeDriver == null)
            {
                visemeDriver = BasisHelpers.GetOrAddComponent<BasisAudioAndVisemeDriver>(audioSource.gameObject);
            }
            visemeDriver.TryInitialize(networkedPlayer.Player);
            if (BasisRemoteVisemeAudioDriver == null)
            {
                BasisRemoteVisemeAudioDriver = BasisHelpers.GetOrAddComponent<BasisRemoteAudioDriver>(audioSource.gameObject);
                BasisRemoteVisemeAudioDriver.BasisAudioReceiver = this;
            }
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
        private void ProcessAudioWithoutResampling(float[] data, int frames, int channels)
        {
            InOrderRead.Remove(frames, out float[] segment);

            for (int i = 0; i < frames; i++)
            {
                float sample = segment[i]; // Single-channel sample from the RingBuffer
                for (int c = 0; c < channels; c++)
                {
                    int index = i * channels + c;
                    data[index] *= sample;
                    data[index] =  Math.Clamp(data[index], -1, 1);
                }
            }
            InOrderRead.BufferedReturn.Enqueue(segment);
        }

        private void ProcessAudioWithResampling(float[] data, int frames, int channels, int outputSampleRate)
        {
            float resampleRatio = (float)RemoteOpusSettings.NetworkSampleRate / outputSampleRate;
            int neededFrames = Mathf.CeilToInt(frames * resampleRatio);

            InOrderRead.Remove(neededFrames, out float[] inputSegment);

            float[] resampledSegment = new float[frames];

            // Resampling using linear interpolation
            for (int i = 0; i < frames; i++)
            {
                float srcIndex = i * resampleRatio;
                int indexLow = Mathf.FloorToInt(srcIndex);
                int indexHigh = Mathf.CeilToInt(srcIndex);
                float frac = srcIndex - indexLow;

                float sampleLow = (indexLow < inputSegment.Length) ? inputSegment[indexLow] : 0;
                float sampleHigh = (indexHigh < inputSegment.Length) ? inputSegment[indexHigh] : 0;

                resampledSegment[i] = Mathf.Lerp(sampleLow, sampleHigh, frac);
            }

            // Apply resampled audio to output buffer
            for (int i = 0; i < frames; i++)
            {
                float sample = resampledSegment[i];
                for (int c = 0; c < channels; c++)
                {
                    int index = i * channels + c;
                    data[index] *= sample;
                }
            }

            InOrderRead.BufferedReturn.Enqueue(inputSegment);
        }
    }
}
