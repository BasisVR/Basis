using Basis.Scripts.Networking;
using Basis.Scripts.Networking.Receivers;
using LiteNetLib;
using static SerializableBasis;
using System.Collections.Generic;
using Basis.Scripts.Networking.NetworkedAvatar;
public static class BasisNetworkHandleAvatar
{
    public static Queue<ServerSideSyncPlayerMessage> Message = new Queue<ServerSideSyncPlayerMessage>();
    public static void HandleAvatarUpdate(NetPacketReader Reader)
    {
        if (Message.TryDequeue(out ServerSideSyncPlayerMessage SSM) == false)
        {
            SSM = new ServerSideSyncPlayerMessage();
        }
        SSM.Deserialize(Reader);
        if (BasisNetworkManagement.RemotePlayers.TryGetValue(SSM.playerIdMessage.playerID, out BasisNetworkReceiver player))
        {
            BasisNetworkAvatarDecompressor.DecompressAndProcessAvatar(player, SSM, SSM.playerIdMessage.playerID);
        }
        else
        {
       //this fires when the network does not yet have a player to accept data.
       //this can happen from a mistake or from the reliable packet not having notified yet.
       //BasisDebug.Log($"Missing Player For Avatar Update {SSM.playerIdMessage.playerID}");
        }
        Message.Enqueue(SSM);
        if (Message.Count > 256)
        {
            Message.Clear();
            BasisDebug.LogError("Messages Exceeded 250! Resetting");
        }
    }
    public static void HandleAvatarChangeMessage(NetPacketReader reader)
    {
        ServerAvatarChangeMessage ServerAvatarChangeMessage = new ServerAvatarChangeMessage();
        ServerAvatarChangeMessage.Deserialize(reader);
        ushort PlayerID = ServerAvatarChangeMessage.uShortPlayerId.playerID;
        if (BasisNetworkManagement.Players.TryGetValue(PlayerID, out BasisNetworkPlayer Player))
        {
            BasisNetworkReceiver networkReceiver = (BasisNetworkReceiver)Player;
            networkReceiver.ReceiveAvatarChangeRequest(ServerAvatarChangeMessage);
        }
        else
        {
            BasisDebug.Log("Missing Player For Message " + ServerAvatarChangeMessage.uShortPlayerId.playerID);
        }
    }
}
