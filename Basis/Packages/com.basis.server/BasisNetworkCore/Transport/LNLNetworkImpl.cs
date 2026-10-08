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

#if UNITY_EDITOR || UNITY_INCLUDE_INSTRUMENTATION
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
#if UNITY_EDITOR || UNITY_INCLUDE_INSTRUMENTATION
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

        int NetPeer.RemoteId => peer.RemoteId;

        int NetPeer.RoundTripTime => peer.RoundTripTime;

        float NetPeer.TimeSinceLastPacket => peer.TimeSinceLastPacket;

        long NetPeer.RemoteTimeDelta => peer.RemoteTimeDelta;

        int NetPeer.Mtu => peer.Mtu;

        object NetPeer.Tag
        {
            get => peer.Tag;
            set => peer.Tag = value;
        }

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

    public class LNLNetManager : NetManager, IBasisSharedPeerIds
    {
        public LiteNetLib.NetManager manager;

        public void UsePeerIdAllocator(BasisPeerIdAllocator allocator)
        {
            if (allocator == null)
            {
                manager.PeerIdAllocator = null;
                manager.PeerIdRelease = null;
                return;
            }
            manager.PeerIdAllocator = allocator.Allocate;
            manager.PeerIdRelease = id => allocator.Release(id);
        }

        public bool IsRunning => manager != null && manager.IsRunning;

        public string ListenDescription => manager != null && manager.IsRunning ? $"UDP port {manager.LocalPort} (LiteNetLib)" : null;

        public LNLNetManager(EventBasedNetListener listener, Configuration configuration)
        {
            LNLTransportConfig lnl = BasisTransportConfigStore.Get<LNLTransportConfig>(BasisNetworkStackRegistry.LiteNetLibId);
            manager = new LiteNetLib.NetManager(listener, null)
            {
                AutoRecycle = false,
                UnconnectedMessagesEnabled = true,
                NatPunchEnabled = lnl.NatPunchEnabled,
                AllowPeerAddressChange = lnl.AllowPeerAddressChange,
                BroadcastReceiveEnabled = false,
                UseNativeSockets = lnl.UseNativeSockets,
                MergeHoldMs = lnl.MergeHoldMs,
                CompactMergeEnabled = lnl.CompactMerged,
                PeerUpdateParallelism = lnl.PeerUpdateParallelism,
                // This pool's share of the machine, decided centrally so it composes with the
                // reduction system's overlapping pool instead of both claiming the whole box.
                PeerUpdateWorkerCap = BasisCpuBudget.PeerUpdateCap,
                PeersPerUpdateWorker = lnl.PeerUpdatePeersPerWorker > 0 ? lnl.PeerUpdatePeersPerWorker : 128,
                MaxUnreliableQueuePerPeer = lnl.MaxUnreliableQueuePerPeer,
                // Both ceilings below move with the connected population, so they are resolved on
                // every join/leave rather than pinned here. A configured value wins; 0 means
                // "size me for this box", which is the default.
                ResolveUnreliableQueuePerPeer =
                    peers => BasisPopulationScale.UnreliableQueuePerPeer(lnl.MaxUnreliableQueuePerPeer, peers),
                ResolvePacketPoolMax =
                    peers => BasisPopulationScale.PacketPoolMax(lnl.PacketPoolSizeMax, peers, lnl.PacketPoolSizePerPeer),
                // Voice is held apart from bulk avatar state so a backlog of position updates can
                // neither delay it nor shed it. Fixed, not population-scaled: this queue holds one
                // stream per talker, not one update per pair.
                PriorityUnreliableChannels = BasisNetworkCommons.BuildPriorityUnreliableChannelMap(),
                MaxPriorityUnreliableQueuePerPeer = lnl.MaxPriorityUnreliableQueuePerPeer,
                ResolvePriorityUnreliableQueuePerPeer =
                    peers => BasisPopulationScale.PriorityQueuePerPeer(lnl.MaxPriorityUnreliableQueuePerPeer, peers),
                ChannelsCount = BasisNetworkCommons.TotalChannels,
                EnableStatistics = configuration.EnableStatistics,
                IPv6Enabled = lnl.IPv6Enabled,
                UpdateTime = BasisNetworkCommons.NetworkIntervalPoll,
                PingInterval = lnl.PingInterval,
                DisconnectTimeout = lnl.DisconnectTimeout,
                UnsyncedEvents = true,
                ReceivePollingTime = BasisNetworkCommons.ReceivePollingTime,
                PacketPoolSize = BasisNetworkCommons.PacketPoolSize,
                SimulateLatency = lnl.SimulateLatency,
                SimulatePacketLoss = lnl.SimulatePacketLoss,
                SimulationMaxLatency = lnl.SimulationMaxLatency,
                SimulationMinLatency = lnl.SimulationMinLatency,
                SimulationPacketLossChance = lnl.SimulationPacketLossChance,
                MtuDiscovery = lnl.MtuDiscovery,
                MtuOverride = lnl.MtuOverride,
                MultiSocketCount = lnl.MultiSocketCount,
                PacketPoolSizePerPeer = lnl.PacketPoolSizePerPeer,
                PacketPoolSizeMax = lnl.PacketPoolSizeMax
            };
        }
        public void Start(IPAddress IPv4Address, IPAddress IPv6Address, int SetPort)
        {
            manager.Start(IPv4Address, IPv6Address, SetPort);
        }

        public void StartManual(IPAddress IPv4Address, IPAddress IPv6Address, int SetPort)
        {
            manager.StartInManualMode(IPv4Address, IPv6Address, SetPort);
        }

        public void PollEvents()
        {
            manager.PollEvents();
        }

        public void ManualUpdate(float elapsedMilliseconds)
        {
            manager.ManualUpdate(elapsedMilliseconds);
        }

        public void Stop()
        {
            manager.Stop();
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

        public long UnreliableDropped => manager.UnreliableDropped;
        public long PriorityUnreliableDropped => manager.PriorityUnreliableDropped;

        public NetStatistics Statistics => new NetStatistics(manager.Statistics);
    }
}
