using System;
using System.Runtime.InteropServices;

// ReSharper disable MemberCanBeProtected.Global
// ReSharper disable ClassNeverInstantiated.Global
// ReSharper disable InconsistentNaming
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
    
    /// <summary>
    /// Managed wrapper over the OpusMultistreamEncoder state (statically linked).
    /// </summary>
    public class StaticOpusMSEncoderSafeHandle : OpusMSEncoderSafeHandle
    {
        /// <inheritdoc/>
        protected override bool ReleaseHandle()
        {
            StaticNativeOpus.opus_multistream_encoder_destroy(handle);
            return true;
        }
    }
}
