/* Copyright (c) 2024 Logan Stromberg

   Redistribution and use in source and binary forms, with or without
   modification, are permitted provided that the following conditions
   are met:

   - Redistributions of source code must retain the above copyright
   notice, this list of conditions and the following disclaimer.

   - Redistributions in binary form must reproduce the above copyright
   notice, this list of conditions and the following disclaimer in the
   documentation and/or other materials provided with the distribution.

   - Neither the name of Internet Society, IETF or IETF Trust, nor the
   names of specific contributors, may be used to endorse or promote
   products derived from this software without specific prior written
   permission.

   THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
   ``AS IS'' AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
   LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
   A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER
   OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL,
   EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO,
   PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR
   PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
   LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
   NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
   SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
*/

using Concentus.Enums;
using Concentus.Structs;
using System;

namespace Concentus
{
    /// <summary>
    /// The Opus multistream encoder structure.
    /// 
    /// Multistream encoding is an aggregate of several internal encoders and extra logic to pack multiple frames into
    /// single packets and map them to the correct channels. The behavior of a multistream encoder is functionally the
    /// same as a single encoder in most other respects.
    /// </summary>
    public interface IOpusMultiStreamEncoder : IDisposable
    {
        /// <summary>
        /// Encodes a multistream Opus frame.
        /// </summary>
        /// <param name="in_pcm">Input signal, interleaved to the total number of surround channels, according to Vorbis channel layouts.
        /// Length should be at least (# of samples) * (# of channels) for a given single frame size (maximum 120ms).</param>
        /// <param name="frame_size">The number of samples per channel in the inpus signal.
        /// The frame size must be a valid Opus framesize for the given sample rate.</param>
        /// <param name="out_data">Destination buffer for the output payload. This must contain at least max_data_bytes</param>
        /// <param name="max_data_bytes">The maximum amount of space allocated for the output payload. This may be used to impose
        /// an upper limit on the instant bitrate, but should not be used as the only bitrate control (use the Bitrate parameter for that)</param>
        /// <returns>The length of the encoded packet, in bytes. This value will always be less than or equal to 1275, the maximum Opus packet size.</returns>
        int EncodeMultistream(ReadOnlySpan<float> in_pcm, int frame_size, Span<byte> out_data, int max_data_bytes);

        /// <summary>
        /// Encodes a multistream Opus frame.
        /// </summary>
        /// <param name="in_pcm">Input signal, interleaved to the total number of surround channels, according to Vorbis channel layouts.
        /// Length should be at least (# of samples) * (# of channels) for a given single frame size (maximum 120ms).</param>
        /// <param name="frame_size">The number of samples per channel in the inpus signal.
        /// The frame size must be a valid Opus framesize for the given sample rate.</param>
        /// <param name="out_data">Destination buffer for the output payload. This must contain at least max_data_bytes</param>
        /// <param name="max_data_bytes">The maximum amount of space allocated for the output payload. This may be used to impose
        /// an upper limit on the instant bitrate, but should not be used as the only bitrate control (use the Bitrate parameter for that)</param>
        /// <returns>The length of the encoded packet, in bytes. This value will always be less than or equal to 1275, the maximum Opus packet size.</returns>
        int EncodeMultistream(ReadOnlySpan<short> in_pcm, int frame_size, Span<byte> out_data, int max_data_bytes);

        /// <summary>
        /// Resets the state of this encoder, usually to prepare it for processing
        /// a new audio stream without reallocating.
        /// </summary>
        void ResetState();

        /// <summary>
        /// Gets the version string of the library backing this implementation.
        /// </summary>
        /// <returns>An arbitrary version string.</returns>
        string GetVersionString();

        /// <summary>
        /// Gets or sets the application (or signal type) of the input signal. This hints
        /// to the encoder what type of details we want to preserve in the encoding.
        /// This cannot be changed after the encoder has started
        /// </summary>
        OpusApplication Application { get; set; }

        /// <summary>
        /// Gets or sets the "preferred" encoded bandwidth. This does not affect the sample rate of the input audio,
        /// only the encoding cutoffs
        /// </summary>
        OpusBandwidth Bandwidth { get; set; }

        /// <summary>
        /// Gets or sets the bitrate for encoder, in bits per second. Valid bitrates are between 6K (6144) and 510K (522240)
        /// </summary>
        int Bitrate { get; set; }

        /// <summary>
        /// Gets or sets the encoder complexity, between 0 and 10
        /// </summary>
        int Complexity { get; set; }

        /// <summary>
        /// Gets the number of channels that this encoder expects in its input. Always constant for the lifetime of the decoder.
        /// </summary>
        int NumChannels { get; }

        /// <summary>
        /// Gets or sets a fixed length for each encoded frame. Typically, the encoder just chooses a frame duration based on the input length
        /// and the current internal mode. This can be used to enforce an exact length if it is required by your application (e.g. monotonous transmission)
        /// </summary>
        OpusFramesize ExpertFrameDuration { get; set; }

        /// <summary>
        /// Returns the final range of the entropy coder. If you need this then I also assume you know what it's for.
        /// </summary>
        uint FinalRange { get; }

        /// <summary>
        /// Gets or sets a user-forced mode for the encoder. There are three modes, SILK, HYBRID, and CELT. Silk can only encode below 40Kbit/s and is best suited
        /// for speech. Silk also has modes such as FEC which may be desirable. Celt sounds better at higher bandwidth and is comparable to AAC. It also performs somewhat faster.
        /// Hybrid is used to create a smooth transition between the two modes. Note that this value may not always be honored due to other factors such
        /// as frame size and bitrate.
        /// </summary>
        OpusMode ForceMode { set; }

        /// <summary>
        /// Gets the number of samples of audio that are being stored in a buffer and are therefore contributing to latency.
        /// </summary>
        int Lookahead { get; }

        /// <summary>
        /// Gets or sets the bit resolution of the input audio signal. Though the encoder always uses 16-bit internally, this can help
        /// it make better decisions about bandwidth and cutoff values
        /// </summary>
        int LSBDepth { get; set; }

        /// <summary>
        /// Gets or sets the maximum bandwidth to be used by the encoder. This can be used if
        /// high-frequency audio is not important to your application (e.g. telephony)
        /// </summary>
        OpusBandwidth MaxBandwidth { get; set; }

        /// <summary>
        /// Gets or sets the expected amount of packet loss in the transmission medium, from 0 to 100.
        /// Only applies if UseInbandFEC is also enabled, and the encoder is in SILK mode.
        /// </summary>
        int PacketLossPercent { get; set; }

        /// <summary>
        /// Gets or sets a flag to disable prediction, which does... something with the SILK codec
        /// </summary>
        bool PredictionDisabled { get; set; }

        /// <summary>
        /// Gets the encoder's input sample rate. This is fixed for the lifetime of the encoder.
        /// </summary>
        int SampleRate { get; }

        /// <summary>
        /// Gets or sets a hint to the encoder for what type of audio is being processed, voice or music.
        /// This is not set by the encoder itself i.e. it's not the result of any actual signal analysis.
        /// </summary>
        OpusSignal SignalType { get; set; }

        /// <summary>
        /// Gets or sets a flag to enable constrained VBR. This only applies when the encoder is in CELT mode (i.e. high bitrates)
        /// </summary>
        bool UseConstrainedVBR { get; set; }

        /// <summary>
        /// Gets or sets a flag to enable Discontinuous Transmission mode. This mode is only available in the SILK encoder
        /// (Bitrate &lt; 40Kbit/s and/or ForceMode == SILK). When enabled, the encoder detects silence and background noise
        /// and reduces the number of output packets, with up to 600ms in between separate packet transmissions.
        /// </summary>
        bool UseDTX { get; set; }

        /// <summary>
        /// Gets or sets a flag to enable Forward Error Correction. This mode is only available in the SILK encoder
        /// (Bitrate &lt; 40Kbit/s and/or ForceMode == SILK). When enabled, lost packets can be partially recovered
        /// by decoding data stored in the following packet.
        /// </summary>
        bool UseInbandFEC { get; set; }

        /// <summary>
        /// Gets or sets a flag to enable Variable Bitrate encoding. This is recommended as it generally improves audio quality
        /// with little impact on average bitrate
        /// </summary>
        bool UseVBR { get; set; }
    }
}