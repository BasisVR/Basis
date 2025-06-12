using System;
using System.Runtime.InteropServices;

namespace OpusSharp.Core.SafeHandlers
{
    /// <summary>
    /// Managed wrapper over the OpusMultistreamDecoder state.
    /// </summary>
    public class OpusMSDecoderSafeHandle : SafeHandle
    {
        /// <summary>
        /// Creates a new <see cref="OpusMSDecoderSafeHandle"/>.
        /// </summary>
        public OpusMSDecoderSafeHandle() : base(IntPtr.Zero, true)
        {
        }

        /// <inheritdoc/>
        public override bool IsInvalid => handle == IntPtr.Zero;

        /// <inheritdoc/>
        protected override bool ReleaseHandle()
        {
            NativeOpus.opus_multistream_decoder_destroy(handle);
            return true;
        }
    }
}
