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

        private System.Tuple<string, IPEndPoint> introducer;

        // Introduce over IPv4 whenever the server has an A record: a name that also resolves to IPv6
        // would otherwise send dual-stack clients over v6 and v4-only clients over v4, and a v6 peer
        // cannot punch a v4 peer. Resolved once per server instead of on every burst packet.
        public void RequestIntroduction(string serverHost, int serverPort, string token)
        {
            System.Tuple<string, IPEndPoint> cached = introducer;
            if (cached == null || cached.Item1 != serverHost || cached.Item2.Port != serverPort)
            {
                IPAddress address;
                if (serverHost == "localhost") address = IPAddress.Loopback;
                else if (!IPAddress.TryParse(serverHost, out address))
                    address = LiteNetLib.NetUtils.ResolveAddress(serverHost, System.Net.Sockets.AddressFamily.InterNetwork) ?? LiteNetLib.NetUtils.ResolveAddress(serverHost);
                introducer = cached = System.Tuple.Create(serverHost, new IPEndPoint(address, serverPort));
            }
            manager.NatPunchModule.SendNatIntroduceRequest(cached.Item2, token);
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
