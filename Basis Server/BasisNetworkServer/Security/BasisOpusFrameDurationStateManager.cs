using Basis.Network.Core;
using System.Threading;
using static BasisNetworkCore.Serializable.SerializableBasis;

namespace BasisNetworkServer.Security
{
    /// <summary>
    /// Runtime-only server toggle for the Opus frame duration. An admin pushes either
    /// 20 or 40 (milliseconds); the server stores it and broadcasts to every connected
    /// client, which switches its encoder packing accordingly. Other valid Opus frame
    /// sizes (2.5, 5, 10, 60) are rejected — only 20/40 are useful in this codebase.
    /// </summary>
    public static class BasisOpusFrameDurationStateManager
    {
        public const int DefaultMs = 20;
        private static int _frameDurationMs = DefaultMs;

        public static int FrameDurationMs => Interlocked.CompareExchange(ref _frameDurationMs, 0, 0);

        public static bool IsAcceptedDuration(int ms) => ms == 20 || ms == 40;

        /// <summary>Set the frame duration (20 or 40 ms only). Returns true if it changed.</summary>
        public static bool SetFrameDurationMs(int ms)
        {
            if (!IsAcceptedDuration(ms)) ms = DefaultMs;
            int previous = Interlocked.Exchange(ref _frameDurationMs, ms);
            return previous != ms;
        }

        public static void SendStateToPeer(NetPeer peer)
        {
            NetDataWriter writer = NetworkServer.RentWriter();
            try
            {
                new AdminRequest().Serialize(writer, AdminRequestMode.GlobalGetOpusFrameDurationState);
                writer.Put((byte)FrameDurationMs);
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
                new AdminRequest().Serialize(writer, AdminRequestMode.GlobalGetOpusFrameDurationState);
                writer.Put((byte)FrameDurationMs);
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
