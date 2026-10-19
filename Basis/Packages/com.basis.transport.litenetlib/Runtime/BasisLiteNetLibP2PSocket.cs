using System.Net;

namespace Basis.Network.Core
{
    public sealed class BasisLiteNetLibP2PSocket : IBasisP2PSocket
    {
        private readonly LiteNetLib.NetManager manager;
        private readonly LiteNetLib.EventBasedNatPunchListener punch = new LiteNetLib.EventBasedNatPunchListener();
        private readonly BasisCryptoLayer crypto = new BasisCryptoLayer();

        public BasisLiteNetLibP2PSocket(EventBasedNetListener listener, BasisP2PIntroduced onIntroduced)
        {
            punch.NatIntroductionSuccess += (target, type, token) => onIntroduced?.Invoke(target, type == LiteNetLib.NatAddressType.Internal, token);
            manager = new LiteNetLib.NetManager(listener, crypto)
            {
                NatPunchEnabled = true,
                UnsyncedEvents = true,
                AutoRecycle = false,
                ChannelsCount = BasisNetworkCommons.TotalChannels,
                UpdateTime = BasisNetworkCommons.NetworkIntervalPoll,
            };
            manager.NatPunchModule.Init(punch);
            manager.NatPunchModule.UnsyncedEvents = true;
        }

        public int LocalPort => manager.LocalPort;

        public bool Start()
        {
            return manager.Start();
        }

        public void Stop()
        {
            manager.Stop();
        }

        public void RequestIntroduction(string serverHost, int serverPort, string token)
        {
            manager.NatPunchModule.SendNatIntroduceRequest(serverHost, serverPort, token);
        }

        public NetPeer Connect(IPEndPoint endPoint, NetDataWriter connectData)
        {
            LiteNetLib.NetPeer peer = manager.Connect(endPoint, connectData.AsReadOnlySpan());
            return peer == null ? null : new LNLNetPeer(peer);
        }

        public void SetEndpointKeys(IPEndPoint endPoint, byte[] sendKey, byte[] recvKey, long initialSendCounter)
        {
            crypto.SetEndpointKeys(endPoint, sendKey, recvKey, initialSendCounter);
        }

        public void RemoveEndpoint(IPEndPoint endPoint)
        {
            crypto.RemoveEndpoint(endPoint);
        }
    }
}
