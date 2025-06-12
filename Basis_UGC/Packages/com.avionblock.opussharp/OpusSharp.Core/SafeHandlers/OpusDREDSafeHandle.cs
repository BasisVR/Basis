using System;
using System.Runtime.InteropServices;

namespace OpusSharp.Core.SafeHandlers
{
    /// <summary>
    /// Managed wrapper over the OpusDRED state.
    /// </summary>
    public class OpusDREDSafeHandle : SafeHandle
    {
        /// <summary>
        /// Creates a new <see cref="OpusDREDDecoderSafeHandle"/>.
        /// </summary>
        public OpusDREDSafeHandle() : base(IntPtr.Zero, true)
        {
        }

        /// <inheritdoc/>
        public override bool IsInvalid => handle == IntPtr.Zero;

        /// <inheritdoc/>
        protected override bool ReleaseHandle()
        {
            NativeOpus.opus_dred_free(handle);
            return true;
        }
    }
}
