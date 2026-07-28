using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Profiler;
using System;
using UnityEngine;
using static SerializableBasis;

namespace Basis.Scripts.Networking.Transmitters
{
    [System.Serializable]
    public class BasisAudioTransmission
    {
#if !UNITY_SERVER
        public OpusSharp.Core.Interfaces.IOpusEncoder encoder;
        private readonly object _encoderLock = new object();
        private OpusSharp.Core.Interfaces.IOpusEncoder _directEncoder;
        private AudioSegmentDataMessage _directSegment = new AudioSegmentDataMessage();
        private readonly NetDataWriter _directWriter = new NetDataWriter();
        private int _directAppliedBitrate;
#endif
        public BasisNetworkPlayer NetworkedPlayer;
        public BasisLocalPlayer Local;
        public bool HasEvents = false;
        public AudioSegmentDataMessage Segment = new AudioSegmentDataMessage();
        [System.NonSerialized] public NetDataWriter writer = new NetDataWriter();
        public byte _sequenceNumber = 0;
        public int SilentForHowLong = 0;

#if !UNITY_SERVER
        // 40ms-mode accumulator: the mic always delivers 20ms (960-sample) chunks via
        // OnAudioReady, but in 40ms mode the encoder needs 1920 samples per call. We
        // copy each tick's 960 valid samples into _frameAccum and only encode once it
        // is full. _frameAccumFilled tracks how many samples are currently buffered.
        private float[] _frameAccum;
        private int _frameAccumFilled;
        private volatile bool _accumResetRequested;
#endif
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
            LocalOpusSettings.OnBitrateChanged -= ApplyBitrate;
            SharedOpusSettings.OnDesiredDurationChanged -= ApplyFrameDuration;
            lock (_encoderLock)
            {
                encoder?.Dispose();
                encoder = null;
                _directEncoder?.Dispose();
                _directEncoder = null;
            }
            _frameAccum = null;
            _frameAccumFilled = 0;
#endif
        }

#if !UNITY_SERVER
        private void InitializeEncoder()
        {
            encoder = CreateConfiguredEncoder(LocalOpusSettings.ServerBitrate);
            LocalOpusSettings.OnPacketLossPercentChanged += ApplyPacketLossPerc;
            LocalOpusSettings.OnBitrateChanged += ApplyBitrate;
            SharedOpusSettings.OnDesiredDurationChanged += ApplyFrameDuration;
        }

        private OpusSharp.Core.Interfaces.IOpusEncoder CreateConfiguredEncoder(int bitrate)
        {
            OpusSharp.Core.Interfaces.IOpusEncoder created;
#if UNITY_IOS && !UNITY_EDITOR
            created = new OpusSharp.Core.Static.OpusEncoder(
                LocalOpusSettings.MicrophoneSampleRate,
                LocalOpusSettings.Channels,
                LocalOpusSettings.OpusApplication
            );
#else
            created = new OpusSharp.Core.Dynamic.OpusEncoder(
                LocalOpusSettings.MicrophoneSampleRate,
                LocalOpusSettings.Channels,
                LocalOpusSettings.OpusApplication
            );
#endif
            ApplyBitrateTo(created, bitrate);
            created.Ctl(OpusSharp.Core.EncoderCTL.OPUS_SET_COMPLEXITY, 5);
            // Forward Error Correction — embed a low-bitrate redundant copy of the
            // previous frame inside each packet. Combined with look-ahead decode on
            // the receiver, this lets a single-packet loss be reconstructed from the
            // next packet instead of falling back to PLC or silence.
            created.Ctl(OpusSharp.Core.EncoderCTL.OPUS_SET_INBAND_FEC, 1);
            int percent = LocalOpusSettings.PacketLossPercent;
            if (percent < 0) percent = 0;
            else if (percent > 100) percent = 100;
            created.Ctl(OpusSharp.Core.EncoderCTL.OPUS_SET_PACKET_LOSS_PERC, percent);
            return created;
        }

        /// <summary>
        /// Apply <see cref="LocalOpusSettings.ServerBitrate"/> to the live server encoder.
        /// Subscribed to <see cref="LocalOpusSettings.OnBitrateChanged"/>.
        /// </summary>
        private void ApplyBitrate(int bitrate)
        {
            lock (_encoderLock)
            {
                if (encoder == null) return;
                ApplyBitrateTo(encoder, bitrate);
            }
        }

        private static void ApplyBitrateTo(OpusSharp.Core.Interfaces.IOpusEncoder target, int bitrate)
        {
            if (bitrate < LocalOpusSettings.DefaultBitrate / 8) bitrate = LocalOpusSettings.DefaultBitrate / 8;
            try
            {
                target.Ctl(OpusSharp.Core.EncoderCTL.OPUS_SET_BITRATE, bitrate);
            }
            catch (OpusSharp.Core.OpusException ex)
            {
                BasisDebug.LogWarning($"Failed to set encoder bitrate: {ex.Message}", BasisDebug.LogTag.Voice);
            }
        }

        /// <summary>
        /// Reset the 40ms-mode accumulator when the frame duration changes. The mic
        /// keeps feeding 20ms chunks; whether OnAudioReady encodes immediately (20ms
        /// mode) or buffers two ticks first (40ms mode) is decided per-call from
        /// <see cref="SharedOpusSettings.DesiredDurationInSeconds"/>.
        /// </summary>
        private void ApplyFrameDuration(float durationSeconds)
        {
            _accumResetRequested = true;
        }

        /// <summary>
        /// Pushes OPUS_SET_PACKET_LOSS_PERC onto the live encoders without tearing
        /// them down. Safe to call multiple times; subscribed to
        /// <see cref="LocalOpusSettings.OnPacketLossPercentChanged"/> so an admin
        /// push updates FEC immediately on the already-running encoders.
        /// </summary>
        private void ApplyPacketLossPerc(int percent)
        {
            if (percent < 0) percent = 0;
            else if (percent > 100) percent = 100;
            try
            {
                lock (_encoderLock)
                {
                    if (encoder != null)
                        encoder.Ctl(OpusSharp.Core.EncoderCTL.OPUS_SET_PACKET_LOSS_PERC, percent);
                    if (_directEncoder != null)
                        _directEncoder.Ctl(OpusSharp.Core.EncoderCTL.OPUS_SET_PACKET_LOSS_PERC, percent);
                }
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
                _frameAccumFilled = 0;
                return;
            }

            InitializeBuffers();

#if !BASIS_DISABLE_MICROPHONE
            // The mic always delivers a 20 ms tick (BasisLocalMicrophoneDriver.ProcessFrameSize
            // = 960 valid samples). When the admin has pushed a 40 ms frame duration we
            // accumulate two ticks before encoding so each Opus packet covers 40 ms.
            int micChunk = BasisLocalMicrophoneDriver.ProcessFrameSize;
            int targetSamples = Mathf.CeilToInt(SharedOpusSettings.DesiredDurationInSeconds * LocalOpusSettings.MicrophoneSampleRate);

            if (targetSamples <= micChunk)
            {
                EncodeAndSend(BasisLocalMicrophoneDriver.processBufferArray, targetSamples);
                _frameAccumFilled = 0;
                SilentForHowLong = 0;
                return;
            }

            if (_accumResetRequested)
            {
                _accumResetRequested = false;
                _frameAccumFilled = 0;
            }

            if (_frameAccum == null || _frameAccum.Length != targetSamples)
            {
                _frameAccum = new float[targetSamples];
                _frameAccumFilled = 0;
            }

            int copy = Mathf.Min(micChunk, targetSamples - _frameAccumFilled);
            Array.Copy(BasisLocalMicrophoneDriver.processBufferArray, 0, _frameAccum, _frameAccumFilled, copy);
            _frameAccumFilled += copy;

            if (_frameAccumFilled < targetSamples)
            {
                return;
            }

            EncodeAndSend(_frameAccum, targetSamples);
            _frameAccumFilled = 0;
            SilentForHowLong = 0;
#endif
#endif
        }

#if !UNITY_SERVER
        private void EncodeAndSend(float[] pcm, int sampleCount)
        {
            try
            {
                writer.Reset();
                lock (_encoderLock)
                {
                    if (encoder == null) return;
                    Segment.LengthUsed = encoder.Encode(pcm, sampleCount, Segment.buffer, Segment.TotalLength);
                }
                byte sequence = _sequenceNumber++;

                // Saturate rather than wrap: a resume after a long mute must still read as
                // intentional silence (>0) on the receiver, which uses 0 to mean "the gap
                // was network loss/stall" when classifying underruns.
                byte silence = SilentForHowLong > 255 ? (byte)255 : (byte)SilentForHowLong;

                Segment.SequenceNumber = sequence;
                Segment.TotalPlayedInSilence = silence;
                Segment.Serialize(writer);

                var peer = BasisNetworkConnection.LocalPlayerPeer;
                if (peer == null)
                {
                    return;
                }
                byte channel = IsInShoutMode ? BasisNetworkCommons.ShoutVoiceChannel : BasisNetworkCommons.VoiceChannel;
                BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.AudioSegmentData, Segment.LengthUsed);
                peer.Send(writer, channel, DeliveryMethod.Unreliable);
                if (!IsInShoutMode)
                {
                    NetDataWriter directWriter = writer;
                    if (BasisP2PManager.HasAnyConnectedSession())
                    {
                        int directBitrate = LocalOpusSettings.DirectConnectBitrate;
                        if (directBitrate != LocalOpusSettings.ServerBitrate)
                        {
                            directWriter = EncodeForDirectConnect(pcm, sampleCount, directBitrate, sequence, silence);
                        }
                    }
                    if (directWriter != null)
                    {
                        BasisP2PManager.BroadcastVoiceViaP2P(directWriter);
                    }
                }
                if (BasisLocalPlayer.Instance != null)
                {
                    BasisLocalPlayer.Instance.AudioReceived?.Invoke();
                }
            }
            catch (Exception ex)
            {
                BasisDebug.LogErrorOnce($"Voice encode/send failed: {ex}", BasisDebug.LogTag.Voice);
            }
        }

        private NetDataWriter EncodeForDirectConnect(float[] pcm, int sampleCount, int bitrate, byte sequence, byte silence)
        {
            lock (_encoderLock)
            {
                if (encoder == null) return null;
                if (_directEncoder == null)
                {
                    _directEncoder = CreateConfiguredEncoder(bitrate);
                    _directAppliedBitrate = bitrate;
                }
                else if (_directAppliedBitrate != bitrate)
                {
                    ApplyBitrateTo(_directEncoder, bitrate);
                    _directAppliedBitrate = bitrate;
                }

                if (_directSegment.buffer == null || _directSegment.TotalLength != Segment.TotalLength)
                {
                    _directSegment.buffer = new byte[Segment.TotalLength];
                    _directSegment.TotalLength = Segment.TotalLength;
                }

                _directSegment.LengthUsed = _directEncoder.Encode(pcm, sampleCount, _directSegment.buffer, _directSegment.TotalLength);
                _directSegment.SequenceNumber = sequence;
                _directSegment.TotalPlayedInSilence = silence;
                _directWriter.Reset();
                _directSegment.Serialize(_directWriter);
                return _directWriter;
            }
        }
#endif

        private void SendSilenceOverNetwork()
        {
#if UNITY_SERVER
            return;
#else
            _frameAccumFilled = 0;

            if (!IsInShoutMode && !NetworkedPlayer.HasReasonToSendAudio)
            {
                return;
            }

            SilentForHowLong++; //how long in sample size this way on the remote side
#endif
        }
    }
}
