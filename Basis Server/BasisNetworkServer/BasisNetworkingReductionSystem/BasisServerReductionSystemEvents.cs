using Basis.Network.Core;
using Basis.Network.Core.Compression;
using BasisNetworkServer.BasisNetworking;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using static BasisNetworkServer.BasisNetworkingReductionSystem.BasisServerReductionSystemEvents;
using static SerializableBasis;
using static Basis.Network.Core.Compression.BasisAvatarBitPacking;

namespace BasisNetworkServer.BasisNetworkingReductionSystem
{
    public class QueuedMessage
    {
        public NetPeer FromPeer;
        public LocalAvatarSyncMessage AvatarMessage;
    }

    public class PlayerState
    {
        public NetPeer Peer;
        public bool IsActive;

        // Used for distance decisions
        public Basis.Scripts.Networking.Compression.Vector3 Position;

        // Base message shell (we swap avatarSerialization before send)
        public ServerSideSyncPlayerMessage SyncMessage;

        // Per-target last sent tick — flat array indexed by player id for O(1) access
        public long[] LastSentTimes;

        // Generation counter: incremented each time this player receives new avatar data.
        // Receivers compare against their LastSeenGeneration to know if there is new data.
        // Access via Interlocked.Read/Increment for thread safety on 32-bit or cross-core visibility.
        public long DataGeneration;

        // Per-receiver: the DataGeneration value of each sender when we last sent to this receiver.
        // Indexed by sender player id.
        public long[] LastSeenGeneration;

        // Cached per-quality payloads (payload bytes only, plus DataQualityLevel)
        public LocalAvatarSyncMessage AvatarHigh;
        public LocalAvatarSyncMessage AvatarMedium;
        public LocalAvatarSyncMessage AvatarLow;
        public LocalAvatarSyncMessage AvatarVeryLow;

        // Pre-serialized keyframe bytes per quality [PlayerID:2][interval_placeholder:1][quality:1][array:N][additionalSize:1]
        // The interval byte at offset 2 is filled per-recipient in the send loop.
        public byte[][] SerializedKeyframe = new byte[4][];
        public int[] SerializedKeyframeLength = new int[4];
    }

    public partial class BasisServerReductionSystemEvents
    {
        private static readonly CancellationTokenSource cts = new();
        private static readonly int MaxConcurrentPlayers = ushort.MaxValue;

        // Initial capacity for flat arrays on PlayerState (LastSentTimes, LastSeenGeneration).
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

        private static readonly ConcurrentQueue<int> playersToRemove = new();

        // Reusable snapshot list for draining currentMessages each tick — avoids allocation per tick.
        private static readonly List<QueuedMessage> _messagesSnapshot = new(1024);

        // Distance -> Quality thresholds (squared meters)
        public static float HighDistanceSq = 9f;        // 3m
        public static float MediumDistanceSq = 100f;    // 10m
        public static float LowDistanceSq = 400f;       // 20m

        // Tick slicing: only process a subset of receivers each tick to spread the O(N²) work.
        // Adaptive: increases when ticks take too long, decreases when under budget.
        private static int _sliceCount = 1;
        private static int _sliceIndex = 0;

        // Thread-local NetDataWriter for serialization — eliminates shared pool contention.
        [ThreadStatic]
        private static NetDataWriter t_serializeWriter;

        static BasisServerReductionSystemEvents()
        {
            _ = StartBackgroundProcessingAsync();
        }

        public static void HandleAvatarMovement(NetPacketReader reader, NetPeer fromPeer)
        {
            var localMessage = new LocalAvatarSyncMessage();
            localMessage.Deserialize(reader);
            reader.Recycle();
            AddMessage(fromPeer, localMessage);
        }

        public static void AddMessage(NetPeer fromPeer, LocalAvatarSyncMessage localMessage)
        {
            var message = QueuedMessagePool.Rent();
            message.FromPeer = fromPeer;
            message.AvatarMessage = localMessage;
            currentMessages.AddOrUpdate(fromPeer.Id, message, (_, _) => message);
        }

        private static async Task StartBackgroundProcessingAsync()
        {
            long intervalMs = 2;

            while (!cts.Token.IsCancellationRequested)
            {
                long startTick = Stopwatch.GetTimestamp();

                // Snapshot messages safely — reuse list to avoid allocation
                _messagesSnapshot.Clear();
                foreach (var kvp in currentMessages)
                {
                    if (currentMessages.TryRemove(kvp.Key, out var msg))
                    {
                        _messagesSnapshot.Add(msg);
                    }
                }

                // Process messages (also adds players)
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

                ProcessPendingRemovals();

                // Network updates
                UpdateCommunicationAndDistances(Stopwatch.GetTimestamp());

                if (NetworkServer.Server != null && NetworkServer.Server.manager != null)
                {
                    NetworkServer.Server.manager.TriggerUpdate();
                }

                // Throttle loop if under time budget
                long elapsedTicks = Stopwatch.GetTimestamp() - startTick;
                double elapsedMs = elapsedTicks / MsToTick;
                long remainingMs = intervalMs - (long)elapsedMs;

                // Adaptive slice count: if tick took > 1.5ms, increase slicing; if < 0.5ms, decrease.
                if (elapsedMs > 1.5 && _sliceCount < 32)
                    _sliceCount++;
                else if (elapsedMs < 0.5 && _sliceCount > 1)
                    _sliceCount--;

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
                        if (id < otherState.LastSeenGeneration.Length)
                            otherState.LastSeenGeneration[id] = 0;
                        if (id < otherState.LastSentTimes.Length)
                            otherState.LastSentTimes[id] = 0;
                    }
                    BNL.Log($"Player {id} removed and cleaned up.");
                }
                else
                {
                    BNL.LogError("Missing Player From Index this is scary! " + id);
                }
            }
        }

        private static void UpdateCommunicationAndDistances(long nowTicks)
        {
            // Take a snapshot of active players under lock for thread-safe iteration.
            // The list is maintained incrementally, so this is just a copy.
            (int id, PlayerState state)[] activeCopy;
            lock (_activePlayersLock)
            {
                activeCopy = _activePlayers.ToArray();
            }

            int playerCount = activeCopy.Length;
            if (playerCount == 0) return;

            // Tick slicing: only process a slice of receivers per tick
            int sliceSize = (playerCount + _sliceCount - 1) / _sliceCount;
            int start = _sliceIndex * sliceSize;
            int end = Math.Min(start + sliceSize, playerCount);
            _sliceIndex = (_sliceIndex + 1) % _sliceCount;

            if (start >= playerCount) return;

            // Create a span/segment view for the slice to iterate
            Parallel.For(start, end, parallelOptions, i =>
            {
                var playerI = activeCopy[i];
                var stateI = playerI.state;
                var peer = stateI.Peer;

                // Congestion check
                int queueDepth = peer.GetPacketsCountInQueue(BasisNetworkCommons.PlayerAvatarChannel, DeliveryMethod.Sequenced);

                // Severe congestion — skip this peer entirely
                if (queueDepth > 512) return;

                // Graduated quality cap based on congestion
                int maxQi;
                int intervalMultiplier;
                if (queueDepth > 256)
                {
                    maxQi = 0;            // force VeryLow
                    intervalMultiplier = 2; // double intervals
                }
                else if (queueDepth > 128)
                {
                    maxQi = 1;            // cap at Low
                    intervalMultiplier = 1;
                }
                else if (queueDepth > 64)
                {
                    maxQi = 2;            // cap at Medium
                    intervalMultiplier = 1;
                }
                else
                {
                    maxQi = 3;            // normal quality
                    intervalMultiplier = 1;
                }

                var sentTimes = stateI.LastSentTimes;
                var seenGens = stateI.LastSeenGeneration;
                if (sentTimes == null || seenGens == null) return;

                for (int index = 0; index < playerCount; index++)
                {
                    var playerJ = activeCopy[index];
                    if (playerI.id == playerJ.id)
                    {
                        continue;
                    }

                    var stateJ = playerJ.state;
                    int jId = playerJ.id;

                    // Bounds check — grow arrays if needed (rare, only when IDs exceed capacity)
                    if (jId >= sentTimes.Length)
                    {
                        lock (stateI)
                        {
                            // Re-check after acquiring lock
                            if (jId >= stateI.LastSentTimes.Length)
                            {
                                int newLen = Math.Max(stateI.LastSentTimes.Length * 2, jId + 1);
                                Array.Resize(ref stateI.LastSentTimes, newLen);
                                Array.Resize(ref stateI.LastSeenGeneration, newLen);
                            }
                            sentTimes = stateI.LastSentTimes;
                            seenGens = stateI.LastSeenGeneration;
                        }
                    }

                    float distSq = DistanceSquared(stateI.Position, stateJ.Position);

                    CalculateIntervalFromDistanceSq(distSq, out byte startAtZeroInterval, out int actualInterval);

                    long lastSent = sentTimes[jId];
                    long elapsed = nowTicks - lastSent;
                    elapsed = Math.Max(0, elapsed);

                    long required = (long)((actualInterval * intervalMultiplier) * MsToTick);

                    // Generation-based new data check: compare sender's generation with what we last saw
                    long senderGen = Interlocked.Read(ref stateJ.DataGeneration);
                    bool hasNewData = senderGen > seenGens[jId];

                    if (hasNewData && elapsed >= required)
                    {
                        // Pick quality by distance, capped by congestion
                        int qi = Math.Min(GetQualityIndex(distSq), maxQi);

                        // Always send full keyframe
                        SendPreSerialized(peer, stateJ, qi, startAtZeroInterval,
                            BasisNetworkCommons.PlayerAvatarChannel,
                            stateJ.SerializedKeyframe, stateJ.SerializedKeyframeLength);

                        sentTimes[jId] = nowTicks;
                        seenGens[jId] = senderGen;
                    }
                }
            });
        }

        /// <summary>
        /// Sends a pre-serialized message, patching the interval byte at offset 2.
        /// Uses a thread-local writer to avoid shared pool contention.
        /// </summary>
        private static void SendPreSerialized(NetPeer peer, PlayerState sourceState, int qi,
            byte interval, byte channel, byte[][] serializedArray, int[] lengthArray)
        {
            int len = lengthArray[qi];
            byte[] src = serializedArray[qi];
            if (src == null || len == 0)
                return;

            NetDataWriter writer = GetThreadWriter();
            writer.Put(src, 0, len);

            // Patch interval byte at offset 2 (after PlayerID ushort)
            if (writer.Length > 2)
            {
                writer.Data[2] = interval;
            }

            peer.Send(writer, channel, DeliveryMethod.Sequenced);
            BasisNetworkStatistics.RecordOutbound(channel, writer.Length);
        }

        /// <summary>
        /// Returns a thread-local NetDataWriter, creating one if needed.
        /// Avoids contention on the shared ConcurrentQueue writer pool.
        /// </summary>
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
        private static int GetQualityIndex(float distSq)
        {
            if (distSq <= HighDistanceSq) return 3;   // High
            if (distSq <= MediumDistanceSq) return 2;  // Medium
            if (distSq <= LowDistanceSq) return 1;     // Low
            return 0;                                   // VeryLow
        }

        /// <summary>
        /// Gets the LocalAvatarSyncMessage for a given quality index.
        /// </summary>
        private static LocalAvatarSyncMessage GetAvatarForQuality(PlayerState state, int qi)
        {
            return qi switch
            {
                3 => state.AvatarHigh,
                2 => state.AvatarMedium,
                1 => state.AvatarLow,
                _ => state.AvatarVeryLow,
            };
        }

        public static NetDataWriter RentWriter()
        {
            return NetworkServer.RentWriter();
        }

        public static void ReturnWriter(NetDataWriter writer)
        {
            NetworkServer.ReturnWriter(writer);
        }

        private static float DistanceSquared(Basis.Scripts.Networking.Compression.Vector3 a, Basis.Scripts.Networking.Compression.Vector3 b)
        {
            float dx = a.x - b.x;
            float dy = a.y - b.y;
            float dz = a.z - b.z;
            return dx * dx + dy * dy + dz * dz;
        }

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
        /// Ensures a flat array is large enough for the given player id.
        /// </summary>
        private static void EnsureArrayCapacity(ref long[] array, int requiredIndex)
        {
            if (requiredIndex < array.Length) return;
            int newLen = Math.Max(array.Length * 2, requiredIndex + 1);
            Array.Resize(ref array, newLen);
        }

        private static void ProcessMessage(QueuedMessage message)
        {
            int id = message.FromPeer.Id;

            var high = message.AvatarMessage;

            if (high.DataQualityLevel != (byte)BitQuality.High)
            {
                BNL.LogError($"Quality Level was {high.DataQualityLevel}");
                high.DataQualityLevel = (byte)BitQuality.High;
            }

            var pos = BasisNetworkCompressionExtensions.ReadPosition(ref high.array);

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
                    LastSentTimes = new long[InitialPlayerArrayCapacity],
                    LastSeenGeneration = new long[InitialPlayerArrayCapacity],
                    DataGeneration = 1,
                };

                // Build lower qualities using the zero-alloc Into variant.
                // First call allocates the arrays; subsequent calls reuse them.
                try
                {
                    AvatarQualityRepacker.BuildAllLowerFromHighInto(
                        high, ref state.AvatarMedium, ref state.AvatarLow, ref state.AvatarVeryLow);
                }
                catch (Exception ex)
                {
                    BNL.LogError($"[ProcessMessage] Repack failed: {ex}");
                    state.AvatarMedium = high;
                    state.AvatarLow = high;
                    state.AvatarVeryLow = high;
                }

                // First frame: pre-serialize
                PreSerializeAll(state);

                playerStates[id] = state;

                // Add to active players list
                lock (_activePlayersLock)
                {
                    _activePlayers.Add((id, state));
                }
            }
            else
            {
                if (!state.IsActive)
                    state.IsActive = true;

                state.Position = pos;

                // Update high quality
                state.AvatarHigh = high;

                // Build lower qualities reusing existing buffers (zero-alloc after warmup)
                try
                {
                    AvatarQualityRepacker.BuildAllLowerFromHighInto(
                        high, ref state.AvatarMedium, ref state.AvatarLow, ref state.AvatarVeryLow);
                }
                catch (Exception ex)
                {
                    BNL.LogError($"[ProcessMessage] Repack failed: {ex}");
                    state.AvatarMedium = high;
                    state.AvatarLow = high;
                    state.AvatarVeryLow = high;
                }

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
        /// Pre-serializes keyframe messages for all 4 quality levels.
        /// The interval byte (offset 2) is left as 0 and patched per-recipient during send.
        /// Uses thread-local writers to avoid shared pool contention.
        /// </summary>
        private static void PreSerializeAll(PlayerState state)
        {
            ushort playerId = state.SyncMessage.playerIdMessage.playerID;

            PreSerializeKeyframe(state, 0, state.AvatarVeryLow, playerId);
            PreSerializeKeyframe(state, 1, state.AvatarLow, playerId);
            PreSerializeKeyframe(state, 2, state.AvatarMedium, playerId);
            PreSerializeKeyframe(state, 3, state.AvatarHigh, playerId);
        }

        private static void PreSerializeKeyframe(PlayerState state, int qi, LocalAvatarSyncMessage msg, ushort playerId)
        {
            if (msg.array == null) return;

            var quality = (BitQuality)msg.DataQualityLevel;
            int expectedPayload = BasisAvatarBitPacking.ConvertToSize(quality);

            // Skip if the array is undersized (e.g. client sent wrong quality level)
            if (msg.array.Length < expectedPayload)
            {
                BNL.LogError($"[PreSerializeKeyframe] Array undersized for quality {quality}: got {msg.array.Length}, need {expectedPayload}. Skipping.");
                return;
            }

            // [PlayerID:2][interval:1][quality:1][array:N][additionalSize:1][additional...]
            int additionalSize = 0;
            if (msg.AdditionalAvatarDatas != null && msg.AdditionalAvatarDatas.Length > 0)
            {
                additionalSize = 1; // LinkedAvatarIndex
                for (int i = 0; i < msg.AdditionalAvatarDatas.Length; i++)
                {
                    additionalSize += 1 + 1 + (msg.AdditionalAvatarDatas[i].array?.Length ?? 0); // PayloadSize + messageIndex + data
                }
            }

            int totalSize = 2 + 1 + 1 + expectedPayload + 1 + additionalSize;

            if (state.SerializedKeyframe[qi] == null || state.SerializedKeyframe[qi].Length < totalSize)
                state.SerializedKeyframe[qi] = new byte[totalSize];

            NetDataWriter writer = GetThreadWriter();
            writer.Put(playerId);
            writer.Put((byte)0); // interval placeholder
            writer.Put(msg.DataQualityLevel);
            writer.Put(msg.array, 0, expectedPayload);

            // Additional avatar data (from current msg)
            if (msg.AdditionalAvatarDatas == null || msg.AdditionalAvatarDatas.Length == 0 || msg.AdditionalAvatarDatas.Length > 256)
            {
                writer.Put((byte)0);
            }
            else
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
