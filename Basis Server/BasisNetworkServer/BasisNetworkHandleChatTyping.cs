using Basis.Network.Core;

namespace BasisNetworkServer
{
    /// <summary>
    /// Server-side router for transient player chat typing state.
    /// </summary>
    public static class BasisNetworkHandleChatTyping
    {
        /// <summary>
        /// Wire format (in): [byte eventType][bool isTyping]
        /// Wire format (out): [byte eventType][ushort senderId][bool isTyping]
        /// </summary>
        public static void HandleEvent(NetPacketReader reader, NetPeer peer, byte eventType)
        {
            bool isTyping = reader.GetBool();
            reader.Recycle();

            NetDataWriter writer = NetworkServer.RentWriter();
            writer.Put(eventType);
            writer.Put((ushort)peer.Id);
            writer.Put(isTyping);

            NetworkServer.BroadcastMessageToClients(writer, BasisNetworkCommons.EventsChannel, peer, NetworkServer.PeerSnapshot, DeliveryMethod.ReliableSequenced);
            NetworkServer.ReturnWriter(writer);
        }
    }
}
