
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

using Concentus.Common;
using Concentus.Native;
using System;
using System.IO;
using System.Numerics;

namespace Concentus
{
    /// <summary>
    /// Central factory class for creating resamplers.
    /// Using these methods allows the runtime to decide the most appropriate
    /// implementation for your platform based on what is available. Native
    /// interop for resamplers is not yet implemented, but may be in the future.
    /// </summary>
    public static class ResamplerFactory
    {
#pragma warning disable CS0618 // Type or member is obsolete
        /// <summary>
        /// Create a new resampler with integer input and output rates (in hertz).
        /// </summary>
        /// <param name="numChannels">The number of channels to be processed</param>
        /// <param name="inRate">Input sampling rate, in hertz</param>
        /// <param name="outRate">Output sampling rate, in hertz</param>
        /// <param name="quality">Resampling quality, from 0 to 10</param>
        /// <param name="logger">An optional logger for the operation</param>
        /// <returns>A newly created resampler</returns>
        public static IResampler CreateResampler(int numChannels, int inRate, int outRate, int quality, TextWriter logger = null)
        {
            return CreateResampler(numChannels, inRate, outRate, inRate, outRate, quality, logger);
        }

        /// <summary>
        /// Create a new resampler with fractional input/output rates. The sampling 
        /// rate ratio is an arbitrary rational number with both the numerator and
        /// denominator being 32-bit integers.
        /// </summary>
        /// <param name="numChannels">The number of channels to be processed</param>
        /// <param name="ratioNum">Numerator of sampling rate ratio</param>
        /// <param name="ratioDen">Denominator of sampling rate ratio</param>
        /// <param name="inRate">Input sample rate rounded to the nearest integer (in hz)</param>
        /// <param name="outRate">Output sample rate rounded to the nearest integer (in hz)</param>
        /// <param name="quality">Resampling quality, from 0 to 10</param>
        /// <param name="logger">An optional logger for the operation</param>
        /// <returns>A newly created resampler</returns>
        public static IResampler CreateResampler(int numChannels, int ratioNum, int ratioDen, int inRate, int outRate, int quality, TextWriter logger = null)
        {
            if (numChannels <= 0) throw new ArgumentOutOfRangeException(nameof(numChannels));
            if (ratioNum <= 0) throw new ArgumentOutOfRangeException(nameof(ratioNum));
            if (ratioDen <= 0) throw new ArgumentOutOfRangeException(nameof(ratioDen));
            if (inRate <= 0) throw new ArgumentOutOfRangeException(nameof(inRate));
            if (outRate <= 0) throw new ArgumentOutOfRangeException(nameof(outRate));
            if (quality < 0 || quality > 10) throw new ArgumentOutOfRangeException(nameof(quality), "Quality must be between 0 and 10");

            return new SpeexResampler(numChannels, ratioNum, ratioDen, inRate, outRate, quality);
        }

#pragma warning restore CS0618 // Type or member is obsolete
    }
}
