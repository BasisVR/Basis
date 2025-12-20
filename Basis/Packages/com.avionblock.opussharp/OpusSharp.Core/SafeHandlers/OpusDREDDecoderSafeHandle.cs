using System;
using System.Runtime.InteropServices;

// ReSharper disable MemberCanBeProtected.Global
// ReSharper disable ClassNeverInstantiated.Global
// ReSharper disable InconsistentNaming
namespace OpusSharp.Core.SafeHandlers
{
    /// <summary>
    /// Managed wrapper over the OpusDREDDecoder state.
    /// </summary>
    public class OpusDREDDecoderSafeHandle : SafeHandle
    {
        /// <summary>
        /// Creates a new <see cref="OpusDecoderSafeHandle"/>.
        /// </summary>
        public OpusDREDDecoderSafeHandle() : base(IntPtr.Zero, true)
        {
        }

        /// <inheritdoc/>
        public override bool IsInvalid => handle == IntPtr.Zero;

        /// <inheritdoc/>
        protected override bool ReleaseHandle()
        {
            NativeOpus.opus_dred_decoder_destroy(handle);
            return true;
        }
    }
    
    /// <summary>
    /// Managed wrapper over the OpusDREDDecoder state (statically linked).
    /// </summary>
    public class StaticOpusDREDDecoderSafeHandle : OpusDecoderSafeHandle
    {
        /// <inheritdoc/>
        protected override bool ReleaseHandle()
        {
            StaticNativeOpus.opus_dred_decoder_destroy(handle);
            return true;
        }
    }
}
