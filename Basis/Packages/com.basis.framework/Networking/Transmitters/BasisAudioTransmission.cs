using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Profiler;
using static SerializableBasis;

namespace Basis.Scripts.Networking.Transmitters
{
    [System.Serializable]
    public class BasisAudioTransmission
    {
#if !UNITY_SERVER
        public OpusSharp.Core.Interfaces.IOpusEncoder encoder;
#endif
        public BasisNetworkPlayer NetworkedPlayer;
        public BasisLocalPlayer Local;
        public bool HasEvents = false;
        public AudioSegmentDataMessage Segment = new AudioSegmentDataMessage();
        public NetDataWriter writer = new NetDataWriter();
        public byte _sequenceNumber = 0;
        public int SilentForHowLong = 0;
        /// <summary>
        /// When true, voice is sent on ShoutVoiceChannel (channel 0) instead of VoiceChannel (channel 3).
        /// Set by admin via BasisNetworkModeration when shout mode is granted to the local player.
        /// </summary>
        public static volatile bool IsInShoutMode = false;
        public void Initialize(BasisNetworkPlayer networkedPlayer)
        {
            NetworkedPlayer = networkedPlayer;
            Local = (BasisLocalPlayer)networkedPlayer.Player;

#if UNITY_SERVER
            return;
#else
            InitializeEncoder();
            AttachMicrophoneEvents();
            InitializeBuffers();
#endif
        }

        public void DeInitialize()
        {
#if UNITY_SERVER
            return;
#else
            if (HasEvents)
            {
                DetachMicrophoneEvents();
            }

            LocalOpusSettings.OnPacketLossPercentChanged -= ApplyPacketLossPerc;
            encoder?.Dispose();
            encoder = null;
#endif
        }

#if !UNITY_SERVER
        private void InitializeEncoder()
        {
#if UNITY_IOS && !UNITY_EDITOR
            encoder = new OpusSharp.Core.Static.OpusEncoder(
    LocalOpusSettings.MicrophoneSampleRate,
    LocalOpusSettings.Channels,
    LocalOpusSettings.OpusApplication
);
#else

            encoder = new OpusSharp.Core.Dynamic.OpusEncoder(
                LocalOpusSettings.MicrophoneSampleRate,
                LocalOpusSettings.Channels,
                LocalOpusSettings.OpusApplication
            );
#endif

            encoder.Ctl(OpusSharp.Core.EncoderCTL.OPUS_SET_BITRATE, 32000);
            encoder.Ctl(OpusSharp.Core.EncoderCTL.OPUS_SET_COMPLEXITY, 5);
            // Forward Error Correction — embed a low-bitrate redundant copy of the
            // previous frame inside each packet. Combined with look-ahead decode on
            // the receiver, this lets a single-packet loss be reconstructed from the
            // next packet instead of falling back to PLC or silence.
            encoder.Ctl(OpusSharp.Core.EncoderCTL.OPUS_SET_INBAND_FEC, 1);
            ApplyPacketLossPerc(LocalOpusSettings.PacketLossPercent);
            LocalOpusSettings.OnPacketLossPercentChanged += ApplyPacketLossPerc;
        }

        /// <summary>
        /// Pushes OPUS_SET_PACKET_LOSS_PERC onto the live encoder without tearing
        /// it down. Safe to call multiple times; subscribed to
        /// <see cref="LocalOpusSettings.OnPacketLossPercentChanged"/> so an admin
        /// push updates FEC immediately on the already-running encoder.
        /// </summary>
        private void ApplyPacketLossPerc(int percent)
        {
            if (encoder == null) return;
            if (percent < 0) percent = 0;
            else if (percent > 100) percent = 100;
            try
            {
                encoder.Ctl(OpusSharp.Core.EncoderCTL.OPUS_SET_PACKET_LOSS_PERC, percent);
            }
            catch (OpusSharp.Core.OpusException ex)
            {
                BasisDebug.LogWarning($"Failed to set encoder packet-loss %: {ex.Message}", BasisDebug.LogTag.Voice);
            }
        }
#endif

        private void AttachMicrophoneEvents()
        {
            if (HasEvents)
            {
                return;
            }

#if !BASIS_DISABLE_MICROPHONE
            BasisLocalMicrophoneDriver.OnHasAudio += OnAudioReady;
            BasisLocalMicrophoneDriver.OnHasSilence += SendSilenceOverNetwork;
#endif

            HasEvents = true;
        }

        private void DetachMicrophoneEvents()
        {
#if !BASIS_DISABLE_MICROPHONE
            BasisLocalMicrophoneDriver.OnHasAudio -= OnAudioReady;
            BasisLocalMicrophoneDriver.OnHasSilence -= SendSilenceOverNetwork;
#endif

            HasEvents = false;
        }

        private void InitializeBuffers()
        {
#if !BASIS_DISABLE_MICROPHONE
            int packetSize = BasisLocalMicrophoneDriver.PacketSize;
#else
            int packetSize = 0;
#endif

            if (packetSize != Segment.TotalLength)
            {
                Segment = new AudioSegmentDataMessage();
                Segment.buffer = new byte[packetSize];
                Segment.TotalLength = packetSize;
            }
        }
        public void OnAudioReady()
        {
#if UNITY_SERVER
            return;
#else
            // In shout mode we always send (everyone hears us).
            // In normal mode we only send if someone is in range.
            if (!IsInShoutMode && !NetworkedPlayer.HasReasonToSendAudio)
            {
                return;
            }

            InitializeBuffers();

            writer.Reset();

#if !BASIS_DISABLE_MICROPHONE
            Segment.LengthUsed = encoder.Encode(BasisLocalMicrophoneDriver.processBufferArray,BasisLocalMicrophoneDriver.ProcessFrameLength,Segment.buffer,Segment.TotalLength);
#endif

            Segment.SequenceNumber = _sequenceNumber++;

            if(SilentForHowLong > 256)
            {
                Segment.TotalPlayedInSilence = 0;
            }
            else
            {
                Segment.TotalPlayedInSilence = (byte)SilentForHowLong;
            }
            Segment.Serialize(writer);

            byte channel = IsInShoutMode ? BasisNetworkCommons.ShoutVoiceChannel : BasisNetworkCommons.VoiceChannel;
            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.AudioSegmentData, Segment.LengthUsed);
            BasisNetworkConnection.LocalPlayerPeer.Send(writer, channel, DeliveryMethod.Unreliable);
            if (BasisLocalPlayer.Instance != null)
            {
                BasisLocalPlayer.Instance.AudioReceived?.Invoke();
            }
            SilentForHowLong = 0;
#endif
        }

        private void SendSilenceOverNetwork()
        {
#if UNITY_SERVER
            return;
#else
            if (!IsInShoutMode && !NetworkedPlayer.HasReasonToSendAudio)
            {
                return;
            }

            SilentForHowLong++; //how long in sample size this way on the remote side
#endif
        }
    }
}
