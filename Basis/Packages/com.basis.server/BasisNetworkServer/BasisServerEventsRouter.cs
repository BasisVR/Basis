using Basis.Network.Core;
using BasisNetworkServer.BasisNetworking;
using static SerializableBasis;

namespace BasisNetworkServer
{
    public static class BasisServerEventsRouter
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

                case BasisNetworkCommons.EventType_AvatarRateChange:
                    HandleAvatarRateChange(reader, peer, eventType);
                    break;

                case BasisNetworkCommons.EventType_PlayerChatTyping:
                    BasisNetworkHandleChatTyping.HandleEvent(reader, peer, eventType);
                    break;
                case BasisNetworkCommons.EventType_TalkModeChanged:
                    HandleTalkModeChanged(reader, peer, eventType);
                    break;

                case BasisNetworkCommons.EventType_MuteStateChanged:
                    HandleMuteStateChanged(reader, peer, eventType);
                    break;

                case BasisNetworkCommons.EventType_ErrorReport:
                    BasisNetworkHandleErrorReport.HandleEvent(reader, peer, eventType);
                    break;

                case BasisNetworkCommons.EventType_VoiceRecordRequest:
                case BasisNetworkCommons.EventType_VoiceRecordConsent:
                    BasisNetworkHandleVoiceRecord.HandleEvent(reader, peer, eventType);
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

        // Wire (in):  [eventType:1][intervalMs:2]
        // Wire (out): [eventType:1][senderId:2][intervalMs:2]
        private static void HandleAvatarRateChange(NetPacketReader reader, NetPeer peer, byte eventType)
        {
            ushort intervalMs = reader.GetUShort();
            reader.Recycle();

            ushort senderId = (ushort)peer.Id;

            NetDataWriter writer = NetworkServer.RentWriter();
            writer.Put(eventType);
            writer.Put(senderId);
            writer.Put(intervalMs);

            NetworkServer.BroadcastMessageToClients(writer, BasisNetworkCommons.EventsChannel, peer, NetworkServer.PeerSnapshot, DeliveryMethod.ReliableOrdered);
            NetworkServer.ReturnWriter(writer);
        }

        // Wire (in):  [eventType:1][modeByte:1]
        // Wire (out): [eventType:1][senderId:2][modeByte:1]
        private static void HandleTalkModeChanged(NetPacketReader reader, NetPeer peer, byte eventType)
        {
            byte mode = reader.GetByte();
            reader.Recycle();

            ushort senderId = (ushort)peer.Id;

            NetDataWriter writer = NetworkServer.RentWriter();
            writer.Put(eventType);
            writer.Put(senderId);
            writer.Put(mode);

            NetworkServer.BroadcastMessageToClients(writer, BasisNetworkCommons.EventsChannel, peer, NetworkServer.PeerSnapshot, DeliveryMethod.ReliableOrdered);
            NetworkServer.ReturnWriter(writer);
        }

        // Wire (in):  [eventType:1][muted:1]
        // Wire (out): [eventType:1][senderId:2][muted:1]
        private static void HandleMuteStateChanged(NetPacketReader reader, NetPeer peer, byte eventType)
        {
            byte muted = reader.GetByte();
            reader.Recycle();

            ushort senderId = (ushort)peer.Id;

            NetDataWriter writer = NetworkServer.RentWriter();
            writer.Put(eventType);
            writer.Put(senderId);
            writer.Put(muted);

            NetworkServer.BroadcastMessageToClients(writer, BasisNetworkCommons.EventsChannel, peer, NetworkServer.PeerSnapshot, DeliveryMethod.ReliableOrdered);
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
