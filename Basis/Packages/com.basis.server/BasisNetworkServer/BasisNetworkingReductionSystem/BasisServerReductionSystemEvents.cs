using Basis.Network.Core;
using Basis.Network.Core.Compression;
using BasisNetworkServer.BasisNetworking;
using System;
using System.Buffers;
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
        private static readonly double MsToTick = Stopwatch.Frequency / 1000.0;

        // Maintained incrementally via ProcessMessage/ProcessPendingRemovals instead of rebuilt every tick.
        private static readonly List<(int id, PlayerState state)> _activePlayers = new();
        private static readonly object _activePlayersLock = new();
        private static (int id, PlayerState state)[] _activePlayersSnapshot = Array.Empty<(int, PlayerState)>();
        private static volatile bool _activePlayersDirty = false;

        private static readonly ConcurrentQueue<int> playersToRemove = new();

        // Reusable snapshot list for draining currentMessages each tick  avoids allocation per tick.
        private static readonly List<QueuedMessage> _messagesSnapshot = new(1024);

        // Distance -> Quality thresholds (squared meters)
        public static float HighDistanceSq = 9f;        // 3m
        public static float MediumDistanceSq = 100f;    // 10m
        public static float LowDistanceSq = 400f;       // 20m

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
            // Do NOT return prev to the pool here — Interlocked.Exchange in the drain phase
            // may have already captured prev into _messagesSnapshot.  Returning it in this
            // factory would double-return the same QueuedMessage, letting two future Rent()
            // calls receive the same object and corrupt each other's data.
            // The orphaned prev (if any) is collected by the GC; this only occurs when two
            // messages for the same peer arrive within the same tick AND the Exchange races
            // with this AddOrUpdate — negligible allocation cost.
            currentMessages.AddOrUpdate(fromPeer.Id, message, (_, prev) => message);
        }

        public static void AddMessage(NetPeer fromPeer, LocalAvatarSyncMessage localMessage, byte sequence)
        {
            var message = QueuedMessagePool.Rent();
            message.FromPeer = fromPeer;
            message.Sequence = sequence;
            message.AvatarMessage = localMessage;

            // Same double-return avoidance as HandleAvatarMovement — see comment there.
            currentMessages.AddOrUpdate(fromPeer.Id, message, (_, prev) => message);
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

                // Phase 2: Process messages
                Parallel.ForEach(_messagesSnapshot, parallelOptions, msg =>
                {
                    try
                    {
                        ProcessMessage(msg);
                    }
                    catch (Exception ex)
                    {
                        BNL.LogError($"[ProcessMessage] Exception: {ex}");
                    }
                });
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
                if (NetworkServer.Server != null && NetworkServer.Server.manager != null)
                {
                    NetworkServer.Server.manager.TriggerUpdate();
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
                    if (stateJ.SerializedKeyframeLength[qi] == 0)
                    {
                        MarkQualityUsed(ref stateJ.UsedQualities, qi);
                        continue;
                    }

                    byte avatarChannel = stateJ.SmallId
                        ? BasisNetworkCommons.GetPlayerAvatarChannelForQuality(qi, stateJ.HasAdditionalData)
                        : BasisNetworkCommons.GetPlayerAvatarLargeChannelForQuality(qi, stateJ.HasAdditionalData);
                    SendPreSerialized(peer, qi, startAtZeroInterval, avatarChannel, stateJ.SerializedKeyframe, stateJ.SerializedKeyframeLength, stateJ.SmallId);

                    MarkQualityUsed(ref stateJ.UsedQualities, qi);

                    tracking[jId].LastSentTime = nowTicks;
                    tracking[jId].LastSeenGeneration = senderGen;

                    localSends++;
                }

                // One Interlocked.Add per receiver (not per send) — ~25 atomics/tick instead of ~32K
                if (localSends > 0 && BSRProfiler.Enabled)
                {
                    Interlocked.Add(ref BSRProfiler.SendCount, localSends);
                }
            });
        }

        /// <summary>
        /// Atomically sets a quality bit in the UsedQualities bitmask.
        /// Called from parallel send loop threads lock-free via CAS.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void MarkQualityUsed(ref int usedQualities, int qi)
        {
            int bit = 1 << qi;
            int cur = Volatile.Read(ref usedQualities);
            while (true)
            {
                if ((cur & bit) != 0)
                {
                    return; // already set
                }

                int updated = cur | bit;
                int was = Interlocked.CompareExchange(ref usedQualities, updated, cur);
                if (was == cur)
                {
                    return;
                }

                cur = was;
            }
        }

        /// <summary>
        /// Sends a pre-serialized message directly into the merge buffer, patching the
        /// interval byte after the copy to avoid racing on the shared source array.
        /// Eliminates the intermediate NetDataWriter copy.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SendPreSerialized(NetPeer peer, int qi, byte interval, byte channel, byte[][] serializedArray, int[] lengthArray, bool smallId)
        {
            int len = lengthArray[qi];
            byte[] src = serializedArray[qi];
            if (src == null || len == 0)
            {
                return;
            }

            // Patch interval byte: offset 1 for byte playerID, offset 2 for ushort playerID
            int intervalOffset = smallId ? 1 : 2;
            if (len <= intervalOffset)
            {
                return;
            }
            peer.SendUnreliableRawMerge(src, 0, len, channel, intervalOffset, interval);
            BasisNetworkStatistics.RecordOutbound(channel, len);
        }

        // Thread-local NetDataWriter for keyframe serialization (ProcessMessage path).
        [ThreadStatic]
        private static NetDataWriter t_serializeWriter;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static NetDataWriter GetThreadWriter()
        {
            var w = t_serializeWriter;
            if (w == null)
            {
                w = new NetDataWriter(true, 512);
                t_serializeWriter = w;
            }
            else
            {
                w.Reset();
            }
            return w;
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
                return;
            }

            var quality = (BitQuality)msg.DataQualityLevel;
            int expectedPayload = BasisAvatarBitPacking.ConvertToSize(quality);

            // Skip if the array is undersized (e.g. client sent wrong quality level)
            if (msg.array.Length < expectedPayload)
            {
                BNL.LogError($"[PreSerializeKeyframe] Array undersized for quality {quality}: got {msg.array.Length}, need {expectedPayload}. Skipping.");
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

            NetDataWriter writer = GetThreadWriter();
            if (state.SmallId)
                writer.Put((byte)playerId);
            else
                writer.Put(playerId);
            writer.Put((byte)0); // interval placeholder
            writer.Put(state.OutboundSequence); // sequence byte
            writer.Put(msg.array, 0, expectedPayload);

            // Additional avatar data only on odd channels (hasAdditional = true)
            if (hasAdditional)
            {
                writer.Put((byte)msg.AdditionalAvatarDatas.Length);
                writer.Put(msg.LinkedAvatarIndex);
                for (int i = 0; i < msg.AdditionalAvatarDatas.Length; i++)
                {
                    msg.AdditionalAvatarDatas[i].Serialize(writer);
                }
            }

            int written = writer.Length;
            Buffer.BlockCopy(writer.Data, 0, state.SerializedKeyframe[qi], 0, written);
            state.SerializedKeyframeLength[qi] = written;
        }

        #endregion
    }
}
