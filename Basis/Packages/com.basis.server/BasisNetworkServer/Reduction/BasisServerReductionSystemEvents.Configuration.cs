using Basis.Network.Core.Compression;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace BasisNetworkServer.BasisNetworkingReductionSystem
{
    public partial class BasisServerReductionSystemEvents
    {
        public static ShardedConcurrentDictionary<PlayerState> playerStates = new();

        // Admin-flagged full-quality broadcast ids. Authoritative across PlayerState recreation;
        // mirrored onto PlayerState.BypassReduction for the hot send loop. Cleared on disconnect.
        private static readonly ConcurrentDictionary<int, bool> _bypassReductionIds = new();
        // Inbound avatar frames, keyed by sender so only the newest per peer survives to the tick.
        // Drained (not cleared) each tick — see ShardedConcurrentDictionary.DrainInto for why the
        // double-buffer this replaced was more expensive than the traffic it carried.
        private static readonly ShardedConcurrentDictionary<QueuedMessage> currentMessages = new();

        public static float BSRBaseMultiplier = 1.0f;
        public static float BSRSIncreaseRate = 0.01f;
        public static int BSRSMillisecondDefaultInterval = 50;

        // Compressed avatar bundle settings (written from NetworkServer.InitializePulseSettings).
        // When enabled, the per-receiver inner loop defers sends into PendingAvatarSend[] and
        // flushes either as one deflated bundle on CompressedAvatarBundleChannel or as
        // individual SendUnreliableRawMerge calls on the original quality channels.
        public static bool EnableAvatarBundleCompression = true;
        public static int AvatarBundleMinMessages = 2;
        public static int AvatarBundleMinBytes = 128;

        // ── Hybrid bundle codec (written from NetworkServer.InitializePulseSettings) ──
        // Keyframe/full bundles compress far better under Zstd with a trained dictionary than
        // under LZ4; delta-only bundles compress WORSE. See BasisAvatarBundleZstd for the
        // measurements. These select which bundles take the Zstd path.
        //
        // Inert unless a dictionary is embedded (BasisAvatarBundleDictionary.Generation != 0) —
        // dictionary-less Zstd measured worse than LZ4, so there is nothing to fall back to.
        public static bool EnableAvatarBundleZstd = true;
        public static bool AvatarBundleZstdDeltaBundles = false;
        public static int AvatarBundleZstdLevel = BasisAvatarBundleZstd.DefaultLevel;
        public static int AvatarBundleZstdMaxShedTier = 1;

        private const int RetainedScratchBytes = 16 * 1024;

        private const int PendingShrinkWindowTicks = 256;

        private const int PendingMinCapacity = 64;

        // Avatar delta compression (written from NetworkServer.InitializePulseSettings).
        // When on, each sender emits a full keyframe every AvatarDeltaKeyframeIntervalMs and, in
        // between, per-quality deltas against that keyframe on DeltaAvatarChannel. When off, every
        // frame is a keyframe (legacy behavior).
        public static bool EnableAvatarDeltaCompression = true;
        public static int AvatarDeltaKeyframeIntervalMs = 500;
        // Ceiling for the adaptive keyframe stretch (0 or <= base disables stretching). While a
        // sender's High deltas stay tiny the periodic keyframe interval doubles up to this cap;
        // receivers that miss a keyframe request one instead of waiting the stretched period out.
        public static int AvatarDeltaKeyframeMaxIntervalMs = 2000;
        // Drop AdditionalAvatarData (face blendshapes, custom behaviour params) from Low and
        // VeryLow tiers — invisible past the Medium distance; the reliable AvatarChannel path
        // still reaches everyone.
        public static bool StripAdditionalDataAtLowQuality = true;

        // ── Bundle compression economics ──────────────────────────────────────────────
        // Bandwidth is the scarce resource here, not CPU. Measured at 1000 players, bundle deflate
        // costs ~19 ms of CPU per 4 ms tick (~4.8 of 32 cores) and returns ~13% of bytes — and 13% of
        // a 936 MB/s outbound stream is ~125 MB/s, which is worth far more than the cores. So
        // compression stays ON by default and the guard below is a safety valve for genuinely
        // incompressible data, NOT a CPU-saving throttle.
        //
        // Realistic first guess so the initial chunk does not overshoot MTU and waste a retry. This
        // one is a free win: it cuts deflate work with no bandwidth cost at all. Measured ratio on
        // quantized bone rotations is ~0.87; the old 0.6 guess made ~8% of bundles compress twice.
        private const float InitialBundleRatioGuess = 0.85f;
        // Same, for the Zstd path. Deliberately pessimistic against the ~0.50 hybrid bundles
        // actually reach: guessing high underfills the first bundle or two per receiver, which
        // costs a few bytes, while guessing low overshoots MTU and burns an entire extra
        // compress on a chunk that is then discarded. The EMA converges within a handful of
        // bundles either way.
        private const float InitialBundleZstdRatioGuess = 0.60f;
        // Share of the MTU budget a first compress attempt aims to fill, adapted per receiver in
        // PlayerState.BundleFillMargin. A flat 0.95 assumed the ratio EMA predicts each chunk well,
        // but the EMA tracks the mean while the budget is a ceiling: at 1000 players 20% of bundles
        // overshot and paid a second deflate on a chunk that was then thrown away, ~17% of all
        // deflate calls. Backing off 0.05 per overshoot and recovering 0.01 per clean bundle settles
        // each receiver just under its own overshoot boundary. The floor keeps a pathological
        // receiver from shrinking bundles indefinitely, since bandwidth is the scarcer resource.
        private const float MaxBundleFillMargin = 0.95f;
        private const float MinBundleFillMargin = 0.75f;
        private const float BundleFillMarginBackoff = 0.05f;
        private const float BundleFillMarginRecover = 0.01f;
        // Skip bundling for a receiver only when compression is returning essentially nothing — at
        // 0.98 it must be saving under 2% before we stop paying for it. Deliberately far above the
        // ~0.87 seen in practice, so normal traffic keeps its savings.
        public static float AvatarBundleMaxRatio = 0.98f;
        // Flushes to skip before re-probing. Long enough that the probe cost is negligible, short
        // enough to pick up a genuine change in payload character (e.g. everyone stops moving).
        public static int AvatarBundleReprobeFlushes = 600;
        // A High delta body at or under this size counts as "small" for the stretch streak
        // (8B mask + position + a couple of quantized bones).
        private const int SmallHighDeltaBytes = 40;
        private const int SmallDeltaStreakToStretch = 4;
        // (senderId, receiverId) pairs whose baseline must be invalidated so the next send to that
        // receiver is a keyframe. Filled by the network thread (DeltaControlKeyframeRequest),
        // drained by the tick thread to keep PeerTracking single-writer.
        private static readonly ConcurrentQueue<(int senderId, int receiverId)> _pendingKeyframeRequests = new();
        // Conservative headroom subtracted from peer.Mtu before checking if a compressed
        // bundle fits in a single UDP datagram. Accounts for LiteNetLib unreliable header,
        // optional packet-layer header, and merge length prefixes.
        private const int BundleMtuHeadroom = 32;
        // Bundle wire header: [flags:1][rawLen:2-LE]. Byte 0 was a message count through v52,
        // documented as a hint and read by no decoder; v53 repurposes it to carry the codec id
        // and dictionary generation, so the hybrid codec costs zero wire bytes.
        // See BasisAvatarBundleZstd for the layout.
        private const int BundleHeaderSize = 3;
        private static readonly double MsToTick = Stopwatch.Frequency / 1000.0;
    }
}
