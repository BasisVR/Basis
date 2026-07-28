namespace Basis.Scripts.Networking.NetworkedAvatar
{
    /// <summary>
    /// Hop-by-hop counters for AdditionalAvatarData (face tracking, avatar behaviour params)
    /// across the avatar sync pipeline. Counting is always on (a few Interlocked adds per
    /// avatar frame); read live via the Basis ▸ Debug ▸ Additional Data editor window.
    ///
    /// Sender hops:   Submitted (behaviour → transmitter) → Attached (Compress put it on a frame).
    /// Receiver hops: Parsed (frame carried a section) → gate results → Dispatched (behaviour ran).
    /// </summary>
    public static class BasisAdditionalDataDiagnostics
    {
        // ── Sender ──
        public static long SenderSubmitted;          // OnAvatarServerReductionSystemMessageSend calls
        public static long SenderSubmitFailedNoTransmitter;
        public static long SenderFramesWithAdditional;   // Compress frames that carried a section
        public static long SenderFramesKeyframe;         // of those, sent as keyframes
        public static long SenderFramesDelta;            // of those, sent as uplink deltas
        public static long SenderAvatarChannelSent;      // OnAvatarNetworkMessageSend (ch15: HVR handshake, low-freq variables, upgrades)

        // ── Receiver, avatar-channel (ch15) path ──
        public static long ReceiverAvatarChannelDispatched; // behaviour OnNetworkMessageReceived delivered
        public static long ReceiverAvatarChannelDeferred;   // future-avatar-index, queued for post-swap replay
        public static long ReceiverAvatarChannelDropped;    // stale index / missing player / no avatar

        // ── Receiver ──
        public static long ReceiverFramesWithAdditional; // frames whose section parsed
        public static long ReceiverDroppedLinkedIndex;   // LinkedAvatarIndex gate rejected
        public static long ReceiverDroppedNoBehaviours;  // NetworkBehaviours null (avatar not ready)
        public static long ReceiverMarshaledToMainThread;// arrived off-main, deferred (P2P socket path)
        public static long ReceiverDroppedStaleOnDrain;  // avatar swapped between enqueue and drain
        public static long ReceiverEntriesDispatched;    // behaviour callbacks actually invoked
        public static long ReceiverEntriesSkippedEmpty;  // size-0 entries skipped
        public static long ReceiverEntriesSkippedIndex;  // messageIndex out of range

        // Last-seen LinkedAvatarIndex pair from a gate rejection, to make a mismatch diagnosable.
        public static int LastGateMessageIndex = -1;
        public static int LastGateReceiverIndex = -1;
    }
}
