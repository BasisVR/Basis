using Basis.Network.Core;
using Basis.Network.Core.Compression;
using BasisNetworkServer.BasisNetworking;
using K4os.Compression.LZ4;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using static SerializableBasis;
using static Basis.Network.Core.Compression.BasisAvatarBitPacking;

namespace BasisNetworkServer.BasisNetworkingReductionSystem
{
    public class QueuedMessage
    {
        public NetPeer FromPeer;
        public byte Sequence;
        public LocalAvatarSyncMessage AvatarMessage;
    }

    /// <summary>
    /// Deferred avatar send recorded once per (sender,receiver) pair in the inner loop.
    /// We keep a reference to the shared pre-serialized source plus the per-receiver
    /// interval byte; the flush stage decides whether to compress these into one bundle
    /// or replay each as an individual SendUnreliableRawMerge.
    /// </summary>
    public struct PendingAvatarSend
    {
        public byte[] Source;
        public int Length;
        public byte Channel;
        public byte Interval;
        public byte IntervalOffset; // 1 for byte-id, 2 for ushort-id
    }

    /// <summary>
    /// Combined per-peer tracking + cached distance data. Exactly 32 bytes = two per cache line.
    /// The send loop reads all fields sequentially per pair with no float math.
    ///
    /// ⚠️ THE SIZE IS LOad-BEARING, not cosmetic. One of these arrays is allocated per player with
    /// InitialPlayerArrayCapacity entries; at 48 bytes that array is 98 KB, which is over the 85,000
    /// byte Large Object Heap threshold. Every player's array then lands on the non-compacting LOH —
    /// ~94 MB at 1000 players, allocated in a burst during a join storm, reclaimed only on gen2.
    /// The 8-byte fields are ordered first so nothing pads; adding another long here would push it
    /// back to 40/48 and put it straight back on the LOH.
    /// </summary>
    public struct PeerTrackingData
    {
        public long LastSentTime;
        public long LastSeenGeneration;
        // Delta baseline tracking: the sender-keyframe generation + quality this receiver was last
        // sent a keyframe for. A delta is only sent when these match the sender's current keyframe;
        // otherwise the receiver is (re)sent a keyframe first. Reset to 0/default on peer removal.
        public long BaselineKeyframeGen;
        // Cached by the slow distance loop, read by the fast send loop (~250Hz). Eliminates per-pair
        // distance math from the hot path. int, not long: this is an interval, and Stopwatch ticks
        // for even a 200-second interval fit comfortably — the real values are tens of milliseconds.
        public int CachedIntervalTicks;
        public byte CachedQualityIndex;
        public byte CachedIntervalByte;
        public byte BaselineQuality;
    }

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

        // Combined per-peer tracking: last sent tick + last seen generation in one struct
        // for cache-friendly O(1) access in the send loop. Indexed by player id.
        public PeerTrackingData[] PeerTracking;

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
        public int PendingCount;

        // Scratch buffers reused tick-to-tick when emitting compressed bundles to this receiver.
        // Avoids per-tick allocations in the deflate path. Sized by the flush logic.
        public byte[] BundleRawScratch;
        public byte[] BundleCompressedScratch;

        // EMA of compressed/raw ratio observed for this receiver's bundles. Used by
        // FlushPendingForReceiver to predict how many messages fit in one MTU-sized chunk
        // so the first compress attempt usually succeeds with no retry. 0 = unseeded.
        public float LastBundleRatio;

        // Flushes remaining before this receiver re-probes whether bundling is worth the CPU.
        // Non-zero means "this receiver's data did not compress well enough last time we looked,
        // send it uncompressed for now". See AvatarBundleMaxRatio.
        public int BundleSkipCountdown;
    }

    public partial class BasisServerReductionSystemEvents
    {
        private static readonly CancellationTokenSource cts = new();
        // Initial capacity for PeerTracking array on PlayerState.
        // Grows if a player ID exceeds this.
        private const int InitialPlayerArrayCapacity = 2048;

        private static readonly ParallelOptions parallelOptions = new()
        {
            // Use every core. The reserved core dated from when the tick thread itself was the
            // bottleneck; measurement says it is not — the send loop already achieves near-perfect
            // parallelism during its phase, and the idle capacity is in the phases around it.
            MaxDegreeOfParallelism = Environment.ProcessorCount
        };

        public static ShardedConcurrentDictionary<PlayerState> playerStates = new();

        // Admin-flagged full-quality broadcast ids. Authoritative across PlayerState recreation;
        // mirrored onto PlayerState.BypassReduction for the hot send loop. Cleared on disconnect.
        private static readonly ConcurrentDictionary<int, bool> _bypassReductionIds = new();
        // Double-buffered message dictionaries: swap and clear instead of allocating per tick.
        private static ShardedConcurrentDictionary<QueuedMessage> currentMessages = new();
        private static ShardedConcurrentDictionary<QueuedMessage> _backMessages = new();

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
        // Bundle wire header: [count:1][rawLen:2-LE]
        private const int BundleHeaderSize = 3;
        private static readonly double MsToTick = Stopwatch.Frequency / 1000.0;

        // Maintained incrementally via ProcessMessage/ProcessPendingRemovals instead of rebuilt every tick.
        private static readonly List<(int id, PlayerState state)> _activePlayers = new();
        private static readonly object _activePlayersLock = new();
        private static (int id, PlayerState state)[] _activePlayersSnapshot = Array.Empty<(int, PlayerState)>();
        private static volatile bool _activePlayersDirty = false;

        private static readonly ConcurrentQueue<int> playersToRemove = new();

        // Lets the tick loop park (~0% CPU) when the server is empty instead of
        // polling at 250Hz. Set() the moment the first packet arrives so there is
        // no join latency. Same approach LiteNetLib's logic thread already uses.
        private static readonly AutoResetEvent _tickWake = new(false);
        private static int _activePlayerCount;

        // Reusable snapshot list for draining currentMessages each tick  avoids allocation per tick.
        private static readonly List<QueuedMessage> _messagesSnapshot = new(1024);

        // Static delegates — avoid closure allocation every tick. The index form is what the tick
        // loop uses (see the range-partitioning note there); the message form is kept for callers
        // that already hold the message.
        private static readonly Action<QueuedMessage> s_processMessageAction = msg =>
        {
            try
            {
                ProcessMessage(msg);
            }
            catch (Exception ex)
            {
                BNL.LogError($"[ProcessMessage] Exception: {ex}");
            }
        };

        private static readonly Action<int> s_processMessageByIndexAction = i =>
        {
            try
            {
                ProcessMessage(_messagesSnapshot[i]);
            }
            catch (Exception ex)
            {
                BNL.LogError($"[ProcessMessage] Exception: {ex}");
            }
        };

        // Distance -> Quality thresholds (squared meters)
        public static float HighDistanceSq = 100f;      // 10m
        public static float MediumDistanceSq = 900f;    // 30m
        public static float LowDistanceSq = 2500f;      // 50m

        /// <summary>
        /// Current tick period in ms, adapted at runtime between <see cref="MinTickIntervalMs"/> and
        /// <see cref="MaxTickIntervalMs"/>.
        ///
        /// Ticking faster is NOT free and buys nothing below a point: clients publish at roughly 11 Hz
        /// and the shortest distance-derived send interval is 50 ms (20 Hz), so at the old fixed 250 Hz
        /// the loop spun 12-20 times between any actual send to a given pair. Each of those ticks still
        /// paid full fork/join barriers on several Parallel loops, and split the work into batches too
        /// small to partition well. Slowing down under load therefore *increases* throughput: bigger
        /// batches, better parallel efficiency, fewer barriers — and no added latency while the period
        /// stays under the shortest send interval.
        /// </summary>
        public static long intervalMs = 10;

        // 4 ms (250 Hz) floor: no pair is ever scheduled faster than 20 Hz, so going below this only
        // adds barriers. 20 ms (50 Hz) ceiling: still comfortably under the 50 ms shortest interval,
        // so even fully backed off the tick never becomes the thing limiting delivery rate.
        public const long MinTickIntervalMs = 4;
        public const long MaxTickIntervalMs = 20;
        // Fallback wake while the server is empty; _tickWake.Set() does the real wake.
        private const int IdleWaitMs = 250;
        // Load-adaptive inter-tick wait: if the tick left more than this much of its budget
        // unused (light load), block on WaitOne (~0% CPU); if less (heavy load, near the
        // budget), busy-spin the small remainder to hit the rate precisely. Doubles as the
        // spin cap — the loop never spins more than this per tick. Set to 0 for pure WaitOne
        // (lowest CPU, looser rate under load); raise toward intervalMs to favor a tight
        // rate at higher load. Saturated ticks (no slack) never wait or spin regardless.
        public static double MaxSpinMs = 2.5;
        // Tick slicing: only process a subset of receivers each tick to spread the O(NÂ²) work.
        // Adaptive: increases when ticks take too long, decreases when under budget.
        private static int _sliceCount = 1;
        private static int _sliceIndex = 0;

        // Distance cache: recalculate quality/interval from distance every N ticks.
        // The fast send loop uses cached values instead of computing distance per pair per tick.
        // At 4ms tick interval, 125 ticks = ~500ms. Players at 6m/s cover 3m in that time,
        // which is within one quality threshold (3m/10m/20m) — acceptable staleness.
        // Cursor for the amortized distance-cache sweep: index of the next receiver to refresh.
        private static int _distanceSliceCursor = 0;
        private static int _distanceTickCounter = 0;
        // Minimum receivers per distance slice. Below roughly this the Parallel.For dispatch costs
        // more than the work it schedules; see UpdateDistanceCacheSlice.
        private const int MinDistanceSliceReceivers = 128;
        // Smoothed tick duration driving the load controller, and a rate limit for its log line.
        private static double _tickMsEma;
        private static long _lastSliceLogTick;

        // Overrun-ratio control signal. Evaluated once per window rather than per tick so the
        // controller cannot chatter, and so a single slow tick cannot move it.
        // ⚠️ WINDOW LENGTH IS LOAD-BEARING. It bounds how fast the controller can respond, and each
        // evaluation moves exactly one step. At 128 ticks the server needed thousands of ticks to
        // reach the slicing an overloaded instance requires — measured: it sat undegraded while the
        // tick climbed to 339 ms, because 4M pairs/tick piled up faster than it could react. Short
        // enough to respond within a second, long enough that a GC pause cannot flip it.
        private const int TickControlWindow = 16;
        private const double OverrunEscalateRatio = 0.25;   // a quarter of ticks missing = real load
        private const double OverrunRecoverRatio = 0.05;    // almost never missing = give capacity back
        // Above this, the server is not merely behind — it is collapsing, and one step per window is
        // too slow to catch up. Escalation takes several steps at once until it is back in control.
        private const double OverrunPanicRatio = 0.75;
        private const int PanicEscalationSteps = 4;
        // Cap on how far shedding may stretch an interval. 3 doublings = 8x, so a VeryLow pair on a
        // ~500 ms interval bottoms out around 4 s — slow and obviously distant, but still visibly
        // alive. Without a cap the interval would grow until it was indistinguishable from frozen,
        // which is the failure this whole mechanism exists to avoid.
        private const int MaxShedIntervalDoublings = 3;
        private static int _tickWindowCount;
        private static int _tickOverrunCount;
        private static double _tickOverrunRatio;
        private static bool _tickControlReady;

        // Distance-ordered load shedding. 0 = send everything. 1 = drop VeryLow pairs (the furthest),
        // 2 = also drop Low, 3 = High only. Raised before slicing because slicing degrades everyone
        // uniformly, which costs nearby players the most (see the send loop for the full reasoning).
        private static int _loadShedTier;
        // Capped at 2 deliberately. Tier 3 would mean "High only", i.e. nobody past ~10 m updates at
        // all — measured at 2000 players the controller drove straight to it and the world outside
        // arm's reach froze. 2 still guarantees everyone inside the Medium band (~30 m) keeps
        // updating, which is the population a player can actually perceive moving.
        private const int MaxLoadShedTier = 2;
        /// <summary>
        /// Master switch for distance-ordered shedding. When false the server keeps the old behaviour
        /// of slicing only — every receiver's rate drops uniformly under load, and no player is ever
        /// dropped outright. Leave it on unless something depends on distant players always updating.
        /// </summary>
        public static bool LoadSheddingEnabled = true;

        private static string LoadShedTierName(int tier) => tier switch
        {
            0 => "none",
            1 => "dropping VeryLow (furthest)",
            2 => "dropping VeryLow+Low",
            _ => "High only (nearest)",
        };

        public static bool WriteLoadLog = true;

        public static double TickMsEma => Volatile.Read(ref _tickMsEma);
        public static double TickOverrunRatio => Volatile.Read(ref _tickOverrunRatio);
        public static int LoadShedTier => Volatile.Read(ref _loadShedTier);
        public static int SliceCount => Volatile.Read(ref _sliceCount);
        public static string LoadShedTierLabel => LoadShedTierName(Volatile.Read(ref _loadShedTier));
        public static int DistanceUpdateIntervalTicks = 125;

        // Cached muscle+tail byte counts for the position-only fast path (skip repack).
        private static readonly int HighMuscleAndTailBytes = MuscleBytes(BitQuality.High) + TailBytes;

        // Generation snapshot: populated once per tick before the O(N²) send loop.
        // Eliminates Interlocked.Read per pair (N² memory fences → N).
        // Pre-allocated to InitialPlayerArrayCapacity to avoid reallocation on early player joins.
        private static long[] _generationSnapshot = new long[InitialPlayerArrayCapacity];

        // Position snapshots: contiguous arrays for cache-friendly reads in the inner loop.
        // Avoids pointer-chasing through scattered heap PlayerState objects per pair.
        private static float[] _posXSnapshot = new float[InitialPlayerArrayCapacity];
        private static float[] _posYSnapshot = new float[InitialPlayerArrayCapacity];
        private static float[] _posZSnapshot = new float[InitialPlayerArrayCapacity];



        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint timeBeginPeriod(uint uMilliseconds);

        static BasisServerReductionSystemEvents()
        {
            // Raise the OS timer to 1ms so WaitOne keeps ~4ms accuracy on Windows (default
            // ~15ms). Windows-only: the winmm P/Invoke is never resolved on Linux/macOS
            // because the call is skipped there (those already resolve to ~1ms). try/catch so
            // a missing winmm (minimal Windows containers) degrades instead of faulting the
            // static ctor and taking the whole reduction system down with it.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try { timeBeginPeriod(1); }
                catch (Exception ex) { BNL.LogError($"[BSR] timeBeginPeriod unavailable, tick timing falls back to OS default: {ex.Message}"); }
            }

            var thread = new Thread(BackgroundTickLoop)
            {
                IsBackground = true,
                Name = "BSR-TickLoop",
                Priority = ThreadPriority.AboveNormal,
            };
            thread.Start();
        }

        public static void HandleAvatarMovement(NetPacketReader reader, NetPeer fromPeer, byte channel)
        {
            // Read the application-level sequence byte prepended by the client
            if (!reader.TryGetByte(out byte sequence))
            {
                reader.Recycle();
                return;
            }

            // Quality and additional-data presence are derived from the channel.
            byte quality = BasisNetworkCommons.GetQualityFromChannel(channel);
            bool hasAdditional = BasisNetworkCommons.ChannelHasAdditionalData(channel);

            // Rent BEFORE deserialize so the pooled byte[] is reused (avoids alloc per frame per player).
            var message = QueuedMessagePool.Rent();
            message.FromPeer = fromPeer;
            message.Sequence = sequence;
            message.AvatarMessage.Deserialize(reader, quality, hasAdditional);
            reader.Recycle();

            if (message.AvatarMessage.array == null)
            {
                BNL.LogError($"[HandleAvatarMovement] Deserialized avatar message has null array from peer {fromPeer.Id}");
                QueuedMessagePool.Return(message);
                return;
            }

            // Every full High frame doubles as the sender's uplink delta baseline (v42).
            if (quality == 3)
            {
                UplinkCaptureBaseline(fromPeer.Id, message.AvatarMessage.array, sequence);
            }

            // Overwrite any pending message for this peer.
            // Uses indexer instead of AddOrUpdate to avoid closure allocation on every call.
            // Do NOT return prev to the pool — the drain phase may have captured it.
            // The orphaned prev (if any) is collected by the GC; this only occurs when two
            // messages for the same peer arrive within the same tick — negligible cost.
            currentMessages[fromPeer.Id] = message;

            // Wake the loop only while it is parked (empty server). Once a player is
            // registered the loop is running, so this read short-circuits with no syscall.
            if (Volatile.Read(ref _activePlayerCount) == 0) _tickWake.Set();
        }

        // ── Uplink avatar deltas (v42) ──
        // Per-sender baseline of the last full keyframe the client uploaded on the High channels.
        // Owned by the network receive thread (capture + delta apply both run there); removed on
        // the tick thread via ProcessPendingRemovals (ConcurrentDictionary makes that safe).
        private sealed class UplinkDeltaState
        {
            public byte[] Baseline;
            public byte BaselineSeq;
            public bool Has;
            public long LastNackTicks;
        }
        private static readonly ConcurrentDictionary<int, UplinkDeltaState> _uplinkStates = new();
        private static readonly long NackMinIntervalTicks = Stopwatch.Frequency; // 1/s per sender

        /// <summary>
        /// Snapshot a freshly-arrived full High keyframe as the sender's uplink delta baseline.
        /// Every ch12/13 arrival is a full payload, so P2P-era full-rate senders and the load
        /// tester feed this for free; delta-era clients send one every ~500 ms.
        /// </summary>
        private static void UplinkCaptureBaseline(int peerId, byte[] payload, byte sequence)
        {
            int size = BasisAvatarBitPacking.ConvertToSize(BitQuality.High);
            if (payload == null || payload.Length < size) return;
            UplinkDeltaState st = _uplinkStates.GetOrAdd(peerId, static _ => new UplinkDeltaState());
            if (st.Baseline == null || st.Baseline.Length < size) st.Baseline = new byte[size];
            Buffer.BlockCopy(payload, 0, st.Baseline, 0, size);
            st.BaselineSeq = sequence;
            st.Has = true;
        }

        private static void NackUplink(NetPeer peer, UplinkDeltaState st)
        {
            long now = Stopwatch.GetTimestamp();
            if (st != null)
            {
                if (now - st.LastNackTicks < NackMinIntervalTicks) return;
                st.LastNackTicks = now;
            }
            NetDataWriter writer = NetworkServer.RentWriter();
            try
            {
                writer.Put(BasisNetworkCommons.DeltaControlUplinkKeyframeRequest);
                NetworkServer.TrySend(peer, writer, BasisNetworkCommons.DeltaAvatarChannel, DeliveryMethod.ReliableOrdered);
            }
            finally
            {
                NetworkServer.ReturnWriter(writer);
            }
        }

        /// <summary>
        /// Inbound DeltaAvatarChannel demux (v42). Header bit 7 = control frame: KeyframeRequest
        /// asks the server to re-key one sender for the requesting receiver. Otherwise it is an
        /// uplink avatar delta [hdr][seq][baseSeq][body][additional?] — reconstructed against the
        /// sender's last uploaded keyframe and fed through the normal avatar ingest path.
        /// </summary>
        public static void HandleDeltaChannelInbound(NetPacketReader reader, NetPeer fromPeer)
        {
            if (!reader.TryGetByte(out byte header))
            {
                reader.Recycle();
                return;
            }

            if (BasisNetworkCommons.IsDeltaControlHeader(header))
            {
                if (header == BasisNetworkCommons.DeltaControlKeyframeRequest && reader.TryGetUShort(out ushort senderId))
                {
                    RequestKeyframe(senderId, fromPeer.Id);
                }
                reader.Recycle();
                return;
            }

            if (BasisNetworkCommons.DeltaHeaderQuality(header) != 3)
            {
                // Clients only ever upload High.
                reader.Recycle();
                return;
            }
            bool hasAdditional = BasisNetworkCommons.DeltaHeaderHasAdditionalData(header);
            if (!reader.TryGetByte(out byte sequence) || !reader.TryGetByte(out byte baseSeq))
            {
                reader.Recycle();
                return;
            }

            _uplinkStates.TryGetValue(fromPeer.Id, out UplinkDeltaState st);
            if (st == null || !st.Has || st.BaselineSeq != baseSeq)
            {
                // Missing/stale baseline (lost keyframe or reorder) — ask for a fresh keyframe.
                NackUplink(fromPeer, st);
                reader.Recycle();
                return;
            }

            int bodyLen = BasisAvatarDeltaCompression.DeltaBodyLength(reader.RawData, reader.Position, reader.AvailableBytes, BitQuality.High);
            if (bodyLen < 0 || bodyLen > reader.AvailableBytes)
            {
                reader.Recycle();
                return;
            }

            int payloadSize = BasisAvatarDeltaCompression.PayloadSize(BitQuality.High);
            var message = QueuedMessagePool.Rent();
            message.FromPeer = fromPeer;
            message.Sequence = sequence;
            if (message.AvatarMessage.array == null || message.AvatarMessage.array.Length != payloadSize)
            {
                message.AvatarMessage.array = new byte[payloadSize];
            }

            if (!BasisAvatarDeltaCompression.TryApplyDelta(st.Baseline, reader.RawData, reader.Position, bodyLen, BitQuality.High, message.AvatarMessage.array))
            {
                QueuedMessagePool.Return(message);
                reader.Recycle();
                return;
            }
            reader.SkipBytes(bodyLen);

            message.AvatarMessage.DataQualityLevel = 3;
            message.AvatarMessage.AdditionalAvatarDataSize = 0;
            message.AvatarMessage.AdditionalAvatarDatas = null;
            if (hasAdditional)
            {
                message.AvatarMessage.DeserializeAdditionalData(reader);
            }
            reader.Recycle();

            // Same ingest as HandleAvatarMovement — ProcessMessage deep-copies and repacks.
            currentMessages[fromPeer.Id] = message;
            if (Volatile.Read(ref _activePlayerCount) == 0) _tickWake.Set();
        }

        public static void AddMessage(NetPeer fromPeer, LocalAvatarSyncMessage localMessage, byte sequence)
        {
            var message = QueuedMessagePool.Rent();
            message.FromPeer = fromPeer;
            message.Sequence = sequence;
            message.AvatarMessage = localMessage;

            // Same as HandleAvatarMovement — indexer avoids closure allocation.
            currentMessages[fromPeer.Id] = message;

            if (Volatile.Read(ref _activePlayerCount) == 0) _tickWake.Set();
        }

        /// <summary>
        /// Dedicated tick loop on its own thread. Targets ~250Hz (4ms) while players are
        /// connected and parks (~0% CPU) when the server is empty. The inter-tick wait uses
        /// AutoResetEvent.WaitOne so an idle or under-budget loop never burns a core; the OS
        /// timer is raised to 1ms (Windows) so the wait keeps ~4ms accuracy.
        /// </summary>
        private static void BackgroundTickLoop()
        {
            while (!cts.Token.IsCancellationRequested)
            {
                long startTick = Stopwatch.GetTimestamp();

                // One bad tick must not kill the thread. An unhandled throw here (e.g. an
                // edge case during mass connect/recycle) would otherwise stop every future
                // tick and silently freeze all avatar sync until server restart.
                try
                {
                    RunTick(startTick);
                }
                catch (Exception ex)
                {
                    BNL.LogError($"[BSR Tick] Unhandled exception: {ex}");
                }

                // Empty server: park until work arrives instead of spinning at 250Hz.
                // _tickWake is signaled by the first inbound packet (and by Shutdown),
                // so this costs ~0% CPU when idle with no added connect latency.
                if (Volatile.Read(ref _activePlayerCount) == 0)
                {
                    _tickWake.WaitOne(IdleWaitMs);
                    continue;
                }

                // Load-adaptive wait. remainMs is the unused budget = a direct load signal:
                // large (light load) -> block on WaitOne (~0% CPU); small (heavy load, near
                // budget) -> spin the remainder to hit the rate precisely, since the scheduler
                // wakes a yielded thread late under load and the core is busy anyway. remainMs
                // <= 0 (saturated) falls through both branches: no wait, no spin.
                long targetTick = startTick + (long)(intervalMs * MsToTick);
                double remainMs = (targetTick - Stopwatch.GetTimestamp()) / MsToTick;
                if (remainMs > MaxSpinMs)
                {
                    _tickWake.WaitOne((int)Math.Round(remainMs));
                }
                else
                {
                    while (Stopwatch.GetTimestamp() < targetTick)
                    {
                        Thread.SpinWait(20);
                    }
                }
            }
        }

        private static void RunTick(long startTick)
        {
            bool profiling = BSRProfiler.Enabled;
            long phaseTick = profiling ? Stopwatch.GetTimestamp() : 0;

            // Phase 1: Drain
            // Swap to the back-buffer so inbound threads write to the cleared dictionary.
            // No allocation per tick — just swap and drain.
            _backMessages.Clear();
            var batch = Interlocked.Exchange(ref currentMessages, _backMessages);
            _backMessages = batch;
            _messagesSnapshot.Clear();
            foreach (var kvp in batch)
            {
                _messagesSnapshot.Add(kvp.Value);
            }
            if (profiling) { BSRProfiler.drainTicks += Stopwatch.GetTimestamp() - phaseTick; phaseTick = Stopwatch.GetTimestamp(); }

            // Phase 2: Process messages (static delegate avoids closure allocation per tick)
            // Range-partitioned rather than Parallel.ForEach over the List. The default enumerable
            // partitioner chunks with buffering and rebalances poorly at these counts — measured
            // ~300 messages/tick taking 0.95 ms against a ~0.06 ms ideal, i.e. ~15x off, because the
            // per-chunk overhead swamped ~6 us of actual work per message. An index range over the
            // backing list lets the scheduler split evenly with no per-item bookkeeping.
            int messageCount = _messagesSnapshot.Count;
            if (messageCount > 0)
            {
                Parallel.For(0, messageCount, parallelOptions, s_processMessageByIndexAction);
            }
            if (profiling) { BSRProfiler.processTicks += Stopwatch.GetTimestamp() - phaseTick; phaseTick = Stopwatch.GetTimestamp(); }

            ProcessPendingRemovals();
            ProcessPendingKeyframeRequests();

            // Phase 2.5: Distance cache update, amortized across the interval instead of one big
            // tick. Doing the whole N^2 matrix on a single tick made that tick cost ~125x its
            // neighbours, which then fed the slice adaptation below and ratcheted it upward.
            long distStart = profiling ? Stopwatch.GetTimestamp() : 0;
            bool didDistanceWork = UpdateDistanceCacheSlice();
            if (profiling && didDistanceWork)
            {
                BSRProfiler.distanceTicks += Stopwatch.GetTimestamp() - distStart;
                phaseTick = Stopwatch.GetTimestamp();
            }

            //Phase 3: Send loop
            long now = Stopwatch.GetTimestamp();
            UpdateCommunicationAndDistances(now);
            if (profiling)
            {
                BSRProfiler.updateTicks += Stopwatch.GetTimestamp() - phaseTick; phaseTick = Stopwatch.GetTimestamp();
            }

            //Phase 4: Network I/O
            BasisNetworkPIPCamera.UpdatePIPPositions(now);
            if (NetworkServer.Server is LNLNetManager lnlReductionServer && lnlReductionServer.manager != null)
            {
                lnlReductionServer.manager.TriggerUpdate();
            }
            if (profiling)
            {
                BSRProfiler.triggerTicks += Stopwatch.GetTimestamp() - phaseTick;
                BSRProfiler.tickCount++;
                BSRProfiler.messagesProcessed += _messagesSnapshot.Count;
            }

            //Tick bookkeeping
            long elapsedTicks = Stopwatch.GetTimestamp() - startTick;
            double elapsedMs = elapsedTicks / MsToTick;

            BSRProfiler.TryPrint();

            // Load controller. It has three levers, escalated in order of how much a player notices:
            //   1. tick PERIOD  — invisible while it stays under the shortest send interval
            //   2. shed tier    — distant players stop updating
            //   3. slicing      — LAST RESORT, cuts everyone's rate uniformly, so it hurts the
            //                     players standing next to you most (their intervals are the short
            //                     ones; a distant player is already on a long interval and unaffected)
            // Recovery unwinds in reverse, restoring visibility before rate.
            //
            // Kept for the log line only — the controller no longer steers on it, see below.
            _tickMsEma = _tickMsEma <= 0.0 ? elapsedMs : (_tickMsEma * 0.9 + elapsedMs * 0.1);

            // ── Control signal: how OFTEN we miss the period, not the average time ──
            // An EMA of tick duration is the wrong signal here. Tick time is heavy-tailed (GC, OS
            // scheduling, bursty inbound), so a handful of slow ticks drag the mean well above the
            // typical tick — measured 18 ms EMA against a 13.2 ms real average at 2000 players. The
            // controller then escalated against load that was not there and kept shedding players
            // while 42% of the box sat idle. Counting overruns is bounded and outlier-insensitive:
            // one 60 ms tick is one overrun, not a permanent shift in the average.
            _tickWindowCount++;
            if (elapsedMs > intervalMs) _tickOverrunCount++;

            if (_tickWindowCount >= TickControlWindow)
            {
                _tickOverrunRatio = _tickOverrunCount / (double)_tickWindowCount;
                _tickWindowCount = 0;
                _tickOverrunCount = 0;
                _tickControlReady = true;
            }

            if (!_tickControlReady)
            {
                return;
            }
            _tickControlReady = false;

            // Adapt the tick PERIOD before degrading anything the players can see. Stretching the
            // period costs nothing while it stays under the shortest send interval, and it directly
            // buys back parallel efficiency (bigger batches, fewer barriers). Only once the period is
            // maxed out do we start shedding distant pairs, and only after that do we slice.
            bool overloaded = _tickOverrunRatio > OverrunEscalateRatio;
            bool comfortable = _tickOverrunRatio < OverrunRecoverRatio;
            int escalationSteps = _tickOverrunRatio > OverrunPanicRatio ? PanicEscalationSteps : 1;
            int previousSliceCount = _sliceCount;
            int previousShedTier = _loadShedTier;
            long previousInterval = intervalMs;

            if (overloaded && intervalMs < MaxTickIntervalMs)
            {
                intervalMs = Math.Min(MaxTickIntervalMs, intervalMs + 2L * escalationSteps);
            }
            else if (comfortable && intervalMs > MinTickIntervalMs
                     && _sliceCount == 1 && _loadShedTier == 0)
            {
                // Only tighten the period once nothing is being degraded — otherwise the loop would
                // speed back up while still dropping players, which is the wrong order of recovery.
                intervalMs = Math.Max(MinTickIntervalMs, intervalMs - 1);
            }

            if (overloaded)
            {
                // Shed the furthest pairs FIRST, and only fall back to slicing once even a
                // High-quality-only workload cannot fit the budget. Slicing is the blunt instrument:
                // it cuts everyone's rate uniformly, which hurts nearby players most (their intervals
                // are the short ones), so it must be the last resort rather than the first.
                if (intervalMs < MaxTickIntervalMs)
                {
                    // Period is still stretching — give that a chance before degrading anything.
                }
                else if (LoadSheddingEnabled && _loadShedTier < MaxLoadShedTier)
                {
                    _loadShedTier = Math.Min(MaxLoadShedTier, _loadShedTier + escalationSteps);
                }
                else if (_sliceCount < 32)
                {
                    _sliceCount = Math.Min(32, _sliceCount + escalationSteps);
                }
            }
            else if (comfortable)
            {
                // Recover visibility BEFORE rate. Restoring a dropped player at a low update rate is
                // far better than leaving them frozen while everyone else speeds up — and unwinding
                // slicing first proved to be a trap: slicing oscillates under normal jitter, so the
                // tier never got a chance to come back down and the server sat permanently at maximum
                // shedding even once the tick was comfortably inside budget.
                if (_loadShedTier > 0)
                {
                    _loadShedTier--;
                }
                else if (_sliceCount > 1)
                {
                    _sliceCount--;
                }
            }

            // Rate-limited: this is a health signal, not a per-change trace.
            if (WriteLoadLog && (_sliceCount != previousSliceCount || _loadShedTier != previousShedTier || intervalMs != previousInterval))
            {
                long nowLog = Stopwatch.GetTimestamp();
                if (nowLog - _lastSliceLogTick > Stopwatch.Frequency * 5)
                {
                    _lastSliceLogTick = nowLog;
                    BNL.Log($"[BSR] Load: {_tickOverrunRatio:P0} of ticks over budget " +
                            $"(mean {_tickMsEma:F2} ms), period {intervalMs} ms " +
                            $"({1000 / Math.Max(1, intervalMs)} Hz), shed tier {_loadShedTier} " +
                            $"({LoadShedTierName(_loadShedTier)}), slicing {_sliceCount}. " +
                            $"Period alone is harmless; tier > 0 means distant players stop updating; " +
                            $"slicing > 1 means everyone's rate is reduced.");
                }
            }
        }

        /// <summary>
        /// Ceiling on removals drained per tick. Each removal is O(N) — it walks every remaining
        /// player to clear the leaver's tracking slot — so an unbounded drain turns a mass disconnect
        /// (the end of any load test, or a region outage) into O(N^2) on the tick thread with avatar
        /// sync frozen for the duration. Spreading it costs a few extra ticks before an ID can be
        /// safely reused, which nothing depends on.
        /// </summary>
        private const int MaxRemovalsPerTick = 8;

        /// <summary>
        /// Spreads a player's periodic-keyframe phase over [0,1) from their id. Knuth's multiplicative
        /// hash, so sequentially assigned ids (exactly what a mass join produces) land far apart
        /// rather than adjacent.
        /// </summary>
        private static double KeyframePhaseFraction(int id)
        {
            uint scrambled = unchecked((uint)id * 2654435761u);
            return scrambled / (double)uint.MaxValue;
        }

        private static void ProcessPendingRemovals()
        {
            int removalsThisTick = 0;
            while (removalsThisTick < MaxRemovalsPerTick && playersToRemove.TryDequeue(out int id))
            {
                removalsThisTick++;
                _uplinkStates.TryRemove(id, out _);
                if (playerStates.TryRemove(id, out var removedState))
                {
                    removedState.IsActive = false;

                    // Return pooled arrays to ArrayPool
                    if (removedState.AvatarHigh.array != null)
                        ArrayPool<byte>.Shared.Return(removedState.AvatarHigh.array);
                    if (removedState.BundleRawScratch != null)
                    {
                        ArrayPool<byte>.Shared.Return(removedState.BundleRawScratch);
                        removedState.BundleRawScratch = null;
                    }
                    if (removedState.BundleCompressedScratch != null)
                    {
                        ArrayPool<byte>.Shared.Return(removedState.BundleCompressedScratch);
                        removedState.BundleCompressedScratch = null;
                    }

                    // Remove from active players list
                    lock (_activePlayersLock)
                    {
                        for (int i = _activePlayers.Count - 1; i >= 0; i--)
                        {
                            if (_activePlayers[i].id == id)
                            {
                                _activePlayers.RemoveAt(i);
                                _activePlayersDirty = true;
                                Interlocked.Decrement(ref _activePlayerCount);
                                break;
                            }
                        }
                    }


                    // Clear stale per-player tracking data for the removed ID across all remaining players.
                    // Without this, when a new player reuses this ID, other players LastSeenGeneration
                    // would still hold the old (high) generation value, causing the new-data check
                    // (senderGen > seenGens[jId]) to fail -- no data would be sent for the new player.
                    // Must enumerate the authoritative dictionary, NOT _activePlayersSnapshot: the
                    // snapshot is only rebuilt when dirty, so a player added earlier this tick may be
                    // absent from it and would keep a stale generation for this id — which is exactly
                    // the reuse bug described above. The per-tick removal budget is what bounds this.
                    foreach (var kvp in playerStates)
                    {
                        var otherState = kvp.Value;
                        if (id < otherState.PeerTracking.Length)
                        {
                            otherState.PeerTracking[id] = default;
                        }
                    }
                    BNL.Log($"Player {id} removed and cleaned up.");
                }
                else
                {
                    BNL.LogError("Missing Player From Index, Normally Quick Disconnect after Connect " + id);
                }
            }
        }

        /// <summary>
        /// Refreshes a slice of the distance cache. Every receiver is still revisited once per
        /// DistanceUpdateIntervalTicks, but the O(N^2) matrix is spread evenly across those ticks
        /// rather than landing on one of them.
        ///
        /// The old shape did the whole matrix on every 125th tick. At 1000 players that is 999,000
        /// pairs on a tick whose neighbours do none, which is a textbook unamortized spike — and it
        /// fed straight into the slice adaptation in RunTick, pushing _sliceCount up every interval
        /// until it pinned at its cap and silently cut everyone's send rate.
        /// </summary>
        /// <returns>True if this tick did any distance work (so profiling only counts real work).</returns>
        private static bool UpdateDistanceCacheSlice()
        {
            if (_activePlayersDirty)
            {
                lock (_activePlayersLock)
                {
                    if (_activePlayersDirty)
                    {
                        _activePlayersSnapshot = _activePlayers.ToArray();
                        _activePlayersDirty = false;
                    }
                }
            }
            var activeCopy = _activePlayersSnapshot;
            int playerCount = activeCopy.Length;
            if (playerCount == 0)
            {
                _distanceSliceCursor = 0;
                return false;
            }

            // Between sweeps: the refresh period is still DistanceUpdateIntervalTicks, we just do the
            // work in chunks rather than all on one tick.
            if (_distanceSliceCursor == 0 && ++_distanceTickCounter < DistanceUpdateIntervalTicks)
            {
                return false;
            }

            // ⚠️ Slice size is bounded BELOW for a reason. This work is a Parallel.For over receivers,
            // so a slice must carry enough receivers to be worth dispatching — sizing it as
            // playerCount/interval gives ~8 at 1000 players, and the parallel setup then costs far
            // more than the distance math it is scheduling. Measured: the naive split made the whole
            // phase ~30x more expensive than the periodic full sweep it replaced. Larger chunks mean
            // the sweep finishes early and simply idles until the next period, which is the point.
            int perTick = Math.Max(MinDistanceSliceReceivers,
                (playerCount + DistanceUpdateIntervalTicks - 1) / DistanceUpdateIntervalTicks);

            if (_distanceSliceCursor >= playerCount)
            {
                _distanceSliceCursor = 0;
            }
            int sliceStart = _distanceSliceCursor;
            int sliceEnd = Math.Min(sliceStart + perTick, playerCount);
            if (sliceEnd >= playerCount)
            {
                // Sweep complete — restart the period counter.
                _distanceSliceCursor = 0;
                _distanceTickCounter = 0;
            }
            else
            {
                _distanceSliceCursor = sliceEnd;
            }

            // Positions are re-snapshotted only when starting a fresh sweep, so every receiver in
            // one sweep is measured against the same frame rather than a moving target.
            if (sliceStart == 0)
            {
                SnapshotPositions(activeCopy, playerCount);
            }

            RunDistanceSlice(activeCopy, playerCount, sliceStart, sliceEnd);
            return true;
        }

        private static void SnapshotPositions((int id, PlayerState state)[] activeCopy, int playerCount)
        {

            // Snapshot positions into contiguous arrays for cache-friendly distance math.
            int maxId = 0;
            for (int i = 0; i < playerCount; i++)
            {
                if (activeCopy[i].id > maxId) maxId = activeCopy[i].id;
            }
            int snapshotLen = maxId + 1;
            if (_posXSnapshot.Length < snapshotLen)
            {
                int newLen = Math.Max(snapshotLen, _posXSnapshot.Length * 2);
                _posXSnapshot = new float[newLen];
                _posYSnapshot = new float[newLen];
                _posZSnapshot = new float[newLen];
            }
            for (int i = 0; i < playerCount; i++)
            {
                int id = activeCopy[i].id;
                var state = activeCopy[i].state;
                _posXSnapshot[id] = state.Position.x;
                _posYSnapshot[id] = state.Position.y;
                _posZSnapshot[id] = state.Position.z;
            }
        }

        private static void RunDistanceSlice((int id, PlayerState state)[] activeCopy, int playerCount, int sliceStart, int sliceEnd)
        {
            Parallel.For(sliceStart, sliceEnd, parallelOptions, i =>
            {
                var (id, state) = activeCopy[i];
                var tracking = state.PeerTracking;
                if (tracking == null) return;

                float iX = _posXSnapshot[id];
                float iY = _posYSnapshot[id];
                float iZ = _posZSnapshot[id];

                for (int index = 0; index < playerCount; index++)
                {
                    int jId = activeCopy[index].id;
                    if (id == jId) continue;

                    // Grow tracking array if needed (same logic as send loop)
                    if (jId >= tracking.Length)
                    {
                        lock (state)
                        {
                            if (jId >= state.PeerTracking.Length)
                            {
                                int newLen = Math.Max(state.PeerTracking.Length * 2, jId + 1);
                                Array.Resize(ref state.PeerTracking, newLen);
                            }
                            tracking = state.PeerTracking;
                        }
                    }

                    float dx = iX - _posXSnapshot[jId];
                    float dy = iY - _posYSnapshot[jId];
                    float dz = iZ - _posZSnapshot[jId];
                    float distSq = dx * dx + dy * dy + dz * dz;

                    CalculateIntervalFromDistanceSq(distSq, out byte intervalByte, out int actualInterval);

                    tracking[jId].CachedIntervalTicks = (int)(actualInterval * MsToTick);
                    tracking[jId].CachedQualityIndex = (byte)GetQualityIndex(distSq);
                    tracking[jId].CachedIntervalByte = intervalByte;
                }
            });
        }

        private static void UpdateCommunicationAndDistances(long nowTicks)
        {
            // Double-buffered snapshot: only rebuild when dirty
            if (_activePlayersDirty)
            {
                lock (_activePlayersLock)
                {
                    if (_activePlayersDirty)
                    {
                        _activePlayersSnapshot = _activePlayers.ToArray();
                        _activePlayersDirty = false;
                    }
                }
            }
            var activeCopy = _activePlayersSnapshot;

            int playerCount = activeCopy.Length;
            if (playerCount == 0)
            {
                return;
            }

            // Snapshot generation counters only (positions handled by slow distance cache).
            int maxId = 0;
            for (int i = 0; i < playerCount; i++)
            {
                if (activeCopy[i].id > maxId) maxId = activeCopy[i].id;
            }
            int snapshotLen = maxId + 1;
            if (_generationSnapshot.Length < snapshotLen)
            {
                _generationSnapshot = new long[Math.Max(snapshotLen, _generationSnapshot.Length * 2)];
            }
            for (int i = 0; i < playerCount; i++)
            {
                int id = activeCopy[i].id;
                _generationSnapshot[id] = Interlocked.Read(ref activeCopy[i].state.DataGeneration);
            }

            // Fallback interval for pairs not yet in the distance cache (new players).
            long minIntervalTicks = (long)(BSRSMillisecondDefaultInterval * BSRBaseMultiplier * MsToTick);

            // Floor on the interval we advertise this tick: a receiver is only visited every
            // _sliceCount ticks, so nothing can actually arrive faster than that regardless of what
            // distance says. Encoded once here rather than per pair.
            int deliverableIntervalMs = (int)(intervalMs * Math.Max(1, _sliceCount));
            byte degradedIntervalByte = BasisNetworkCommons.EncodeAvatarIntervalByte(
                deliverableIntervalMs, BSRSMillisecondDefaultInterval);

            // Tick slicing: only process a slice of receivers per tick
            int sliceSize = (playerCount + _sliceCount - 1) / _sliceCount;
            int start = _sliceIndex * sliceSize;
            int end = Math.Min(start + sliceSize, playerCount);
            _sliceIndex = (_sliceIndex + 1) % _sliceCount;

            if (start >= playerCount)
            {
                return;
            }

            bool bundlingEnabled = EnableAvatarBundleCompression;

            Parallel.For(start, end, parallelOptions, i =>
            {
                var (id, state) = activeCopy[i];
                var stateI = state;
                var peer = stateI.Peer;

                var tracking = stateI.PeerTracking;
                if (tracking == null)
                {
                    return;
                }

                // Per-receiver pending buffer: collect what would be sent this tick
                // and decide compress-or-individual at the end. Lazily grown.
                var pending = stateI.PendingSends;
                if (pending == null)
                {
                    pending = new PendingAvatarSend[64];
                    stateI.PendingSends = pending;
                }
                int pendingCount = 0;

                // Thread-local send counter — no Interlocked in the hot loop
                long localSends = 0;

                for (int index = 0; index < playerCount; index++)
                {
                    int jId = activeCopy[index].id;
                    if (id == jId)
                    {
                        continue;
                    }

                    // Bounds check — grow array if needed (rare, only when IDs exceed capacity)
                    if (jId >= tracking.Length)
                    {
                        lock (stateI)
                        {
                            if (jId >= stateI.PeerTracking.Length)
                            {
                                int newLen = Math.Max(stateI.PeerTracking.Length * 2, jId + 1);
                                Array.Resize(ref stateI.PeerTracking, newLen);
                            }
                            tracking = stateI.PeerTracking;
                        }
                    }

                    // 1. New data check — plain array read, no pointer chase. This is the cheapest
                    // test in the loop and rejects most pairs (senders publish well below tick rate),
                    // so nothing more expensive may run above it. The P2P check in particular used to
                    // sit higher up, paying a ConcurrentDictionary lookup on every pair in the matrix
                    // instead of only the ones that survive this gate.
                    long senderGen = _generationSnapshot[jId];
                    if (senderGen <= tracking[jId].LastSeenGeneration)
                    {
                        continue;
                    }

                    // Their avatar data goes peer-to-peer, so the server must not also relay it.
                    // Guarded by a plain counter so the common case (no P2P sessions at all) costs a
                    // register compare rather than a hash lookup.
                    if (BasisServerP2PBroker.HasOffloadedPairs &&
                        BasisNetworkServer.BasisServerP2PBroker.IsP2POffloaded(jId, id))
                    {
                        continue;
                    }

                    PlayerState stateJ = activeCopy[index].state;

                    // Full-quality broadcast bypasses the distance throttle + quality reduction; the new-data gate still bounds it to the source rate.
                    bool bypassReduction = stateJ.BypassReduction;

                    // Quality tier for this pair, from the distance cache. Needed before the
                    // interval check because load shedding scales the interval by it.
                    int qi = bypassReduction ? 3 : tracking[jId].CachedQualityIndex;

                    // 2. Interval check using cached distance results (no float math).
                    //    Load shedding is applied here as an interval MULTIPLIER rather than as a
                    //    drop, so an overloaded server slows distant players down instead of freezing
                    //    them. A player 100 m away updating at 1 Hz reads as "far away"; the same
                    //    player receiving nothing reads as a broken client, which is exactly what
                    //    dropping the tier outright looked like in testing. Each tier below the shed
                    //    threshold doubles the interval, so degradation is graded by distance.
                    if (!bypassReduction)
                    {
                        long elapsed = nowTicks - tracking[jId].LastSentTime;
                        long required = tracking[jId].CachedIntervalTicks;
                        if (required <= 0) required = minIntervalTicks;

                        int shedSteps = _loadShedTier - qi;
                        if (shedSteps > 0)
                        {
                            required <<= Math.Min(shedSteps, MaxShedIntervalDoublings);
                        }

                        if (elapsed < required)
                        {
                            continue;
                        }
                    }

                    // Why shed by distance at all: tick slicing gets the priority backwards. Slicing
                    // visits a receiver once every _sliceCount ticks, capping its effective rate at
                    // tickRate/_sliceCount. A DISTANT sender is already on a ~500 ms interval so
                    // slicing costs it nothing; a NEARBY sender wants ~20 Hz and gets cut hard. So
                    // slicing hurts precisely the players you can see. Stretching intervals by tier is
                    // O(1) per pair, needs no sorting, and degrades the least visible pairs first.
                    // Report the cadence we will ACTUALLY deliver at, not the one distance asked for.
                    // The client decodes this byte into the interpolation window it plays the pose
                    // over, so a server that promises 50 ms while slicing delivers every 128 ms leaves
                    // every client extrapolating past the end of its window — visible as remote-player
                    // stutter that looks like a client bug. degradedIntervalByte is computed once per
                    // tick from the current slice factor and period; the encoding is monotonic in ms,
                    // so taking the larger byte is the same as taking the longer interval.
                    byte startAtZeroInterval;
                    if (bypassReduction)
                    {
                        startAtZeroInterval = 0;
                    }
                    else
                    {
                        // Include the shed stretch: if this pair is being slowed to a quarter rate the
                        // client must interpolate over that longer window, or it runs off the end of
                        // its buffer and stutters exactly like an under-delivering server.
                        int shedSteps = _loadShedTier - qi;
                        byte pairInterval = tracking[jId].CachedIntervalByte;
                        if (shedSteps > 0)
                        {
                            int stretched = BasisNetworkCommons.DecodeAvatarIntervalMs(pairInterval, BSRSMillisecondDefaultInterval)
                                            << Math.Min(shedSteps, MaxShedIntervalDoublings);
                            pairInterval = BasisNetworkCommons.EncodeAvatarIntervalByte(stretched, BSRSMillisecondDefaultInterval);
                        }
                        startAtZeroInterval = Math.Max(pairInterval, degradedIntervalByte);
                    }

                    // Delta vs keyframe: send a delta only when the current frame is a delta, the
                    // receiver already holds the current keyframe at this quality, and the delta is
                    // serialized. Otherwise send — and (re)baseline the receiver to — the keyframe.
                    bool sendDelta = EnableAvatarDeltaCompression
                        && !bypassReduction
                        && !stateJ.CurrentIsKeyframe
                        && tracking[jId].BaselineKeyframeGen == stateJ.KeyframeGen
                        && tracking[jId].BaselineQuality == qi
                        && stateJ.SerializedDeltaLength[qi] > 0
                        && stateJ.SerializedDelta[qi] != null;

                    byte[] srcArr;
                    int srcLen;
                    byte avatarChannel;
                    byte intervalOffset;
                    if (sendDelta)
                    {
                        srcArr = stateJ.SerializedDelta[qi];
                        srcLen = stateJ.SerializedDeltaLength[qi];
                        avatarChannel = BasisNetworkCommons.DeltaAvatarChannel;
                        // delta frame layout: [header:1][playerId:1|2][interval:1]...
                        intervalOffset = (byte)(stateJ.SmallId ? 2 : 3);
                    }
                    else
                    {
                        // Keyframe path (also the fallback when the receiver lacks the current baseline).
                        srcLen = stateJ.SerializedKeyframeLength[qi];
                        srcArr = stateJ.SerializedKeyframe[qi];
                        if (srcLen == 0 || srcArr == null)
                        {
                            MarkQualityUsed(ref stateJ.UsedQualities, qi);
                            continue;
                        }
                        avatarChannel = stateJ.SmallId
                            ? BasisNetworkCommons.GetPlayerAvatarChannelForQuality(qi, stateJ.SerializedHasAdditional[qi])
                            : BasisNetworkCommons.GetPlayerAvatarLargeChannelForQuality(qi, stateJ.SerializedHasAdditional[qi]);
                        // keyframe frame layout: [playerId:1|2][interval:1]...
                        intervalOffset = (byte)(stateJ.SmallId ? 1 : 2);
                        // Receiver now holds this keyframe generation + quality; subsequent deltas apply.
                        tracking[jId].BaselineKeyframeGen = stateJ.KeyframeGen;
                        tracking[jId].BaselineQuality = (byte)qi;
                    }

                    // Defer the send. Cheaper per-pair than SendUnreliableRawMerge:
                    // a single struct write vs pool-rent + BlockCopy + enqueue.
                    if (pendingCount == pending.Length)
                    {
                        Array.Resize(ref pending, pending.Length * 2);
                        stateI.PendingSends = pending;
                    }
                    ref PendingAvatarSend p = ref pending[pendingCount++];
                    p.Source = srcArr;
                    p.Length = srcLen;
                    p.Channel = avatarChannel;
                    p.Interval = startAtZeroInterval;
                    p.IntervalOffset = intervalOffset;

                    MarkQualityUsed(ref stateJ.UsedQualities, qi);

                    tracking[jId].LastSentTime = nowTicks;
                    tracking[jId].LastSeenGeneration = senderGen;

                    localSends++;
                }

                stateI.PendingCount = pendingCount;
                if (pendingCount > 0)
                {
                    FlushPendingForReceiver(stateI, peer, bundlingEnabled);
                }

                // One Interlocked.Add per receiver (not per send) — ~25 atomics/tick instead of ~32K
                if (localSends > 0 && BSRProfiler.Enabled)
                {
                    Interlocked.Add(ref BSRProfiler.SendCount, localSends);
                }
            });
        }

        /// <summary>
        /// Flushes the per-receiver PendingSends buffer to the wire. When bundling is
        /// enabled and the receiver has at least <see cref="AvatarBundleMinMessages"/>
        /// messages queued, packs them greedily into one or more MTU-sized deflated
        /// bundles on <see cref="BasisNetworkCommons.CompressedAvatarBundleChannel"/>.
        /// Any tail too small to bundle (or pathological pairs that won't compress)
        /// gets replayed as individual unreliable sends on the original quality channel.
        /// </summary>
        /// <summary>
        /// Fixed-size per-channel byte/count accumulator for one receiver's flush. The avatar tail
        /// only ever touches a handful of distinct channels (the quality ladder plus its
        /// additional-data and large-id variants), so a short linear scan beats both a 256-entry
        /// array and a dictionary — and, unlike a stackalloc, costs nothing to set up.
        /// </summary>
        private struct TailStats
        {
            private const int Capacity = 8;

            private byte _c0, _c1, _c2, _c3, _c4, _c5, _c6, _c7;
            private long _n0, _n1, _n2, _n3, _n4, _n5, _n6, _n7;
            private long _b0, _b1, _b2, _b3, _b4, _b5, _b6, _b7;
            private int _used;

            public void Add(byte channel, int length)
            {
                for (int i = 0; i < _used; i++)
                {
                    if (ChannelAt(i) == channel)
                    {
                        AddAt(i, length);
                        return;
                    }
                }

                if (_used < Capacity)
                {
                    SetChannelAt(_used, channel);
                    _used++;
                    AddAt(_used - 1, length);
                    return;
                }

                // More distinct channels in one flush than expected: record directly rather than
                // dropping the sample. Correct, just not batched.
                BasisNetworkStatistics.RecordOutboundBatch(channel, 1, length);
            }

            public void Flush()
            {
                for (int i = 0; i < _used; i++)
                {
                    BasisNetworkStatistics.RecordOutboundBatch(ChannelAt(i), CountAt(i), BytesAt(i));
                }
                _used = 0;
            }

            private byte ChannelAt(int i) => i switch
            {
                0 => _c0, 1 => _c1, 2 => _c2, 3 => _c3, 4 => _c4, 5 => _c5, 6 => _c6, _ => _c7,
            };

            private void SetChannelAt(int i, byte v)
            {
                switch (i)
                {
                    case 0: _c0 = v; _n0 = 0; _b0 = 0; break;
                    case 1: _c1 = v; _n1 = 0; _b1 = 0; break;
                    case 2: _c2 = v; _n2 = 0; _b2 = 0; break;
                    case 3: _c3 = v; _n3 = 0; _b3 = 0; break;
                    case 4: _c4 = v; _n4 = 0; _b4 = 0; break;
                    case 5: _c5 = v; _n5 = 0; _b5 = 0; break;
                    case 6: _c6 = v; _n6 = 0; _b6 = 0; break;
                    default: _c7 = v; _n7 = 0; _b7 = 0; break;
                }
            }

            private void AddAt(int i, int length)
            {
                switch (i)
                {
                    case 0: _n0++; _b0 += length; break;
                    case 1: _n1++; _b1 += length; break;
                    case 2: _n2++; _b2 += length; break;
                    case 3: _n3++; _b3 += length; break;
                    case 4: _n4++; _b4 += length; break;
                    case 5: _n5++; _b5 += length; break;
                    case 6: _n6++; _b6 += length; break;
                    default: _n7++; _b7 += length; break;
                }
            }

            private long CountAt(int i) => i switch
            {
                0 => _n0, 1 => _n1, 2 => _n2, 3 => _n3, 4 => _n4, 5 => _n5, 6 => _n6, _ => _n7,
            };

            private long BytesAt(int i) => i switch
            {
                0 => _b0, 1 => _b1, 2 => _b2, 3 => _b3, 4 => _b4, 5 => _b5, 6 => _b6, _ => _b7,
            };
        }

        private static void FlushPendingForReceiver(PlayerState stateI, NetPeer peer, bool bundlingEnabled)
        {
            int count = stateI.PendingCount;
            if (count <= 0) return;
            var pending = stateI.PendingSends;

            // Per-receiver-tick stats accumulators: fold per-send Interlocked into one
            // RecordOutboundBatch per channel at flush.
            //
            // This used to be two `stackalloc long[256]`. Stack allocation is not free — with
            // localsinit on (no [SkipLocalsInit] anywhere in this tree) both spans are zeroed on
            // every call, so at 1000 receivers x 250Hz that was ~1 GB/s of memset to track the
            // handful of channels the avatar path actually uses, followed by a 256-slot scan to
            // drain them. A small linear-probe accumulator covers the real channel count with no
            // zeroing and no scan; it falls back to direct recording if a run somehow uses more.
            TailStats tail = default;
            long bundleCount = 0;
            long bundleBytes = 0;

            // Bundling is only worth its CPU if the payload actually compresses. When a receiver's
            // observed ratio says otherwise we stop deflating for it and re-probe occasionally,
            // rather than paying full deflate cost every tick for a few percent.
            bool bundleThisFlush = bundlingEnabled && count >= AvatarBundleMinMessages;
            if (bundleThisFlush && stateI.BundleSkipCountdown > 0)
            {
                stateI.BundleSkipCountdown--;
                bundleThisFlush = false;
            }

            int cursor = 0;
            if (bundleThisFlush)
            {
                cursor = EmitGreedyBundles(stateI, peer, pending, count, ref bundleCount, ref bundleBytes);

                // LastBundleRatio is an EMA over what we just emitted, so this reflects real
                // observed behaviour for this receiver rather than a one-off bad chunk.
                if (cursor > 0 && stateI.LastBundleRatio > AvatarBundleMaxRatio)
                {
                    stateI.BundleSkipCountdown = AvatarBundleReprobeFlushes;
                }
            }

            // Send anything not packed into a bundle (the tail < min, or all of pending
            // when bundling is disabled / pathological no-fit). Equivalent to the
            // pre-bundling path; LiteNetLib's merge buffer still packs these into UDP packets.
            int tailSent = 0;
            for (int i = cursor; i < count; i++)
            {
                ref PendingAvatarSend p = ref pending[i];
                if (p.Length <= p.IntervalOffset) continue;
                peer.SendUnreliableRawMerge(p.Source, 0, p.Length, p.Channel, p.IntervalOffset, p.Interval);
                tail.Add(p.Channel, p.Length);
                tailSent++;
            }

            // Flush accumulated stats in one Interlocked.Add per (channel, metric).
            if (BasisNetworkStatistics.IsRecordingData)
            {
                if (bundleCount > 0)
                {
                    BasisNetworkStatistics.RecordOutboundBatch(BasisNetworkCommons.CompressedAvatarBundleChannel, bundleCount, bundleBytes);
                }
                if (tailSent > 0)
                {
                    tail.Flush();
                }
            }
            // Profiler attribution: distinguish "tail of bundled receiver" (cursor > 0) from
            // "fallback because bundling produced nothing" (cursor == 0 with bundling enabled).
            if (BSRProfiler.Enabled && tailSent > 0)
            {
                Interlocked.Add(ref BSRProfiler.bundleTailUncompressed, tailSent);
                if (bundlingEnabled && cursor == 0 && count >= AvatarBundleMinMessages)
                {
                    Interlocked.Increment(ref BSRProfiler.bundleFallbacks);
                }
            }
            // Clear the payload references, not just the count. PendingSends grows to the player
            // count and is never shrunk, so leaving the Source pointers behind keeps ~1M dead
            // references reachable at 1000 players — every GC has to trace them, and they pin the
            // serialized payloads of players who may already have disconnected.
            for (int i = 0; i < count; i++)
            {
                pending[i].Source = null;
            }
            stateI.PendingCount = 0;

            // Return tick-scoped scratch buffers to the pool. Without this they'd retain
            // ~85KB+ per PlayerState forever (LOH at 1k+ players, gen2 pause amplifier).
            if (stateI.BundleRawScratch != null)
            {
                ArrayPool<byte>.Shared.Return(stateI.BundleRawScratch);
                stateI.BundleRawScratch = null;
            }
            if (stateI.BundleCompressedScratch != null)
            {
                ArrayPool<byte>.Shared.Return(stateI.BundleCompressedScratch);
                stateI.BundleCompressedScratch = null;
            }
        }

        /// <summary>
        /// Greedily packs as many pending messages as fit into MTU-sized compressed
        /// bundles, emitting each on <see cref="BasisNetworkCommons.CompressedAvatarBundleChannel"/>.
        /// Uses a per-receiver EMA of the compressed/raw ratio so the first deflate
        /// attempt usually succeeds; on overshoot we shrink using the actual observed
        /// ratio and retry once. Returns the index of the first not-yet-emitted entry —
        /// callers send the [cursor, count) tail uncompressed.
        /// </summary>
        private static int EmitGreedyBundles(PlayerState stateI, NetPeer peer, PendingAvatarSend[] pending, int count, ref long bundleCount, ref long bundleBytes)
        {
            int budget = peer.Mtu - BundleMtuHeadroom - BundleHeaderSize;
            if (budget <= 0) return 0;

            // Initial ratio guess. Measured at 1000 players this sits around 0.87, not the 0.6 this
            // used to assume — quantized bone rotations are close to incompressible. Guessing too
            // optimistic makes the very first chunk overshoot MTU and burn a whole extra deflate on
            // the retry path, which was costing ~7-8% of all bundles.
            // Stays in [0.05, 0.95] so prediction never picks zero or full-budget chunks.
            float ratio = stateI.LastBundleRatio;
            if (ratio < 0.05f || ratio > 0.95f) ratio = InitialBundleRatioGuess;

            int cursor = 0;
            // AvatarBundleMinMessages gates *starting* to bundle (caller already checked it for
            // the first chunk). Inside the loop, individual chunks are sized by what fits in MTU;
            // a chunk of only 1-2 large messages is still worthwhile if rawLen ≥ AvatarBundleMinBytes
            // so the deflate header pays back. The outer condition just keeps the receiver tail of
            // < min uncompressed (since uncompressed sends merge fine for tiny remainders).
            while (count - cursor >= AvatarBundleMinMessages)
            {
                // Predict raw chunk size that would compress to ~budget * 0.95 (small safety
                // margin so we don't waste a retry on near-MTU overshoots). Then walk pending
                // accumulating sizes until we hit that target or run out of messages.
                int targetRaw = (int)((budget * 0.95f) / ratio);
                int chunkEnd = PickChunkEnd(pending, cursor, count, targetRaw);
                if (chunkEnd <= cursor) break;

                int rawLen = BuildRawForRange(stateI, pending, cursor, chunkEnd);
                if (rawLen < AvatarBundleMinBytes) break;

                if (TryDeflateAndEmit(stateI, peer, cursor, chunkEnd, rawLen, budget, ref bundleCount, ref bundleBytes, out int compressedLen))
                {
                    UpdateRatioEMA(ref stateI.LastBundleRatio, compressedLen, rawLen, weightOnObserved: 0.3f);
                    cursor = chunkEnd;
                    ratio = stateI.LastBundleRatio;
                    continue;
                }

                // Overshoot — recompute target using the actual ratio we just observed and
                // retry with a smaller chunk. Heavier weight on the observed value: this
                // receiver's payload likely just compresses worse than predicted.
                UpdateRatioEMA(ref stateI.LastBundleRatio, compressedLen, rawLen, weightOnObserved: 0.7f);
                float observed = (float)compressedLen / rawLen;
                if (observed < 0.05f) observed = 0.05f;
                if (observed > 0.99f) observed = 0.99f;

                int retryTargetRaw = (int)((budget * 0.92f) / observed);
                int retryEnd = PickChunkEnd(pending, cursor, chunkEnd, retryTargetRaw);
                if (retryEnd >= chunkEnd) retryEnd = cursor + Math.Max(1, (chunkEnd - cursor) * 3 / 4);
                if (retryEnd <= cursor) break;

                int retryRawLen = BuildRawForRange(stateI, pending, cursor, retryEnd);
                if (retryRawLen < AvatarBundleMinBytes) break;

                if (BSRProfiler.Enabled) Interlocked.Increment(ref BSRProfiler.bundleRetries);
                if (!TryDeflateAndEmit(stateI, peer, cursor, retryEnd, retryRawLen, budget, ref bundleCount, ref bundleBytes, out int retryCompressed))
                {
                    // Two failures in a row — give up on bundling for this receiver this tick;
                    // caller replays cursor..count uncompressed.
                    break;
                }

                UpdateRatioEMA(ref stateI.LastBundleRatio, retryCompressed, retryRawLen, weightOnObserved: 0.5f);
                cursor = retryEnd;
                ratio = stateI.LastBundleRatio;
            }
            return cursor;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int PickChunkEnd(PendingAvatarSend[] pending, int cursor, int hardEnd, int targetRaw)
        {
            int chunkEnd = cursor;
            int rawAccum = 0;
            while (chunkEnd < hardEnd)
            {
                int entrySize = 3 + pending[chunkEnd].Length; // [chan:1][len:2][bytes]
                // Always include at least one entry so the chunk grows; only break once
                // adding the next would exceed the predicted budget.
                if (chunkEnd > cursor && rawAccum + entrySize > targetRaw) break;
                rawAccum += entrySize;
                chunkEnd++;
            }
            return chunkEnd;
        }

        /// <summary>
        /// Writes <c>[origChannel:1][len:2-LE][bytes (interval-patched)]</c> for each
        /// pending entry in <c>[start, end)</c> into <c>stateI.BundleRawScratch</c>
        /// (grown on demand) and returns the total bytes written.
        /// </summary>
        private static int BuildRawForRange(PlayerState stateI, PendingAvatarSend[] pending, int start, int end)
        {
            int upperBound = 0;
            for (int i = start; i < end; i++) upperBound += 3 + pending[i].Length;

            byte[] raw = stateI.BundleRawScratch;
            if (raw == null || raw.Length < upperBound)
            {
                if (raw != null) ArrayPool<byte>.Shared.Return(raw);
                raw = ArrayPool<byte>.Shared.Rent(Math.Max(upperBound, 4096));
                stateI.BundleRawScratch = raw;
            }

            int rawPos = 0;
            for (int i = start; i < end; i++)
            {
                ref PendingAvatarSend p = ref pending[i];
                int len = p.Length;
                if (len <= p.IntervalOffset) continue;

                raw[rawPos++] = p.Channel;
                BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(rawPos, 2), (ushort)len);
                rawPos += 2;
                Buffer.BlockCopy(p.Source, 0, raw, rawPos, len);
                // Patch the per-receiver interval byte in our copy (source is shared).
                raw[rawPos + p.IntervalOffset] = p.Interval;
                rawPos += len;
            }
            return rawPos;
        }

        /// <summary>
        /// LZ4-compresses <c>stateI.BundleRawScratch[0..rawLen]</c> into the payload region of
        /// <c>stateI.BundleCompressedScratch</c> (after the reserved bundle-header prefix),
        /// emits one UDP datagram on CompressedAvatarBundleChannel if it fits the peer-MTU
        /// budget, and reports the compressed payload length. On overshoot returns false
        /// (caller retries with a smaller chunk). LZ4Codec.Encode is a single static call
        /// with no allocations and no per-call setup — at high call rates this is ~10× cheaper
        /// than DeflateStream, which allocates an internal window + hashtable on every Write.
        /// </summary>
        private static bool TryDeflateAndEmit(PlayerState stateI, NetPeer peer, int chunkStart, int chunkEnd, int rawLen, int budget, ref long bundleCount, ref long bundleBytes, out int compressedLen)
        {
            compressedLen = 0;
            byte[] raw = stateI.BundleRawScratch;
            byte[] compressed = stateI.BundleCompressedScratch;
            // LZ4 worst case is rawLen + (rawLen / 255) + 16 (returned by MaximumOutputSize).
            int compCapacityNeeded = BundleHeaderSize + LZ4Codec.MaximumOutputSize(rawLen);
            if (compressed == null || compressed.Length < compCapacityNeeded)
            {
                if (compressed != null) ArrayPool<byte>.Shared.Return(compressed);
                compressed = ArrayPool<byte>.Shared.Rent(Math.Max(compCapacityNeeded, 4096));
                stateI.BundleCompressedScratch = compressed;
            }

            bool profiling = BSRProfiler.Enabled;
            long deflateStart = profiling ? Stopwatch.GetTimestamp() : 0;

            // Encode directly into the wire packet's payload region. Returns -1 if the
            // destination span isn't large enough — shouldn't happen given the sizing above,
            // but if it does we treat it as an overshoot and let the caller retry smaller.
            compressedLen = LZ4Codec.Encode(
                raw.AsSpan(0, rawLen),
                compressed.AsSpan(BundleHeaderSize, compressed.Length - BundleHeaderSize),
                LZ4Level.L00_FAST);

            if (profiling) Interlocked.Add(ref BSRProfiler.bundleDeflateTicks, Stopwatch.GetTimestamp() - deflateStart);

            if (compressedLen <= 0 || compressedLen > budget)
            {
                return false;
            }

            int wireLen = BundleHeaderSize + compressedLen;
            int chunkCount = chunkEnd - chunkStart;
            compressed[0] = (byte)Math.Min(chunkCount, 255);
            BinaryPrimitives.WriteUInt16LittleEndian(compressed.AsSpan(1, 2), (ushort)Math.Min(rawLen, ushort.MaxValue));

            peer.SendUnreliableRawMerge(compressed, 0, wireLen, BasisNetworkCommons.CompressedAvatarBundleChannel);
            bundleCount++;
            bundleBytes += wireLen;

            if (profiling)
            {
                Interlocked.Increment(ref BSRProfiler.bundlesEmitted);
                Interlocked.Add(ref BSRProfiler.bundleMessages, chunkCount);
                Interlocked.Add(ref BSRProfiler.bundleRawBytes, rawLen);
                Interlocked.Add(ref BSRProfiler.bundleCompressedBytes, compressedLen);
            }
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void UpdateRatioEMA(ref float ema, int compressed, int raw, float weightOnObserved)
        {
            if (raw <= 0) return;
            float observed = (float)compressed / raw;
            if (observed < 0.05f) observed = 0.05f;
            if (observed > 0.99f) observed = 0.99f;
            float prev = ema;
            if (prev < 0.05f || prev > 0.95f) prev = observed; // unseeded → adopt
            ema = prev * (1f - weightOnObserved) + observed * weightOnObserved;
        }

        /// <summary>
        /// Atomically sets a quality bit in the UsedQualities bitmask.
        /// Called from parallel send loop threads lock-free via CAS.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void MarkQualityUsed(ref int usedQualities, int qi)
        {
            int bit = 1 << qi;
            // Bits are sticky (only set, never cleared in the send loop), so a plain read
            // showing "set" is always correct. Avoids the Volatile.Read barrier in the
            // common case after the first few ticks when all 4 bits converge.
            if ((usedQualities & bit) != 0) return;

            int cur = Volatile.Read(ref usedQualities);
            while (true)
            {
                if ((cur & bit) != 0) return;
                int updated = cur | bit;
                int was = Interlocked.CompareExchange(ref usedQualities, updated, cur);
                if (was == cur) return;
                cur = was;
            }
        }

        /// <summary>
        /// Maps squared distance to quality index (matches BitQuality enum values).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GetQualityIndex(float distSq)
        {
            if (distSq <= HighDistanceSq) return 3;   // High
            if (distSq <= MediumDistanceSq) return 2;  // Medium
            if (distSq <= LowDistanceSq) return 1;     // Low
            return 0;                                   // VeryLow
        }

        /// <summary>
        /// Picks the avatar snapshot a joining viewer should be given for one already-present subject,
        /// using the same distance thresholds the steady-state send loop uses.
        ///
        /// The join fill used to hand every joiner a High payload for every player in the instance,
        /// however far away they were — so a 2000-player join shipped 2000 full-quality poses when the
        /// very next reduction tick would have dropped almost all of them to VeryLow. The per-quality
        /// payloads are already built and cached on PlayerState by the tick loop, so choosing between
        /// them here costs nothing extra.
        ///
        /// Falls back to High whenever the decision cannot be made safely: no state for either player,
        /// the subject is flagged for full-quality broadcast, or the repacked tier is not built yet
        /// (BuildAllLowerFromHighInto nulls the lower arrays when a repack fails).
        /// </summary>
        /// <param name="viewerPosition">
        /// The joining player's world position, decoded from the ready message it just sent. It is
        /// passed in rather than looked up because AddMessage only QUEUES the join pose — PlayerState
        /// (and its Position) is not created until the ~250Hz tick drains that queue, so a lookup here
        /// would race the tick and fall back to High for some joiners and not others.
        /// </param>
        public static bool TryGetJoinSnapshot(Basis.Scripts.Networking.Compression.Vector3 viewerPosition, int subjectId, out LocalAvatarSyncMessage snapshot)
        {
            snapshot = default;

            if (!playerStates.TryGetValue(subjectId, out PlayerState subject) || subject == null)
            {
                return false;
            }

            LocalAvatarSyncMessage high = subject.SyncMessage.avatarSerialization;
            if (high.array == null)
            {
                return false;
            }

            if (subject.BypassReduction)
            {
                snapshot = high;
                return true;
            }

            float dx = viewerPosition.x - subject.Position.x;
            float dy = viewerPosition.y - subject.Position.y;
            float dz = viewerPosition.z - subject.Position.z;
            float distSq = dx * dx + dy * dy + dz * dz;

            LocalAvatarSyncMessage tier = GetQualityIndex(distSq) switch
            {
                3 => high,
                2 => subject.AvatarMedium,
                1 => subject.AvatarLow,
                _ => subject.AvatarVeryLow,
            };

            snapshot = tier.array != null ? tier : high;
            return true;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CalculateIntervalFromDistanceSq(float distanceSq, out byte offsetByte, out int actualInterval)
        {
            int rawInterval = (int)(BSRSMillisecondDefaultInterval * (BSRBaseMultiplier + (distanceSq * BSRSIncreaseRate)));

            offsetByte = BasisNetworkCommons.EncodeAvatarIntervalByte(rawInterval, BSRSMillisecondDefaultInterval);
            actualInterval = BasisNetworkCommons.DecodeAvatarIntervalMs(offsetByte, BSRSMillisecondDefaultInterval);
        }

        public static void Shutdown()
        {
            cts.Cancel();
            _tickWake.Set();
        }

        public static void SetBypassReduction(int id, bool enable)
        {
            if (enable) _bypassReductionIds[id] = true;
            else _bypassReductionIds.TryRemove(id, out _);

            if (playerStates.TryGetValue(id, out var state))
                state.BypassReduction = enable;
        }

        public static void RemovePlayer(int id)
        {
            playersToRemove.Enqueue(id);
        }

        /// <summary>
        /// Propagates AdditionalAvatarData from the high quality message to lower quality variants.
        /// BuildAllLowerFromHighInto only handles the muscle/position/rotation payload;
        /// additional data (blendshapes, custom avatar behaviours) must be propagated separately.
        /// When StripAdditionalDataAtLowQuality is on, Low and VeryLow drop it entirely —
        /// face/detail data is invisible past the Medium distance, and the reliable AvatarChannel
        /// path still carries low-frequency behaviour state to everyone.
        /// </summary>
        internal static void PropagateAdditionalData(
            in LocalAvatarSyncMessage high,
            ref LocalAvatarSyncMessage medium,
            ref LocalAvatarSyncMessage low,
            ref LocalAvatarSyncMessage veryLow)
        {
            medium.AdditionalAvatarDatas = high.AdditionalAvatarDatas;
            medium.AdditionalAvatarDataSize = high.AdditionalAvatarDataSize;
            medium.LinkedAvatarIndex = high.LinkedAvatarIndex;

            bool strip = StripAdditionalDataAtLowQuality;
            low.AdditionalAvatarDatas = strip ? null : high.AdditionalAvatarDatas;
            low.AdditionalAvatarDataSize = strip ? (byte)0 : high.AdditionalAvatarDataSize;
            low.LinkedAvatarIndex = high.LinkedAvatarIndex;

            veryLow.AdditionalAvatarDatas = strip ? null : high.AdditionalAvatarDatas;
            veryLow.AdditionalAvatarDataSize = strip ? (byte)0 : high.AdditionalAvatarDataSize;
            veryLow.LinkedAvatarIndex = high.LinkedAvatarIndex;
        }
        /// <summary>
        /// Carries the position from the high-quality source into all lower quality arrays.
        /// High stores float32 (12B); lower tiers store int24 millimetres (9B), so the first
        /// non-null lower array transcodes and the rest copy its 9 bytes.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CopyPositionToLowerQualities(
            byte[] highArray,
            ref LocalAvatarSyncMessage medium,
            ref LocalAvatarSyncMessage low,
            ref LocalAvatarSyncMessage veryLow)
        {
            int lowerPos = BasisAvatarBitPacking.WritePositionQuantized;
            byte[] first = null;
            if (medium.array != null)
            {
                BasisAvatarBitPacking.TranscodePositionToQuantized(highArray, medium.array);
                first = medium.array;
            }
            if (low.array != null)
            {
                if (first != null) Buffer.BlockCopy(first, 0, low.array, 0, lowerPos);
                else { BasisAvatarBitPacking.TranscodePositionToQuantized(highArray, low.array); first = low.array; }
            }
            if (veryLow.array != null)
            {
                if (first != null) Buffer.BlockCopy(first, 0, veryLow.array, 0, lowerPos);
                else BasisAvatarBitPacking.TranscodePositionToQuantized(highArray, veryLow.array);
            }
        }

        private static void ProcessMessage(QueuedMessage message)
        {
            if (message.FromPeer == null)
            {
                QueuedMessagePool.Return(message);
                return;
            }

            int id = message.FromPeer.Id;
            byte inboundSeq = message.Sequence;

            var poolMsg = message.AvatarMessage;

            if (poolMsg.array == null)
            {
                BNL.LogError($"[ProcessMessage] Avatar array is null for peer {id}");
                QueuedMessagePool.Return(message);
                return;
            }

            if (BasisNetworkServer.Security.BasisGlobalLockManager.AdditionalAvatarDataLock)
            {
                poolMsg.AdditionalAvatarDatas = null;
                poolMsg.AdditionalAvatarDataSize = 0;
            }

            var incomingQuality = (BitQuality)poolMsg.DataQualityLevel;
            bool isHighQuality = incomingQuality == BitQuality.High;

            if (!BasisAvatarBitPacking.IsValidQuality(incomingQuality))
            {
                QueuedMessagePool.Return(message);
                return;
            }

            int expectedPayloadSize = BasisAvatarBitPacking.ConvertToSize(incomingQuality);
            if (poolMsg.array.Length < expectedPayloadSize)
            {
                QueuedMessagePool.Return(message);
                return;
            }

            var pos = BasisNetworkCompressionExtensions.ReadPosition(ref poolMsg.array);

            // Deep-copy the avatar payload so state.AvatarHigh owns its own buffer.
            // Without this copy, QueuedMessagePool.Return() preserves the byte[] and
            // re-rents it for other peers — silently overwriting state.AvatarHigh.array.
            // Uses ArrayPool to avoid per-message heap allocation (~208 bytes * 11K/sec).
            byte[] rentedArray = ArrayPool<byte>.Shared.Rent(expectedPayloadSize);
            Buffer.BlockCopy(poolMsg.array, 0, rentedArray, 0, expectedPayloadSize);
            var high = new LocalAvatarSyncMessage
            {
                DataQualityLevel = poolMsg.DataQualityLevel,
                AdditionalAvatarDatas = poolMsg.AdditionalAvatarDatas,
                AdditionalAvatarDataSize = poolMsg.AdditionalAvatarDataSize,
                LinkedAvatarIndex = poolMsg.LinkedAvatarIndex,
                array = rentedArray,
            };

            if (!playerStates.TryGetValue(id, out var state))
            {
                state = new PlayerState
                {
                    Peer = message.FromPeer,
                    IsActive = true,
                    Position = pos,
                    SyncMessage = new ServerSideSyncPlayerMessage
                    {
                        playerIdMessage = new PlayerIdMessage { playerID = (ushort)id },
                        avatarSerialization = high
                    },
                    AvatarHigh = high,
                    HighArrayActualSize = expectedPayloadSize,
                    PeerTracking = new PeerTrackingData[InitialPlayerArrayCapacity],
                    // Stagger the periodic keyframe phase per player. A keyframe tick serialises all
                    // four qualities instead of just the used ones (~4x the work) and puts a much
                    // larger payload on the wire. Seeding every player from their first frame makes a
                    // mass join produce a synchronized herd: a large slice of the instance emits
                    // keyframes on the same tick, every interval, forever. Offsetting the phase by a
                    // hash of the player id spreads that cost flat, without changing the per-player
                    // cadence. Hashed rather than random so it is deterministic and allocation-free
                    // (Random.Shared does not exist on the netstandard2.1 target this also builds for).
                    LastKeyframeTimeTicks = Stopwatch.GetTimestamp() - (long)(KeyframePhaseFraction(id)
                        * AvatarDeltaKeyframeIntervalMs * MsToTick),
                    DataGeneration = 1,
                    LastInboundSequence = inboundSeq,
                    HasReceivedFirst = true,
                    OutboundSequence = 0,
                    SmallId = id <= byte.MaxValue,
                    BypassReduction = _bypassReductionIds.ContainsKey(id),
                };

                if (isHighQuality)
                {
                    try
                    {
                        AvatarQualityRepacker.BuildAllLowerFromHighInto(high, ref state.AvatarMedium, ref state.AvatarLow, ref state.AvatarVeryLow);
                    }
                    catch (Exception ex)
                    {
                        BNL.LogError($"[ProcessMessage] Repack failed: {ex}");
                        // Don't alias high into lower slots — that sends High-packed muscle
                        // data on lower-quality channels, causing bit-width mismatches.
                        // Null the arrays so PreSerializeAll skips them; the repacker's
                        // EnsureBuffer and the position-only fast path both handle null safely.
                        state.AvatarMedium.array = null;
                        state.AvatarLow.array = null;
                        state.AvatarVeryLow.array = null;
                    }
                }
                else
                {
                    // Non-High quality: can't repack downward. Null lower slots to
                    // avoid sending mismatched quality data on wrong channels.
                    state.AvatarMedium.array = null;
                    state.AvatarLow.array = null;
                    state.AvatarVeryLow.array = null;
                }

                // Propagate additional avatar data (e.g. blendshapes) to quality variants.
                // BuildAllLowerFromHighInto only handles muscle/position payload;
                // additional data must be copied separately.
                PropagateAdditionalData(high, ref state.AvatarMedium, ref state.AvatarLow, ref state.AvatarVeryLow);
                state.HasAdditionalData = high.AdditionalAvatarDatas != null && high.AdditionalAvatarDatas.Length > 0;

                // First frame: always a keyframe (generation 1).
                PreSerializeFrame(state, 1, forceKeyframe: true);

                playerStates[id] = state;

                // Add to active players list
                lock (_activePlayersLock)
                {
                    _activePlayers.Add((id, state));
                    _activePlayersDirty = true;
                    Interlocked.Increment(ref _activePlayerCount);
                }
            }
            else
            {
                // Peer-slot reuse: LiteNetLib recycles NetPeer ids after disconnect.
                // If the incoming peer is a different instance, the stored Peer is the
                // old disconnected one — sends to it silently no-op, so the new player
                // would never receive avatar data. Refresh the Peer ref and treat the
                // next frame as the first frame so the sequence-delta check doesn't
                // drop it against the previous player's last sequence.
                if (!ReferenceEquals(state.Peer, message.FromPeer))
                {
                    state.Peer = message.FromPeer;
                    state.HasReceivedFirst = false;
                    state.SmallId = id <= byte.MaxValue;
                    state.BypassReduction = _bypassReductionIds.ContainsKey(id);
                }

                // Drop stale inbound packets (unreliable can deliver out of order)
                if (state.HasReceivedFirst)
                {
                    byte delta = unchecked((byte)(inboundSeq - state.LastInboundSequence));
                    if (delta == 0 || delta >= 128)
                    {
                        // Duplicate or stale — discard. Return the just-rented array.
                        ArrayPool<byte>.Shared.Return(rentedArray);
                        QueuedMessagePool.Return(message);
                        return;
                    }
                }
                state.LastInboundSequence = inboundSeq;
                state.HasReceivedFirst = true;

                if (!state.IsActive)
                {
                    state.IsActive = true;
                }

                state.Position = pos;

                // Increment outbound sequence for this sender's new update
                unchecked { state.OutboundSequence++; }

                byte[] prevArray = state.AvatarHigh.array;
                int prevActualSize = state.HighArrayActualSize;
                state.AvatarHigh = high;
                state.HighArrayActualSize = expectedPayloadSize;

                if (isHighQuality)
                {
                    // Check if muscles+tail changed (skip expensive bit repacking if only position moved).
                    // Uses HighArrayActualSize instead of .Length since ArrayPool may return larger arrays.
                    int muscleAndTailBytes = HighMuscleAndTailBytes;
                    bool musclesOrTailChanged = prevArray == null
                        || ReferenceEquals(prevArray, high.array)
                        || prevActualSize != expectedPayloadSize
                        || !high.array.AsSpan(WritePosition, muscleAndTailBytes)
                            .SequenceEqual(prevArray.AsSpan(WritePosition, muscleAndTailBytes));

                    // Force a full repack when any lower quality array is null (e.g. after a
                    // previous repack failure).  Without this, the position-only fast path would
                    // skip the null arrays indefinitely and far receivers would never see the player.
                    bool needsRecovery = state.AvatarMedium.array == null
                        || state.AvatarLow.array == null
                        || state.AvatarVeryLow.array == null;

                    if (musclesOrTailChanged || needsRecovery)
                    {
                        try
                        {
                            AvatarQualityRepacker.BuildAllLowerFromHighInto(high, ref state.AvatarMedium, ref state.AvatarLow, ref state.AvatarVeryLow);
                        }
                        catch (Exception ex)
                        {
                            BNL.LogError($"[ProcessMessage] Repack failed: {ex}");
                            state.AvatarMedium.array = null;
                            state.AvatarLow.array = null;
                            state.AvatarVeryLow.array = null;
                        }
                    }
                    else
                    {
                        // Position-only fast path: carry position to all lower qualities without
                        // re-packing bones (float32 in High, int24-mm in the lower tiers).
                        CopyPositionToLowerQualities(high.array, ref state.AvatarMedium, ref state.AvatarLow, ref state.AvatarVeryLow);
                    }
                }
                else
                {
                    // Non-High quality: can't repack downward safely.
                    state.AvatarMedium.array = null;
                    state.AvatarLow.array = null;
                    state.AvatarVeryLow.array = null;
                }

                // Propagate additional avatar data to quality variants
                PropagateAdditionalData(high, ref state.AvatarMedium, ref state.AvatarLow, ref state.AvatarVeryLow);
                state.HasAdditionalData = high.AdditionalAvatarDatas != null && high.AdditionalAvatarDatas.Length > 0;

                // Keep SyncMessage in sync (shell)
                state.SyncMessage.avatarSerialization = high;

                // publishGen = the DataGeneration value this frame becomes after the increment below.
                PreSerializeFrame(state, state.DataGeneration + 1, forceKeyframe: false);

                // Return the previous tick's array to the pool now that muscle comparison is done.
                if (prevArray != null)
                {
                    ArrayPool<byte>.Shared.Return(prevArray);
                }

                // Single atomic increment replaces O(N) CAS bit-setting across all other players.
                // Receivers detect new data by comparing this generation against their LastSeenGeneration.
                Interlocked.Increment(ref state.DataGeneration);
            }

            QueuedMessagePool.Return(message);
        }

        #region Pre-serialization

        /// <summary>
        /// Pre-serializes keyframe messages only for quality levels that have receivers.
        /// UsedQualities bits accumulate from the send loop (never reset) so that tick slicing
        /// doesn't cause quality oscillation — each slice contributes its needed bits and they
        /// persist across ticks. Converges to the correct set within a few ticks.
        /// Quality levels with no receivers get SerializedKeyframeLength set to 0 so the send loop
        /// skips them and marks them as needed for next tick (one-tick catch-up delay, ~4ms).
        /// First frame for a player serializes all 4 levels.
        /// </summary>
        private static void PreSerializeAll(PlayerState state)
        {
            ushort playerId = state.SyncMessage.playerIdMessage.playerID;

            // Read accumulated quality bits. Bits are sticky — set by MarkQualityUsed in the
            // send loop and never reset. With tick slicing (32 slices), each slice's receivers
            // contribute their needed quality bits over successive ticks. NOT resetting prevents
            // oscillation where each tick only has bits from 1/32 of receivers.
            int mask = Volatile.Read(ref state.UsedQualities);
            if (mask == 0) mask = 0xF; // new player or no sends yet: serialize all

            LocalAvatarSyncMessage msg;
            for (int qi = 0; qi < 4; qi++)
            {
                if ((mask & (1 << qi)) != 0)
                {
                    msg = qi switch
                    {
                        0 => state.AvatarVeryLow,
                        1 => state.AvatarLow,
                        2 => state.AvatarMedium,
                        _ => state.AvatarHigh,
                    };
                    PreSerializeKeyframe(state, qi, msg, playerId);
                    BSRProfiler.IncrementPreSerializations();
                }
                else
                {
                    // Mark as not available  send loop will skip and request it for next tick.
                    state.SerializedKeyframeLength[qi] = 0;
                    BSRProfiler.IncrementPreSerializationsSkipped();
                }
            }
        }

        internal static void PreSerializeKeyframe(PlayerState state, int qi, LocalAvatarSyncMessage msg, ushort playerId)
        {
            if (msg.array == null)
            {
                state.SerializedKeyframeLength[qi] = 0;
                return;
            }

            var quality = (BitQuality)msg.DataQualityLevel;

            // Guard: the message's quality must match the quality slot index.
            // AvatarHigh may contain non-High quality data if the client sent a
            // lower quality. Without this check, the payload would be sent on the
            // wrong channel, causing size mismatches on the receiver (e.g. "Need 169, have 99").
            if ((int)quality != qi)
            {
                state.SerializedKeyframeLength[qi] = 0;
                return;
            }

            int expectedPayload = BasisAvatarBitPacking.ConvertToSize(quality);

            // Skip if the array is undersized (e.g. client sent wrong quality level)
            if (msg.array.Length < expectedPayload)
            {
                BNL.LogError($"[PreSerializeKeyframe] Array undersized for quality {quality}: got {msg.array.Length}, need {expectedPayload}. Skipping.");
                state.SerializedKeyframeLength[qi] = 0;
                return;
            }

            // Byte-ID:   [PlayerID:1][interval:1][sequence:1][array:N][additional...]
            // Ushort-ID: [PlayerID:2][interval:1][sequence:1][array:N][additional...]
            // Quality and additional-data presence are derived from the channel number.
            bool hasAdditional = state.HasAdditionalData
                && msg.AdditionalAvatarDatas != null
                && msg.AdditionalAvatarDatas.Length > 0
                && msg.AdditionalAvatarDatas.Length <= 255;
            state.SerializedHasAdditional[qi] = hasAdditional;

            int additionalSize = 0;
            if (hasAdditional)
            {
                additionalSize = 1 + 1; // AdditionalSize + LinkedAvatarIndex
                for (int i = 0; i < msg.AdditionalAvatarDatas.Length; i++)
                {
                    additionalSize += 1 + 1 + (msg.AdditionalAvatarDatas[i].array?.Length ?? 0); // PayloadSize + messageIndex + data
                }
            }

            int idSize = state.SmallId ? 1 : 2;
            int totalSize = idSize + 1 + 1 + expectedPayload + additionalSize;

            if (state.SerializedKeyframe[qi] == null || state.SerializedKeyframe[qi].Length < totalSize)
            {
                state.SerializedKeyframe[qi] = new byte[totalSize];
            }

            // Write directly to SerializedKeyframe — avoids the intermediate NetDataWriter
            // buffer and the final BlockCopy (~40MB/sec saved at 200K+ pre-serializations/5s).
            byte[] dst = state.SerializedKeyframe[qi];
            int offset = 0;

            if (state.SmallId)
            {
                dst[offset++] = (byte)playerId;
            }
            else
            {
                dst[offset++] = (byte)(playerId & 0xFF);
                dst[offset++] = (byte)((playerId >> 8) & 0xFF);
            }

            dst[offset++] = 0; // interval placeholder (patched per-receiver in send loop)
            dst[offset++] = state.OutboundSequence;

            Buffer.BlockCopy(msg.array, 0, dst, offset, expectedPayload);
            offset += expectedPayload;

            if (hasAdditional)
            {
                dst[offset++] = (byte)msg.AdditionalAvatarDatas.Length;
                dst[offset++] = msg.LinkedAvatarIndex;
                for (int i = 0; i < msg.AdditionalAvatarDatas.Length; i++)
                {
                    var ad = msg.AdditionalAvatarDatas[i];
                    // Every entry writes the full [size:1][messageIndex:1] header (size 0 for
                    // null/oversized payloads) — a bare size byte would desync the entries after
                    // it. Must match AdditionalAvatarData.Serialize/Deserialize exactly.
                    if (ad.array == null || ad.array.Length > 255)
                    {
                        dst[offset++] = 0;
                        dst[offset++] = ad.messageIndex;
                    }
                    else
                    {
                        byte payloadSize = (byte)ad.array.Length;
                        dst[offset++] = payloadSize;
                        dst[offset++] = ad.messageIndex;
                        if (payloadSize > 0)
                        {
                            Buffer.BlockCopy(ad.array, 0, dst, offset, payloadSize);
                            offset += payloadSize;
                        }
                    }
                }
            }

            state.SerializedKeyframeLength[qi] = offset;
        }

        /// <summary>
        /// Delta-aware pre-serialization entry point. Decides keyframe vs delta for this generation
        /// (sender-global), then builds the corresponding wire buffers. <paramref name="publishGen"/> is
        /// the DataGeneration value this frame will be published as (stamped onto the keyframe gen so
        /// receivers can tell whether they hold the current baseline). When delta compression is
        /// disabled this degrades to the legacy all-keyframe path.
        /// </summary>
        private static void PreSerializeFrame(PlayerState state, long publishGen, bool forceKeyframe)
        {
            if (!EnableAvatarDeltaCompression)
            {
                PreSerializeAll(state);
                state.CurrentIsKeyframe = true;
                return;
            }

            long now = Stopwatch.GetTimestamp();
            long keyframeIntervalTicks = (long)(EffectiveKeyframeIntervalMs(state.KeyframeStretchShift) * MsToTick);

            bool isKeyframe = forceKeyframe
                || state.BypassReduction
                || state.KeyframePayload[3] == null
                || state.KeyframePayloadLength[3] == 0
                || (now - state.LastKeyframeTimeTicks) >= keyframeIntervalTicks;

            // Promotion: if the High delta isn't actually smaller than a High keyframe (fully-moving
            // avatar), just send a keyframe — never pay delta overhead to be larger than a keyframe.
            if (!isKeyframe)
            {
                LocalAvatarSyncMessage highMsg = state.AvatarHigh;
                int highPayload = BasisAvatarBitPacking.ConvertToSize(BitQuality.High);
                if (highMsg.array == null || (int)highMsg.DataQualityLevel != 3 || highMsg.array.Length < highPayload)
                {
                    isKeyframe = true;
                }
                else
                {
                    int probeCap = BasisAvatarDeltaCompression.MaxDeltaSize(BitQuality.High);
                    if (state.DeltaProbeScratch == null || state.DeltaProbeScratch.Length < probeCap)
                        state.DeltaProbeScratch = new byte[probeCap];
                    int dl = BasisAvatarDeltaCompression.BuildDelta(state.KeyframePayload[3], highMsg.array, BitQuality.High, state.DeltaProbeScratch, 0);
                    if (dl < 0 || dl >= highPayload)
                    {
                        isKeyframe = true;
                        state.KeyframeStretchShift = 0;
                        state.SmallDeltaStreak = 0;
                    }
                    else
                    {
                        UpdateKeyframeStretch(state, dl);
                    }
                }
            }

            ushort playerId = state.SyncMessage.playerIdMessage.playerID;

            if (isKeyframe)
            {
                state.KeyframeGen = publishGen;
                state.KeyframeSequence = state.OutboundSequence;
                state.LastKeyframeTimeTicks = now;
                state.CurrentIsKeyframe = true;

                // Serialize all four quality keyframes (that have valid arrays) and snapshot their
                // payloads as the delta baseline, so a receiver at ANY quality can rebaseline off the
                // last keyframe without waiting for the next keyframe interval.
                for (int qi = 0; qi < 4; qi++)
                {
                    LocalAvatarSyncMessage msg = QualityMsg(state, qi);
                    if (msg.array == null || (int)msg.DataQualityLevel != qi)
                    {
                        state.SerializedKeyframeLength[qi] = 0;
                        state.KeyframePayloadLength[qi] = 0;
                        state.SerializedDeltaLength[qi] = 0;
                        BSRProfiler.IncrementPreSerializationsSkipped();
                        continue;
                    }
                    int payload = BasisAvatarBitPacking.ConvertToSize((BitQuality)qi);
                    if (msg.array.Length < payload)
                    {
                        state.SerializedKeyframeLength[qi] = 0;
                        state.KeyframePayloadLength[qi] = 0;
                        state.SerializedDeltaLength[qi] = 0;
                        continue;
                    }
                    if (state.KeyframePayload[qi] == null || state.KeyframePayload[qi].Length < payload)
                        state.KeyframePayload[qi] = new byte[payload];
                    Buffer.BlockCopy(msg.array, 0, state.KeyframePayload[qi], 0, payload);
                    state.KeyframePayloadLength[qi] = payload;

                    PreSerializeKeyframe(state, qi, msg, playerId);
                    state.SerializedDeltaLength[qi] = 0; // no delta on a keyframe generation
                    BSRProfiler.IncrementPreSerializations();
                }
            }
            else
            {
                state.CurrentIsKeyframe = false;
                // Build deltas only for qualities that had receivers (sticky UsedQualities). Keyframe
                // buffers are left intact from the last keyframe tick so a lagging receiver can rebaseline.
                int mask = Volatile.Read(ref state.UsedQualities);
                if (mask == 0) mask = 0xF;
                for (int qi = 0; qi < 4; qi++)
                {
                    if ((mask & (1 << qi)) == 0) { state.SerializedDeltaLength[qi] = 0; continue; }
                    PreSerializeDelta(state, qi, QualityMsg(state, qi), playerId);
                }
            }
        }

        /// <summary>
        /// Effective periodic keyframe interval for a given stretch shift: base doubled per step,
        /// clamped to AvatarDeltaKeyframeMaxIntervalMs. A max at or below the base disables stretching.
        /// </summary>
        internal static int EffectiveKeyframeIntervalMs(int stretchShift)
        {
            int baseMs = AvatarDeltaKeyframeIntervalMs;
            int maxMs = AvatarDeltaKeyframeMaxIntervalMs;
            if (maxMs <= baseMs || stretchShift <= 0) return baseMs;
            long stretched = (long)baseMs << (stretchShift > 8 ? 8 : stretchShift);
            return stretched >= maxMs ? maxMs : (int)stretched;
        }

        internal static void UpdateKeyframeStretch(PlayerState state, int highDeltaLength)
        {
            if (highDeltaLength > SmallHighDeltaBytes)
            {
                state.KeyframeStretchShift = 0;
                state.SmallDeltaStreak = 0;
                return;
            }
            if (EffectiveKeyframeIntervalMs(state.KeyframeStretchShift + 1) == EffectiveKeyframeIntervalMs(state.KeyframeStretchShift)) return;
            if (++state.SmallDeltaStreak >= SmallDeltaStreakToStretch)
            {
                state.SmallDeltaStreak = 0;
                state.KeyframeStretchShift++;
            }
        }

        /// <summary>
        /// Test seam (InternalsVisibleTo BasisServerTests): drives the private keyframe/delta
        /// pre-serialization decision exactly as ProcessMessage does, so the additional-data
        /// pipeline tests can cover the real fan-out path without a live NetPeer.
        /// </summary>
        internal static void TestOnly_PreSerializeFrame(PlayerState state, long publishGen, bool forceKeyframe)
            => PreSerializeFrame(state, publishGen, forceKeyframe);

        /// <summary>
        /// Test seam (InternalsVisibleTo BasisServerTests): builds the raw (pre-LZ4) bundle block
        /// for a pending range exactly as the bundle emitter does — the tests LZ4 it and decode it
        /// the way the client does, proving additional data survives the bundle path byte-exactly.
        /// The bytes land in <see cref="PlayerState.BundleRawScratch"/>; the return is the length.
        /// </summary>
        internal static int TestOnly_BuildRawForRange(PlayerState state, PendingAvatarSend[] pending, int start, int end)
            => BuildRawForRange(state, pending, start, end);

        /// <summary>
        /// Queues a receiver's request for a fresh keyframe from a sender (DeltaControlKeyframeRequest).
        /// The tick thread invalidates the pair's baseline so its next send is a keyframe.
        /// </summary>
        public static void RequestKeyframe(int senderId, int receiverId)
        {
            _pendingKeyframeRequests.Enqueue((senderId, receiverId));
            _tickWake.Set();
        }

        private static void ProcessPendingKeyframeRequests()
        {
            while (_pendingKeyframeRequests.TryDequeue(out var pair))
            {
                // PeerTracking lives on the RECEIVER, indexed by sender id.
                if (!playerStates.TryGetValue(pair.receiverId, out PlayerState receiver)) continue;
                var tracking = receiver.PeerTracking;
                if (tracking == null || pair.senderId < 0 || pair.senderId >= tracking.Length) continue;
                ref PeerTrackingData t = ref tracking[pair.senderId];
                t.BaselineKeyframeGen = -1;
                // Reopen the new-data and interval gates: a fully idle sender publishes no new
                // generation, so without this the requested keyframe would wait for motion.
                t.LastSeenGeneration = 0;
                t.LastSentTime = 0;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static LocalAvatarSyncMessage QualityMsg(PlayerState state, int qi) => qi switch
        {
            0 => state.AvatarVeryLow,
            1 => state.AvatarLow,
            2 => state.AvatarMedium,
            _ => state.AvatarHigh,
        };

        /// <summary>
        /// Serializes a delta frame for quality <paramref name="qi"/> onto DeltaAvatarChannel:
        ///   [header:1][playerId:1|2][interval:1][sequence:1][baseSeq:1][delta body][additional?]
        /// diffed against the last-keyframe baseline. Sets SerializedDeltaLength[qi]=0 if unbuildable.
        /// </summary>
        internal static void PreSerializeDelta(PlayerState state, int qi, LocalAvatarSyncMessage msg, ushort playerId)
        {
            var q = (BitQuality)qi;
            if (msg.array == null || (int)msg.DataQualityLevel != qi || state.KeyframePayload[qi] == null)
            {
                state.SerializedDeltaLength[qi] = 0;
                return;
            }
            int payload = BasisAvatarBitPacking.ConvertToSize(q);
            if (msg.array.Length < payload || state.KeyframePayloadLength[qi] < payload)
            {
                state.SerializedDeltaLength[qi] = 0;
                return;
            }

            int additionalSize = AdditionalDataSize(state, msg, out bool hasAdditional);
            int idSize = state.SmallId ? 1 : 2;
            int headerBytes = 1 + idSize + 1 + 1 + 1; // header + playerId + interval + sequence + baseSeq
            int cap = headerBytes + BasisAvatarDeltaCompression.MaxDeltaSize(q) + additionalSize;

            if (state.SerializedDelta[qi] == null || state.SerializedDelta[qi].Length < cap)
                state.SerializedDelta[qi] = new byte[cap];

            byte[] dst = state.SerializedDelta[qi];
            int o = 0;
            dst[o++] = BasisNetworkCommons.BuildDeltaHeader(qi, hasAdditional, !state.SmallId);
            if (state.SmallId)
            {
                dst[o++] = (byte)playerId;
            }
            else
            {
                dst[o++] = (byte)(playerId & 0xFF);
                dst[o++] = (byte)((playerId >> 8) & 0xFF);
            }
            dst[o++] = 0; // interval placeholder (patched per-receiver in the send loop)
            dst[o++] = state.OutboundSequence;
            dst[o++] = state.KeyframeSequence; // baseSeq — the keyframe this delta reconstructs against

            int bodyLen = BasisAvatarDeltaCompression.BuildDelta(state.KeyframePayload[qi], msg.array, q, dst, o);
            if (bodyLen < 0)
            {
                state.SerializedDeltaLength[qi] = 0;
                return;
            }
            o += bodyLen;

            if (hasAdditional) o = WriteAdditionalData(dst, o, msg);

            state.SerializedDeltaLength[qi] = o;
            BSRProfiler.IncrementPreSerializations();
        }

        private static int AdditionalDataSize(PlayerState state, LocalAvatarSyncMessage msg, out bool hasAdditional)
        {
            hasAdditional = state.HasAdditionalData
                && msg.AdditionalAvatarDatas != null
                && msg.AdditionalAvatarDatas.Length > 0
                && msg.AdditionalAvatarDatas.Length <= 255;
            if (!hasAdditional) return 0;
            int sz = 1 + 1; // AdditionalSize + LinkedAvatarIndex
            for (int i = 0; i < msg.AdditionalAvatarDatas.Length; i++)
                sz += 1 + 1 + (msg.AdditionalAvatarDatas[i].array?.Length ?? 0);
            return sz;
        }

        private static int WriteAdditionalData(byte[] dst, int offset, LocalAvatarSyncMessage msg)
        {
            dst[offset++] = (byte)msg.AdditionalAvatarDatas.Length;
            dst[offset++] = msg.LinkedAvatarIndex;
            for (int i = 0; i < msg.AdditionalAvatarDatas.Length; i++)
            {
                var ad = msg.AdditionalAvatarDatas[i];
                // Full [size:1][messageIndex:1] header per entry, size 0 for null/oversized —
                // must match AdditionalAvatarData.Serialize/Deserialize exactly (see PreSerializeKeyframe).
                if (ad.array == null || ad.array.Length > 255)
                {
                    dst[offset++] = 0;
                    dst[offset++] = ad.messageIndex;
                }
                else
                {
                    byte payloadSize = (byte)ad.array.Length;
                    dst[offset++] = payloadSize;
                    dst[offset++] = ad.messageIndex;
                    if (payloadSize > 0)
                    {
                        Buffer.BlockCopy(ad.array, 0, dst, offset, payloadSize);
                        offset += payloadSize;
                    }
                }
            }
            return offset;
        }

        #endregion
    }

    /// <summary>
    /// Power-of-two-sharded ConcurrentDictionary&lt;int, TValue&gt; replacement. Splits writes
    /// across N=NextPow2(ProcessorCount) inner dicts indexed by a scrambled key, so per-bucket
    /// lock contention drops by ~N× under ingress storms (HandleAvatarMovement → currentMessages
    /// and ProcessMessage → playerStates upserts at 1k+ players, 250Hz). Reference-swappable —
    /// preserves the existing Interlocked.Exchange double-buffer pattern. Enumeration walks
    /// shards sequentially and inherits ConcurrentDictionary's per-shard snapshot semantics.
    /// </summary>
    public sealed class ShardedConcurrentDictionary<TValue> : System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<int, TValue>>
    {
        private readonly ConcurrentDictionary<int, TValue>[] _shards;
        private readonly int _mask;

        public ShardedConcurrentDictionary()
            : this(NextPowerOfTwo(Math.Max(1, Environment.ProcessorCount))) { }

        public ShardedConcurrentDictionary(int shardCount)
        {
            if (shardCount <= 0 || (shardCount & (shardCount - 1)) != 0)
                throw new ArgumentException("shardCount must be a positive power of two", nameof(shardCount));
            _shards = new ConcurrentDictionary<int, TValue>[shardCount];
            _mask = shardCount - 1;
            for (int i = 0; i < shardCount; i++) _shards[i] = new ConcurrentDictionary<int, TValue>();
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private ConcurrentDictionary<int, TValue> ShardOf(int key) => _shards[Scramble(key) & _mask];

        // 32-bit integer hash mix (Murmur3-style). Player ids are dense small ints assigned by
        // LiteNetLib; without scrambling, ids 0..N-1 would all hash to shard 0 under low-bit
        // masking, completely defeating the shard split.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static int Scramble(int key)
        {
            unchecked
            {
                uint x = (uint)key;
                x ^= x >> 16;
                x *= 0x7feb352du;
                x ^= x >> 15;
                x *= 0x846ca68bu;
                x ^= x >> 16;
                return (int)x;
            }
        }

        public bool TryGetValue(int key, out TValue value) => ShardOf(key).TryGetValue(key, out value);
        public bool TryRemove(int key, out TValue value) => ShardOf(key).TryRemove(key, out value);

        public TValue this[int key]
        {
            get => ShardOf(key)[key];
            set => ShardOf(key)[key] = value;
        }

        public void Clear()
        {
            for (int i = 0; i < _shards.Length; i++) _shards[i].Clear();
        }

        public System.Collections.Generic.IEnumerator<System.Collections.Generic.KeyValuePair<int, TValue>> GetEnumerator()
        {
            for (int i = 0; i < _shards.Length; i++)
            {
                foreach (var kvp in _shards[i]) yield return kvp;
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        private static int NextPowerOfTwo(int x)
        {
            if (x <= 1) return 1;
            int p = 1;
            while (p < x) p <<= 1;
            return p;
        }
    }
}
