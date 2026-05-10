using System;
using System.Net;
using System.Net.Sockets;

namespace Basis.Network.Core
{

    public partial class EventBasedNetListener : LiteNetLib.INetEventListener
    {
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

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            read.channel = channelNumber;
            read.method = (DeliveryMethod)(byte)deliveryMethod;
#endif

            NetworkReceiveEvent?.Invoke(new LNLNetPeer(peer), read, channelNumber, (DeliveryMethod)(byte)deliveryMethod);
        }

        void LiteNetLib.INetEventListener.OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, LiteNetLib.NetPacketReader reader, LiteNetLib.UnconnectedMessageType messageType)
        {
            // Broadcast packets aren't part of the server-info contract — only respond to direct probes.
            if (messageType != LiteNetLib.UnconnectedMessageType.BasicMessage) return;

            NetPacketReader read = new NetPacketReader(reader);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            read.channel = 255;
            read.method = DeliveryMethod.Unreliable;
#endif
            NetworkReceiveUnconnectedEvent?.Invoke(remoteEndPoint, read);
        }

        void LiteNetLib.INetEventListener.OnNetworkLatencyUpdate(LiteNetLib.NetPeer peer, int latency)
        {
            // unused
        }
    }

    public partial struct DisconnectInfo
    {
        internal DisconnectInfo(LiteNetLib.DisconnectInfo info)
        {
            NetPacketReader reader = new NetPacketReader(info.AdditionalData);

            // TODO: better enum conversion?
            Reason = (DisconnectReason)(int)info.Reason;
            SocketErrorCode = info.SocketErrorCode;
            AdditionalData = reader;
        }
    }

    public sealed partial class NetStatistics
    {
        internal NetStatistics(LiteNetLib.NetStatistics stats)
        {
            PacketsSent = stats.PacketsSent;
            PacketsReceived = stats.PacketsReceived;
            BytesSent = stats.BytesSent;
            BytesReceived = stats.BytesReceived;
            PacketLoss = stats.PacketLoss;
        }
    }

    public partial class NetPacketReader
    {
        internal NetPacketReader(LiteNetLib.NetPacketReader reader) : base((LiteNetLib.Utils.NetDataReader)reader)
        {
            RecycleInternal = () => reader.Recycle();
        }
    }

    public class LNLConnectionRequest : ConnectionRequest
    {
        readonly LiteNetLib.ConnectionRequest request;
        readonly NetDataReader data;

        internal LNLConnectionRequest(LiteNetLib.ConnectionRequest request)
        {
            this.request = request;
            data = new NetDataReader(request.Data);
        }

        public NetDataReader Data => data;

        public IPEndPoint RemoteEndPoint => request.RemoteEndPoint;

        public string Identity => request.RemoteEndPoint?.Address?.ToString() ?? string.Empty;

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

        internal LNLNetPeer(LiteNetLib.NetPeer lnlPeer)
        {
            peer = lnlPeer;
        }

        int NetPeer.Id => peer.Id;

        IPAddress NetPeer.Address => peer.Address;

        string NetPeer.Identity => peer.Address?.ToString() ?? string.Empty;

        int NetPeer.RemoteId => peer.RemoteId;

        int NetPeer.RoundTripTime => peer.RoundTripTime;

        float NetPeer.TimeSinceLastPacket => peer.TimeSinceLastPacket;

        long NetPeer.RemoteTimeDelta => peer.RemoteTimeDelta;

        int NetPeer.Mtu => peer.Mtu;

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
            peer.Send(data.AsReadOnlySpan(), channelNumber, (LiteNetLib.DeliveryMethod)(byte)deliveryMethod);
        }

        void NetPeer.SendUnreliableRawMerge(byte[] data, int offset, int length, byte channelNumber, int patchOffset, byte patchValue)
        {
            peer.SendUnreliableRawMerge(data, offset, length, channelNumber, patchOffset, patchValue);
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

    public class LNLNetManager : NetManager
    {
        public LiteNetLib.NetManager manager;

        public LNLNetManager(EventBasedNetListener listener, Configuration configuration)
        {
            manager = new LiteNetLib.NetManager(listener, null)
            {
                AutoRecycle = false,
                UnconnectedMessagesEnabled = true,
                NatPunchEnabled = configuration.NatPunchEnabled,
                AllowPeerAddressChange = configuration.AllowPeerAddressChange,
                BroadcastReceiveEnabled = false,
                UseNativeSockets = configuration.UseNativeSockets,
                ChannelsCount = BasisNetworkCommons.TotalChannels,
                EnableStatistics = configuration.EnableStatistics,
                IPv6Enabled = configuration.IPv6Enabled,
                UpdateTime = BasisNetworkCommons.NetworkIntervalPoll,
                PingInterval = configuration.PingInterval,
                DisconnectTimeout = configuration.DisconnectTimeout,
                UnsyncedEvents = true,
                ReceivePollingTime = BasisNetworkCommons.ReceivePollingTime,
                PacketPoolSize = BasisNetworkCommons.PacketPoolSize,
                SimulateLatency = configuration.SimulateLatency,
                SimulatePacketLoss = configuration.SimulatePacketLoss,
                SimulationMaxLatency = configuration.SimulationMaxLatency,
                SimulationMinLatency = configuration.SimulationMinLatency,
                SimulationPacketLossChance = configuration.SimulationPacketLossChance,
                MtuDiscovery = configuration.MtuDiscovery,
                MtuOverride = configuration.MtuOverride
            };
        }
        public void Start(IPAddress IPv4Address, IPAddress IPv6Address, int SetPort)
        {
            manager.Start(IPv4Address, IPv6Address, SetPort);
        }

        public void Stop()
        {
            manager.Stop();
        }

        public void PollEvents()
        {
            manager.TriggerUpdate();
        }

        public Basis.Network.Core.NetPeer Connect(string sIP, int port, NetDataWriter Writer)
        {

            LiteNetLib.NetPeer peer = manager.Connect(LiteNetLib.NetUtils.MakeEndPoint(sIP, port), Writer.AsReadOnlySpan());
            return new LNLNetPeer(peer);
        }

        public bool SendUnconnectedMessage(NetDataWriter writer, IPEndPoint remoteEndPoint)
        {
            return manager.SendUnconnectedMessage(writer.AsReadOnlySpan(), remoteEndPoint);
        }

        public int ConnectedPeersCount => manager.ConnectedPeersCount;

        public NetStatistics Statistics => new NetStatistics(manager.Statistics);
    }
}
