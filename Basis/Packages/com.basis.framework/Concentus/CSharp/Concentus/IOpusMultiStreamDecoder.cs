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
    /// The Opus multistream decoder structure.
    /// 
    /// Multistream decoding is an aggregate of several internal decoders and extra logic to parse multiple frames from
    /// single packets and map them to the correct channels. The behavior of a multistream decoder is functionally the
    /// same as a single decoder in most other respects.
    /// </summary>
    public interface IOpusMultiStreamDecoder : IDisposable
    {
        /// <summary>
        /// Decodes a multichannel Opus packet, putting the decoded audio into a floating-point buffer.
        /// </summary>
        /// <param name="data">The input payload. This may be empty if the previous packet was lost in transit (when PLC is enabled)</param>
        /// <param name="out_pcm">A buffer to put the output PCM. The output size is (# of samples) * (# of channels) for a given single frame size (maximum 120ms).</param>
        /// <param name="frame_size">The number of samples (per channel) of available space in the output PCM buf.
        /// It should contain at least enough space to contain (# of samples) * (# of channels) for a given single frame size (maximum 120ms).
        /// In the case of PLC (data == NULL) or FEC (decode_fec == true),
        /// then frame_size needs to be exactly the duration of the audio that is missing, otherwise the decoder will
        /// not be in an optimal state to decode the next incoming packet. For the PLC and FEC cases, frame_size *must*
        /// be a multiple of 10 ms.</param>
        /// <param name="decode_fec">Indicates that we want to recreate the PREVIOUS (lost) packet using FEC data from THIS packet. Using this packet
        /// recovery scheme, you will actually decode this packet twice, first with decode_fec TRUE and then again with FALSE. If FEC data is not
        /// available in this packet, the decoder will simply generate a best-effort recreation of the lost packet.</param>
        /// <returns>The number of decoded samples</returns>
        int DecodeMultistream(ReadOnlySpan<byte> data, Span<float> out_pcm, int frame_size, bool decode_fec);

        /// <summary>
        /// Decodes a multichannel Opus packet, putting the decoded audio into an int16 buffer.
        /// </summary>
        /// <param name="data">The input payload. This may be empty if the previous packet was lost in transit (when PLC is enabled)</param>
        /// <param name="out_pcm">A buffer to put the output PCM. The output size is (# of samples) * (# of channels) for a given single frame size (maximum 120ms).</param>
        /// <param name="frame_size">The number of samples (per channel) of available space in the output PCM buf.
        /// It should contain at least enough space to contain (# of samples) * (# of channels) for a given single frame size (maximum 120ms).
        /// In the case of PLC (data == NULL) or FEC (decode_fec == true),
        /// then frame_size needs to be exactly the duration of the audio that is missing, otherwise the decoder will
        /// not be in an optimal state to decode the next incoming packet. For the PLC and FEC cases, frame_size *must*
        /// be a multiple of 10 ms.</param>
        /// <param name="decode_fec">Indicates that we want to recreate the PREVIOUS (lost) packet using FEC data from THIS packet. Using this packet
        /// recovery scheme, you will actually decode this packet twice, first with decode_fec TRUE and then again with FALSE. If FEC data is not
        /// available in this packet, the decoder will simply generate a best-effort recreation of the lost packet.</param>
        /// <returns>The number of decoded samples</returns>
        int DecodeMultistream(ReadOnlySpan<byte> data, Span<short> out_pcm, int frame_size, bool decode_fec);

        /// <summary>
        /// Resets all buffers and prepares this decoder to process a fresh (unrelated) stream
        /// </summary>
        void ResetState();

        /// <summary>
        /// Gets the version string of the library backing this implementation.
        /// </summary>
        /// <returns>An arbitrary version string.</returns>
        string GetVersionString();

        /// <summary>
        /// Gets the encoded bandwidth of the last packet decoded. This may be lower than the actual decoding sample rate,
        /// and is only an indicator of the encoded audio's quality
        /// </summary>
        OpusBandwidth Bandwidth { get; }

        /// <summary>
        /// Returns the final range of the entropy coder. If you need this then I also assume you know what it's for.
        /// </summary>
        uint FinalRange { get; }

        /// <summary>
        /// Gets or sets the gain (Q8) to use in decoding
        /// </summary>
        int Gain { get; set; }

        /// <summary>
        /// Gets the duration of the last packet, in PCM samples per channel
        /// </summary>
        int LastPacketDuration { get; }

        /// <summary>
        /// Gets the sample rate that this decoder decodes to. Always constant for the lifetime of the decoder
        /// </summary>
        int SampleRate { get; }

        /// <summary>
        /// Gets the number of channels of the input data. Always constant for the lifetime of the decoder
        /// </summary>
        int NumChannels { get; }
    }
}