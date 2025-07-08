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
        SyncedToPlayerPulse playerData = PlayerSync.GetPulse(serverSideSyncPlayer.Id);
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
}
