// ReSharper disable InconsistentNaming
// ReSharper disable IdentifierTypo
namespace OpusSharp.Core
{
    /// <summary>
    /// Error codes for opus.
    /// </summary>
    public enum OpusErrorCodes
    {
        /// <summary>
        /// No error.
        /// </summary>
        OPUS_OK = 0,

        /// <summary>
        /// One or more invalid/out of range arguments.
        /// </summary>
        OPUS_BAD_ARG = -1,

        /// <summary>
        /// Not enough bytes allocated in the buffer.
        /// </summary>
        OPUS_BUFFER_TOO_SMALL = -2,

        /// <summary>
        /// An internal error was detected.
        /// </summary>
        OPUS_INTERNAL_ERROR = -3,

        /// <summary>
        /// The compressed data passed is corrupted.
        /// </summary>
        OPUS_INVALID_PACKET = -4,

        /// <summary>
        /// Invalid/unsupported request number.
        /// </summary>
        OPUS_UNIMPLEMENTED = -5,

        /// <summary>
        /// An encoder or decoder structure is invalid or already freed.
        /// </summary>
        OPUS_INVALID_STATE = -6,

        /// <summary>
        /// Memory allocation has failed.
        /// </summary>
        OPUS_ALLOC_FAIL = -7
    }

    /// <summary>
    /// Pre-defined values for CTL interface.
    /// </summary>
    public enum OpusPredefinedValues
    {
        /// <summary>
        /// Auto/default setting.
        /// </summary>
        OPUS_AUTO = -1000,

        /// <summary>
        /// Maximum bitrate.
        /// </summary>
        OPUS_BITRATE_MAX = -1,

        /// <summary>
        /// Best for most VoIP/Video Conference applications where listening quality and intelligibility matter most.
        /// </summary>
        OPUS_APPLICATION_VOIP = 2048,

        /// <summary>
        /// Best for broadcast/high-fidelity application where the decoded audio should be as close as possible to the input.
        /// </summary>
        OPUS_APPLICATION_AUDIO = 2049,

        /// <summary>
        /// Only use when lowest-achievable latency is what matters most. Voice-optimized modes cannot be used.
        /// </summary>
        OPUS_APPLICATION_RESTRICTED_LOWDELAY = 2051,

        /// <summary>
        /// Signal being encoded is voice.
        /// </summary>
        OPUS_SIGNAL_VOICE = 3001,

        /// <summary>
        /// Signal being encoded is music.
        /// </summary>
        OPUS_SIGNAL_MUSIC = 3002,

        /// <summary>
        /// 4 kHz bandpass.
        /// </summary>
        OPUS_BANDWIDTH_NARROWBAND = 1101,

        /// <summary>
        /// 6 kHz bandpass.
        /// </summary>
        OPUS_BANDWIDTH_MEDIUMBAND = 1102,

        /// <summary>
        /// 8 kHz bandpass.
        /// </summary>
        OPUS_BANDWIDTH_WIDEBAND = 1103,

        /// <summary>
        /// 12 kHz bandpass.
        /// </summary>
        OPUS_BANDWIDTH_SUPERWIDEBAND = 1104,

        /// <summary>
        /// 20 kHz bandpass.
        /// </summary>
        OPUS_BANDWIDTH_FULLBAND = 1105,

        /// <summary>
        /// Select frame size from the argument (default).
        /// </summary>
        OPUS_FRAMESIZE_ARG = 5000,

        /// <summary>
        /// Use 2.5 ms frames.
        /// </summary>
        OPUS_FRAMESIZE_2_5_MS = 5001,

        /// <summary>
        /// Use 5 ms frames.
        /// </summary>
        OPUS_FRAMESIZE_5_MS = 5002,

        /// <summary>
        /// Use 10 ms frames.
        /// </summary>
        OPUS_FRAMESIZE_10_MS = 5003,

        /// <summary>
        /// Use 20 ms frames.
        /// </summary>
        OPUS_FRAMESIZE_20_MS = 5004,

        /// <summary>
        /// Use 40 ms frames.
        /// </summary>
        OPUS_FRAMESIZE_40_MS = 5005,

        /// <summary>
        /// Use 60 ms frames.
        /// </summary>
        OPUS_FRAMESIZE_60_MS = 5006,

        /// <summary>
        /// Use 80 ms frames.
        /// </summary>
        OPUS_FRAMESIZE_80_MS = 5007,

        /// <summary>
        /// Use 100 ms frames.
        /// </summary>
        OPUS_FRAMESIZE_100_MS = 5008,

        /// <summary>
        /// Use 120 ms frames.
        /// </summary>
        OPUS_FRAMESIZE_120_MS = 5009
    }

    /// <summary>
    /// These macros are used with the opus_decoder_ctl and opus_encoder_ctl calls to generate a particular request.
    /// </summary>
    public enum GenericCTL
    {
        /// <summary>
        /// Resets the codec state to be equivalent to a freshly initialized state.
        /// </summary>
        OPUS_RESET_STATE = 4028,

        /// <summary>
        /// Gets the final state of the codec's entropy coder.
        /// </summary>
        OPUS_GET_FINAL_RANGE = 4031,

        /// <summary>
        /// Gets the encoder's configured bandpass or the decoder's last bandpass.
        /// </summary>
        OPUS_GET_BANDWIDTH = 4009,

        /// <summary>
        /// Gets the sampling rate the encoder or decoder was initialized with.
        /// </summary>
        OPUS_GET_SAMPLE_RATE = 4029,

        /// <summary>
        /// If set to 1, disables the use of phase inversion for intensity stereo, improving the quality of mono down-mixes, but slightly reducing normal stereo quality.
        /// </summary>
        OPUS_SET_PHASE_INVERSION_DISABLED = 4046,

        /// <summary>
        /// Gets the decoder/encoder's configured phase inversion status.
        /// </summary>
        OPUS_GET_PHASE_INVERSION_DISABLED = 4047,
    }

    /// <summary>
    /// These are convenience macros for use with the opus_encoder_ctl interface.
    /// </summary>
    public enum EncoderCTL
    {
        /// <summary>
        /// Configures the encoder's intended application.
        /// </summary>
        OPUS_SET_APPLICATION = 4000,

        /// <summary>
        /// Gets the encoder's configured application.
        /// </summary>
        OPUS_GET_APPLICATION = 4001,

        /// <summary>
        /// Configures the bitrate in the encoder.
        /// </summary>
        OPUS_SET_BITRATE = 4002,

        /// <summary>
        /// Gets the encoder's bitrate configuration.
        /// </summary>
        OPUS_GET_BITRATE = 4003,

        /// <summary>
        /// Configures the maximum bandpass that the encoder will select automatically.
        /// </summary>
        OPUS_SET_MAX_BANDWIDTH = 4004,

        /// <summary>
        /// Gets the encoder's configured maximum allowed bandpass.
        /// </summary>
        OPUS_GET_MAX_BANDWIDTH = 4005,

        /// <summary>
        /// Enables or disables variable bitrate (VBR) in the encoder.
        /// </summary>
        OPUS_SET_VBR = 4006,

        /// <summary>
        /// Determine if variable bitrate (VBR) is enabled in the encoder.
        /// </summary>
        OPUS_GET_VBR = 4007,

        /// <summary>
        /// Sets the encoder's bandpass to a specific value.
        /// </summary>
        OPUS_SET_BANDWIDTH = 4008,

        /// <summary>
        /// Configures the encoder's computational complexity.
        /// </summary>
        OPUS_SET_COMPLEXITY = 4010,

        /// <summary>
        /// Gets the encoder's complexity configuration.
        /// </summary>
        OPUS_GET_COMPLEXITY = 4011,

        /// <summary>
        /// Configures the encoder's use of in-band forward error correction (FEC).
        /// </summary>
        OPUS_SET_INBAND_FEC = 4012,

        /// <summary>
        /// Gets encoder's configured use of in-band forward error correction.
        /// </summary>
        OPUS_GET_INBAND_FEC = 4013,

        /// <summary>
        /// Configures the encoder's expected packet loss percentage.
        /// </summary>
        OPUS_SET_PACKET_LOSS_PERC = 4014,

        /// <summary>
        /// Gets the encoder's configured packet loss percentage.
        /// </summary>
        OPUS_GET_PACKET_LOSS_PERC = 4015,

        /// <summary>
        /// Configures the encoder's use of discontinuous transmission (DTX).
        /// </summary>
        OPUS_SET_DTX = 4016,

        /// <summary>
        /// Gets encoder's configured use of discontinuous transmission.
        /// </summary>
        OPUS_GET_DTX = 4017,

        /// <summary>
        /// Enables or disables constrained VBR in the encoder.
        /// </summary>
        OPUS_SET_VBR_CONSTRAINT = 4020,

        /// <summary>
        /// Determine if constrained VBR is enabled in the encoder.
        /// </summary>
        OPUS_GET_VBR_CONSTRAINT = 4021,

        /// <summary>
        /// Configures mono/stereo forcing in the encoder.
        /// </summary>
        OPUS_SET_FORCE_CHANNELS = 4022,

        /// <summary>
        /// Gets the encoder's forced channel configuration.
        /// </summary>
        OPUS_GET_FORCE_CHANNELS = 4023,

        /// <summary>
        /// Configures the type of signal being encoded.
        /// </summary>
        OPUS_SET_SIGNAL = 4024,

        /// <summary>
        /// Gets the encoder's configured signal type.
        /// </summary>
        OPUS_GET_SIGNAL = 4025,

        /// <summary>
        /// Gets the total samples of delay added by the entire codec.
        /// </summary>
        OPUS_GET_LOOKAHEAD = 4027,

        /// <summary>
        /// Configures the depth of signal being encoded.
        /// </summary>
        OPUS_SET_LSB_DEPTH = 4036,

        /// <summary>
        /// Gets the encoder's configured signal depth.
        /// </summary>
        OPUS_GET_LSB_DEPTH = 4037,

        /// <summary>
        /// Configures the encoder's use of variable duration frames.
        /// </summary>
        OPUS_SET_EXPERT_FRAME_DURATION = 4040,

        /// <summary>
        /// Gets the encoder's configured use of variable duration frames.
        /// </summary>
        OPUS_GET_EXPERT_FRAME_DURATION = 4041,

        /// <summary>
        /// If set to 1, disables almost all use of prediction, making frames almost completely independent. This reduces quality.
        /// </summary>
        OPUS_SET_PREDICTION_DISABLED = 4042,

        /// <summary>
        /// Gets the encoder's configured prediction status.
        /// </summary>
        OPUS_GET_PREDICTION_DISABLED = 4043,

        /// <summary>
        /// Gets the DTX state of the encoder.
        /// </summary>
        OPUS_GET_IN_DTX = 4049, //Encoder only, seems to be listed as generic in docs though.
        
        /// <summary>
        /// If non-zero, enables Deep Redundancy (DRED) and use the specified maximum number of 10-ms redundant frames.
        /// </summary>
        OPUS_SET_DRED_DURATION = 4050,

        /// <summary>
        /// Gets the encoder's configured Deep Redundancy (DRED) maximum number of frames.
        /// </summary>
        OPUS_GET_DRED_DURATION = 4051,

        /// <summary>
        /// Provide external DNN weights from binary object (only when explicitly built without the weights).
        /// </summary>
        OPUS_SET_DNN_BLOB = 4052,
    }

    /// <summary>
    /// These are convenience macros for use with the opus_decoder_ctl interface
    /// </summary>
    public enum DecoderCTL
    {
        /// <summary>
        /// Configures decoder gain adjustment.
        /// </summary>
        OPUS_SET_GAIN = 4034,

        /// <summary>
        /// Gets the decoder's configured gain adjustment.
        /// </summary>
        OPUS_GET_GAIN = 4045,/* Should have been 4035 */

        /// <summary>
        /// Gets the duration (in samples) of the last packet successfully decoded or concealed.
        /// </summary>
        OPUS_GET_LAST_PACKET_DURATION = 4039,

        /// <summary>
        /// Gets the pitch of the last decoded frame, if available.
        /// </summary>
        OPUS_GET_PITCH = 4033,
    }

    /// <summary>
    /// These are convenience macros that are specific to the opus_multistream_encoder_ctl() and opus_multistream_decoder_ctl() interface.
    /// </summary>
    public enum MultistreamCTL
    {
        /// <summary>
        /// Gets the encoder state for an individual stream of a multi-stream encoder.
        /// </summary>
        OPUS_MULTISTREAM_GET_ENCODER_STATE = 5120,

        /// <summary>
        /// Gets the decoder state for an individual stream of a multi-stream decoder.
        /// </summary>
        OPUS_MULTISTREAM_GET_DECODER_STATE = 5122
    }
}
