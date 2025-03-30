using Basis.Network.Core;
using LiteNetLib;
using LiteNetLib.Utils;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Compression;
using System.IO;
using static BasisServerReductionSystem;
using static SerializableBasis;
using System.Linq;
namespace BasisNetworkServer
{
    public static class BasisPlayerPackerPlayers
    {
        public static ManagedTimer timer;//create a new timer
        public static ConcurrentDictionary<NetPeer, BasisPlayerPacker> Players = new ConcurrentDictionary<NetPeer, BasisPlayerPacker>();
        public static NetDataWriter Writer = new NetDataWriter();
        public static void Initalize()
        {
            timer = new ManagedTimer(PushData, null, 8, 8);
        }
        public static void AddPlayer(NetPeer NetPeer)
        {
            BasisPlayerPacker Packer = new BasisPlayerPacker();
            Packer.localClient = NetPeer;

            Players.TryAdd(NetPeer, Packer);
        }
        public static void RemovePlayer(NetPeer NetPeer)
        {
            Players.TryRemove(NetPeer, out BasisPlayerPacker Packer);
        }
        private static void PushData(object state)
        {
            ICollection<BasisPlayerPacker> value = Players.Values;
            foreach (BasisPlayerPacker Packer in value)
            {
                Packer.Push(Writer);
            }
        }
        public static void AddData(NetPeer Destination, ServerSideSyncPlayerMessage SSSPM)
        {
            if (Players.TryGetValue(Destination, out BasisPlayerPacker Packer))
            {
                Packer.Add(SSSPM);
            }
        }
    }
    /// <summary>
    /// the goal of this class is to optimize packets before send out by combining data.
    /// </summary>
    public class BasisPlayerPacker
    {
        public ConcurrentQueue<ServerSideSyncPlayerMessage> SSSPM = new ConcurrentQueue<ServerSideSyncPlayerMessage>();
        public NetPeer localClient;
        public void Add(ServerSideSyncPlayerMessage ServerSideSyncPlayerMessage)
        {
            SSSPM.Enqueue(ServerSideSyncPlayerMessage);
        }
        public void Push(NetDataWriter Writer)
        {
            foreach (ServerSideSyncPlayerMessage message in SSSPM)
            {
                message.Serialize(Writer);
            }
            SSSPM.Clear();
            byte[] UnCompressedData = Writer.CopyData();
            Writer.Reset();

            if (UnCompressedData != null && UnCompressedData.Length != 0)
            {
                byte[] CompressedData = CompressByteArray(UnCompressedData);
                BNL.Log("Compressed Data " + CompressedData.Length);
                NetworkServer.SendOutValidated(localClient, CompressedData, BasisNetworkCommons.MovementCompressedChannel, DeliveryMethod.Sequenced);
            }
        }
        // Method to compress a large byte array using Deflate
        public static byte[] CompressByteArray(byte[] data)
        {
            using (MemoryStream compressedMemoryStream = new MemoryStream())
            {
                // Create a DeflateStream to write compressed data
                using (DeflateStream deflateStream = new DeflateStream(compressedMemoryStream, CompressionLevel.Optimal))
                {
                    // Write the byte array to the DeflateStream
                    deflateStream.Write(data, 0, data.Length);
                }

                // Return the compressed data as a byte array
                return compressedMemoryStream.ToArray();
            }
        }

    }
}
