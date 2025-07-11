using System;
using System.Threading;
using Basis.Network.Core;
using Basis.Scripts.Networking.Compression;
using LiteNetLib;
using LiteNetLib.Utils;
using static LockedBoolArray;
using static SerializableBasis;
/// <summary>
/// Structure to synchronize data with a specific player.
/// </summary>
public class SyncedToPlayerPulse
{
    // The player ID to which the data is being sent
    // public NetPeer playerID;
    public ServerSideSyncPlayerMessage lastPlayerInformation;
    public Vector3 Position;

    public LockedBoolArray SyncBoolArray = new LockedBoolArray();
    public LockedServerSideReducablePlayerArray ChunkedServerSideReducablePlayerArray = new LockedServerSideReducablePlayerArray();


    public static int BSRSMillisecondDefaultInterval = 50;
    public static byte ByteBSRSMillisecondDefaultInterval;
    public static int BSRBaseMultiplier = 1;
    public static float BSRSIncreaseRate = 0.005f;
    /// <summary>
    /// Supply new data to a specific player.
    /// </summary>
    /// <param name="playerID">The ID of the player</param>
    /// <param name="serverSideSyncPlayerMessage">The message to be synced</param>
    /// <param name="serverSidePlayer"></param>
    public void SupplyNewData(NetPeer playerID, ServerSideSyncPlayerMessage serverSideSyncPlayerMessage, NetPeer serverSidePlayer, Vector3 Position)
    {
        ServerSideReducablePlayer playerData = ChunkedServerSideReducablePlayerArray.GetPlayer(serverSidePlayer.Id);
        if (playerData != null)
        {
            // Update the player's message
            playerData.serverSideSyncPlayerMessage = serverSideSyncPlayerMessage;
            playerData.Position = Position;
            ChunkedServerSideReducablePlayerArray.SetPlayer(serverSidePlayer.Id, playerData);
        }
        else
        {
            ServerReductionClientPayload clientPayload = new ServerReductionClientPayload
            {
                localClient = playerID,
                dataCameFromThisUser = serverSidePlayer.Id
            };
            serverSideSyncPlayerMessage.interval = ByteBSRSMillisecondDefaultInterval;
            ServerSideReducablePlayer newPlayer = new ServerSideReducablePlayer
            {
                serverSideSyncPlayerMessage = serverSideSyncPlayerMessage,
                timer = new Timer(SendPlayerData, clientPayload, BSRSMillisecondDefaultInterval, BSRSMillisecondDefaultInterval),
                Writer = new NetDataWriter(true, 204),
                Position = Position,
            };
            SendPlayerData(clientPayload);
            ChunkedServerSideReducablePlayerArray.SetPlayer(serverSidePlayer.Id, newPlayer);
        }
        SyncBoolArray.SetBool(serverSidePlayer.Id, true);
    }
    /// <summary>
    /// Callback function to send player data at regular intervals.
    /// </summary>
    /// <param name="state">The player ID (passed from the timer)</param>
    private void SendPlayerData(object state)
    {
        ServerReductionClientPayload playerID = (ServerReductionClientPayload)state;
        if (SyncBoolArray.GetBool(playerID.dataCameFromThisUser))
        {
            SyncBoolArray.SetBool(playerID.dataCameFromThisUser, false);

            ServerSideReducablePlayer playerData = ChunkedServerSideReducablePlayerArray.GetPlayer(playerID.dataCameFromThisUser);
            if (playerData != null)
            {
                SyncedToPlayerPulse pulse = BasisServerReductionSystem.PlayerSync.GetPulse(playerID.localClient.Id);
                if (pulse != null)
                {
                    try
                    {
                        // Calculate the distance between the two points
                        float activeDistance = (pulse.Position - playerData.Position).SquaredMagnitude();

                        int adjustedInterval = (int)(BSRSMillisecondDefaultInterval * (BSRBaseMultiplier + (activeDistance * BSRSIncreaseRate)));
                        if (adjustedInterval > byte.MaxValue)
                        {
                            adjustedInterval = byte.MaxValue;
                        }
                        byte ByteAdjusted = (byte)adjustedInterval;
                        if (playerData.LastInterval != ByteAdjusted)
                        {
                            playerData.LastInterval = ByteAdjusted;
                            //  Console.WriteLine("Adjusted Interval is" + adjustedInterval);
                            playerData.timer.Change(adjustedInterval, adjustedInterval);
                            //how long does this data need to last for
                        }
                        playerData.serverSideSyncPlayerMessage.interval = ByteAdjusted;

                        int Size = playerID.localClient.GetPacketsCountInQueue(BasisNetworkCommons.PlayerAvatarChannel, DeliveryMethod.Sequenced);
                        if (Size < NetworkServer.MaxMessages && playerData.Writer != null)
                        {
                            playerData.serverSideSyncPlayerMessage.Serialize(playerData.Writer);
                            NetworkServer.SendOutValidated(playerID.localClient, playerData.Writer, BasisNetworkCommons.PlayerAvatarChannel, DeliveryMethod.Sequenced);
                            playerData.Writer.Reset();
                        }
                        else
                        {
                       //     BNL.LogError("Max Message Reached ");
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
        }
    }
}
