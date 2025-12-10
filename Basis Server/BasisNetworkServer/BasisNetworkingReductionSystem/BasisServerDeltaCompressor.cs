using Basis.Network.Core;
using BasisNetworkServer.BasisNetworking;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using static SerializableBasis;

namespace BasisNetworkServer.BasisNetworkingReductionSystem
{
    public static class BasisServerDeltaCompressor
    {
        // Reuse arrays via shared pool
        private static readonly ArrayPool<byte> BytePool = ArrayPool<byte>.Shared;
        public static ConcurrentDictionary<int, DeltaData> DeltaStorage = new ConcurrentDictionary<int, DeltaData>();

        public class DeltaData
        {
            // 176-byte baseline buffer (rented once, reused)
            public byte[] Baseline;

            // Scratch buffers for indices/values (also rented, reused)
            public byte[] Indices;
            public byte[] Values;

            public bool HasBaseline;
        }

        public static void SendOut(NetPeer peer, ServerSideSyncPlayerMessage tempMsg)
        {
            SendOutFull(peer, tempMsg);
        }

        // Call this when a player disconnects so you don’t leak pooled buffers forever
        public static void ReleaseDeltaData(int index)
        {
            if (DeltaStorage.TryRemove(index, out var data))
            {
                if (data.Baseline != null) BytePool.Return(data.Baseline);
                if (data.Indices != null) BytePool.Return(data.Indices);
                if (data.Values != null) BytePool.Return(data.Values);
            }
        }

        private static void SendOutFull(NetPeer peer, ServerSideSyncPlayerMessage msg)
        {
            NetDataWriter writer = BasisServerReductionSystemEvents.RentWriter();

            msg.Serialize(writer);

            peer.Send(writer, BasisNetworkCommons.PlayerAvatarChannel, DeliveryMethod.ReliableSequenced);
            BasisNetworkStatistics.RecordOutbound(BasisNetworkCommons.PlayerAvatarChannel, writer.Length);
            BasisServerReductionSystemEvents.ReturnWriter(writer);
        }
    }
}
