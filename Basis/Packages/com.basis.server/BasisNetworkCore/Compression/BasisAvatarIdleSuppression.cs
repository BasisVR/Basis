using System;

namespace Basis.Network.Core.Compression
{
    /// <summary>
    /// Decides whether a freshly-encoded local avatar frame must be transmitted or can be
    /// suppressed as redundant.
    ///
    /// The avatar payload is fully quantized, so two byte-identical payloads reconstruct to the
    /// exact same pose on every receiver. Re-sending a byte-identical frame therefore changes
    /// nothing visible — it is pure wire redundancy. Suppressing it is lossless by construction;
    /// any real motion above the quantization step changes at least one byte and is always sent.
    ///
    /// A heartbeat still forces a periodic resend so a late joiner, or a receiver that lost the
    /// final pre-idle frame, converges to the correct rest pose and the receiver's interpolation
    /// window never starves for long. The receiver holds the last snapshot when starved, so the
    /// only observable effect of suppression is fewer packets, never a different pose.
    ///
    /// Pure and Unity-free so it is unit-tested in BasisServerTests alongside the codecs.
    /// </summary>
    public static class BasisAvatarIdleSuppression
    {
        /// <summary>
        /// Resend an unchanged pose at least this often (seconds). The server serves late joiners
        /// and lost-baseline receivers from its own cached keyframes (v42 adds request-driven
        /// re-keys), so the heartbeat only bounds staleness for edge cases; P2P peers additionally
        /// get a forced send whenever the session set changes.
        /// </summary>
        public const double DefaultHeartbeatSeconds = 5.0;

        /// <summary>
        /// True when the frame must be sent. Returns false (suppress) only when the new payload is
        /// byte-identical to the last one actually sent, there is no additional (blendshape) data
        /// riding along this frame, the linked-avatar index is unchanged, and the heartbeat window
        /// has not elapsed.
        /// </summary>
        public static bool ShouldSend(
            ReadOnlySpan<byte> current,
            ReadOnlySpan<byte> lastSent,
            bool hasLastSent,
            bool hasAdditionalData,
            bool linkedAvatarChanged,
            double nowSeconds,
            double lastSentSeconds,
            double heartbeatSeconds)
        {
            if (!hasLastSent) return true;
            if (hasAdditionalData) return true;
            if (linkedAvatarChanged) return true;
            if (nowSeconds - lastSentSeconds >= heartbeatSeconds) return true;
            if (current.Length != lastSent.Length) return true;
            return !current.SequenceEqual(lastSent);
        }
    }
}
