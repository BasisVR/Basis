using System;
using System.Threading;
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
    public static int MaxMessages = 80;
    /// <summary>
    /// add the new client
    /// then update all existing clients arrays
    /// </summary>
    /// <param name="playerID"></param>
    /// <param name="playerToUpdate"></param>
    /// <param name="serverSideSyncPlayer"></param>
    public static void AddOrUpdatePlayer(NetPeer playerID, ServerSideSyncPlayerMessage playerToUpdate, NetPeer serverSideSyncPlayer)
    {
        SyncedToPlayerPulse playerData =  PlayerSync.GetPulse(serverSideSyncPlayer.Id);
        Vector3 Position = BasisNetworkCompressionExtensions.DecompressAndProcessAvatarFaster(playerToUpdate);
        //stage 1 lets update whoever send us this datas last player information
        if (playerData != null)
        {
            playerData.lastPlayerInformation = playerToUpdate;
            playerData.Position = Position;
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
                Position = Position,
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
                    player.timer.Dispose();
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
                    SSRP.timer.Dispose();
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
                playerData.serverSideSyncPlayerMessage = serverSideSyncPlayerMessage;
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
            serverSideSyncPlayerMessage.interval = (byte)Configuration.BSRSMillisecondDefaultInterval;
            ServerSideReducablePlayer newPlayer = new ServerSideReducablePlayer
            {
                serverSideSyncPlayerMessage = serverSideSyncPlayerMessage,
                timer = new Timer(SendPlayerData, clientPayload, Configuration.BSRSMillisecondDefaultInterval, Configuration.BSRSMillisecondDefaultInterval),
                Writer = new NetDataWriter(true, 204),
                Position = BasisNetworkCompressionExtensions.DecompressAndProcessAvatarFaster(serverSideSyncPlayerMessage),
            };
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
                    SyncedToPlayerPulse pulse = PlayerSync.GetPulse(playerID.localClient.Id);
                    if (pulse != null)
                    {
                        try
                        {
                            // Calculate the distance between the two points
                            float activeDistance = Distance(pulse.Position, playerData.Position);
                            // Adjust the timer interval based on the new syncRateMultiplier
                            //wherer BSRSMillisecondDefaultInterval is the interval rate we send to players (fastest possible)
                            //second part of the operation is adding latency to reduce sendout.
                            //BSRBaseMultiplier = 1
                            // activeDistance 5m
                            //0.005f
                            //   1 * 0.025  = 1 + (5 * 005f);
                            // 50 * 1 * 0.025
                            int adjustedInterval = (int)(Configuration.BSRSMillisecondDefaultInterval * (Configuration.BSRBaseMultiplier + (activeDistance * Configuration.BSRSIncreaseRate)));
                            if (adjustedInterval > byte.MaxValue)
                            {
                                adjustedInterval = byte.MaxValue;
                            }
                            byte ByteAdjusted = (byte)adjustedInterval;
                            if (playerData.serverSideSyncPlayerMessage.interval != ByteAdjusted)
                            {
                                //  Console.WriteLine("Adjusted Interval is" + adjustedInterval);
                                playerData.timer.Change(adjustedInterval, adjustedInterval);
                                //how long does this data need to last for
                                playerData.serverSideSyncPlayerMessage.interval = ByteAdjusted;
                            }
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
                }
                SyncBoolArray.SetBool(playerID.dataCameFromThisUser, false);
            }
        }
    }
    public static float Distance(Vector3 pointA, Vector3 pointB)
    {
        return (pointB - pointA).SquaredMagnitude(); // Avoid intermediate objects if possible
    }
}
