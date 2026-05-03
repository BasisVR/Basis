using Basis.Network.Core;
using System.Threading;
using static BasisNetworkCore.Serializable.SerializableBasis;

namespace BasisNetworkServer.Security
{
    /// <summary>
    /// Runtime-only server toggle for the Opus encoder's in-band FEC aggressiveness.
    /// An admin pushes a 0..100 percentage; the server broadcasts it to every connected
    /// client, which applies it to its active encoder via OPUS_SET_PACKET_LOSS_PERC.
    /// </summary>
    public static class BasisOpusPacketLossStateManager
    {
        // 0..100 clamped. Interlocked because clients and the admin handler can race.
        private static int _packetLossPercent = 10;

        public static int PacketLossPercent => Interlocked.CompareExchange(ref _packetLossPercent, 0, 0);

        /// <summary>Clamp, set, and report whether the value actually changed.</summary>
        public static bool SetPacketLossPercent(int percent)
        {
            if (percent < 0) percent = 0;
            else if (percent > 100) percent = 100;
            int previous = Interlocked.Exchange(ref _packetLossPercent, percent);
            return previous != percent;
        }

        public static void SendStateToPeer(NetPeer peer)
        {
            NetDataWriter writer = NetworkServer.RentWriter();
            try
            {
                new AdminRequest().Serialize(writer, AdminRequestMode.GlobalGetOpusPacketLossState);
                writer.Put((byte)PacketLossPercent);
                NetworkServer.TrySend(peer, writer, BasisNetworkCommons.AdminChannel, DeliveryMethod.ReliableOrdered);
            }
            finally
            {
                NetworkServer.ReturnWriter(writer);
            }
        }

        public static void BroadcastState()
        {
            NetDataWriter writer = NetworkServer.RentWriter();
            try
            {
                new AdminRequest().Serialize(writer, AdminRequestMode.GlobalGetOpusPacketLossState);
                writer.Put((byte)PacketLossPercent);
                NetworkServer.BroadcastMessageToClients(
                    writer,
                    BasisNetworkCommons.AdminChannel,
                    NetworkServer.PeerSnapshot,
                    DeliveryMethod.ReliableOrdered);
            }
            finally
            {
                NetworkServer.ReturnWriter(writer);
            }
        }
    }
}
