using Basis.Network.Core;
using Basis.Network.Server.Generic;
using System;
using System.Threading;
using static BasisNetworkCore.Serializable.SerializableBasis;
namespace BasisNetworkServer.Security
{
    /// <summary>
    /// Runtime-only server policy controlling whether headless clients may stay connected.
    /// </summary>
    public static class BasisHeadlessConnectionPolicyManager
    {
        public const string DisallowedReason = "Headless client disallowed by server.";

        private static int _headlessDisallowed;

        public static bool HeadlessDisallowed => Interlocked.CompareExchange(ref _headlessDisallowed, 0, 0) == 1;

        public static void InitializeFromConfig(bool disallowHeadless)
        {
            Interlocked.Exchange(ref _headlessDisallowed, disallowHeadless ? 1 : 0);
        }

        public static bool SetDisallowHeadless(bool disallowHeadless)
        {
            int requestedValue = disallowHeadless ? 1 : 0;
            int previousValue = Interlocked.Exchange(ref _headlessDisallowed, requestedValue);
            return previousValue != requestedValue;
        }

        public static bool IsHeadlessClient(SerializableBasis.ClientMetaDataMessage metaData)
        {
            return IsHeadlessPlatform(metaData.playerPlatform);
        }

        public static bool IsHeadlessPlatform(string playerPlatform)
        {
            if (string.IsNullOrWhiteSpace(playerPlatform))
            {
                return false;
            }

            return string.Equals(playerPlatform, "Headless", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(playerPlatform, "WindowsServer", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(playerPlatform, "LinuxServer", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(playerPlatform, "OSXServer", StringComparison.OrdinalIgnoreCase);
        }

        public static void DisconnectConnectedHeadlessPeers()
        {
            NetPeer[] peers = NetworkServer.PeerSnapshot;
            foreach (NetPeer peer in peers)
            {
                if (!BasisSavedState.GetLastPlayerMetaData(peer, out SerializableBasis.ClientMetaDataMessage metaData) ||
                    !IsHeadlessClient(metaData))
                {
                    continue;
                }

                BasisServerHandle.BasisServerHandleEvents.RejectWithReason(peer, DisallowedReason);
            }
        }

        public static void SendStateToPeer(NetPeer peer)
        {
            NetDataWriter writer = NetworkServer.RentWriter();
            try
            {
                new AdminRequest().Serialize(writer, AdminRequestMode.GlobalGetHeadlessDisallowState);
                writer.Put(HeadlessDisallowed);
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
                new AdminRequest().Serialize(writer, AdminRequestMode.GlobalGetHeadlessDisallowState);
                writer.Put(HeadlessDisallowed);
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
