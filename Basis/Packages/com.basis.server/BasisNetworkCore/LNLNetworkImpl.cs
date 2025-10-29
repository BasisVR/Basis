using System.Net;
using System.Net.Sockets;

namespace Basis.Network.Core {
    public class LNLConnectionRequest: ConnectionRequest {
        LiteNetLib.ConnectionRequest request;

        internal LNLConnectionRequest(LiteNetLib.ConnectionRequest request) {
            this.request = request;
        }

        //public NetDataReader Data => request.Data; // TODO: convert to common NetDataReader
        public NetDataReader Data => 
            throw new System.NotImplementedException();

        public IPEndPoint RemoteEndPoint => request.RemoteEndPoint;

        NetPeer ConnectionRequest.Accept()
        {
            return new LNLNetPeer(request.Accept());
        }

        void ConnectionRequest.Reject(NetDataWriter w)
        {
            // request.Reject(w);
            // request.Reject(w.Data, 0, w.Length, false);
            // TODO: convert writer or use array overload.
            request.Reject();
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
            LiteNetLib.Utils.NetDataWriter writer = null; // TODO: writer conversion, or use array/span overloads
            peer.Send(writer, channelNumber, (LiteNetLib.DeliveryMethod)(byte)deliveryMethod);
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
            LiteNetLib.Utils.NetDataWriter writer = null; // TODO: from common NetDataWriter or grab a span.
            LiteNetLib.NetPeer peer = manager.Connect(sIP, port, writer);
            return new LNLNetPeer(peer);
        }

        public int ConnectedPeersCount => manager.ConnectedPeersCount;

        public NetStatistics Statistics => (NetStatistics)manager.Statistics;
    }
}