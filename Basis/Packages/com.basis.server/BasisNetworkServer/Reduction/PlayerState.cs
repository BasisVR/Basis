using Basis.Network.Core;
using static SerializableBasis;

namespace BasisNetworkServer.BasisNetworkingReductionSystem
{
    public class PlayerState
    {
        public NetPeer Peer;
        public bool IsActive;

        // Admin-set: bypass the distance reduction system and fan High data to every receiver at the source rate.
        public bool BypassReduction;

        // Used for distance decisions
        public Basis.Scripts.Networking.Compression.Vector3 Position;

        // Base message shell (we swap avatarSerialization before send)
        public ServerSideSyncPlayerMessage SyncMessage;

        // Per-peer tracking, indexed by sender player id. See PeerTrackingData for why it is as narrow
        // as it is; both arrays are grown together and must always be the same length.
        public PeerTrackingData[] PeerTracking;

        /// <summary>
        /// The sender data generation this receiver last got, split out of PeerTrackingData because it
        /// is the send loop's FIRST test and rejects most pairs before anything else is read.
        ///
        /// The loop walks every sender for every receiver, so that one comparison sets the cache traffic
        /// of the whole pass. Inside the record a 64 byte line carried 2 pairs at the old width and 5 at
        /// the new one; as its own array it carries 16, so the gate touches a quarter to an eighth of the
        /// memory it used to. Nothing else in the pass is read densely enough to be worth splitting out
        /// after this one - the rest is only reached by pairs that survive the gate.
        ///
        /// uint, not long: it counts avatar updates, and at 90 Hz the low 32 bits take a year and a half
        /// to wrap. Compared with `senderGen &lt;= seen`, both truncated the same way.
        /// </summary>
        public uint[] PeerLastSeenGeneration;

        // Generation counter: incremented each time this player receives new avatar data.
        // Receivers compare against their LastSeenGeneration to know if there is new data.
        // Access via Interlocked.Read/Increment for thread safety on 32-bit or cross-core visibility.
        public long DataGeneration;

        // Cached during ProcessMessage to avoid dereference chain in the inner send loop.
        public bool HasAdditionalData;

        // Cached per-quality payloads (payload bytes only, plus DataQualityLevel).
        // AvatarHigh owns its own byte[] — never shares with the QueuedMessagePool.
        // This prevents pool reuse from silently corrupting the muscle-change comparison.
        public LocalAvatarSyncMessage AvatarHigh;
        public LocalAvatarSyncMessage AvatarMedium;
        public LocalAvatarSyncMessage AvatarLow;
        public LocalAvatarSyncMessage AvatarVeryLow;

        // Inbound sequence tracking for unreliable clientâ†’server packets
        public byte LastInboundSequence;
        public bool HasReceivedFirst;

        // Outbound sequence stamped into pre-serialized data (increments per new avatar update)
        public byte OutboundSequence;

        // Pre-serialized keyframe bytes per quality.
        // Byte-ID: [PlayerID:1][interval_placeholder:1][sequence:1][array:N][additional...]
        // Ushort-ID: [PlayerID:2][interval_placeholder:1][sequence:1][array:N][additional...]
        // The interval byte offset depends on SmallId (1 for byte, 2 for ushort).
        // Quality is derived from the channel number — not stored in the payload.
        public byte[][] SerializedKeyframe = new byte[4][];
        public int[] SerializedKeyframeLength = new int[4];
        // Whether each quality's serialized keyframe carries an additional-data section — the
        // send loop must pick the matching (odd/even) channel per quality, since low tiers can
        // have their additional data stripped while High/Medium keep it.
        public bool[] SerializedHasAdditional = new bool[4];

        // ── Delta compression (per sender) ──
        // Snapshot of each quality's payload bytes at the last keyframe — the baseline deltas diff against.
        public byte[][] KeyframePayload = new byte[4][];
        public int[] KeyframePayloadLength = new int[4];
        // Pre-serialized delta frames per quality (DeltaAvatarChannel wire), rebuilt each delta tick.
        public byte[][] SerializedDelta = new byte[4][];
        public int[] SerializedDeltaLength = new int[4];
        // Generation + outbound sequence of the current keyframe; deltas reference KeyframeSeq as baseSeq.
        public long KeyframeGen;
        public byte KeyframeSequence;
        // Stopwatch ticks of the last keyframe, for the periodic-keyframe cadence.
        public long LastKeyframeTimeTicks;
        // Adaptive keyframe stretch: a streak of small High deltas (idle-ish sender) doubles the
        // periodic keyframe interval step by step, up to AvatarDeltaKeyframeMaxIntervalMs. Any
        // large delta or keyframe promotion resets it. Lost keyframes are recovered on demand via
        // DeltaControlKeyframeRequest instead of waiting out the stretched cadence.
        public int KeyframeStretchShift;
        public int SmallDeltaStreak;
        // True when the current generation was emitted as a keyframe (so the send loop never picks a delta).
        public bool CurrentIsKeyframe;
        // Scratch for the "is the High delta smaller than a High keyframe?" promotion probe.
        public byte[] DeltaProbeScratch;

        // True when playerID fits in a byte (≤255). Set once at creation.
        public bool SmallId;

        // Lazy pre-serialization: bitmask of which quality levels had receivers last tick.
        // Updated atomically from the parallel send loop. Read/reset in ProcessMessage.
        // Bit 0 = VeryLow, Bit 1 = Low, Bit 2 = Medium, Bit 3 = High.
        public int UsedQualities;

        // Actual payload size stored in AvatarHigh.array (which may be larger if from ArrayPool).
        // Used for muscle-change comparison instead of .Length to handle pooled arrays correctly.
        public int HighArrayActualSize;

        // Per-receiver bundle accumulator. Populated in UpdateCommunicationAndDistances
        // and drained in FlushPendingForReceiver. Allocated lazily on first use.
        // Only this player's own receive thread (one Parallel.For body) writes here,
        // so no synchronization is needed.
        public PendingAvatarSend[] PendingSends;
        public int PendingPeak;
        public int PendingPeakTicks;
        public int PendingCount;

        // Scratch buffers reused tick-to-tick when emitting compressed bundles to this receiver.
        // Avoids per-tick allocations in the deflate path. Sized by the flush logic.
        public byte[] BundleRawScratch;
        public byte[] BundleCompressedScratch;
        public PendingAvatarSend[] PendingSortScratch;

        // EMA of compressed/raw ratio observed for this receiver's bundles. Used by
        // FlushPendingForReceiver to predict how many messages fit in one MTU-sized chunk
        // so the first compress attempt usually succeeds with no retry. 0 = unseeded.
        public float LastBundleRatio;

        // Same, for the Zstd path. Kept SEPARATE rather than folded into LastBundleRatio because
        // the two codecs sit far apart — ~0.87 for LZ4 on deltas against ~0.50 for dictionary
        // Zstd on keyframes — and a receiver in steady state produces both classes in the same
        // flush. One blended EMA would sit between them and mispredict every chunk in both
        // directions at once: too large for the LZ4 chunks (overshoot, wasted second compress)
        // and too small for the Zstd ones (underfilled datagrams), while BundleFillMargin
        // thrashed trying to correct for a ratio that was never wrong in one consistent
        // direction. 0 = unseeded.
        public float LastBundleZstdRatio;

        // Share of the MTU budget the first compress attempt aims to fill. LastBundleRatio is an EMA
        // of the MEAN ratio, but the budget is a ceiling, so sizing against the mean overshoots on
        // every chunk that compresses worse than average — measured at 20% of emitted bundles, each
        // costing a deflate of a chunk that is then discarded and rebuilt. Backs off fast on an
        // overshoot and recovers slowly, so receivers whose payloads compress predictably keep
        // full-sized bundles while erratic ones stop paying for the guess. 0 = unseeded.
        public float BundleFillMargin;

        // Flushes remaining before this receiver re-probes whether bundling is worth the CPU.
        // Non-zero means "this receiver's data did not compress well enough last time we looked,
        // send it uncompressed for now". See AvatarBundleMaxRatio.
        public int BundleSkipCountdown;
    }
}
