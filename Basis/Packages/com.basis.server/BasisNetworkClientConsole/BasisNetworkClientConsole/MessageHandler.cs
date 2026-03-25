using Basis.Network.Core;
using BasisNetworkClient;
using static SerializableBasis;

namespace Basis.Network
{
    public static class MessageHandler
    {
        public static void OnDisconnect(NetPeer peer, DisconnectInfo info)
        {
            BNL.LogError($"Peer {peer.Id} disconnected.");
        }

        public static void OnReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
        {
            if (peer.Id != 0) return;

            switch (channel)
            {
                case BasisNetworkCommons.AuthIdentityChannel:
                    AuthIdentityMessage(peer, reader, channel);
                    return; // already recycled inside
                case BasisNetworkCommons.metaDataChannel:
                    break;
                case BasisNetworkCommons.PlayerAvatarVeryLowChannel:
                case BasisNetworkCommons.PlayerAvatarVeryLowAdditionalChannel:
                case BasisNetworkCommons.PlayerAvatarLowChannel:
                case BasisNetworkCommons.PlayerAvatarLowAdditionalChannel:
                case BasisNetworkCommons.PlayerAvatarMediumChannel:
                case BasisNetworkCommons.PlayerAvatarMediumAdditionalChannel:
                case BasisNetworkCommons.PlayerAvatarHighChannel:
                case BasisNetworkCommons.PlayerAvatarHighAdditionalChannel:
                    // Full avatar update — just consume it
                    break;
                case BasisNetworkCommons.DisconnectionChannel:
                    // Just consume disconnection messages
                    break;
                default:
                    // Silently consume other channels
                    break;
            }

            reader.Recycle();
        }

        public static void AuthIdentityMessage(NetPeer peer, NetPacketReader Reader, byte channel)
        {
            BNL.Log("Validated Size " + Reader.AvailableBytes);
            if (BasisDIDAuthIdentityClient.IdentityMessage(peer, Reader, out NetDataWriter Writer))
            {
                BNL.Log("Sent Identity To Server!");
                peer.Send(Writer, BasisNetworkCommons.AuthIdentityChannel, DeliveryMethod.ReliableOrdered);
                Reader.Recycle();
            }
            else
            {
                BNL.LogError("Failed Identity Message!");
                Reader.Recycle();
            }
            BNL.Log("Completed");
        }
    }
}
