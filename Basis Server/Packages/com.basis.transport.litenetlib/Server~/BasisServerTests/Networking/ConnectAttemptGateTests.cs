using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Basis.Network.Core;
using Xunit;

namespace BasisServerTests;

[Collection("LiteNetLib global logger")]
public class ConnectAttemptGateTests
{
    private static readonly long Second = Stopwatch.Frequency;
    [Fact]
    public void Gate_AdmitsTheBurstThenDropsUntilItRefills()
    {
        var gate = new BasisConnectAttemptGate(2, 3);
        IPAddress address = IPAddress.Parse("203.0.113.7");
        Assert.True(gate.TryAdmit(address, 0));
        Assert.True(gate.TryAdmit(address, 0));
        Assert.True(gate.TryAdmit(address, 0));
        Assert.False(gate.TryAdmit(address, 0));
        Assert.False(gate.TryAdmit(address, Second / 4));
        Assert.True(gate.TryAdmit(address, Second / 2 + 1));
        Assert.False(gate.TryAdmit(address, Second / 2 + 1));
        Assert.True(gate.TryAdmit(IPAddress.Parse("203.0.113.8"), 0));
        Assert.Equal(5, gate.Admitted);
        Assert.Equal(3, gate.Dropped);
    }
    [Fact]
    public void Gate_CountsIPv6PerSlash64AndMappedIPv4AsIPv4()
    {
        var gate = new BasisConnectAttemptGate(0.001, 1);
        Assert.True(gate.TryAdmit(IPAddress.Parse("2001:db8:1:2::1"), 0));
        Assert.False(gate.TryAdmit(IPAddress.Parse("2001:db8:1:2:ffff::9"), 0));
        Assert.True(gate.TryAdmit(IPAddress.Parse("2001:db8:1:3::1"), 0));
        Assert.True(gate.TryAdmit(IPAddress.Parse("198.51.100.4"), 0));
        Assert.False(gate.TryAdmit(IPAddress.Parse("::ffff:198.51.100.4"), 0));
    }
    [Fact]
    public void Gate_StaysBoundedUnderManyAddresses()
    {
        var gate = new BasisConnectAttemptGate(1, 1);
        for (int i = 0; i < BasisConnectAttemptGate.MaxTrackedAddresses + 1000; i++) gate.TryAdmit(new IPAddress(BitConverter.GetBytes(i + 1)), 0);
        Assert.True(gate.TrackedAddresses <= BasisConnectAttemptGate.MaxTrackedAddresses);
    }
    [Fact]
    public void Reject_KeepsNoPeerForAnAttackerThatNeverAnswers()
    {
        var serverListener = new EventBasedNetListener();
        var server = new LiteNetLib.NetManager(serverListener) { DisconnectTimeout = 30000 };
        int requests = 0;
        serverListener.ConnectionRequestEvent += request =>
        {
            Interlocked.Increment(ref requests);
            var writer = new NetDataWriter();
            writer.Put("go away");
            request.Reject(writer);
        };
        var attackers = new List<UdpClient>();
        try
        {
            Assert.True(server.Start(IPAddress.Loopback, IPAddress.IPv6Loopback, 0));
            var target = new IPEndPoint(IPAddress.Loopback, server.LocalPort);
            for (int i = 0; i < 32; i++)
            {
                var attacker = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                attackers.Add(attacker);
                LiteNetLib.NetPacket packet = LiteNetLib.NetConnectRequestPacket.Make(new LiteNetLib.Utils.NetDataWriter(), target.Serialize(), DateTime.UtcNow.Ticks + i, 0);
                attacker.Send(packet.RawData, packet.Size, target);
            }
            Assert.True(Poll(server, new List<LiteNetLib.NetManager>(), () => Volatile.Read(ref requests) == 32));
            Poll(server, new List<LiteNetLib.NetManager>(), () => false, 300);
            Assert.Equal(0, server.GetPeersCount(LiteNetLib.ConnectionState.Any));
        }
        finally
        {
            foreach (var attacker in attackers) attacker.Dispose();
            server.Stop();
        }
    }
    [Fact]
    public void Reject_StillTellsARealClientWhy()
    {
        var serverListener = new EventBasedNetListener();
        var server = new LiteNetLib.NetManager(serverListener);
        serverListener.ConnectionRequestEvent += request =>
        {
            var writer = new NetDataWriter();
            writer.Put("go away");
            request.Reject(writer);
        };
        var clientListener = new LiteNetLib.EventBasedNetListener();
        string? reason = null;
        clientListener.PeerDisconnectedEvent += (peer, info) => { if (info.Reason == LiteNetLib.DisconnectReason.ConnectionRejected) reason = info.AdditionalData.GetString(); };
        var client = new LiteNetLib.NetManager(clientListener);
        try
        {
            Assert.True(server.Start(IPAddress.Loopback, IPAddress.IPv6Loopback, 0));
            Assert.True(client.Start());
            client.Connect(new IPEndPoint(IPAddress.Loopback, server.LocalPort), ReadOnlySpan<byte>.Empty);
            Assert.True(Poll(server, new List<LiteNetLib.NetManager> { client }, () => reason != null));
            Assert.Equal("go away", reason);
            Assert.Equal(0, server.GetPeersCount(LiteNetLib.ConnectionState.Any));
        }
        finally
        {
            client.Stop();
            server.Stop();
        }
    }
    [Fact]
    public void Filter_DropsRequestsBeforeTheServerSeesThem()
    {
        var serverListener = new LiteNetLib.EventBasedNetListener();
        var gate = new BasisConnectAttemptGate(0.001, 2) { ExemptLoopback = false };
        var server = new LiteNetLib.NetManager(serverListener) { ConnectRequestFilter = gate.TryAdmit };
        int requests = 0;
        serverListener.ConnectionRequestEvent += request => { requests++; request.Accept(); };
        var clients = new List<LiteNetLib.NetManager>();
        try
        {
            Assert.True(server.Start(IPAddress.Loopback, IPAddress.IPv6Loopback, 0));
            for (int i = 0; i < 5; i++)
            {
                var client = new LiteNetLib.NetManager(new LiteNetLib.EventBasedNetListener()) { ReconnectDelay = 100, MaxConnectAttempts = 5 };
                Assert.True(client.Start());
                clients.Add(client);
                client.Connect(new IPEndPoint(IPAddress.Loopback, server.LocalPort), ReadOnlySpan<byte>.Empty);
            }
            Poll(server, clients, () => false, 1500);
            Assert.Equal(2, requests);
            Assert.Equal(2, server.ConnectedPeersCount);
            Assert.True(gate.Dropped >= 3);
        }
        finally
        {
            foreach (var client in clients) client.Stop();
            server.Stop();
        }
    }
    private static bool Poll(LiteNetLib.NetManager server, List<LiteNetLib.NetManager> clients, Func<bool> done, int timeoutMs = 5000)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            server.PollEvents();
            foreach (var client in clients) client.PollEvents();
            if (done()) return true;
            Thread.Sleep(10);
        }
        return done();
    }
}
