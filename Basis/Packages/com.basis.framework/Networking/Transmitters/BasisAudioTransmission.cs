using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Profiler;
using OpusSharp.Core;
using static SerializableBasis;

namespace Basis.Scripts.Networking.Transmitters
{
    [System.Serializable]
    public class BasisAudioTransmission
    {
        public OpusEncoder encoder;
        public BasisNetworkPlayer NetworkedPlayer;
        public BasisLocalPlayer Local;
        public bool HasEvents = false;
        public AudioSegmentDataMessage AudioSegmentData = new AudioSegmentDataMessage();
        public AudioSegmentDataMessage SilentSegmentData = new AudioSegmentDataMessage();
        public NetDataWriter writer = new NetDataWriter();
        public int SilentForHowLong = 0;
        public void Initialize(BasisNetworkPlayer networkedPlayer)
        {
            NetworkedPlayer = networkedPlayer;
            Local = (BasisLocalPlayer)networkedPlayer.Player;

            InitializeEncoder();
            AttachMicrophoneEvents();
            InitializeBuffers();
        }

        public void DeInitialize()
        {
            if (HasEvents)
            {
                DetachMicrophoneEvents();
            }

            encoder?.Dispose();
            encoder = null;
        }

        private void InitializeEncoder()
        {
            encoder = new OpusEncoder(
                LocalOpusSettings.MicrophoneSampleRate,
                LocalOpusSettings.Channels,
                LocalOpusSettings.OpusApplication
            );

            // Example: Configure Opus encoder here (optional)
            // int complexity = 5;
            // encoder.Ctl(EncoderCTL.OPUS_SET_COMPLEXITY, ref complexity);
        }

        private void AttachMicrophoneEvents()
        {
            if (HasEvents)
            {
                return;
            }

            BasisLocalMicrophoneDriver.OnHasAudio += OnAudioReady;
            BasisLocalMicrophoneDriver.OnHasSilence += SendSilenceOverNetwork;

            HasEvents = true;
        }

        private void DetachMicrophoneEvents()
        {
            BasisLocalMicrophoneDriver.OnHasAudio -= OnAudioReady;
            BasisLocalMicrophoneDriver.OnHasSilence -= SendSilenceOverNetwork;

            HasEvents = false;
        }

        private void InitializeBuffers()
        {
            int packetSize = BasisLocalMicrophoneDriver.PacketSize;

            if (packetSize != AudioSegmentData.TotalLength)
            {
                AudioSegmentData = new AudioSegmentDataMessage(new byte[packetSize]);
            }

            if (packetSize != SilentSegmentData.TotalLength)
            {
                SilentSegmentData = new AudioSegmentDataMessage(new byte[packetSize]);
            }
        }
        public void OnAudioReady()
        {
            if (!NetworkedPlayer.HasReasonToSendAudio)
            {
                return;
            }

            InitializeBuffers();

            writer.Reset();

            AudioSegmentData.LengthUsed = encoder.Encode(BasisLocalMicrophoneDriver.processBufferArray,BasisLocalMicrophoneDriver.SampleRate,AudioSegmentData.buffer,AudioSegmentData.TotalLength);

            if(SilentForHowLong > 256)
            {
                AudioSegmentData.TotalPlayedInSilence = 0;
            }
            else
            {
                AudioSegmentData.TotalPlayedInSilence = (byte)SilentForHowLong;
            }
            AudioSegmentData.Serialize(writer);

            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.AudioSegmentData, AudioSegmentData.LengthUsed);
            SendOutVoice(writer);
            SilentForHowLong = 0;
        }

        private void SendSilenceOverNetwork()
        {
            if (!NetworkedPlayer.HasReasonToSendAudio)
            {
                return;
            }

            SilentForHowLong++; //how long in sample size this way on the remote side
        }

        public void SendOutVoice(NetDataWriter writer)
        {
            BasisNetworkConnection.LocalPlayerPeer.Send(writer, BasisNetworkCommons.VoiceChannel, DeliveryMethod.Sequenced);
            if (BasisLocalPlayer.Instance != null)
            {
                BasisLocalPlayer.Instance.AudioReceived?.Invoke();
            }
        }
    }
}
