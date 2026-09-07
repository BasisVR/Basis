using Basis.Network.Core;
using BasisNetworkServer.BasisNetworking;

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
            bool isTyping;
            try
            {
                isTyping = reader.GetBool();
            }
            finally
            {
                reader.Recycle();
            }

            if (!BasisNetworkChat.TypingLimiter.TryConsume(peer))
            {
                return;
            }

            if (BasisNetworkChat.IsChatBlockedFor(peer))
            {
                return;
            }

            if (peer.Id < 0 || peer.Id > ushort.MaxValue)
            {
                BNL.LogError($"Cannot broadcast chat typing state for peer id {peer.Id}: outside ushort wire format.");
                return;
            }

            NetDataWriter writer = NetworkServer.RentWriter();
            try
            {
                writer.Put(eventType);
                writer.Put((ushort)peer.Id);
                writer.Put(isTyping);

                NetworkServer.BroadcastMessageToClients(writer, BasisNetworkCommons.EventsChannel, peer, NetworkServer.PeerSnapshot, DeliveryMethod.Sequenced);
            }
            finally
            {
                NetworkServer.ReturnWriter(writer);
            }
        }
    }
}
