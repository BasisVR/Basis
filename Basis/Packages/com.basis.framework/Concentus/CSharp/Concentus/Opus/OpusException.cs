/* Copyright (c) 2016 Logan Stromberg

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
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;


namespace Concentus
{
    /// <summary>
    /// An exception type which wraps a raw Opus error code.
    /// </summary>
    public class OpusException : Exception
    {
        /// <summary>
        /// Gets the raw Opus error code as defined in the C spec. These codes can
        /// be found in the <see cref="OpusError"/> enumeration.
        /// </summary>
        public int OpusErrorCode { get; private set; }

        /// <summary>
        /// Creates a new empty <see cref="OpusException"/>.
        /// This constructor is discouraged as it does not set the raw error code.
        /// </summary>
        internal OpusException() : base("Unknown error")
        {
            OpusErrorCode = OpusError.CONCENTUS_UNKNOWN_ERROR;
        }

        /// <summary>
        /// Creates a new <see cref="OpusException"/> with a custom error message.
        /// This constructor is discouraged as it does not set the raw error code.
        /// so it reports 
        /// </summary>
        internal OpusException(string message) : base(message)
        {
            OpusErrorCode = OpusError.CONCENTUS_UNKNOWN_ERROR;
        }

        /// <summary>
        /// Creates a new <see cref="OpusException"/> with a custom error message.
        /// This constructor is discouraged as it does not set the raw error code.
        /// so it reports 
        /// </summary>
        internal OpusException(int opusError) : base(CodecHelpers.opus_strerror(opusError))
        {
            OpusErrorCode = opusError;
        }

        /// <summary>
        /// Creates a new <see cref="OpusException"/> with a custom error message and matching Opus error code.
        /// </summary>
        /// <param name="message">The entire error message string.</param>
        /// <param name="opusError">The raw error code that can be passed to other C-style error handlers
        /// if necessary (it is not used to format the error string).</param>
        internal OpusException(string message, int opusError) : base(message)
        {
            OpusErrorCode = opusError;
        }
    }
}
