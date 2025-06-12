using System;
using System.Runtime.InteropServices;

namespace OpusSharp.Core.SafeHandlers
{
    /// <summary>
    /// Managed wrapper over the OpusMultistreamEncoder state.
    /// </summary>
    public class OpusMSEncoderSafeHandle : SafeHandle
    {
        /// <summary>
        /// Creates a new <see cref="OpusMSEncoderSafeHandle"/>.
        /// </summary>
        public OpusMSEncoderSafeHandle() : base(IntPtr.Zero, true)
        {
        }

        /// <inheritdoc/>
        public override bool IsInvalid => handle == IntPtr.Zero;

        /// <inheritdoc/>
        protected override bool ReleaseHandle()
        {
            NativeOpus.opus_multistream_encoder_destroy(handle);
            return true;
        }
    }
}
