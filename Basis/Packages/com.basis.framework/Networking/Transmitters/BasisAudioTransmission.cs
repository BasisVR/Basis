using UnityEngine;
using LiteNetLib;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Profiler;
using static SerializableBasis;
using LiteNetLib.Utils;
using Basis.Network.Core;
using Concentus;
using System;
using System.Threading;

namespace Basis.Scripts.Networking.Transmitters
{
    [System.Serializable]
    public static class BasisAudioTransmission
    {
        public static IOpusEncoder Encoder;
        public static BasisNetworkPlayer NetworkedPlayer;
        public static BasisOpusSettings settings;
        public static BasisLocalPlayer Local;
        public static MicrophoneRecorder Recorder;

        public static bool IsInitalized = false;
        public static AudioSegmentDataMessage AudioSegmentData = new AudioSegmentDataMessage();
        public static AudioSegmentDataMessage audioSilentSegmentData = new AudioSegmentDataMessage();
        private static int _hasReasonToSendAudioInt; // Store as int for Interlocked

        public static bool HasReasonToSendAudio
        {
            get
            {
                return Interlocked.CompareExchange(ref _hasReasonToSendAudioInt, 0, 0) == 1;
            }
            set
            {
                int newValue = value ? 1 : 0;
                Interlocked.Exchange(ref _hasReasonToSendAudioInt, newValue);
            }
        }
        public static void OnEnable(BasisNetworkPlayer networkedPlayer)
        {
            if (!IsInitalized)
            {
                // Assign the networked player and base network send functionality
                NetworkedPlayer = networkedPlayer;

                // Retrieve the Opus settings from the singleton instance
                settings = BasisDeviceManagement.Instance.BasisOpusSettings;
                Encoder =  OpusCodecFactory.CreateEncoder(settings.GetSampleFreq(), 1, settings.OpusApplication);
                Encoder.Bitrate = settings.BitrateKPS;
                Encoder.Complexity = settings.Complexity;
                Encoder.SignalType = settings.OpusSignal;
                // Cast the networked player to a local player to access the microphone recorder
                Local = (BasisLocalPlayer)networkedPlayer.Player;
                Recorder = Local.MicrophoneRecorder;

                // If there are no events hooked up yet, attach them
                if (Recorder != null)
                {
                    // Hook up the event handlers
                    MicrophoneRecorder.OnHasSilence += SendSilenceOverNetwork;
                    // Ensure the output buffer is properly initialized and matches the packet size
                    if (AudioSegmentData.buffer == null || Recorder.PacketSize != AudioSegmentData.buffer.Length)
                    {
                        AudioSegmentData.buffer = new byte[Recorder.PacketSize];
                    }
                }

                IsInitalized = true;
            }
        }
        public static void OnDisable()
        {
            HasReasonToSendAudio = false;
            if (IsInitalized)
            {
                MicrophoneRecorder.OnHasSilence -= SendSilenceOverNetwork;
                IsInitalized = false;
            }
            if (Recorder != null)
            {
                GameObject.Destroy(Recorder.gameObject);
            }
            Encoder.Dispose();
            Encoder = null;
        }
        public static void OnAudioReady(ReadOnlySpan<float> EncodeSpan)
        {
            if (HasReasonToSendAudio)
            {
                Span<byte> SpanOut = AudioSegmentData.buffer.AsSpan();
                AudioSegmentData.LengthUsed = Encoder.Encode(EncodeSpan, Recorder.processBufferArray.Length, SpanOut, Recorder.PacketSize);
               // SpanOut.CopyTo(AudioSegmentData.buffer); redudent?

                NetDataWriter writer = new NetDataWriter();
                AudioSegmentData.Serialize(writer);
                BasisNetworkProfiler.AudioSegmentDataMessageCounter.Sample(AudioSegmentData.LengthUsed);
                BasisNetworkManagement.LocalPlayerPeer.Send(writer, BasisNetworkCommons.VoiceChannel, DeliveryMethod.Sequenced);
                Local.AudioReceived?.Invoke(true);
            }
        }
        public static void SendSilenceOverNetwork()
        {
            if (HasReasonToSendAudio)
            {
                NetDataWriter writer = new NetDataWriter();
                audioSilentSegmentData.LengthUsed = 0;
                audioSilentSegmentData.Serialize(writer);
                BasisNetworkProfiler.AudioSegmentDataMessageCounter.Sample(writer.Length);
                BasisNetworkManagement.LocalPlayerPeer.Send(writer, BasisNetworkCommons.VoiceChannel, DeliveryMethod.Sequenced);
                Local.AudioReceived?.Invoke(false);
            }
        }
    }
}
