using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Basis.Network.Core;
using Basis.Network.Core.Compression;
using Basis.Scripts.Networking.Compression;
using LiteNetLib;
using LiteNetLib.Utils;
using static SerializableBasis;
public partial class BasisServerReductionSystem
{
    // Default interval in milliseconds for the timer
    public static Configuration Configuration;
    public static ChunkedSyncedToPlayerPulseArray PlayerSync = new ChunkedSyncedToPlayerPulseArray(64);
    public static int MaxMessages = 500;
    public static Timer DistanceCalculator;
    private static CancellationTokenSource cancellationTokenSource;
    private static Task loopTask;
    public static void StartLoop()
    {
        if (loopTask == null || loopTask.IsCompleted)
        {
            cancellationTokenSource = new CancellationTokenSource();
            loopTask = Task.Run(() => LoopAsync(cancellationTokenSource.Token));
            HighResolutionScheduler.Start();
        }
    }
    public static void StopLoop()
    {
        cancellationTokenSource?.Cancel();
    }
    /// <summary>
    /// add the new client
    /// then update all existing clients arrays
    /// </summary>
    /// <param name="playerID"></param>
    /// <param name="playerToUpdate"></param>
    /// <param name="serverSideSyncPlayer"></param>
    public static void AddOrUpdatePlayer(NetPeer playerID, ServerSideSyncPlayerMessage playerToUpdate, NetPeer serverSideSyncPlayer)
    {
        SyncedToPlayerPulse playerData = PlayerSync.GetPulse(serverSideSyncPlayer.Id);
        //stage 1 lets update whoever send us this datas last player information
        Vector3 Data = BasisNetworkCompressionExtensions.DecompressAndProcessAvatarFaster(playerToUpdate);
        if (playerData != null)
        {
            playerData.lastPlayerInformation = playerToUpdate;
            playerData.Position = Data;
        }
        playerData = PlayerSync.GetPulse(playerID.Id);
        //ok now we can try to schedule sending out this data!
        if (playerData != null)
        {
            // Update the player's message
            playerData.SupplyNewData(playerID, playerToUpdate, serverSideSyncPlayer);
        }
        else
        {
            //first time request create said data!
            playerData = new SyncedToPlayerPulse
            {
                //   playerID = playerID,
                lastPlayerInformation = playerToUpdate,
                Position = BasisNetworkCompressionExtensions.DecompressAndProcessAvatarFaster(playerToUpdate),
            };
            PlayerSync.SetPulse(playerID.Id, playerData);
            playerData.SupplyNewData(playerID, playerToUpdate, serverSideSyncPlayer);
        }
    }
    public static void RemovePlayer(NetPeer playerID)
    {
        SyncedToPlayerPulse Pulse = PlayerSync.GetPulse(playerID.Id);
        PlayerSync.SetPulse(playerID.Id, null);
        if (Pulse != null)
        {
            for (int Index = 0; Index < BasisNetworkCommons.MaxConnections; Index++)
            {
                ServerSideReducablePlayer player = Pulse.ChunkedServerSideReducablePlayerArray.GetPlayer(Index);
                if (player != null)
                {
                    //   player.timer.Dispose();
                    HighResolutionScheduler.RemoveEvent(player.timer);
                    Pulse.ChunkedServerSideReducablePlayerArray.SetPlayer(Index, null);
                }
            }
        }
        for (int Index = 0; Index < BasisNetworkCommons.MaxConnections; Index++)
        {
            SyncedToPlayerPulse player = PlayerSync.GetPulse(Index);
            if (player != null)
            {
                ServerSideReducablePlayer SSRP = player.ChunkedServerSideReducablePlayerArray.GetPlayer(Index);
                if (SSRP != null)
                {
                    // SSRP.timer.Dispose();
                    HighResolutionScheduler.RemoveEvent(SSRP.timer);
                    Pulse.ChunkedServerSideReducablePlayerArray.SetPlayer(Index, null);
                }
            }
        }
    }
    /// <summary>
    /// Structure to synchronize data with a specific player.
    /// </summary>
    public partial class SyncedToPlayerPulse
    {
        // The player ID to which the data is being sent
        // public NetPeer playerID;
        public ServerSideSyncPlayerMessage lastPlayerInformation;
        public Vector3 Position;
        /// <summary>
        /// Dictionary to hold queued messages for each player.
        /// Key: Player ID, Value: Server-side player data
        /// </summary>
        public ChunkedBoolArray SyncBoolArray = new ChunkedBoolArray(64);
        public ChunkedServerSideReducablePlayerArray ChunkedServerSideReducablePlayerArray = new ChunkedServerSideReducablePlayerArray(64);
        /// <summary>
        /// Supply new data to a specific player.
        /// </summary>
        /// <param name="playerID">The ID of the player</param>
        /// <param name="serverSideSyncPlayerMessage">The message to be synced</param>
        /// <param name="serverSidePlayer"></param>
        public void SupplyNewData(NetPeer playerID, ServerSideSyncPlayerMessage serverSideSyncPlayerMessage, NetPeer serverSidePlayer)
        {
            ServerSideReducablePlayer playerData = ChunkedServerSideReducablePlayerArray.GetPlayer(serverSidePlayer.Id);
            if (playerData != null)
            {
                // Update the player's message
                playerData.serverSideSyncPlayerMessage.playerIdMessage = serverSideSyncPlayerMessage.playerIdMessage;
                playerData.serverSideSyncPlayerMessage.avatarSerialization = serverSideSyncPlayerMessage.avatarSerialization;
                playerData.Position = BasisNetworkCompressionExtensions.DecompressAndProcessAvatarFaster(serverSideSyncPlayerMessage);
                SyncBoolArray.SetBool(serverSidePlayer.Id, true);
                ChunkedServerSideReducablePlayerArray.SetPlayer(serverSidePlayer.Id, playerData);
            }
            else
            {
                // If the player doesn't exist, add them with default settings
                AddPlayer(playerID, serverSideSyncPlayerMessage, serverSidePlayer);
            }
        }

        /// <summary>
        /// Adds a new player to the queue with a default timer and settings.
        /// </summary>
        /// <param name="playerID">The ID of the player</param>
        /// <param name="serverSideSyncPlayerMessage">The initial message to be sent</param>
        /// <param name="serverSidePlayer"></param>
        public void AddPlayer(NetPeer playerID, ServerSideSyncPlayerMessage serverSideSyncPlayerMessage, NetPeer serverSidePlayer)
        {
            ClientPayload clientPayload = new ClientPayload
            {
                localClient = playerID,
                dataCameFromThisUser = serverSidePlayer.Id
            };

            Guid playerSyncJob = HighResolutionScheduler.AddEvent(() =>
            {
                SendPlayerData(clientPayload);
                // Send player data here
            }, Configuration.BSRSMillisecondDefaultInterval); // Run every 50ms


            ServerSideReducablePlayer newPlayer = new ServerSideReducablePlayer
            {
                serverSideSyncPlayerMessage = serverSideSyncPlayerMessage,
                //    timer = new Timer(SendPlayerData, clientPayload, Configuration.BSRSMillisecondDefaultInterval, Configuration.BSRSMillisecondDefaultInterval),
                timer = playerSyncJob,
                Writer = new NetDataWriter(true, 204),
                Position = BasisNetworkCompressionExtensions.DecompressAndProcessAvatarFaster(serverSideSyncPlayerMessage),
            };
            serverSideSyncPlayerMessage.interval = (byte)Configuration.BSRSMillisecondDefaultInterval;
            SendPlayerData(clientPayload);
            SyncBoolArray.SetBool(serverSidePlayer.Id, true);
            ChunkedServerSideReducablePlayerArray.SetPlayer(serverSidePlayer.Id, newPlayer);
        }
        /// <summary>
        /// Callback function to send player data at regular intervals.
        /// </summary>
        /// <param name="state">The player ID (passed from the timer)</param>
        private void SendPlayerData(object state)
        {
            ClientPayload playerID = (ClientPayload)state;
            if (SyncBoolArray.GetBool(playerID.dataCameFromThisUser))
            {
                ServerSideReducablePlayer playerData = ChunkedServerSideReducablePlayerArray.GetPlayer(playerID.dataCameFromThisUser);
                if (playerData != null)
                {
                    try
                    {
                        int Size = playerID.localClient.GetPacketsCountInQueue(BasisNetworkCommons.MovementChannel, DeliveryMethod.Sequenced);
                        if (Size < MaxMessages)
                        {
                            playerData.serverSideSyncPlayerMessage.Serialize(playerData.Writer);
                            NetworkServer.SendOutValidated(playerID.localClient, playerData.Writer, BasisNetworkCommons.MovementChannel, DeliveryMethod.Sequenced);
                            playerData.Writer.Reset();
                        }
                    }
                    catch (Exception e)
                    {
                        if (e.InnerException != null)
                        {
                            BNL.LogError($"SRS Encounter Issue {e.Message} {e.StackTrace} {e.InnerException}");
                        }
                        else
                        {
                            BNL.LogError($"SRS Encounter Issue {e.Message} {e.StackTrace}");
                        }
                    }
                }
                SyncBoolArray.SetBool(playerID.dataCameFromThisUser, false);
            }
        }
    }
    private static async Task LoopAsync(CancellationToken token)
    {
        float baseInterval = Configuration.BSRSMillisecondDefaultInterval;//50
        float increaseRate = Configuration.BSRSIncreaseRate;//0.005f
        while (!token.IsCancellationRequested)
        {
            try
            {
                int count = PlayerSync.ActiveChunkCount;
                for (int topIndex = 0; topIndex < count; topIndex++)
                {
                    SyncedToPlayerPulse topPulse = PlayerSync.GetPulse(topIndex);
                    if (topPulse == null)
                    {
                        continue;
                    }

                    for (int bottomIndex = 0; bottomIndex < count; bottomIndex++)
                    {
                        if (topIndex == bottomIndex)
                        {
                            continue;
                        }

                        SyncedToPlayerPulse bottomPulse = PlayerSync.GetPulse(bottomIndex);
                        if (bottomPulse == null)
                        {
                            continue;
                        }

                        float activeDistance = SquaredDistance(topPulse.Position, bottomPulse.Position);

                        int adjustedInterval = (int)(baseInterval * (activeDistance * increaseRate));

                        if (adjustedInterval > byte.MaxValue)
                        {
                            adjustedInterval = byte.MaxValue;
                        }

                        byte byteAdjusted = (byte)adjustedInterval;

                        ServerSideReducablePlayer playerData = topPulse.ChunkedServerSideReducablePlayerArray.GetPlayer(bottomIndex);
                        if (playerData != null && playerData.serverSideSyncPlayerMessage.interval != byteAdjusted)
                        {
                            HighResolutionScheduler.ChangeInterval(playerData.timer, adjustedInterval);
                            playerData.serverSideSyncPlayerMessage.interval = byteAdjusted;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                if (e.InnerException != null)
                {
                    BNL.LogError($"SRS Loop Encountered Issue: {e.Message} {e.StackTrace} {e.InnerException}");
                }
                else
                {
                    BNL.LogError($"SRS Loop Encountered Issue: {e.Message} {e.StackTrace}");
                }
            }
            await Task.Delay(33, token);
        }
    }
    public static float SquaredDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dy = a.y - b.y;
        float dz = a.z - b.z;
        return dx * dx + dy * dy + dz * dz;
    }
    /// <summary>
    /// Structure representing a player's server-side data that can be reduced.
    /// </summary>
    public class ServerSideReducablePlayer
    {
        public Guid timer;//create a new timer
        public ServerSideSyncPlayerMessage serverSideSyncPlayerMessage;
        public NetDataWriter Writer;
        public Vector3 Position;
    }
    public class HighResolutionScheduler
    {
        private class ScheduledItem
        {
            public Guid Id;
            public Action Callback;
            public int IntervalMs;
            public long NextTick;
        }

        private static readonly ConcurrentDictionary<Guid, ScheduledItem> scheduledItems = new();
        private static readonly ConcurrentQueue<ScheduledItem> toAdd = new();
        private static readonly ConcurrentQueue<Guid> toRemove = new();

        private static Thread schedulerThread;
        private static bool running;
        private static Stopwatch stopwatch;
        public static void Start()
        {
            if (running) return;

            running = true;
            stopwatch = Stopwatch.StartNew();
            schedulerThread = new Thread(RunLoop)
            {
                IsBackground = true,
                Priority = ThreadPriority.Highest
            };
            schedulerThread.Start();
        }

        public static void Stop()
        {
            running = false;
            schedulerThread?.Join();
        }

        public static Guid AddEvent(Action callback, int intervalMs)
        {
            var item = new ScheduledItem
            {
                Id = Guid.NewGuid(),
                Callback = callback,
                IntervalMs = intervalMs,
                NextTick = stopwatch.ElapsedMilliseconds + intervalMs
            };

            toAdd.Enqueue(item);
            return item.Id;
        }

        public static void RemoveEvent(Guid id)
        {
            toRemove.Enqueue(id);
        }

        public static void ChangeInterval(Guid id, int newInterval)
        {
            if (scheduledItems.TryGetValue(id, out var item))
            {
                item.IntervalMs = newInterval;
                item.NextTick = stopwatch.ElapsedMilliseconds + newInterval;
            }
        }

        private static void RunLoop()
        {
            while (running)
            {
                long now = stopwatch.ElapsedMilliseconds;

                // Add new events
                while (toAdd.TryDequeue(out var item))
                {
                    scheduledItems[item.Id] = item;
                }

                // Remove events
                while (toRemove.TryDequeue(out var id))
                {
                    scheduledItems.TryRemove(id, out _);
                }

                int processed = 0;

                foreach (var kvp in scheduledItems)
                {
                    var item = kvp.Value;
                    if (now >= item.NextTick)
                    {
                        try
                        {
                            item.Callback?.Invoke();
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[Scheduler] Exception: {ex}");
                        }

                        item.NextTick = now + item.IntervalMs;
                        processed++;
                    }
                }
            }
        }
    }
}
