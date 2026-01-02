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
        public AudioSegmentDataMessage Segment = new AudioSegmentDataMessage();
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
#if UNITY_IOS && !UNITY_EDITOR
            encoder = new OpusEncoder(
                LocalOpusSettings.MicrophoneSampleRate,
                LocalOpusSettings.Channels,
                LocalOpusSettings.OpusApplication,
                use_static: true
            );
#else
            encoder = new OpusEncoder(
                LocalOpusSettings.MicrophoneSampleRate,
                LocalOpusSettings.Channels,
                LocalOpusSettings.OpusApplication,
                use_static: false
            );
#endif

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

            if (packetSize != Segment.TotalLength)
            {
                Segment = new AudioSegmentDataMessage();
                Segment.buffer = new byte[packetSize];
                Segment.TotalLength = packetSize;
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

            Segment.LengthUsed = encoder.Encode(BasisLocalMicrophoneDriver.processBufferArray,BasisLocalMicrophoneDriver.SampleRate,Segment.buffer,Segment.TotalLength);

            if(SilentForHowLong > 256)
            {
                Segment.TotalPlayedInSilence = 0;
            }
            else
            {
                Segment.TotalPlayedInSilence = (byte)SilentForHowLong;
            }
            Segment.Serialize(writer);

            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.AudioSegmentData, Segment.LengthUsed);
            BasisNetworkConnection.LocalPlayerPeer.Send(writer, BasisNetworkCommons.VoiceChannel, DeliveryMethod.Sequenced);
            if (BasisLocalPlayer.Instance != null)
            {
                BasisLocalPlayer.Instance.AudioReceived?.Invoke();
            }
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
    }
}
