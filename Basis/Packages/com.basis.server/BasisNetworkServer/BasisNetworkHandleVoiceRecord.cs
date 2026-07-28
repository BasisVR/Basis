using Basis.Network.Core;
using BasisNetworkServer.BasisNetworking;

namespace BasisNetworkServer
{
    /// <summary>
    /// Server-side relay for voice-record consent messages sent on
    /// <see cref="BasisNetworkCommons.EventsChannel"/> with sub-bytes
    /// <see cref="BasisNetworkCommons.EventType_VoiceRecordRequest"/> and
    /// <see cref="BasisNetworkCommons.EventType_VoiceRecordConsent"/>.
    ///
    /// A recorder asks a recordee for permission to capture their voice; the recordee
    /// replies with a consent decision. The server does not evaluate consent — it only
    /// rewrites the payload with the sender's peer id and forwards it to the single
    /// target peer, mirroring the temp-block relay.
    /// </summary>
    public static class BasisNetworkHandleVoiceRecord
    {
        /// <summary>
        /// Wire (in):  [byte eventType][ushort targetID]{[byte state] when consent}[byte purpose]
        /// Wire (out): [byte eventType][ushort senderID]{[byte state] when consent}[byte purpose]
        /// </summary>
        public static void HandleEvent(NetPacketReader reader, NetPeer peer, byte eventType)
        {
            bool hasState = eventType == BasisNetworkCommons.EventType_VoiceRecordConsent;
            int needed = hasState ? 4 : 3; // targetID(2) [+ state(1)] + purpose(1)
            if (reader.AvailableBytes < needed)
            {
                reader.Recycle();
                return;
            }
            ushort targetId = reader.GetUShort();
            byte state = hasState ? reader.GetByte() : (byte)0;
            byte purpose = reader.GetByte();
            reader.Recycle();

            if (!NetworkServer.AuthenticatedPeers.TryGetValue(targetId, out NetPeer targetPeer))
            {
                return;
            }

            NetDataWriter writer = NetworkServer.RentWriter();
            writer.Put(eventType);
            writer.Put((ushort)peer.Id);
            if (hasState)
            {
                writer.Put(state);
            }
            writer.Put(purpose);

            NetworkServer.TrySend(targetPeer, writer, BasisNetworkCommons.EventsChannel, DeliveryMethod.ReliableOrdered);
            NetworkServer.ReturnWriter(writer);
        }
    }
}
