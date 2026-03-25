using Basis.Network.Core;
using Basis.Network.Core.Compression;
using BasisNetworkServer.BasisNetworking;
using System;
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
    /// Combined per-peer tracking data. Two longs in one struct = one cache line fetch
    /// instead of two parallel array accesses in the O(NÂ²) send loop.
    /// </summary>
    public struct PeerTrackingData
    {
        public long LastSentTime;
        public long LastSeenGeneration;
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

        // Pre-serialized keyframe bytes per quality [PlayerID:2][interval_placeholder:1][sequence:1][array:N][additionalSize:1]
        // The interval byte at offset 2 is filled per-recipient in the send loop.
        // Quality is derived from the channel number — not stored in the payload.
        public byte[][] SerializedKeyframe = new byte[4][];
        public int[] SerializedKeyframeLength = new int[4];

        // Lazy pre-serialization: bitmask of which quality levels had receivers last tick.
        // Updated atomically from the parallel send loop. Read/reset in ProcessMessage.
        // Bit 0 = VeryLow, Bit 1 = Low, Bit 2 = Medium, Bit 3 = High.
        public int UsedQualities;
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
        private static ConcurrentDictionary<int, QueuedMessage> currentMessages = new();

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

        // Thread-local NetDataWriter for serialization  eliminates shared pool contention.
        [ThreadStatic]
        private static NetDataWriter t_serializeWriter;

        // Cached muscle+tail byte counts for the position-only fast path (skip repack).
        private static readonly int HighMuscleAndTailBytes = MuscleBytes(BitQuality.High) + TailBytes;

        // Generation snapshot: populated once per tick before the O(NÂ²) send loop.
        // Eliminates Interlocked.Read per pair (NÂ² memory fences â†’ N).
        private static long[] _generationSnapshot = Array.Empty<long>();

        // Position snapshots: contiguous arrays for cache-friendly reads in the inner loop.
        // Avoids pointer-chasing through scattered heap PlayerState objects per pair.
        private static float[] _posXSnapshot = Array.Empty<float>();
        private static float[] _posYSnapshot = Array.Empty<float>();
        private static float[] _posZSnapshot = Array.Empty<float>();



        static BasisServerReductionSystemEvents()
        {
            _ = StartBackgroundProcessingAsync();
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

        private static async Task StartBackgroundProcessingAsync()
        {
            while (!cts.Token.IsCancellationRequested)
            {
                long startTick = Stopwatch.GetTimestamp();
                bool profiling = BSRProfiler.Enabled;
                long phaseTick = profiling ? Stopwatch.GetTimestamp() : 0;

                // Phase 1: Drain
                // Atomically swap in a fresh dictionary so inbound threads write to the new one.
                // ConcurrentDictionary's enumerator is NOT a point-in-time snapshot  items added
                // during iteration can be seen, causing duplicate messages for the same peer to be
                // drained and then processed in parallel, racing on the shared PlayerState.
                var batch = Interlocked.Exchange(ref currentMessages, new ConcurrentDictionary<int, QueuedMessage>());
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
                long remainingMs = intervalMs - (long)elapsedMs;

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

                if (remainingMs > 0)
                {
                    await Task.Delay((int)remainingMs, cts.Token);
                }
                else
                {
                    await Task.Yield();
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

            // Snapshot all hot data into contiguous arrays (N reads instead of NÂ² pointer chases).
            int maxId = 0;
            for (int i = 0; i < playerCount; i++)
            {
                if (activeCopy[i].id > maxId) maxId = activeCopy[i].id;
            }
            int snapshotLen = maxId + 1;
            if (_generationSnapshot.Length < snapshotLen)
            {
                _generationSnapshot = new long[snapshotLen];
                _posXSnapshot = new float[snapshotLen];
                _posYSnapshot = new float[snapshotLen];
                _posZSnapshot = new float[snapshotLen];
            }
            for (int i = 0; i < playerCount; i++)
            {
                int id = activeCopy[i].id;
                var state = activeCopy[i].state;
                _generationSnapshot[id] = Interlocked.Read(ref state.DataGeneration);
                _posXSnapshot[id] = state.Position.x;
                _posYSnapshot[id] = state.Position.y;
                _posZSnapshot[id] = state.Position.z;
            }

            // Pre-compute minimum interval in ticks cheapest early-exit threshold.
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

                // Cache receiver position from snapshot for distance calc
                float iX = _posXSnapshot[id];
                float iY = _posYSnapshot[id];
                float iZ = _posZSnapshot[id];

                // Thread-local send counter no Interlocked in the hot loop
                long localSends = 0;

                for (int index = 0; index < playerCount; index++)
                {
                    int jId = activeCopy[index].id;
                    if (id == jId)
                    {
                        continue;
                    }

                    // Bounds check grow array if needed (rare, only when IDs exceed capacity)
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

                    // Reordered checks: cheapest first, skip distance calc when possible

                    // 1. New data check plain array read, no pointer chase
                    long senderGen = _generationSnapshot[jId];
                    if (senderGen <= tracking[jId].LastSeenGeneration)
                    {
                        continue;
                    }

                    // 2. Minimum interval check skip without distance calc
                    long elapsed = nowTicks - tracking[jId].LastSentTime;
                    if (elapsed < minIntervalTicks)
                    {
                        continue;
                    }

                    // 3. Distance from contiguous snapshot arrays (cache-friendly)
                    float dx = iX - _posXSnapshot[jId];
                    float dy = iY - _posYSnapshot[jId];
                    float dz = iZ - _posZSnapshot[jId];
                    float distSq = dx * dx + dy * dy + dz * dz;

                    // 4. Full interval check with distance
                    CalculateIntervalFromDistanceSq(distSq, out byte startAtZeroInterval, out int actualInterval);
                    long required = (long)((actualInterval) * MsToTick);

                    if (elapsed < required)
                    {
                        continue;
                    }

                    // 5. Quality + send
                    int qi = GetQualityIndex(distSq);

                    PlayerState stateJ = activeCopy[index].state;

                    // Lazy pre-serialization: skip if not serialized, mark needed for next tick
                    if (stateJ.SerializedKeyframeLength[qi] == 0)
                    {
                        MarkQualityUsed(ref stateJ.UsedQualities, qi);
                        continue;
                    }

                    byte avatarChannel = BasisNetworkCommons.GetPlayerAvatarChannelForQuality(qi, stateJ.HasAdditionalData);
                    SendPreSerialized(peer, qi, startAtZeroInterval, avatarChannel,stateJ.SerializedKeyframe, stateJ.SerializedKeyframeLength);

                    MarkQualityUsed(ref stateJ.UsedQualities, qi);

                    tracking[jId].LastSentTime = nowTicks;
                    tracking[jId].LastSeenGeneration = senderGen;

                    localSends++;
                }

                // One Interlocked.Add per receiver (not per send) ~25 atomics/tick instead of ~32K
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
        /// Sends a pre-serialized message, patching the interval byte at offset 2.
        /// Uses a thread-local writer to avoid shared pool contention.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SendPreSerialized(NetPeer peer, int qi,byte interval, byte channel, byte[][] serializedArray, int[] lengthArray)
        {
            int len = lengthArray[qi];
            byte[] src = serializedArray[qi];
            if (src == null || len == 0)
            {
                return;
            }

            NetDataWriter writer = GetThreadWriter();
            writer.Put(src, 0, len);

            // Patch interval byte at offset 2 (after PlayerID ushort)
            if (writer.Length > 2)
            {
                writer.Data[2] = interval;
            }

            peer.Send(writer, channel, DeliveryMethod.Unreliable);
            BasisNetworkStatistics.RecordOutbound(channel, writer.Length);
        }

        /// <summary>
        /// Returns a thread-local NetDataWriter, creating one if needed.
        /// Avoids contention on the shared ConcurrentQueue writer pool.
        /// </summary>
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
            // This corrupts the prevArray muscle comparison on the next tick, causing
            // intermittent missed repacks where lower-quality receivers get stale muscles.
            // A fresh allocation (~150 bytes) per player per tick is negligible and
            // preserves the position-only fast path (prevArray stays a distinct object
            // with the previous tick's data).
            var high = new LocalAvatarSyncMessage
            {
                DataQualityLevel = poolMsg.DataQualityLevel,
                AdditionalAvatarDatas = poolMsg.AdditionalAvatarDatas,
                AdditionalAvatarDataSize = poolMsg.AdditionalAvatarDataSize,
                LinkedAvatarIndex = poolMsg.LinkedAvatarIndex,
                array = new byte[poolMsg.array.Length],
            };
            Buffer.BlockCopy(poolMsg.array, 0, high.array, 0, poolMsg.array.Length);

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
                    PeerTracking = new PeerTrackingData[InitialPlayerArrayCapacity],
                    DataGeneration = 1,
                    LastInboundSequence = inboundSeq,
                    HasReceivedFirst = true,
                    OutboundSequence = 0,
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
                        // Duplicate or stale  discard
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
                state.AvatarHigh = high;

                if (isHighQuality)
                {
                    // Check if muscles+tail changed (skip expensive bit repacking if only position moved).
                    // ReferenceEquals guard (defense-in-depth): with the deep-copy above, prevArray
                    // and high.array should always be distinct objects. The guard remains as a safety
                    // net in case future changes reintroduce buffer sharing.
                    int muscleAndTailBytes = HighMuscleAndTailBytes;
                    bool musclesOrTailChanged = prevArray == null
                        || ReferenceEquals(prevArray, high.array)
                        || prevArray.Length != high.array.Length
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
                        if (state.AvatarMedium.array != null)
                        {
                            Buffer.BlockCopy(high.array, 0, state.AvatarMedium.array, 0, WritePosition);
                        }

                        if (state.AvatarLow.array != null)
                        {
                            Buffer.BlockCopy(high.array, 0, state.AvatarLow.array, 0, WritePosition);
                        }

                        if (state.AvatarVeryLow.array != null)
                        {
                            Buffer.BlockCopy(high.array, 0, state.AvatarVeryLow.array, 0, WritePosition);
                        }
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

            // Even channels (no additional):  [PlayerID:2][interval:1][sequence:1][array:N]
            // Odd channels (has additional):   [PlayerID:2][interval:1][sequence:1][array:N][AdditionalSize:1][LinkedAvatarIndex:1][Additional...]
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

            int totalSize = 2 + 1 + 1 + expectedPayload + additionalSize;

            if (state.SerializedKeyframe[qi] == null || state.SerializedKeyframe[qi].Length < totalSize)
            {
                state.SerializedKeyframe[qi] = new byte[totalSize];
            }

            NetDataWriter writer = GetThreadWriter();
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
