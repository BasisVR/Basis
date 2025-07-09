using Basis.Network.Core;
using Basis.Scripts.Networking.Compression;
using LiteNetLib;
using static SerializableBasis;
public class BasisServerReductionSystem
{
    public static ChunkedSyncedToPlayerPulseArray PlayerSync = new ChunkedSyncedToPlayerPulseArray(256);
    /// <summary>
    /// add the new client
    /// then update all existing clients arrays
    /// </summary>
    /// <param name="playerID"></param>
    /// <param name="playerToUpdate"></param>
    /// <param name="Frompeer"></param>
    public static void AddOrUpdatePlayer(Vector3 Position, NetPeer playerID, ServerSideSyncPlayerMessage playerToUpdate, NetPeer Frompeer)
    {
        SyncedToPlayerPulse OtherPulse = PlayerSync.GetPulse(playerID.Id);
        //ok now we can try to schedule sending out this data!
        if (OtherPulse != null)
        {
            // Update the player's message
            OtherPulse.SupplyNewData(playerID, playerToUpdate, Frompeer, Position);
        }
        else
        {
            //first time request create said data!
            OtherPulse = new SyncedToPlayerPulse
            {
                lastPlayerInformation = playerToUpdate,
                Position = Position,
            };
            PlayerSync.SetPulse(playerID.Id, OtherPulse);
            OtherPulse.SupplyNewData(playerID, playerToUpdate, Frompeer, Position);
        }
    }
    public static void RemovePlayer(NetPeer playerID)
    {
        int playerIndex = playerID.Id;
        SyncedToPlayerPulse targetPulse = PlayerSync.GetPulse(playerIndex);

        // Clear the target player's pulse and dispose their associated timers
        PlayerSync.SetPulse(playerIndex, null);

        if (targetPulse != null)
        {
            ClearReducablePlayers(targetPulse);
        }

        // Ensure all other pulses remove any references to this player
        for (int Index = 0; Index < BasisNetworkCommons.MaxConnections; Index++)
        {
            SyncedToPlayerPulse otherPulse = PlayerSync.GetPulse(Index);
            if (otherPulse != null)
            {
                ServerSideReducablePlayer playerRef = otherPulse.ChunkedServerSideReducablePlayerArray.GetPlayer(playerIndex);
                if (playerRef != null)
                {
                    playerRef.timer.Dispose();
                    otherPulse.ChunkedServerSideReducablePlayerArray.SetPlayer(playerIndex, null);
                }
            }
        }
    }

    /// <summary>
    /// Disposes all ServerSideReducablePlayer timers and clears references from the given pulse.
    /// </summary>
    private static void ClearReducablePlayers(SyncedToPlayerPulse pulse)
    {
        for (int Index = 0; Index < BasisNetworkCommons.MaxConnections; Index++)
        {
            ServerSideReducablePlayer player = pulse.ChunkedServerSideReducablePlayerArray.GetPlayer(Index);
            if (player != null)
            {
                player.timer.Dispose();
                pulse.ChunkedServerSideReducablePlayerArray.SetPlayer(Index, null);
            }
        }
    }
}
