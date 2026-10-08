using Basis.Network.Core;
using BasisNetworkCore.Security;
using BasisNetworkServer.Security;
using BasisServerHandle;
using System.Net;
using System.Net.Sockets;
using Xunit;
using static Basis.Network.Core.Compression.BasisAvatarBitPacking;

namespace BasisServerTests;

internal sealed class ScriptedTransport : NetManager, IBasisSharedPeerIds
{
    public readonly string Id;
    public readonly EventBasedNetListener Listener;
    public BasisPeerIdAllocator? Allocator;
    public int Started;
    public int Stopped;
    public bool Running = true;
    public readonly List<IPEndPoint> UnconnectedSentTo = new();

    public ScriptedTransport(string id, EventBasedNetListener listener)
    {
        Id = id;
        Listener = listener;
    }

    public int Connected { get; set; }
    public void UsePeerIdAllocator(BasisPeerIdAllocator allocator) => Allocator = allocator;
    public void Start(IPAddress iPv4Address, IPAddress iPv6Address, int setPort) => Started++;
    public void Stop() => Stopped++;
    public NetPeer Connect(string sIP, int port, NetDataWriter writer) => null!;
    public bool SendUnconnectedMessage(NetDataWriter writer, IPEndPoint remoteEndPoint)
    {
        UnconnectedSentTo.Add(remoteEndPoint);
        return true;
    }
    public NetStatistics Statistics { get; } = new NetStatistics { BytesSent = 100, BytesReceived = 50, PacketsSent = 4, PacketsReceived = 2 };
    public int ConnectedPeersCount => Connected;
    public string StackId => Id;
    public bool IsRunning => Running;
    public long UnreliableDropped => 3;
    public long PriorityUnreliableDropped => 1;
    public string ListenDescription => Id + " listener";
}

[Collection("BasisServer shared network statics")]
public class MultiTransportTests
{
    private static (BasisMultiTransportNetManager Composite, List<ScriptedTransport> Transports, EventBasedNetListener Outer) Scripted(params string[] ids)
    {
        List<ScriptedTransport> made = new();
        EventBasedNetListener outer = new EventBasedNetListener();
        BasisMultiTransportNetManager composite = new BasisMultiTransportNetManager(ids, outer, new Configuration(), (id, listener, configuration) =>
        {
            ScriptedTransport transport = new ScriptedTransport(id, listener);
            made.Add(transport);
            return transport;
        });
        return (composite, made, outer);
    }

    [Fact]
    public void EveryTransportDrawsFromOneIdAllocator()
    {
        var (composite, transports, _) = Scripted("a", "b");
        Assert.Equal(2, transports.Count);
        Assert.Same(composite.PeerIds, transports[0].Allocator);
        Assert.Same(composite.PeerIds, transports[1].Allocator);
        Assert.Equal("a,b", ((NetManager)composite).StackId);
    }

    [Fact]
    public void EventsFromAnyTransportReachTheServerListener()
    {
        var (_, transports, outer) = Scripted("a", "b");
        List<string> seen = new();
        outer.ConnectionRequestEvent += _ => seen.Add("request");
        outer.PeerConnectedEvent += _ => seen.Add("connected");
        outer.NetworkReceiveEvent += (_, reader, channel, _) => seen.Add("receive" + channel);
        outer.PeerDisconnectedEvent += (_, info) => seen.Add("disconnected" + info.Reason);
        outer.NetworkErrorEvent += (_, error) => seen.Add("error" + error);
        FakeNetPeer peer = new FakeNetPeer(7, "203.0.113.4");
        transports[1].Listener.RaiseConnectionRequest(LifecycleSupport.Request(Array.Empty<byte>()));
        transports[0].Listener.RaisePeerConnected(peer);
        transports[1].Listener.RaiseNetworkReceive(peer, NetPacketReader.Create(new byte[] { 1 }, 0, 1, () => { }), 18, DeliveryMethod.ReliableOrdered);
        transports[0].Listener.RaisePeerDisconnected(peer, new DisconnectInfo { Reason = DisconnectReason.Timeout });
        transports[1].Listener.RaiseNetworkError(new IPEndPoint(IPAddress.Loopback, 1), SocketError.ConnectionReset);
        Assert.Equal(new[] { "request", "connected", "receive18", "disconnectedTimeout", "errorConnectionReset" }, seen);
    }

    [Fact]
    public void UnconnectedRepliesGoBackThroughTheTransportTheQueryCameIn()
    {
        var (composite, transports, outer) = Scripted("a", "b");
        int raised = 0;
        outer.NetworkReceiveUnconnectedEvent += (_, _) => raised++;
        IPEndPoint asker = new IPEndPoint(IPAddress.Parse("198.51.100.20"), 5555);
        transports[1].Listener.RaiseNetworkReceiveUnconnected(asker, NetPacketReader.Create(new byte[] { 1 }, 0, 1, () => { }));
        Assert.Equal(1, raised);
        NetDataWriter reply = WebSocketTestSupport.Writer(1, 2);
        Assert.True(composite.SendUnconnectedMessage(reply, asker));
        Assert.Equal(new[] { asker }, transports[1].UnconnectedSentTo);
        Assert.Empty(transports[0].UnconnectedSentTo);
        IPEndPoint stranger = new IPEndPoint(IPAddress.Parse("198.51.100.21"), 5555);
        composite.SendUnconnectedMessage(reply, stranger);
        Assert.Equal(new[] { stranger }, transports[0].UnconnectedSentTo);
    }

    [Fact]
    public void CountsStatisticsAndDropsAreSummed()
    {
        var (composite, transports, _) = Scripted("a", "b");
        transports[0].Connected = 2;
        transports[1].Connected = 3;
        NetManager manager = composite;
        Assert.Equal(5, manager.ConnectedPeersCount);
        Assert.Equal(200, manager.Statistics.BytesSent);
        Assert.Equal(100, manager.Statistics.BytesReceived);
        Assert.Equal(8, manager.Statistics.PacketsSent);
        Assert.Equal(6, manager.UnreliableDropped);
        Assert.Equal(2, manager.PriorityUnreliableDropped);
        Assert.Equal("a listener, b listener", manager.ListenDescription);
        Assert.True(manager.IsRunning);
        transports[1].Running = false;
        Assert.False(manager.IsRunning);
    }

    [Fact]
    public void StartAndStopReachEveryTransportAndStopFreesTheIds()
    {
        var (composite, transports, _) = Scripted("a", "b");
        ((NetManager)composite).Start();
        composite.PeerIds.Allocate();
        composite.PeerIds.Allocate();
        composite.Stop();
        Assert.All(transports, transport => Assert.Equal(1, transport.Started));
        Assert.All(transports, transport => Assert.Equal(1, transport.Stopped));
        Assert.Equal(0, composite.PeerIds.LiveCount);
        Assert.Equal(0, composite.PeerIds.Allocate());
    }

    [Fact]
    public void StackListsParseLeniently()
    {
        Assert.Equal(new[] { BasisNetworkStackRegistry.LiteNetLibId }, BasisNetworkStackRegistry.ParseStackList(""));
        Assert.Equal(new[] { BasisNetworkStackRegistry.LiteNetLibId }, BasisNetworkStackRegistry.ParseStackList(null!));
        Assert.Equal(new[] { "litenetlib", "websocket" }, BasisNetworkStackRegistry.ParseStackList("litenetlib,websocket"));
        Assert.Equal(new[] { "websocket", "litenetlib" }, BasisNetworkStackRegistry.ParseStackList(" websocket ; litenetlib "));
        Assert.Equal(new[] { "litenetlib" }, BasisNetworkStackRegistry.ParseStackList("litenetlib, LiteNetLib"));
        Assert.True(BasisNetworkStackRegistry.ContainsStack("litenetlib+websocket", "WebSocket"));
        Assert.False(BasisNetworkStackRegistry.ContainsStack("", BasisNetworkStackRegistry.WebSocketId));
    }

    [Fact]
    public void RegistryBuildsACompositeOnlyWhenMoreThanOneStackIsUsable()
    {
        string original = BasisNetworkStackRegistry.ActiveStackId;
        try
        {
            NetManager both = BasisNetworkStackRegistry.Create("litenetlib, websocket", new EventBasedNetListener(), new Configuration());
            BasisMultiTransportNetManager composite = Assert.IsType<BasisMultiTransportNetManager>(both);
            Assert.IsType<LNLNetManager>(composite.Transports[0]);
            Assert.IsType<BasisWebSocketNetManager>(composite.Transports[1]);
            Assert.Equal("litenetlib,websocket", BasisNetworkStackRegistry.ActiveStackId);
            Assert.NotNull(both.LiteNetLibManager());
            Assert.NotNull(both.FindTransport<BasisWebSocketNetManager>());

            NetManager one = BasisNetworkStackRegistry.Create("websocket, not-a-real-stack", new EventBasedNetListener(), new Configuration());
            Assert.IsType<BasisWebSocketNetManager>(one);
            Assert.Null(one.LiteNetLibManager());
            Assert.Single(one.Transports());
        }
        finally
        {
            BasisNetworkStackRegistry.SetActiveStackId(original);
        }
    }

    [Fact]
    public void TickActiveTicksEveryStackOfAComposite()
    {
        string original = BasisNetworkStackRegistry.ActiveStackId;
        string first = ConfigTestSupport.NewStackId();
        string second = ConfigTestSupport.NewStackId();
        int ticks = 0;
        BasisNetworkStackRegistry.Register(first, "First", (listener, configuration) => null!);
        BasisNetworkStackRegistry.Register(second, "Second", (listener, configuration) => null!);
        BasisNetworkStackRegistry.RegisterTick(first, () => ticks++);
        BasisNetworkStackRegistry.RegisterTick(second, () => ticks += 10);
        try
        {
            BasisNetworkStackRegistry.SetActiveStackId(first + "," + second);
            BasisNetworkStackRegistry.TickActive();
            Assert.Equal(11, ticks);
        }
        finally
        {
            BasisNetworkStackRegistry.SetActiveStackId(original);
        }
    }

    [Fact]
    public async Task LiteNetLibAndWebSocketPlayersGetDistinctIdsFromOneSpace()
    {
        int port = WebSocketTestSupport.FreeTcpAndUdpPort();
        EventBasedNetListener serverListener = new EventBasedNetListener();
        RecordedEvents server = new RecordedEvents(serverListener);
        List<NetPeer> accepted = new();
        server.OnRequest = request =>
        {
            NetPeer peer = request.Accept();
            lock (accepted) accepted.Add(peer);
            return peer;
        };
        BasisMultiTransportNetManager composite = new BasisMultiTransportNetManager(
            new[] { BasisNetworkStackRegistry.LiteNetLibId, BasisNetworkStackRegistry.WebSocketId },
            serverListener,
            new Configuration(),
            (id, listener, configuration) => id == BasisNetworkStackRegistry.WebSocketId
                ? new BasisWebSocketNetManager(listener, WebSocketTestSupport.FastConfig())
                : BasisNetworkStackRegistry.CreateSingle(id, listener, configuration));
        composite.Start(IPAddress.Loopback, IPAddress.IPv6Loopback, port);
        EventBasedNetListener webListener = new EventBasedNetListener();
        RecordedEvents web = new RecordedEvents(webListener);
        BasisWebSocketNetManager webClient = new BasisWebSocketNetManager(webListener, WebSocketTestSupport.FastConfig());
        EventBasedNetListener udpListener = new EventBasedNetListener();
        RecordedEvents udp = new RecordedEvents(udpListener);
        NetManager udpClient = BasisNetworkStackRegistry.CreateSingle(BasisNetworkStackRegistry.LiteNetLibId, udpListener, new Configuration());
        try
        {
            Assert.True(((NetManager)composite).IsRunning);
            ((NetManager)webClient).Start();
            udpClient.Start();
            NetPeer webPeer = webClient.Connect("127.0.0.1", port, WebSocketTestSupport.Writer(1));
            NetPeer udpPeer = udpClient.Connect("127.0.0.1", port, WebSocketTestSupport.Writer(2));
            await web.NextConnected();
            await udp.NextConnected();
            await WebSocketTestSupport.Until(() => { lock (accepted) return accepted.Count == 2; });
            NetPeer[] peers;
            lock (accepted) peers = accepted.ToArray();
            Assert.NotEqual(peers[0].Id, peers[1].Id);
            Assert.All(peers, peer => Assert.True(composite.PeerIds.IsLive(peer.Id)));
            Assert.Contains(peers, peer => peer.StackId == BasisNetworkStackRegistry.WebSocketId && peer.Id == webPeer.RemoteId);
            Assert.Contains(peers, peer => peer.StackId == BasisNetworkStackRegistry.LiteNetLibId && peer.Id == udpPeer.RemoteId);
            Assert.Equal(2, ((NetManager)composite).ConnectedPeersCount);

            NetPeer serverSideWeb = peers.First(peer => peer.StackId == BasisNetworkStackRegistry.WebSocketId);
            NetPeer serverSideUdp = peers.First(peer => peer.StackId == BasisNetworkStackRegistry.LiteNetLibId);
            serverSideWeb.Send(new byte[] { 42 }, BasisNetworkCommons.ChatChannel, DeliveryMethod.ReliableOrdered);
            serverSideUdp.Send(new byte[] { 43 }, BasisNetworkCommons.ChatChannel, DeliveryMethod.ReliableOrdered);
            Assert.Equal(new byte[] { 42 }, (await web.NextReceived()).Data);
            Assert.Equal(new byte[] { 43 }, (await udp.NextReceived()).Data);
        }
        finally
        {
            webClient.Stop();
            udpClient.Stop();
            composite.Stop();
        }
    }

    [Fact]
    public void ACompositeWithATransportThatCannotBindIsNotRunning()
    {
        int port = WebSocketTestSupport.FreeTcpAndUdpPort();
        TcpListener blocker = new TcpListener(IPAddress.Loopback, port);
        blocker.Start();
        BasisMultiTransportNetManager composite = new BasisMultiTransportNetManager(
            new[] { BasisNetworkStackRegistry.LiteNetLibId, BasisNetworkStackRegistry.WebSocketId },
            new EventBasedNetListener(),
            new Configuration(),
            (id, listener, configuration) => id == BasisNetworkStackRegistry.WebSocketId
                ? new BasisWebSocketNetManager(listener, WebSocketTestSupport.FastConfig())
                : BasisNetworkStackRegistry.CreateSingle(id, listener, configuration));
        try
        {
            composite.Start(IPAddress.Loopback, IPAddress.IPv6Loopback, port);
            NetManager manager = composite;
            Assert.False(manager.IsRunning);
            Assert.True(composite.Transports[0].IsRunning);
            Assert.False(composite.Transports[1].IsRunning);
        }
        finally
        {
            composite.Stop();
            blocker.Stop();
        }
    }
}

[Collection("BasisServer shared network statics")]
public class MixedTransportServerTests
{
    [Fact]
    public async Task TheServerAdmitsAWebPlayerAndAUdpPlayerIntoOneWorld()
    {
        using ServerStaticsScope scope = new ServerStaticsScope();
        int port = WebSocketTestSupport.FreeTcpAndUdpPort();
        Configuration config = new Configuration
        {
            SetPort = (ushort)port,
            PeerLimit = 100,
            UseAuth = false,
            UseAuthIdentity = false,
            HasFileSupport = false,
            EnableStatistics = false,
            BasisUserRestrictionMode = BasisUserRestrictionMode.Normal,
            NetworkStackId = "litenetlib,websocket",
            OverrideAutoDiscoveryOfIpv = true,
            IPv4Address = "127.0.0.1",
            IPv6Address = "::1",
        };
        NetworkServer.Configuration = config;
        NetworkServer.Auth = new FakeAuth { Result = true };
        NetworkServer.AuthIdentity = new MapAuthIdentity();
        NetworkServer.AllowList = new BasisAllowList();
        NetworkServer.BanList = new BasisBanList();
        NetworkServer.HighQualityLength = ConvertToSize(BitQuality.High);
        Assert.True(NetworkServer.SetupServer(config));
        BasisServerHandleEvents.SubscribeServerEvents();

        EventBasedNetListener webListener = new EventBasedNetListener();
        RecordedEvents web = new RecordedEvents(webListener);
        BasisWebSocketNetManager webClient = new BasisWebSocketNetManager(webListener, WebSocketTestSupport.FastConfig());
        EventBasedNetListener udpListener = new EventBasedNetListener();
        RecordedEvents udp = new RecordedEvents(udpListener);
        NetManager udpClient = BasisNetworkStackRegistry.CreateSingle(BasisNetworkStackRegistry.LiteNetLibId, udpListener, new Configuration());
        try
        {
            Assert.IsType<BasisMultiTransportNetManager>(NetworkServer.Server);
            ((NetManager)webClient).Start();
            udpClient.Start();
            byte[] webJoin = LifecycleSupport.ConnectPayload(BasisNetworkVersion.ServerVersion, Array.Empty<byte>(), LifecycleSupport.MakeReady(LifecycleSupport.NewUuid(), "Browser Player"));
            NetPeer webPeer = webClient.Connect("127.0.0.1", port, WebSocketTestSupport.Writer(webJoin));
            await web.NextConnected();
            await WebSocketTestSupport.Until(() => web.Received.Any(item => item.Channel == BasisNetworkCommons.metaDataChannel));
            await WebSocketTestSupport.Until(() => NetworkServer.AuthenticatedPeers.Count == 1);

            byte[] udpJoin = LifecycleSupport.ConnectPayload(BasisNetworkVersion.ServerVersion, Array.Empty<byte>(), LifecycleSupport.MakeReady(LifecycleSupport.NewUuid(), "Desktop Player"));
            NetPeer udpPeer = udpClient.Connect("127.0.0.1", port, WebSocketTestSupport.Writer(udpJoin));
            await udp.NextConnected();
            await WebSocketTestSupport.Until(() => udp.Received.Any(item => item.Channel == BasisNetworkCommons.metaDataChannel));
            await WebSocketTestSupport.Until(() => NetworkServer.AuthenticatedPeers.Count == 2);

            NetPeer[] admitted = NetworkServer.AuthenticatedPeers.Values.ToArray();
            Assert.Contains(admitted, peer => peer.StackId == BasisNetworkStackRegistry.WebSocketId && peer.Id == webPeer.RemoteId);
            Assert.Contains(admitted, peer => peer.StackId == BasisNetworkStackRegistry.LiteNetLibId && peer.Id == udpPeer.RemoteId);
            Assert.NotEqual(webPeer.RemoteId, udpPeer.RemoteId);
            Assert.Single(NetworkServer.PeersOnTransport(BasisNetworkStackRegistry.WebSocketId));

            await WebSocketTestSupport.Until(() => web.Received.Any(item => item.Channel == BasisNetworkCommons.CreateRemotePlayersForNewPeerChannel), 8000);
            await WebSocketTestSupport.Until(() => udp.Received.Any(item => item.Channel == BasisNetworkCommons.CreateRemotePlayersForNewPeerChannel), 8000);

            webPeer.Disconnect();
            await WebSocketTestSupport.Until(() => NetworkServer.AuthenticatedPeers.Count == 1);
            Assert.Equal(udpPeer.RemoteId, NetworkServer.AuthenticatedPeers.Keys.Single());
        }
        finally
        {
            webClient.Stop();
            udpClient.Stop();
            DateTime deadline = DateTime.UtcNow.AddSeconds(3);
            while (!NetworkServer.AuthenticatedPeers.IsEmpty && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }
            foreach (NetPeer straggler in NetworkServer.AuthenticatedPeers.Values.ToArray())
            {
                BasisServerHandleEvents.HandlePeerDisconnected(straggler, new DisconnectInfo { Reason = DisconnectReason.DisconnectPeerCalled });
            }
            BasisServerHandleEvents.StopWorker();
            NetworkServer.Server = null!;
            NetworkServer.Listener = null!;
        }
    }
}
