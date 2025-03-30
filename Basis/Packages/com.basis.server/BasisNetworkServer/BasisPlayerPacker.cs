using Basis.Network.Core;
using LiteNetLib;
using LiteNetLib.Utils;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Compression;
using System.IO;
using static BasisServerReductionSystem;
using static SerializableBasis;
namespace BasisNetworkServer
{
    public static class BasisPlayerPackerPlayers
    {
        public static ManagedTimer timer;//create a new timer
        public static ConcurrentDictionary<NetPeer, BasisPlayerPacker> Players = new ConcurrentDictionary<NetPeer, BasisPlayerPacker>();
        public static void Initalize()
        {
            timer = new ManagedTimer(PushData, null, 4, 4);
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
                Packer.Push();
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
        public ConcurrentDictionary<ushort, ServerSideSyncPlayerMessage> SSSPM = new ConcurrentDictionary<ushort, ServerSideSyncPlayerMessage>();
        public NetPeer localClient;
        public NetDataWriter Writer = new NetDataWriter();
        public void Add(ServerSideSyncPlayerMessage ServerSideSyncPlayerMessage)
        {
            SSSPM[ServerSideSyncPlayerMessage.playerIdMessage.playerID] = ServerSideSyncPlayerMessage;
        }

        public void Push()
        {
            ICollection<ServerSideSyncPlayerMessage> Values = SSSPM.Values;
            Writer.Reset();
            foreach (ServerSideSyncPlayerMessage Message in Values)
            {
                Message.Serialize(Writer);
            }
           int MaxSize = localClient.GetMaxSinglePacketSize( DeliveryMethod.Sequenced);
            BNL.Log("Max Network Size can be is " + MaxSize);
           // GetMaxSinglePacketSize
            byte[] UnCompressedData = Writer.Data;

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
                using (DeflateStream deflateStream = new DeflateStream(compressedMemoryStream, CompressionLevel.Optimal, true))
                {
                    deflateStream.Write(data, 0, data.Length);
                } // Ensure deflateStream is closed before reading compressedMemoryStream

                return compressedMemoryStream.ToArray();
            }
        }
    }
}
