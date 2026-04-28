using Basis.Network.Core;
using System.Collections.Concurrent;
using static BasisNetworkCore.Serializable.SerializableBasis;

namespace BasisNetworkServer.Security
{
    /// <summary>
    /// Per-user admin override for the Opus encoder bitrate (bits per second).
    /// The server keeps the override keyed by netId; when the targeted user's peer is
    /// connected, it pushes a UserOpusBitrateOverride message so the client applies
    /// OPUS_SET_BITRATE to its live encoder. A value of 0 means "no override — use the
    /// client's default bitrate". State is cleared when the peer disconnects.
    /// </summary>
    public static class BasisUserOpusBitrateStateManager
    {
        // Opus accepts 500..512000 bps; clamp to a slightly tighter, conservative range
        // for voice. 0 is reserved as the "clear override" sentinel.
        public const int MinBitrate = 6000;
        public const int MaxBitrate = 510000;

        private static readonly ConcurrentDictionary<int, int> _overrides = new();

        public static bool TryGetBitrate(int netId, out int bitrate) =>
            _overrides.TryGetValue(netId, out bitrate);

        /// <summary>
        /// Set or clear the bitrate override for a user. Pass 0 to clear.
        /// Returns the value that was actually stored after clamping (0 = cleared).
        /// </summary>
        public static int SetBitrate(int netId, int bitrate)
        {
            if (bitrate <= 0)
            {
                _overrides.TryRemove(netId, out _);
                return 0;
            }

            if (bitrate < MinBitrate) bitrate = MinBitrate;
            else if (bitrate > MaxBitrate) bitrate = MaxBitrate;
            _overrides[netId] = bitrate;
            return bitrate;
        }

        public static void ClearForPeer(int netId) => _overrides.TryRemove(netId, out _);

        public static void SendStateToPeer(NetPeer peer)
        {
            int bitrate = TryGetBitrate(peer.Id, out int v) ? v : 0;
            SendOverrideToPeer(peer, bitrate);
        }

        public static void SendOverrideToPeer(NetPeer peer, int bitrate)
        {
            NetDataWriter writer = NetworkServer.RentWriter();
            try
            {
                new AdminRequest().Serialize(writer, AdminRequestMode.UserOpusBitrateOverride);
                writer.Put(bitrate);
                NetworkServer.TrySend(peer, writer, BasisNetworkCommons.AdminChannel, DeliveryMethod.ReliableOrdered);
            }
            finally
            {
                NetworkServer.ReturnWriter(writer);
            }
        }
    }
}
