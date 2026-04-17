using Basis.Network.Core;
using BasisNetworkServer.BasisNetworking;

namespace BasisNetworkServer
{
    /// <summary>
    /// Server-side router for session-scoped "temp block" notifications sent on
    /// <see cref="BasisNetworkCommons.EventsChannel"/> with sub-byte
    /// <see cref="BasisNetworkCommons.EventType_PlayerTempBlock"/>.
    ///
    /// Clients emit a temp-block message when a user toggles their local block on
    /// another player; the server rewrites the payload with the sender's peer id and
    /// forwards it to the target peer only, so the target can mirror the block on
    /// their client without the state being persisted on either end.
    /// </summary>
    public static class BasisNetworkHandleTempBlock
    {
        /// <summary>
        /// Wire format (in): [byte eventType][ushort targetID][bool isBlocked]
        /// Wire format (out to target): [byte eventType][ushort senderID][bool isBlocked]
        /// </summary>
        public static void HandleEvent(NetPacketReader reader, NetPeer peer, byte eventType)
        {
            ushort targetId = reader.GetUShort();
            bool isBlocked = reader.GetBool();
            reader.Recycle();

            if (!NetworkServer.AuthenticatedPeers.TryGetValue(targetId, out NetPeer targetPeer))
            {
                return;
            }

            NetDataWriter writer = NetworkServer.RentWriter();
            writer.Put(eventType);
            writer.Put((ushort)peer.Id);
            writer.Put(isBlocked);

            NetworkServer.TrySend(targetPeer, writer, BasisNetworkCommons.EventsChannel, DeliveryMethod.ReliableOrdered);
            NetworkServer.ReturnWriter(writer);
        }
    }
}
