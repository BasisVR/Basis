using System.Net;
using System.Net.Sockets;

namespace Basis.Network.Core {

    public partial class EventBasedNetListener: LiteNetLib.INetEventListener {
        void LiteNetLib.INetEventListener.OnConnectionRequest(LiteNetLib.ConnectionRequest request)
        {
            ConnectionRequestEvent?.Invoke(new LNLConnectionRequest(request));
        }

        void LiteNetLib.INetEventListener.OnPeerDisconnected(LiteNetLib.NetPeer peer, LiteNetLib.DisconnectInfo disconnectInfo)
        {
            PeerDisconnectedEvent?.Invoke(new LNLNetPeer(peer), new DisconnectInfo(disconnectInfo));
        }

        void LiteNetLib.INetEventListener.OnPeerConnected(LiteNetLib.NetPeer peer)
        {
            PeerConnectedEvent?.Invoke(new LNLNetPeer(peer));
        }

        void LiteNetLib.INetEventListener.OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        {
            NetworkErrorEvent?.Invoke(endPoint, socketError);
        }

        void LiteNetLib.INetEventListener.OnNetworkReceive(LiteNetLib.NetPeer peer, LiteNetLib.NetPacketReader reader, byte channelNumber, LiteNetLib.DeliveryMethod deliveryMethod)
        {
            NetPacketReader read = new NetPacketReader(reader);
            NetworkReceiveEvent?.Invoke(new LNLNetPeer(peer), read, channelNumber, (DeliveryMethod)(byte)deliveryMethod);
        }

        void LiteNetLib.INetEventListener.OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, LiteNetLib.NetPacketReader reader, LiteNetLib.UnconnectedMessageType messageType)
        {
            // unused
        }

        void LiteNetLib.INetEventListener.OnNetworkLatencyUpdate(LiteNetLib.NetPeer peer, int latency)
        {
            // unused
        }
    }

    public partial struct DisconnectInfo {
        internal DisconnectInfo(LiteNetLib.DisconnectInfo info) {
            NetPacketReader reader = new NetPacketReader(info.AdditionalData);

            // TODO: better enum conversion?
            Reason = (DisconnectReason)(int)info.Reason;
            SocketErrorCode = info.SocketErrorCode;
            AdditionalData = reader;
        }
    }

    public sealed partial class NetStatistics {
        internal NetStatistics(LiteNetLib.NetStatistics stats) {
            PacketsSent = stats.PacketsSent;
            PacketsReceived = stats.PacketsReceived;
            BytesSent = stats.BytesSent;
            BytesReceived = stats.BytesReceived;
            PacketLoss = stats.PacketLoss;
        }
    }

    public partial class NetPacketReader {
        internal NetPacketReader(LiteNetLib.NetPacketReader reader): base((LiteNetLib.Utils.NetDataReader)reader) {
			RecycleInternal = () => reader.Recycle();
		}
    }

    public class LNLConnectionRequest: ConnectionRequest {
        readonly LiteNetLib.ConnectionRequest request;
        readonly NetDataReader data;

        internal LNLConnectionRequest(LiteNetLib.ConnectionRequest request) {
            this.request = request;
            data = new NetDataReader(request.Data);
        }

        public NetDataReader Data => data;

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

        public override bool Equals(object obj)
        {
            if (obj == null || !(obj is LNLNetPeer))
            {
                return false;
            }
            else
            {
                return peer.Equals(((LNLNetPeer)obj).peer);
            }
        }

        public override int GetHashCode()
        {
            return peer.GetHashCode();
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