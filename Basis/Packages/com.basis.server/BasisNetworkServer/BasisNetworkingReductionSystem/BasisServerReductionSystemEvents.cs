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
    /// Combined per-peer tracking + cached distance data. 32 bytes = two per cache line.
    /// The send loop reads all fields sequentially per pair with no float math.
    /// </summary>
    public struct PeerTrackingData
    {
        public long LastSentTime;
        public long LastSeenGeneration;
        // Cached by the slow distance loop (~2Hz), read by the fast send loop (~250Hz).
        // Eliminates per-pair distance math from the hot path.
        public long CachedIntervalTicks;
        public byte CachedQualityIndex;
        public byte CachedIntervalByte;
    }

    public class PlayerState
    {
        public NetPeer Peer;
        public bool IsActive;

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
    }

    public partial class BasisServerReductionSystemEvents
    {
        private static readonly CancellationTokenSource cts = new();
        // Initial capacity for PeerTracking array on PlayerState.
        // Grows if a player ID exceeds this.
        private const int InitialPlayerArrayCapacity = 2048;

        private static readonly ParallelOptions parallelOptions = new()
        {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1)
        };

        public static ConcurrentDictionary<int, PlayerState> playerStates = new();
        // Double-buffered message dictionaries: swap and clear instead of allocating per tick.
        private static ConcurrentDictionary<int, QueuedMessage> currentMessages = new();
        private static ConcurrentDictionary<int, QueuedMessage> _backMessages = new();

        public static float BSRBaseMultiplier = 1.0f;
        public static float BSRSIncreaseRate = 0.01f;
        public static int BSRSMillisecondDefaultInterval = 50;

        // Compressed avatar bundle settings (written from NetworkServer.InitializePulseSettings).
        // When enabled, the per-receiver inner loop defers sends into PendingAvatarSend[] and
        // flushes either as one deflated bundle on CompressedAvatarBundleChannel or as
        // individual SendUnreliableRawMerge calls on the original quality channels.
        public static bool EnableAvatarBundleCompression = false;
        public static int AvatarBundleMinMessages = 4;
        public static int AvatarBundleMinBytes = 300;
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

        // Reusable snapshot list for draining currentMessages each tick  avoids allocation per tick.
        private static readonly List<QueuedMessage> _messagesSnapshot = new(1024);

        // Static delegate for Parallel.ForEach — avoids closure allocation every tick.
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

        // Distance -> Quality thresholds (squared meters)
        public static float HighDistanceSq = 100f;      // 10m
        public static float MediumDistanceSq = 900f;    // 30m
        public static float LowDistanceSq = 2500f;      // 50m

        public static long intervalMs = 4;
        // Tick slicing: only process a subset of receivers each tick to spread the O(NÂ²) work.
        // Adaptive: increases when ticks take too long, decreases when under budget.
        private static int _sliceCount = 1;
        private static int _sliceIndex = 0;

        // Distance cache: recalculate quality/interval from distance every N ticks.
        // The fast send loop uses cached values instead of computing distance per pair per tick.
        // At 4ms tick interval, 125 ticks = ~500ms. Players at 6m/s cover 3m in that time,
        // which is within one quality threshold (3m/10m/20m) — acceptable staleness.
        private static int _distanceTickCounter = 0;
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



        static BasisServerReductionSystemEvents()
        {
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

            // Overwrite any pending message for this peer.
            // Uses indexer instead of AddOrUpdate to avoid closure allocation on every call.
            // Do NOT return prev to the pool — the drain phase may have captured it.
            // The orphaned prev (if any) is collected by the GC; this only occurs when two
            // messages for the same peer arrive within the same tick — negligible cost.
            currentMessages[fromPeer.Id] = message;
        }

        public static void AddMessage(NetPeer fromPeer, LocalAvatarSyncMessage localMessage, byte sequence)
        {
            var message = QueuedMessagePool.Rent();
            message.FromPeer = fromPeer;
            message.Sequence = sequence;
            message.AvatarMessage = localMessage;

            // Same as HandleAvatarMovement — indexer avoids closure allocation.
            currentMessages[fromPeer.Id] = message;
        }

        /// <summary>
        /// Dedicated tick loop running on its own thread with sub-millisecond precision.
        /// Replaces the async Task.Delay approach which had ~15ms resolution on Windows,
        /// limiting tick rate to ~70Hz. This achieves the target tick rate of ~250Hz (4ms).
        /// Uses coarse Thread.Sleep for long waits + SpinWait for precise final timing.
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

                // Precise timing: coarse sleep for bulk wait, then spin to exact target.
                // Thread.Sleep(1) has ~1-2ms resolution vs Task.Delay's ~15ms on Windows.
                long targetTick = startTick + (long)(intervalMs * MsToTick);
                long nowTick = Stopwatch.GetTimestamp();
                double remainMs = (targetTick - nowTick) / MsToTick;

                if (remainMs > 2.0)
                {
                    Thread.Sleep(Math.Max(1, (int)(remainMs - 1)));
                }

                // Spin-wait for sub-millisecond precision to hit the exact target
                while (Stopwatch.GetTimestamp() < targetTick)
                {
                    Thread.SpinWait(20);
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
            Parallel.ForEach(_messagesSnapshot, parallelOptions, s_processMessageAction);
            if (profiling) { BSRProfiler.processTicks += Stopwatch.GetTimestamp() - phaseTick; phaseTick = Stopwatch.GetTimestamp(); }

            ProcessPendingRemovals();

            // Phase 2.5: Distance cache update (runs at ~2Hz instead of every tick)
            _distanceTickCounter++;
            if (_distanceTickCounter >= DistanceUpdateIntervalTicks)
            {
                _distanceTickCounter = 0;
                long distStart = profiling ? Stopwatch.GetTimestamp() : 0;
                UpdateDistanceCache();
                if (profiling) { BSRProfiler.distanceTicks += Stopwatch.GetTimestamp() - distStart; phaseTick = Stopwatch.GetTimestamp(); }
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
            if (NetworkServer.Server != null)
            {
                NetworkServer.Server.PollEvents();
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

            // Adaptive slice count: if tick took > 3ms, increase slicing; if < 1ms, decrease.
            if (elapsedMs > 3.0 && _sliceCount < 32)
            {
                _sliceCount++;
            }
            else if (elapsedMs < 1.0 && _sliceCount > 1)
            {
                _sliceCount--;
            }
        }

        private static void ProcessPendingRemovals()
        {
            while (playersToRemove.TryDequeue(out int id))
            {
                if (playerStates.TryRemove(id, out var removedState))
                {
                    removedState.IsActive = false;

                    // Return pooled arrays to ArrayPool
                    if (removedState.AvatarHigh.array != null)
                        ArrayPool<byte>.Shared.Return(removedState.AvatarHigh.array);

                    // Remove from active players list
                    lock (_activePlayersLock)
                    {
                        for (int i = _activePlayers.Count - 1; i >= 0; i--)
                        {
                            if (_activePlayers[i].id == id)
                            {
                                _activePlayers.RemoveAt(i);
                                _activePlayersDirty = true;
                                break;
                            }
                        }
                    }


                    // Clear stale per-player tracking data for the removed ID across all remaining players.
                    // Without this, when a new player reuses this ID, other players LastSeenGeneration
                    // would still hold the old (high) generation value, causing the new-data check
                    // (senderGen > seenGens[jId]) to fail -- no data would be sent for the new player.
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

        private static void UpdateDistanceCache()
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
            if (playerCount == 0) return;

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

            Parallel.For(0, playerCount, parallelOptions, i =>
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

                    tracking[jId].CachedIntervalTicks = (long)(actualInterval * MsToTick);
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

                    // 1. New data check — plain array read, no pointer chase
                    long senderGen = _generationSnapshot[jId];
                    if (senderGen <= tracking[jId].LastSeenGeneration)
                    {
                        continue;
                    }

                    // 2. Interval check using cached distance results (no float math)
                    long elapsed = nowTicks - tracking[jId].LastSentTime;
                    long required = tracking[jId].CachedIntervalTicks;
                    if (required <= 0) required = minIntervalTicks;
                    if (elapsed < required)
                    {
                        continue;
                    }

                    // 3. Quality + interval byte from distance cache
                    int qi = tracking[jId].CachedQualityIndex;
                    byte startAtZeroInterval = tracking[jId].CachedIntervalByte;

                    PlayerState stateJ = activeCopy[index].state;

                    // Lazy pre-serialization: skip if not serialized, mark needed for next tick
                    int srcLen = stateJ.SerializedKeyframeLength[qi];
                    byte[] srcArr = stateJ.SerializedKeyframe[qi];
                    if (srcLen == 0 || srcArr == null)
                    {
                        MarkQualityUsed(ref stateJ.UsedQualities, qi);
                        continue;
                    }

                    byte avatarChannel = stateJ.SmallId
                        ? BasisNetworkCommons.GetPlayerAvatarChannelForQuality(qi, stateJ.HasAdditionalData)
                        : BasisNetworkCommons.GetPlayerAvatarLargeChannelForQuality(qi, stateJ.HasAdditionalData);

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
                    p.IntervalOffset = (byte)(stateJ.SmallId ? 1 : 2);

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
        private static void FlushPendingForReceiver(PlayerState stateI, NetPeer peer, bool bundlingEnabled)
        {
            int count = stateI.PendingCount;
            if (count <= 0) return;
            var pending = stateI.PendingSends;

            int cursor = 0;
            if (bundlingEnabled && count >= AvatarBundleMinMessages)
            {
                cursor = EmitGreedyBundles(stateI, peer, pending, count);
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
                BasisNetworkStatistics.RecordOutbound(p.Channel, p.Length);
                tailSent++;
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
            stateI.PendingCount = 0;
        }

        /// <summary>
        /// Greedily packs as many pending messages as fit into MTU-sized compressed
        /// bundles, emitting each on <see cref="BasisNetworkCommons.CompressedAvatarBundleChannel"/>.
        /// Uses a per-receiver EMA of the compressed/raw ratio so the first deflate
        /// attempt usually succeeds; on overshoot we shrink using the actual observed
        /// ratio and retry once. Returns the index of the first not-yet-emitted entry —
        /// callers send the [cursor, count) tail uncompressed.
        /// </summary>
        private static int EmitGreedyBundles(PlayerState stateI, NetPeer peer, PendingAvatarSend[] pending, int count)
        {
            int budget = peer.Mtu - BundleMtuHeadroom - BundleHeaderSize;
            if (budget <= 0) return 0;

            // Initial ratio guess: deflate Fastest on bit-packed avatar data observed ~0.6.
            // Stays in [0.05, 0.95] so prediction never picks zero or full-budget chunks.
            float ratio = stateI.LastBundleRatio;
            if (ratio < 0.05f || ratio > 0.95f) ratio = 0.6f;

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

                if (TryDeflateAndEmit(stateI, peer, cursor, chunkEnd, rawLen, budget, out int compressedLen))
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
                if (!TryDeflateAndEmit(stateI, peer, cursor, retryEnd, retryRawLen, budget, out int retryCompressed))
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
                raw = new byte[Math.Max(upperBound, 4096)];
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
        private static bool TryDeflateAndEmit(PlayerState stateI, NetPeer peer, int chunkStart, int chunkEnd, int rawLen, int budget, out int compressedLen)
        {
            compressedLen = 0;
            byte[] raw = stateI.BundleRawScratch;
            byte[] compressed = stateI.BundleCompressedScratch;
            // LZ4 worst case is rawLen + (rawLen / 255) + 16 (returned by MaximumOutputSize).
            int compCapacityNeeded = BundleHeaderSize + LZ4Codec.MaximumOutputSize(rawLen);
            if (compressed == null || compressed.Length < compCapacityNeeded)
            {
                compressed = new byte[Math.Max(compCapacityNeeded, 4096)];
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
            BasisNetworkStatistics.RecordOutbound(BasisNetworkCommons.CompressedAvatarBundleChannel, wireLen);

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
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CalculateIntervalFromDistanceSq(float distanceSq, out byte offsetByte, out int actualInterval)
        {
            int rawInterval = (int)(BSRSMillisecondDefaultInterval * (BSRBaseMultiplier + (distanceSq * BSRSIncreaseRate)));
            int encodedInterval = rawInterval - BSRSMillisecondDefaultInterval;

            offsetByte = (byte)Math.Clamp(encodedInterval, 0, byte.MaxValue);
            actualInterval = offsetByte + BSRSMillisecondDefaultInterval;
        }

        public static void Shutdown() => cts.Cancel();

        public static void RemovePlayer(int id)
        {
            playersToRemove.Enqueue(id);
        }

        /// <summary>
        /// Propagates AdditionalAvatarData from the high quality message to lower quality variants.
        /// BuildAllLowerFromHighInto only handles the muscle/position/rotation payload;
        /// additional data (blendshapes, custom avatar behaviours) must be propagated separately.
        /// VeryLow quality strips additional data entirely  face/detail data is invisible at 20m+.
        /// </summary>
        private static void PropagateAdditionalData(
            in LocalAvatarSyncMessage high,
            ref LocalAvatarSyncMessage medium,
            ref LocalAvatarSyncMessage low,
            ref LocalAvatarSyncMessage veryLow)
        {
            medium.AdditionalAvatarDatas = high.AdditionalAvatarDatas;
            medium.AdditionalAvatarDataSize = high.AdditionalAvatarDataSize;
            medium.LinkedAvatarIndex = high.LinkedAvatarIndex;

            low.AdditionalAvatarDatas = high.AdditionalAvatarDatas;
            low.AdditionalAvatarDataSize = high.AdditionalAvatarDataSize;
            low.LinkedAvatarIndex = high.LinkedAvatarIndex;

            veryLow.AdditionalAvatarDatas = high.AdditionalAvatarDatas;
            veryLow.AdditionalAvatarDataSize = high.AdditionalAvatarDataSize;
            veryLow.LinkedAvatarIndex = high.LinkedAvatarIndex;
        }
        /// <summary>
        /// Copies position bytes from the high-quality source to all lower quality arrays.
        /// Position encoding is identical across all quality levels.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CopyPositionToLowerQualities(
            byte[] highArray,
            ref LocalAvatarSyncMessage medium,
            ref LocalAvatarSyncMessage low,
            ref LocalAvatarSyncMessage veryLow)
        {
            if (medium.array != null)
                Buffer.BlockCopy(highArray, 0, medium.array, 0, WritePosition);
            if (low.array != null)
                Buffer.BlockCopy(highArray, 0, low.array, 0, WritePosition);
            if (veryLow.array != null)
                Buffer.BlockCopy(highArray, 0, veryLow.array, 0, WritePosition);
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
                    DataGeneration = 1,
                    LastInboundSequence = inboundSeq,
                    HasReceivedFirst = true,
                    OutboundSequence = 0,
                    SmallId = id <= byte.MaxValue,
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

                // First frame: pre-serialize
                PreSerializeAll(state);

                playerStates[id] = state;

                // Add to active players list
                lock (_activePlayersLock)
                {
                    _activePlayers.Add((id, state));
                    _activePlayersDirty = true;
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
                        // Position-only fast path: copy position bytes to all lower qualities.
                        // Position is identical across all quality levels (no bit-width difference).
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

                PreSerializeAll(state);

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

        private static void PreSerializeKeyframe(PlayerState state, int qi, LocalAvatarSyncMessage msg, ushort playerId)
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
                    if (ad.array == null || ad.array.Length > 255)
                    {
                        dst[offset++] = 0;
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

        #endregion
    }
}
