using Basis.Network.Core;
using BasisNetworkServer.BasisNetworking;
using static SerializableBasis;

namespace BasisNetworkServer
{
    /// <summary>
    /// Routes incoming messages on <see cref="BasisNetworkCommons.EventsChannel"/>.
    /// The first byte is the event type; the rest is event-specific payload.
    /// </summary>
    public static class BasisNetworkEvents
    {
        public static void HandleEvent(NetPacketReader reader, NetPeer peer)
        {
            byte eventType = reader.GetByte();

            switch (eventType)
            {
                case BasisNetworkCommons.EventType_CameraShutterSound:
                    HandleCameraShutterSound(peer, eventType);
                    reader.Recycle();
                    break;

                case BasisNetworkCommons.EventType_CameraCountdown:
                    HandleCameraCountdown(reader, peer, eventType);
                    break;

                case BasisNetworkCommons.EventType_PlayerTempBlock:
                    BasisNetworkHandleTempBlock.HandleEvent(reader, peer, eventType);
                    break;

                default:
                    BNL.LogError($"Unknown EventsChannel event type: {eventType}");
                    reader.Recycle();
                    break;
            }
        }

        private static void HandleCameraShutterSound(NetPeer peer, byte eventType)
        {
            ushort peerId = (ushort)peer.Id;

            NetDataWriter writer = NetworkServer.RentWriter();
            writer.Put(eventType);

            CameraShutterSoundMessage outMsg = new CameraShutterSoundMessage
            {
                PlayerID = peerId,
            };
            outMsg.Serialize(writer);

            NetworkServer.BroadcastMessageToClients(writer, BasisNetworkCommons.EventsChannel, peer, NetworkServer.PeerSnapshot, DeliveryMethod.Sequenced);
            NetworkServer.ReturnWriter(writer);
        }

        private static void HandleCameraCountdown(NetPacketReader reader, NetPeer peer, byte eventType)
        {
            ClientCameraCountdownMessage clientMsg = new ClientCameraCountdownMessage();
            clientMsg.Deserialize(reader);
            reader.Recycle();

            ushort peerId = (ushort)peer.Id;

            NetDataWriter writer = NetworkServer.RentWriter();
            writer.Put(eventType);

            CameraCountdownMessage outMsg = new CameraCountdownMessage
            {
                PlayerID = peerId,
                Seconds = clientMsg.Seconds,
            };
            outMsg.Serialize(writer);

            NetworkServer.BroadcastMessageToClients(writer, BasisNetworkCommons.EventsChannel, peer, NetworkServer.PeerSnapshot, DeliveryMethod.Sequenced);
            NetworkServer.ReturnWriter(writer);
        }
    }
}
