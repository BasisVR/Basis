using Basis.Network.Core;
using System.Net;
using System.Net.Sockets;
using Xunit;

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
        NetDataWriter reply = TransportTestSupport.Writer(1, 2);
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
        Assert.False(BasisNetworkStackRegistry.ContainsStack("", "websocket"));
    }

    [Fact]
    public void RegistryBuildsACompositeOnlyWhenMoreThanOneStackIsUsable()
    {
        string original = BasisNetworkStackRegistry.ActiveStackId;
        string zeroth = ConfigTestSupport.NewStackId();
        string first = ConfigTestSupport.NewStackId();
        string second = ConfigTestSupport.NewStackId();
        BasisNetworkStackRegistry.Register(zeroth, "Zeroth", (listener, configuration) => new ScriptedTransport(zeroth, listener));
        BasisNetworkStackRegistry.Register(first, "First", (listener, configuration) => new ScriptedTransport(first, listener));
        BasisNetworkStackRegistry.Register(second, "Second", (listener, configuration) => new ScriptedTransport(second, listener));
        try
        {
            NetManager all = BasisNetworkStackRegistry.Create($"{zeroth}, {first}, {second}", new EventBasedNetListener(), new Configuration());
            BasisMultiTransportNetManager composite = Assert.IsType<BasisMultiTransportNetManager>(all);
            Assert.Equal(zeroth, Assert.IsType<ScriptedTransport>(composite.Transports[0]).Id);
            Assert.Equal(first, Assert.IsType<ScriptedTransport>(composite.Transports[1]).Id);
            Assert.Equal(second, Assert.IsType<ScriptedTransport>(composite.Transports[2]).Id);
            Assert.Equal($"{zeroth},{first},{second}", BasisNetworkStackRegistry.ActiveStackId);
            Assert.NotNull(all.FindTransport<ScriptedTransport>());

            NetManager one = BasisNetworkStackRegistry.Create(second + ", not-a-real-stack", new EventBasedNetListener(), new Configuration());
            Assert.Equal(second, Assert.IsType<ScriptedTransport>(one).Id);
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
}
