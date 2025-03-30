using Basis.Network.Core;
using LiteNetLib;
using LiteNetLib.Utils;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Compression;
using System.IO;
using static BasisServerReductionSystem;
using static SerializableBasis;
using System.Xml.Linq;
using System;
namespace BasisNetworkServer
{
    public static class BasisPlayerPackerPlayers
    {
        public static ManagedTimer timer;//create a new timer
        public static ConcurrentDictionary<NetPeer, BasisPlayerPacker> Players = new ConcurrentDictionary<NetPeer, BasisPlayerPacker>();
        public static void Initalize()
        {
            timer = new ManagedTimer(PushData, null, 10, 10);
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
        List<byte> CombinedData = new List<byte>(6);
        public void Add(ServerSideSyncPlayerMessage ServerSideSyncPlayerMessage)
        {
            SSSPM[ServerSideSyncPlayerMessage.playerIdMessage.playerID] = ServerSideSyncPlayerMessage;
        }

        public void Push()
        {
            int count = SSSPM.Count;
            if (count != 0)
            {
                NetDataWriter Writer = new NetDataWriter(true, 208);
                ICollection<ServerSideSyncPlayerMessage> Values = SSSPM.Values;
                if (count > 6)
                {
                    int MaxSize = localClient.GetMaxSinglePacketSize(DeliveryMethod.Sequenced);
                    List<byte[]> RawChunks = new List<byte[]>();

                    // Prepare the raw chunks
                    foreach (ServerSideSyncPlayerMessage Message in Values)
                    {
                        Writer.Reset();
                        Message.Serialize(Writer);
                        int LengthOfBytes = Writer.Length;
                        byte[] Data = Writer.Data;
                        RawChunks.Add(Data);
                    }
                    // Clear the message collection after sending all data
                    SSSPM.Clear();

                    foreach (byte[] Raw in RawChunks)
                    {
                        // Check if adding this chunk would exceed the max size
                        if (CombinedData.Count + Raw.Length > MaxSize)
                        {
                            // If the combined data is already too large, send the data and clear the combined list.
                            SendOut(CombinedData.ToArray(), CombinedData.Count);
                            CombinedData.Clear();
                        }

                        // Add the chunk to the combined data list
                        CombinedData.AddRange(Raw);
                    }
                    CombinedData.Clear();

                    // If there's remaining data after processing all chunks, send it out
                    if (CombinedData.Count > 0)
                    {
                        SendOut(CombinedData.ToArray(), CombinedData.Count);
                    }
                }
                else
                {
                    foreach (ServerSideSyncPlayerMessage Message in Values)
                    {
                        Writer.Reset();
                        Message.Serialize(Writer);
                        SendOutSingle(Writer);
                    }

                }
            }
        }
        public void SendOut(byte[] Data, int length)
        {
            byte[] CompressedData = CompressByteArray(Data, length);

            if (CompressedData != null && CompressedData.Length > 0)
            {
                //   BNL.Log($"Compressed Data Size: {CompressedData.Length}");
                NetworkServer.SendOutValidated(
                    localClient,
                    CompressedData,
                    BasisNetworkCommons.MovementCompressedChannel,
                    DeliveryMethod.Sequenced
                );
            }
            else
            {
                BNL.LogError("Compression resulted in an empty or null byte array.");
            }
        }
        public void SendOutSingle(NetDataWriter NetDataWriter)
        {
            NetworkServer.SendOutValidated(
                localClient,
                NetDataWriter,
                BasisNetworkCommons.MovementCompressedChannel,
                DeliveryMethod.Sequenced
            );
        }

        // Method to compress a large byte array using Deflate
        public static byte[] CompressByteArray(byte[] data,int length)
        {
            using (MemoryStream compressedMemoryStream = new MemoryStream())
            {
                using (DeflateStream deflateStream = new DeflateStream(compressedMemoryStream, CompressionLevel.Optimal, true))
                {
                    deflateStream.Write(data, 0, length);
                } // Ensure deflateStream is closed before reading compressedMemoryStream

                return compressedMemoryStream.ToArray();
            }
        }
    }
}
