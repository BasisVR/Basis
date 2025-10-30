using System.Net;
using System.Net.Sockets;

namespace Basis.Network.Core {
    public class LNLConnectionRequest: ConnectionRequest {
        LiteNetLib.ConnectionRequest request;

        internal LNLConnectionRequest(LiteNetLib.ConnectionRequest request) {
            this.request = request;
        }

        public NetDataReader Data => new NetDataReader(request.Data);

        public IPEndPoint RemoteEndPoint => request.RemoteEndPoint;

        NetPeer ConnectionRequest.Accept()
        {
            return new LNLNetPeer(request.Accept());
        }

        void ConnectionRequest.Reject(NetDataWriter w)
        {
            request.Reject(w.Data, 0, w.Length, false);
        }
    }

    public class LNLNetPeer : NetPeer
    {
        private readonly LiteNetLib.NetPeer peer;

        internal LNLNetPeer(LiteNetLib.NetPeer lnlPeer) {
            peer = lnlPeer;
        }

        int NetPeer.Id => peer.Id;

        IPAddress NetPeer.Address => peer.Address;

        int NetPeer.RemoteId => peer.RemoteId;

        int NetPeer.RoundTripTime => peer.RoundTripTime;

        float NetPeer.TimeSinceLastPacket => peer.TimeSinceLastPacket;

        long NetPeer.RemoteTimeDelta => peer.RemoteTimeDelta;

        void NetPeer.Disconnect()
        {
            peer.Disconnect();
        }

        void NetPeer.Disconnect(byte[] b)
        {
            peer.Disconnect(b);
        }

        void NetPeer.DisconnectForce()
        {
            peer.NetManager.DisconnectPeerForce(peer);
        }

        int NetPeer.GetPacketsCountInQueue(byte channel, DeliveryMethod deliveryMethod)
        {
            return peer.GetPacketsCountInQueue(channel, (LiteNetLib.DeliveryMethod)(byte)deliveryMethod);
        }

        void NetPeer.Send(byte[] data, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            peer.Send(data, channelNumber, (LiteNetLib.DeliveryMethod)(byte)deliveryMethod);
        }

        void NetPeer.Send(NetDataWriter data, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            peer.Send(data.Data, 0, data.Length, channelNumber, (LiteNetLib.DeliveryMethod)(byte)deliveryMethod);
        }
    }

    public class LNLNetManager: NetManager {
        protected LiteNetLib.NetManager manager;

        public LNLNetManager(EventBasedNetListener listener, bool UseNativeSockets) {
            manager = new LiteNetLib.NetManager(listener)
            {
                AutoRecycle = false,
                UnconnectedMessagesEnabled = false,
                NatPunchEnabled = true,
                AllowPeerAddressChange = true,
                BroadcastReceiveEnabled = false,
                UseNativeSockets = UseNativeSockets,//unity does not work with this
                ChannelsCount = BasisNetworkCommons.TotalChannels,
                EnableStatistics = true,
                UpdateTime = BasisNetworkCommons.NetworkIntervalPoll,
                PingInterval = BasisNetworkCommons.PingInterval,
                UnsyncedEvents = true,
                ReceivePollingTime = BasisNetworkCommons.ReceivePollingTime,
                PacketPoolSize = BasisNetworkCommons.PacketPoolSize,
            };
        }

        public void Start(IPAddress IPv4Address, IPAddress IPv6Address, int SetPort) {
            manager.Start(IPv4Address, IPv6Address, SetPort);
        }

        public void Stop() {
            manager.Stop();
        }

        public Basis.Network.Core.NetPeer Connect(string sIP, int port, NetDataWriter Writer) {
            
            LiteNetLib.NetPeer peer = manager.Connect(LiteNetLib.NetUtils.MakeEndPoint(sIP, port), Writer.AsReadOnlySpan());
            return new LNLNetPeer(peer);
        }

        public int ConnectedPeersCount => manager.ConnectedPeersCount;

        public NetStatistics Statistics => new NetStatistics(manager.Statistics);
    }
}