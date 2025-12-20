using System.Runtime.InteropServices;
using System;

// ReSharper disable MemberCanBeProtected.Global
// ReSharper disable ClassNeverInstantiated.Global
namespace OpusSharp.Core.SafeHandlers
{
    /// <summary>
    /// Managed wrapper over the OpusEncoder state.
    /// </summary>
    public class OpusEncoderSafeHandle : SafeHandle
    {
        /// <summary>
        /// Creates a new <see cref="OpusEncoderSafeHandle"/>.
        /// </summary>
        public OpusEncoderSafeHandle(): base(IntPtr.Zero, true)
        {
        }

        /// <inheritdoc/>
        public override bool IsInvalid => handle == IntPtr.Zero;

        /// <inheritdoc/>
        protected override bool ReleaseHandle()
        {
            NativeOpus.opus_encoder_destroy(handle);
            return true;
        }
    }
    
    /// <summary>
    /// Managed wrapper over the OpusEncoder state (statically linked).
    /// </summary>
    public class StaticOpusEncoderSafeHandle : OpusEncoderSafeHandle
    {
        /// <inheritdoc/>
        protected override bool ReleaseHandle()
        {
            StaticNativeOpus.opus_encoder_destroy(handle);
            return true;
        }
    }
}
