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

        // Who needs updates FROM whom (bitset indexed by player id)
        public FastBitSet HasNewDataFrom;

        // Base message shell (we swap avatarSerialization before send)
        public ServerSideSyncPlayerMessage SyncMessage;

        // Per-target last sent tick (used by distance-based interval logic)
        public Dictionary<int, long> LastSentTimes = new();

        // Cached per-quality payloads (payload bytes only, plus DataQualityLevel)
        public LocalAvatarSyncMessage AvatarHigh;
        public LocalAvatarSyncMessage AvatarMedium;
        public LocalAvatarSyncMessage AvatarLow;
        public LocalAvatarSyncMessage AvatarVeryLow;
    }
    public partial class BasisServerReductionSystemEvents
    {
        private static readonly CancellationTokenSource cts = new();
        private static readonly int MaxConcurrentPlayers = 1024;

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

        private static List<(int id, PlayerState state)> _threadLocalActivePlayers = new();
        public static readonly ConcurrentQueue<NetDataWriter> WriterPool = new();
        private static readonly ConcurrentQueue<int> playersToRemove = new();

        // Distance → Quality thresholds (squared meters)
        // Tune these however you like.
        public static float HighDistanceSq = 9f;        // 3m
        public static float MediumDistanceSq = 100f;    // 10m
        public static float LowDistanceSq = 400f;       // 20m
        // else => Low

        static BasisServerReductionSystemEvents()
        {
            _ = StartBackgroundProcessingAsync(); // fire-and-forget async background task
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

                // Snapshot messages safely
                var messagesSnapshot = new List<QueuedMessage>(currentMessages.Count);
                foreach (var kvp in currentMessages)
                {
                    if (currentMessages.TryRemove(kvp.Key, out var msg))
                    {
                        messagesSnapshot.Add(msg);
                    }
                }

                // Process messages (also adds players)
                Parallel.ForEach(messagesSnapshot, parallelOptions, msg =>
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
                long elapsedMs = (long)(elapsedTicks / MsToTick);
                long remainingMs = intervalMs - elapsedMs;

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

                    foreach (var kvp in playerStates)
                    {
                        var state = kvp.Value;
                        lock (state)
                        {
                            state.HasNewDataFrom?.Set(id, false);
                            state.LastSentTimes?.Remove(id);
                        }
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
            _threadLocalActivePlayers.Clear();
            foreach (var kvp in playerStates)
            {
                if (kvp.Value.IsActive)
                {
                    _threadLocalActivePlayers.Add((kvp.Key, kvp.Value));
                }
            }

            int playerCount = _threadLocalActivePlayers.Count;

            Parallel.ForEach(_threadLocalActivePlayers, parallelOptions, playerI =>
            {
                var stateI = playerI.state;
                var peer = stateI.Peer;

                bool canSend = peer.GetPacketsCountInQueue(BasisNetworkCommons.PlayerAvatarChannel, DeliveryMethod.Sequenced) < 2048;
                var sentTimes = stateI.LastSentTimes;

                for (int index = 0; index < playerCount; index++)
                {
                    (int id, PlayerState state) playerJ = _threadLocalActivePlayers[index];
                    if (playerI.id == playerJ.id)
                    {
                        continue;
                    }

                    var stateJ = playerJ.state;

                    float distSq = DistanceSquared(stateI.Position, stateJ.Position);

                    CalculateIntervalFromDistanceSq(distSq, out byte startAtZeroInterval, out int actualInterval);

                    if (!sentTimes.ContainsKey(playerJ.id))
                    {
                        sentTimes[playerJ.id] = 0;
                    }

                    if (stateI.HasNewDataFrom == null)
                    {
                        continue;
                    }

                    long lastSent = sentTimes[playerJ.id];
                    long elapsed = nowTicks - lastSent;
                    elapsed = Math.Max(0, elapsed);

                    long required = (long)(actualInterval * MsToTick);

                    bool hasNewData = stateI.HasNewDataFrom.Get(playerJ.id);

                    if (canSend && hasNewData && elapsed >= required)
                    {
                        stateI.HasNewDataFrom.Set(playerJ.id, false);

                        // Pick quality by distance
                        LocalAvatarSyncMessage chosen;
                        if (distSq <= HighDistanceSq)
                        {
                            chosen = stateJ.AvatarHigh;
                        }
                        else if (distSq <= MediumDistanceSq)
                        {
                            chosen = stateJ.AvatarMedium;
                        }
                        else if (distSq <= LowDistanceSq)
                        {
                            chosen = stateJ.AvatarLow;
                        }
                        else
                        {
                            chosen = stateJ.AvatarVeryLow;
                        }

                        // Build outgoing message (swap just the avatar payload)
                        ServerSideSyncPlayerMessage tempMsg = stateJ.SyncMessage;
                        tempMsg.interval = startAtZeroInterval;
                        tempMsg.avatarSerialization = chosen;

                        SendOutFull(peer, tempMsg);
                        sentTimes[playerJ.id] = nowTicks;
                    }
                }
            });
        }

        private static void SendOutFull(NetPeer peer, ServerSideSyncPlayerMessage msg)
        {
            NetDataWriter writer = RentWriter();

            msg.Serialize(writer);

            peer.Send(writer, BasisNetworkCommons.PlayerAvatarChannel, DeliveryMethod.Sequenced);
            BasisNetworkStatistics.RecordOutbound(BasisNetworkCommons.PlayerAvatarChannel, writer.Length);

            ReturnWriter(writer);
        }

        public static NetDataWriter RentWriter()
        {
            // 208 was your original; keep it or increase if you add more fields.
            return WriterPool.TryDequeue(out var writer) ? writer : new NetDataWriter(true, 208);
        }

        public static void ReturnWriter(NetDataWriter writer)
        {
            writer.Reset();
            WriterPool.Enqueue(writer);
        }

        private static float DistanceSquared(Basis.Scripts.Networking.Compression.Vector3 a, Basis.Scripts.Networking.Compression.Vector3 b)
        {
            float dx = a.x - b.x;
            float dy = a.y - b.y;
            float dz = a.z - b.z;
            return dx * dx + dy * dy + dz * dz;
        }

        /// <summary>
        /// Calculates the offset byte and the actual interval from the squared distance.
        /// </summary>
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

        private static void ProcessMessage(QueuedMessage message)
        {
            int id = message.FromPeer.Id;

            // Incoming payloads are expected to be High; enforce if you want:
            var high = message.AvatarMessage;

            if (high.DataQualityLevel != (byte)BitQuality.High)
            {
                BNL.LogError($"Quality Level was {high.DataQualityLevel}");
                high.DataQualityLevel = (byte)BitQuality.High;
            }

            // Position is the first 12 bytes (your simplified layout)
            var pos = BasisNetworkCompressionExtensions.ReadPosition(ref high.array);

            // Build derived qualities from packed high (no floats)
            LocalAvatarSyncMessage medium;
            LocalAvatarSyncMessage low;
            LocalAvatarSyncMessage veryLow;

            try
            {
                (medium, low, veryLow) = AvatarQualityRepacker.BuildAllLowerFromHigh(high);
            }
            catch (Exception ex)
            {
                // If something goes wrong, fall back to sending high only
                BNL.LogError($"[ProcessMessage] Repack failed: {ex}");

                medium = high;
                low = high;
                veryLow = high;
            }

            if (!playerStates.TryGetValue(id, out var state))
            {
                state = new PlayerState
                {
                    Peer = message.FromPeer,
                    IsActive = true,
                    Position = pos,
                    HasNewDataFrom = new FastBitSet(MaxConcurrentPlayers),
                    SyncMessage = new ServerSideSyncPlayerMessage
                    {
                        playerIdMessage = new PlayerIdMessage { playerID = (ushort)id },
                        avatarSerialization = high
                    },
                    AvatarHigh = high,
                    AvatarMedium = medium,
                    AvatarLow = low,
                    AvatarVeryLow = veryLow,
                };

                state.HasNewDataFrom.SetAll(true);
                playerStates[id] = state;

                // Everyone else needs new data from this new player
                foreach (var kvp in playerStates)
                {
                    if (kvp.Key == id || !kvp.Value.IsActive)
                    {
                        continue;
                    }

                    kvp.Value.HasNewDataFrom.Set(id, true);
                }
            }
            else
            {
                if (!state.IsActive)
                    state.IsActive = true;

                state.Position = pos;

                // Update cached payloads
                state.AvatarHigh = high;
                state.AvatarMedium = medium;
                state.AvatarLow = low;
                state.AvatarVeryLow = veryLow;

                // Keep SyncMessage in sync (shell)
                state.SyncMessage.avatarSerialization = high;

                // Mark all other players as having new data FROM this sender (id)
                foreach (var kvp in playerStates)
                {
                    if (kvp.Key == id)
                    {
                        continue;
                    }

                    var other = kvp.Value;
                    if (!other.IsActive)
                    {
                        continue;
                    }

                    other.HasNewDataFrom?.Set(id, true);
                }
            }

            QueuedMessagePool.Return(message);
        }
    }
}
