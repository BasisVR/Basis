using Basis.Network.Core;
using System.Threading;
using static BasisNetworkCore.Serializable.SerializableBasis;

namespace BasisNetworkServer.Security
{
    /// <summary>
    /// Runtime-only server toggle for headless audio clip playback.
    /// </summary>
    public static class BasisHeadlessAudioStateManager
    {
        private static int _headlessAudioOff;

        public static bool HeadlessAudioOff => Interlocked.CompareExchange(ref _headlessAudioOff, 0, 0) == 1;

        public static bool ToggleHeadlessAudio()
        {
            int prev;
            int next;
            do
            {
                prev = _headlessAudioOff;
                next = prev == 0 ? 1 : 0;
            }
            while (Interlocked.CompareExchange(ref _headlessAudioOff, next, prev) != prev);

            return next == 1;
        }

        public static void SendStateToPeer(NetPeer peer)
        {
            NetDataWriter writer = NetworkServer.RentWriter();
            new AdminRequest().Serialize(writer, AdminRequestMode.GlobalGetHeadlessAudioState);
            writer.Put(HeadlessAudioOff);
            NetworkServer.TrySend(peer, writer, BasisNetworkCommons.AdminChannel, DeliveryMethod.ReliableOrdered);
            NetworkServer.ReturnWriter(writer);
        }

        public static void BroadcastState()
        {
            NetDataWriter writer = NetworkServer.RentWriter();
            new AdminRequest().Serialize(writer, AdminRequestMode.GlobalGetHeadlessAudioState);
            writer.Put(HeadlessAudioOff);
            NetworkServer.BroadcastMessageToClients(
                writer,
                BasisNetworkCommons.AdminChannel,
                NetworkServer.PeerSnapshot,
                DeliveryMethod.ReliableOrdered);
            NetworkServer.ReturnWriter(writer);
        }
    }
}
