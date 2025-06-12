using System;

namespace OpusSharp.Core.Extensions
{
    /// <summary>
    /// Contains the <see cref="OpusEncoder"/> helper extensions.
    /// </summary>
    public static class OpusEncoderExtensions
    {
        /// <summary>
        /// Configures the encoder's intended application.
        /// </summary>
        /// <param name="encoder">The encoder state.</param>
        /// <param name="application">The application to set. Must be one of <see cref="OpusPredefinedValues.OPUS_APPLICATION_VOIP"/>, <see cref="OpusPredefinedValues.OPUS_APPLICATION_AUDIO"/> or <see cref="OpusPredefinedValues.OPUS_APPLICATION_RESTRICTED_LOWDELAY"/>.</param>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static void SetApplication(this OpusEncoder encoder, OpusPredefinedValues application)
        {
            encoder.Ctl(EncoderCTL.OPUS_SET_APPLICATION, (int)application);
        }

        /// <summary>
        /// Gets the encoder's configured application.
        /// </summary>
        /// <param name="encoder">The encoder state.</param>
        /// <returns>The encoder's configured application.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static OpusPredefinedValues GetApplication(this OpusEncoder encoder)
        {
            var value = (int)OpusPredefinedValues.OPUS_AUTO;
            encoder.Ctl(EncoderCTL.OPUS_GET_APPLICATION, ref value);
            return (OpusPredefinedValues)value;
        }

        /// <summary>
        /// Configures the bitrate in the encoder.
        /// </summary>
        /// <param name="encoder">The encoder state.</param>
        /// <param name="bitRate">The bitRate to set.</param>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static void SetBitRate(this OpusEncoder encoder, int bitRate)
        {
            encoder.Ctl(EncoderCTL.OPUS_SET_BITRATE, bitRate);
        }

        /// <summary>
        /// Gets the encoder's bitrate configuration.
        /// </summary>
        /// <param name="encoder">The encoder state.</param>
        /// <returns>The encoder's bitrate.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static int GetBitRate(this OpusEncoder encoder)
        {
            var value = 0;
            encoder.Ctl(EncoderCTL.OPUS_GET_BITRATE, ref value);
            return value;
        }

        /// <summary>
        /// Configures the maximum bandpass that the encoder will select automatically.
        /// </summary>
        /// <param name="encoder">The encoder state.</param>
        /// <param name="maxBandwidth">The max bandwidth to set.</param>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static void SetMaxBandwidth(this OpusEncoder encoder, int maxBandwidth)
        {
            encoder.Ctl(EncoderCTL.OPUS_SET_MAX_BANDWIDTH, maxBandwidth);
        }

        /// <summary>
        /// Configures the maximum bandpass that the encoder will select automatically.
        /// </summary>
        /// <param name="encoder">The encoder state.</param>
        /// <returns>The encoder's max bandwidth.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static int GetMaxBandwidth(this OpusEncoder encoder)
        {
            var value = 0;
            encoder.Ctl(EncoderCTL.OPUS_GET_MAX_BANDWIDTH, ref value);
            return value;
        }

        /// <summary>
        /// Enables or disables variable bitrate (VBR) in the encoder.
        /// </summary>
        /// <param name="encoder">The encoder state.</param>
        /// <param name="enabled">Whether to enable/disable the encoder's vbr.</param>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static void SetVbr(this OpusEncoder encoder, bool enabled)
        {
            encoder.Ctl(EncoderCTL.OPUS_SET_VBR, enabled ? 1 : 0);
        }
        
        /// <summary>
        /// Enables or disables variable bitrate (VBR) in the encoder.
        /// </summary>
        /// <param name="encoder">The encoder state.</param>
        /// <returns>Whether the vbr is enabled or not.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static bool GetVbr(this OpusEncoder encoder)
        {
            var enabled = 0;
            encoder.Ctl(EncoderCTL.OPUS_GET_VBR, ref enabled);
            return enabled == 1;
        }

        /// <summary>
        /// Sets the encoder's bandpass to a specific value.
        /// </summary>
        /// <param name="encoder">The encoder state.</param>
        /// <param name="bandwidth">The bandwidth to set.</param>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static void SetBandwidth(this OpusEncoder encoder, OpusPredefinedValues bandwidth)
        {
            encoder.Ctl(EncoderCTL.OPUS_SET_BANDWIDTH, (int)bandwidth);
        }

        /// <summary>
        /// Configures the encoder's computational complexity.
        /// </summary>
        /// <param name="encoder">The encoder state.</param>
        /// <param name="complexity">The complexity to set between 1-10.</param>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static void SetComplexity(this OpusEncoder encoder, int complexity)
        {
            encoder.Ctl(EncoderCTL.OPUS_SET_COMPLEXITY, complexity);
        }

        /// <summary>
        /// Gets the encoder's complexity configuration.
        /// </summary>
        /// <param name="encoder">The encoder state.</param>
        /// <returns>The encoder's complexity.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static int GetComplexity(this OpusEncoder encoder)
        {
            var value = 0;
            encoder.Ctl(EncoderCTL.OPUS_GET_COMPLEXITY, ref value);
            return value;
        }

        /// <summary>
        /// Configures the encoder's use of in-band forward error correction (FEC).
        /// </summary>
        /// <param name="encoder">The encoder state.</param>
        /// <param name="mode">The FEC mode to set.</param>
        /// <remarks>0: Disable in-band FEC (default). 1: In-band FEC enabled. If the packet loss rate is sufficiently high, Opus will automatically switch to SILK even at high rates to enable use of that FEC. 2: In-band FEC enabled, but does not necessarily switch to SILK if we have music.</remarks>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static void SetInbandFec(this OpusEncoder encoder, int mode)
        {
            encoder.Ctl(EncoderCTL.OPUS_SET_INBAND_FEC, mode);
        }

        /// <summary>
        /// Gets encoder's configured use of in-band forward error correction.
        /// </summary>
        /// <param name="encoder">The encoder state.</param>
        /// <returns>The encoder's configured use of forward error correction.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static int GetInbandFec(this OpusEncoder encoder)
        {
            var value = 0;
            encoder.Ctl(EncoderCTL.OPUS_GET_INBAND_FEC, ref value);
            return value;
        }

        /// <summary>
        /// Configures the encoder's expected packet loss percentage.
        /// </summary>
        /// <param name="encoder">The encoder state.</param>
        /// <param name="percent">The expected packet loss percentage value between 0-100.</param>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static void SetPacketLostPercent(this OpusEncoder encoder, int percent)
        {
            encoder.Ctl(EncoderCTL.OPUS_SET_PACKET_LOSS_PERC, percent);
        }
        
        /// <summary>
        /// Gets the encoder's configured packet loss percentage.
        /// </summary>
        /// <param name="encoder">The encoder state.</param>
        /// <returns>The encoder's packet loss percentage.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static int GetPacketLostPercent(this OpusEncoder encoder)
        {
            var value = 0;
            encoder.Ctl(EncoderCTL.OPUS_GET_PACKET_LOSS_PERC, ref value);
            return value;
        }

        /// <summary>
        /// Configures the encoder's use of discontinuous transmission (DTX).
        /// </summary>
        /// <param name="encoder">The encoder state.</param>
        /// <param name="enabled">Whether to enable the DTX or not.</param>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static void SetDtx(this OpusEncoder encoder, bool enabled)
        {
            encoder.Ctl(EncoderCTL.OPUS_SET_DTX, enabled ? 1 : 0);
        }

        /// <summary>
        /// Gets encoder's configured use of discontinuous transmission.
        /// </summary>
        /// <param name="encoder">The encoder state.</param>
        /// <returns>Whether DTX is enabled or not.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static bool GetDtx(this OpusEncoder encoder)
        {
            var enabled = 0;
            encoder.Ctl(EncoderCTL.OPUS_GET_DTX, ref enabled);
            return enabled == 1;
        }

        /// <summary>
        /// Enables or disables constrained VBR in the encoder.
        /// </summary>
        /// <param name="encoder">The encoder state.</param>
        /// <param name="enabled">Whether to enable the constrained VBR or not.</param>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static void SetVbrConstraint(this OpusEncoder encoder, bool enabled)
        {
            encoder.Ctl(EncoderCTL.OPUS_SET_VBR_CONSTRAINT, enabled ? 1 : 0);
        }

        /// <summary>
        /// Determine if constrained VBR is enabled in the encoder.
        /// </summary>
        /// <param name="encoder">The encoder state.</param>
        /// <returns>Whether the constrained VBR is enabled or not.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static bool GetVbrConstraint(this OpusEncoder encoder)
        {
            var enabled = 0;
            encoder.Ctl(EncoderCTL.OPUS_GET_VBR_CONSTRAINT, ref enabled);
            return enabled == 1;
        }

        /// <summary>
        /// Configures mono/stereo forcing in the encoder.
        /// </summary>
        /// <param name="encoder">The encoder state.</param>
        /// <param name="channels">The number of channels to set.</param>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static void SetForceChannels(this OpusEncoder encoder, int channels)
        {
            encoder.Ctl(EncoderCTL.OPUS_SET_FORCE_CHANNELS, channels);
        }

        /// <summary>
        /// Gets the encoder's forced channel configuration.
        /// </summary>
        /// <param name="encoder">The encoder state.</param>
        /// <returns>The number of channels enforced by the encoder.</returns>
        /// <remarks>By default, this is set to <see cref="OpusPredefinedValues.OPUS_AUTO"/>.</remarks>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static int GetForceChannels(this OpusEncoder encoder)
        {
            var value = 0;
            encoder.Ctl(EncoderCTL.OPUS_GET_FORCE_CHANNELS, ref value);
            return value;
        }

        /// <summary>
        /// Configures the type of signal being encoded.
        /// </summary>
        /// <param name="encoder">The encoder state.</param>
        /// <param name="signal">The signal type to set. Must be one of <see cref="OpusPredefinedValues.OPUS_AUTO"/>, <see cref="OpusPredefinedValues.OPUS_SIGNAL_VOICE"/> or <see cref="OpusPredefinedValues.OPUS_SIGNAL_MUSIC"/>.</param>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static void SetSignal(this OpusEncoder encoder, OpusPredefinedValues signal)
        {
            encoder.Ctl(EncoderCTL.OPUS_SET_SIGNAL, (int)signal);
        }

        /// <summary>
        /// Gets the encoder's configured signal type.
        /// </summary>
        /// <param name="encoder">The encoder state.</param>
        /// <returns>The encoder's signal type.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static OpusPredefinedValues GetSignal(this OpusEncoder encoder)
        {
            var value = (int)OpusPredefinedValues.OPUS_AUTO;
            encoder.Ctl(EncoderCTL.OPUS_GET_SIGNAL, ref value);
            return (OpusPredefinedValues)value;
        }

        /// <summary>
        /// Gets the total samples of delay added by the entire codec.
        /// </summary>
        /// <param name="encoder">The encoder state.</param>
        /// <returns>The total samples of delay added by the entire codec.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static int GetLookahead(this OpusEncoder encoder)
        {
            var value = 0;
            encoder.Ctl(EncoderCTL.OPUS_GET_LOOKAHEAD, ref value);
            return value;
        }

        /// <summary>
        /// Configures the depth of signal being encoded.
        /// </summary>
        /// <param name="encoder">The encoder state.</param>
        /// <param name="depth">The LSB depth to set.</param>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static void SetLsbDepth(this OpusEncoder encoder, int depth)
        {
            encoder.Ctl(EncoderCTL.OPUS_SET_LSB_DEPTH, depth);
        }

        /// <summary>
        /// Gets the encoder's configured signal depth.
        /// </summary>
        /// <param name="encoder">The encoder state.</param>
        /// <returns>The encoder's LSB depth.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static int GetLsbDepth(this OpusEncoder encoder)
        {
            var value = 0;
            encoder.Ctl(EncoderCTL.OPUS_GET_LSB_DEPTH, ref value);
            return value;
        }

        /// <summary>
        /// Configures the encoder's use of variable duration frames.
        /// </summary>
        /// <param name="encoder">The encoder state.</param>
        /// <param name="expertDuration">The frame duration to set. Must be one of <see cref="OpusPredefinedValues.OPUS_FRAMESIZE_ARG"/>, <see cref="OpusPredefinedValues.OPUS_FRAMESIZE_2_5_MS"/>, <see cref="OpusPredefinedValues.OPUS_FRAMESIZE_5_MS"/>, <see cref="OpusPredefinedValues.OPUS_FRAMESIZE_10_MS"/>, <see cref="OpusPredefinedValues.OPUS_FRAMESIZE_20_MS"/>, <see cref="OpusPredefinedValues.OPUS_FRAMESIZE_40_MS"/>, <see cref="OpusPredefinedValues.OPUS_FRAMESIZE_60_MS"/>, <see cref="OpusPredefinedValues.OPUS_FRAMESIZE_80_MS"/>, <see cref="OpusPredefinedValues.OPUS_FRAMESIZE_100_MS"/> or <see cref="OpusPredefinedValues.OPUS_FRAMESIZE_120_MS"/></param>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static void SetExpertFrameDuration(this OpusEncoder encoder, OpusPredefinedValues expertDuration)
        {
            encoder.Ctl(EncoderCTL.OPUS_SET_EXPERT_FRAME_DURATION, (int)expertDuration);
        }

        /// <summary>
        /// Gets the encoder's configured use of variable duration frames.
        /// </summary>
        /// <param name="encoder">The encoder state.</param>
        /// <returns>The encoder's configured expert frame duration.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static OpusPredefinedValues GetExpertFrameDuration(this OpusEncoder encoder)
        {
            var value = (int)OpusPredefinedValues.OPUS_AUTO;
            encoder.Ctl(EncoderCTL.OPUS_GET_EXPERT_FRAME_DURATION, ref value);
            return (OpusPredefinedValues)value;
        }

        /// <summary>
        /// If set to true, disables almost all use of prediction, making frames almost completely independent. This reduces quality.
        /// </summary>
        /// <param name="encoder">The encoder state.</param>
        /// <param name="disabled">Whether to disable to encoder's prediction.</param>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static void SetPredictionDisabled(this OpusEncoder encoder, bool disabled)
        {
            encoder.Ctl(EncoderCTL.OPUS_SET_PREDICTION_DISABLED, disabled ? 1 : 0);
        }

        /// <summary>
        /// Gets the encoder's configured prediction status.
        /// </summary>
        /// <param name="encoder">The encoder state.</param>
        /// <returns>Whether the encoder's prediction is disabled or not.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static bool GetPredictionDisabled(this OpusEncoder encoder)
        {
            var value = 0;
            encoder.Ctl(EncoderCTL.OPUS_GET_PREDICTION_DISABLED, ref value);
            return value == 1;
        }

        /// <summary>
        /// Gets the DTX state of the encoder.
        /// </summary>
        /// <param name="encoder">The encoder state.</param>
        /// <returns>The dtx state of the encoder.</returns>
        /// <remarks>I have no idea how this works even by looking at the source code, it seems to do something with speech but that's as far as I've gotten. (Sine)</remarks>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static int GetInDtx(this OpusEncoder encoder)
        {
            var value = 0;
            encoder.Ctl(EncoderCTL.OPUS_GET_IN_DTX, ref value);
            return value;
        }
        
        /// <summary>
        /// Resets the codec state to be equivalent to a freshly initialized state.
        /// </summary>
        /// <param name="encoder">The encoder state.</param>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static void Reset(this OpusEncoder encoder)
        {
            encoder.Ctl(GenericCTL.OPUS_RESET_STATE);
        }

        /// <summary>
        /// Gets the final state of the codec's entropy coder.
        /// </summary>
        /// <param name="encoder">The encoder state.</param>
        /// <returns>The final state of the codec's entropy coder.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static uint GetFinalRange(this OpusEncoder encoder)
        {
            var finalRange = 0u;
            encoder.Ctl(GenericCTL.OPUS_GET_FINAL_RANGE, ref finalRange);
            return finalRange;
        }

        /// <summary>
        /// Gets the encoder's last bandpass.
        /// </summary>
        /// <param name="encoder">The encoder state.</param>
        /// <returns>The encoder's last bandpass.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static OpusPredefinedValues GetBandwidth(this OpusEncoder encoder)
        {
            var bandPass = 0;
            encoder.Ctl(GenericCTL.OPUS_GET_BANDWIDTH, ref bandPass);
            return (OpusPredefinedValues)bandPass;
        }

        /// <summary>
        /// Gets the sampling rate the encoder was initialized with.
        /// </summary>
        /// <param name="encoder">The encoder state.</param>
        /// <returns>The encoder's configured sample rate.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static int GetSampleRate(this OpusEncoder encoder)
        {
            var sampleRate = 0;
            encoder.Ctl(GenericCTL.OPUS_GET_SAMPLE_RATE, ref sampleRate);
            return sampleRate;
        }

        /// <summary>
        /// If set to true, disables the use of phase inversion for intensity stereo, improving the quality of mono down-mixes, but slightly reducing normal stereo quality.
        /// </summary>
        /// <param name="encoder">The encoder state.</param>
        /// <param name="disabled">Whether to disable or not.</param>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static void SetPhaseInversionDisabled(this OpusEncoder encoder, bool disabled)
        {
            encoder.Ctl(GenericCTL.OPUS_SET_PHASE_INVERSION_DISABLED, disabled ? 1 : 0);
        }

        /// <summary>
        /// Gets the encoder's configured phase inversion status.
        /// </summary>
        /// <param name="encoder">The encoder state.</param>
        /// <returns>Whether the phase inversion is disabled or not.</returns>
        /// <exception cref="OpusException" />
        /// <exception cref="ObjectDisposedException" />
        public static bool GetPhaseInversionDisabled(this OpusEncoder encoder)
        {
            var disabled = 0;
            encoder.Ctl(GenericCTL.OPUS_GET_PHASE_INVERSION_DISABLED, ref disabled);
            return disabled == 1;
        }
    }
}