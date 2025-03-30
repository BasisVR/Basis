using Basis.Network.Core;
using LiteNetLib;
using LiteNetLib.Utils;
using System.IO.Compression;
using System.IO;
using static Basis.Network.Core.Serializable.SerializableBasis;
using static SerializableBasis;

public static class NetworkClient
{
    public static NetManager client;
    public static EventBasedNetListener listener;
    private static NetPeer peer;
    private static bool IsInUse;
    /// <summary>
    /// inital data is typically the 
    /// </summary> 
    /// <param name="IP"></param>
    /// <param name="port"></param>
    /// <param name="ReadyMessage"></param>
    public static NetPeer StartClient(string IP, int port, ReadyMessage ReadyMessage, byte[] AuthenticationMessage, bool UseNativeSockets = false)
    {
        if (IsInUse == false)
        {
            listener = new EventBasedNetListener();
            client = new NetManager(listener)
            {
                AutoRecycle = false,
                UnconnectedMessagesEnabled = false,
                NatPunchEnabled = true,
                AllowPeerAddressChange = true,
                BroadcastReceiveEnabled = false,
                UseNativeSockets = UseNativeSockets,//unity does not work with this
                ChannelsCount = BasisNetworkCommons.TotalChannels,
                EnableStatistics = true,
                UpdateTime = BasisNetworkCommons.NetworkIntervalPoll,
                PingInterval = 1500,
                UnsyncedEvents = true,
            };
            client.Start();
            NetDataWriter Writer = new NetDataWriter(true,12);
            //this is the only time we dont put key!
            Writer.Put(BasisNetworkVersion.ServerVersion);
            BytesMessage AuthBytes = new BytesMessage();
            AuthBytes.Serialize(Writer, AuthenticationMessage);
            ReadyMessage.Serialize(Writer);
            peer = client.Connect(IP, port, Writer);
            return peer;
        }
        else
        {
            BNL.LogError("Call Shutdown First!");
            return null;
        }
    }
    // Method to decompress a large byte array using Deflate
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
    public static void Disconnect()
    {
        IsInUse = false;
        BNL.Log("Client Called Disconnect from server");
        peer?.Disconnect();
        client?.Stop();

        BNL.Log("Worker thread stopped.");
    }
}
