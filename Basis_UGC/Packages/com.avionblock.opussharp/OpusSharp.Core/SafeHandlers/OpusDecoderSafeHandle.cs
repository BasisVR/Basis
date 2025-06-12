using System.Runtime.InteropServices;
using System;

namespace OpusSharp.Core.SafeHandlers
{
    /// <summary>
    /// Managed wrapper over the OpusDecoder state.
    /// </summary>
    public class OpusDecoderSafeHandle : SafeHandle
    {
        /// <summary>
        /// Creates a new <see cref="OpusDecoderSafeHandle"/>.
        /// </summary>
        public OpusDecoderSafeHandle() : base(IntPtr.Zero, true)
        {
        }
        
        /// <inheritdoc/>
        public override bool IsInvalid => handle == IntPtr.Zero;

        /// <inheritdoc/>
        protected override bool ReleaseHandle()
        {
            NativeOpus.opus_decoder_destroy(handle);
            return true;
        }
    }
}
