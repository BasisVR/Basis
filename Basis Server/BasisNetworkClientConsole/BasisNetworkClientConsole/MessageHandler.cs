using Basis.Network.Core;
using BasisNetworkClient;
using LiteNetLib;
using LiteNetLib.Utils;
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
                    break;
                case BasisNetworkCommons.metaDataChannel:
                    var message = new ServerMetaDataMessage();
                    message.Deserialize(reader);
                    break;
            }
        }
        public static void AuthIdentityMessage(NetPeer peer, NetPacketReader Reader, byte channel)
        {
            BNL.Log("Validated Size " + Reader.AvailableBytes);
            if (BasisDIDAuthIdentityClient.IdentityMessage(peer, Reader, out NetDataWriter Writer))
            {
                BNL.Log("Sent Identity To Server!");
                peer.Send(Writer, BasisNetworkCommons.AuthIdentityChannel, DeliveryMethod.ReliableSequenced);
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
