using Basis.Scripts.Networking;
using Basis.Scripts.Networking.Recievers;
using LiteNetLib;
using static SerializableBasis;
using System.Collections.Generic;
using Basis.Scripts.Networking.NetworkedAvatar;
using System.IO.Compression;
using System.IO;
public static class BasisNetworkHandleAvatar
{
    public static Queue<ServerSideSyncPlayerMessage> Message = new Queue<ServerSideSyncPlayerMessage>();
    public static void HandleAvatarCompressedUpdate(NetPacketReader Reader)
    {
        byte[] Bytes = Reader.GetRemainingBytes();
        byte[] Decompressed = DecompressByteArray(Bytes);
        Reader.SetSource(Decompressed);
        while (Reader.AvailableBytes > 0)
        {
            HandleAvatarUpdate(Reader);
        }
    }
    // Method to decompress a byte array using Deflate
    public static byte[] DecompressByteArray(byte[] compressedData)
    {
        using (MemoryStream compressedMemoryStream = new MemoryStream(compressedData))
        using (MemoryStream decompressedMemoryStream = new MemoryStream())
        {
            // Create a DeflateStream to read the compressed data
            using (DeflateStream deflateStream = new DeflateStream(compressedMemoryStream, CompressionMode.Decompress))
            {
                // Copy the decompressed data to the new memory stream
                deflateStream.CopyTo(decompressedMemoryStream);
            }

            // Return the decompressed data as a byte array
            return decompressedMemoryStream.ToArray();
        }
    }
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
            BasisDebug.Log($"Missing Player For Avatar Update {SSM.playerIdMessage.playerID}");
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
