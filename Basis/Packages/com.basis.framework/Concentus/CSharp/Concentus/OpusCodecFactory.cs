
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
using Concentus.Native;
using Concentus.Structs;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Concentus
{
    /// <summary>
    /// Central factory class for creating Opus encoder / decoder structs.
    /// Using these methods allows the runtime to decide the most appropriate
    /// implementation for your platform based on what is available (generally,
    /// this means using a P/Invoke native adapter if native libopus is present)
    /// </summary>
    public static class OpusCodecFactory
    {
#pragma warning disable CS0618 // Type or member is obsolete

        private static readonly object _mutex = new object();
        private static bool _nativeLibInitialized = false;
        private static bool _isNativeLibAvailable = false;
        private static bool _userAllowNativeLib = true;

        /// <summary>
        /// Creates an IOpusEncoder appropriate for the current platform.
        /// This could potentially involve a native code layer.
        /// </summary>
        /// <param name="sampleRate">The input sample rate. Must be a valid Opus samplerate (8K, 12K, 16K, 24K, 48K)</param>
        /// <param name="numChannels">The number of channels of input (1 or 2)</param>
        /// <param name="application">The hint for the type of audio or application this encoder will be used for.</param>
        /// <param name="messageLogger">An optional logger for debugging messages about native library bindings.</param>
        /// <returns>A newly created opus encoder.</returns>
        public static IOpusEncoder CreateEncoder(
            int sampleRate,
            int numChannels,
            OpusApplication application = OpusApplication.OPUS_APPLICATION_AUDIO,
            TextWriter messageLogger = null)
        {
            if (_userAllowNativeLib && NativeLibraryAvailable(messageLogger))
            {
                return NativeOpusEncoder.Create(sampleRate, numChannels, application);
            }
            else
            {
                return new OpusEncoder(sampleRate, numChannels, application);
            }
        }

        /// <summary>
        /// Creates an IOpusEncoder appropriate for the current platform.
        /// This could potentially involve a native code layer.
        /// </summary>
        /// <param name="sampleRate">The output sample rate to decode to.
        /// Doesn't have to be the same sample rate the audio was encoded at.
        /// Must be a valid Opus samplerate (8K, 12K, 16K, 24K, 48K)</param>
        /// <param name="numChannels">The number of channels to decode to (1 or 2).
        /// Doesn't have to be the same channel count the audio was encoded at.</param>
        /// <param name="messageLogger">An optional logger for debugging messages about native library bindings.</param>
        /// <returns>A newly created opus decoder.</returns>
        public static IOpusDecoder CreateDecoder(
            int sampleRate,
            int numChannels,
            TextWriter messageLogger = null)
        {
            if (_userAllowNativeLib && NativeLibraryAvailable(messageLogger))
            {
                return NativeOpusDecoder.Create(sampleRate, numChannels);
            }
            else
            {
                return new OpusDecoder(sampleRate, numChannels);
            }
        }

        /// <summary>
        /// Creates a multichannel Opus encoder using the "new API". This constructor allows you to use predefined Vorbis channel mappings, or specify your own.
        /// </summary>
        /// <param name="sampleRate">The samples rate of the input</param>
        /// <param name="numChannels">The total number of channels to encode (1 - 255)</param>
        /// <param name="mappingFamily">The mapping family to use. 0 = mono/stereo, 1 = use Vorbis mappings, 255 = use raw channel mapping</param>
        /// <param name="streams">The number of streams to encode</param>
        /// <param name="coupledStreams">The number of coupled streams</param>
        /// <param name="mapping">A channel mapping describing which streams go to which channels: see <seealso href="https://opus-codec.org/docs/opus_api-1.5/group__opus__multistream.html"/></param>
        /// <param name="application">The application to use for the encoders</param>
        /// <param name="messageLogger">An optional logger for debugging messages about native library bindings.</param>
        /// <returns>A newly created opus multistream encoder.</returns>
        public static IOpusMultiStreamEncoder CreateMultiStreamEncoder(
            int sampleRate,
            int numChannels,
            int mappingFamily,
            out int streams,
            out int coupledStreams,
            byte[] mapping,
            OpusApplication application,
            TextWriter messageLogger = null)
        {
            if (_userAllowNativeLib && NativeLibraryAvailable(messageLogger))
            {
                return NativeOpusMultistreamEncoder.Create(sampleRate, numChannels, mappingFamily, out streams, out coupledStreams, mapping, application);
            }
            else
            {
                return OpusMSEncoder.CreateSurround(sampleRate, numChannels, mappingFamily, out streams, out coupledStreams, mapping, application);
            }
        }

        /// <summary>
        /// Creates a new multichannel decoder
        /// </summary>
        /// <param name="sampleRate">The sample rate to decode to.</param>
        /// <param name="numChannels">The total number of channels being decoded.</param>
        /// <param name="streams">The number of streams being decoded.</param>
        /// <param name="coupledStreams">The number of coupled streams being decoded.</param>
        /// <param name="mapping">A channel mapping describing which streams go to which channels: see <seealso href="https://opus-codec.org/docs/opus_api-1.5/group__opus__multistream.html"/></param>
        /// <param name="messageLogger">An optional logger for debugging messages about native library bindings.</param>
        /// <returns>A newly created opus multistream decoder.</returns>
        public static IOpusMultiStreamDecoder CreateMultiStreamDecoder(
            int sampleRate,
            int numChannels,
            int streams,
            int coupledStreams,
            byte[] mapping,
            TextWriter messageLogger = null)
        {
            if (_userAllowNativeLib && NativeLibraryAvailable(messageLogger))
            {
                return NativeOpusMultistreamDecoder.Create(sampleRate, numChannels, streams, coupledStreams, mapping);
            }
            else
            {
                return new OpusMSDecoder(sampleRate, numChannels, streams, coupledStreams, mapping);
            }
        }

#pragma warning restore CS0618 // Type or member is obsolete

            /// <summary>
            /// Gets or sets a global flag that determines whether the codec factory should attempt to
            /// use a native opus.dll or libopus implementation. True by default, but you can override
            /// the value to false if the library probe causes problems or something.
            /// </summary>
        public static bool AttemptToUseNativeLibrary
        {
            get
            {
                return _userAllowNativeLib;
            }
            set
            {
                _userAllowNativeLib = value;
            }
        }

        private static bool NativeLibraryAvailable(TextWriter messageLogger)
        {
            lock (_mutex)
            {
                if (!_nativeLibInitialized)
                {
                    try
                    {
                        _isNativeLibAvailable = NativeOpus.Initialize(messageLogger);
                        messageLogger?.WriteLine($"Is native opus available? {_isNativeLibAvailable}");
                    }
                    catch (Exception e)
                    {
                        messageLogger?.WriteLine(e.ToString());
                    }

                    _nativeLibInitialized = true;
                }

                return _isNativeLibAvailable;
            }
        }
    }
}
