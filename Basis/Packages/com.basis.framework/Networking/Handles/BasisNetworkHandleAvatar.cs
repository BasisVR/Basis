using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.Recievers;
using LiteNetLib;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using static SerializableBasis;
public static class BasisNetworkHandleAvatar
{
    public static Queue<ServerSideSyncPlayerMessage> Message = new Queue<ServerSideSyncPlayerMessage>();
    public static void HandleAvatarCompressedUpdate(NetPacketReader Reader)
    {
        byte[] Bytes = Reader.GetRemainingBytes();
        byte[] Decompressed = DecompressByteArray(Bytes);

        Reader.SetSource(Decompressed);

        while(Reader.AvailableBytes >= 208)//208 is the smallest size a uncompressed avatar update can be.
        {
            HandleAvatarUpdate(Reader);
        }
    }
    // Method to decompress a byte array using Deflate
    public static byte[] DecompressByteArray(byte[] compressedData)
    {
        using (MemoryStream compressedMemoryStream = new MemoryStream(compressedData))
        {
            using (MemoryStream decompressedMemoryStream = new MemoryStream())
            {
                using (DeflateStream deflateStream = new DeflateStream(compressedMemoryStream, CompressionMode.Decompress))
                {
                    deflateStream.CopyTo(decompressedMemoryStream);
                } // Ensure deflateStream is properly disposed before reading

                return decompressedMemoryStream.ToArray();
            }
        }
    }
    public static void HandleAvatarUpdate(NetPacketReader Reader)
    {
        ServerSideSyncPlayerMessage SSM = new ServerSideSyncPlayerMessage();
        SSM.avatarSerialization = new LocalAvatarSyncMessage();

        SSM.Deserialize(Reader);
        if (BasisNetworkManagement.RemotePlayers.TryGetValue(SSM.playerIdMessage.playerID, out BasisNetworkReceiver player))
        {
            BasisNetworkAvatarDecompressor.DecompressAndProcessAvatar(player, SSM, SSM.playerIdMessage.playerID);
        }
        else
        {
            BasisDebug.Log($"Missing Player For Avatar Update {SSM.playerIdMessage.playerID}");
        }
        // Message.Enqueue(SSM);
        // if (Message.Count > 256)
        //   {
        //     Message.Clear();
        //  BasisDebug.LogError("Messages Exceeded 250! Resetting");
        // }
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
