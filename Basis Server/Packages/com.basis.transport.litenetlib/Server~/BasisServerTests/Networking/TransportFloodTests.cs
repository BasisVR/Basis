using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Basis.Network.Core;
using BasisNetworkServer.Security;
using Xunit;

namespace BasisServerTests;

[Collection("LiteNetLib global logger")]
public class TransportFloodTests
{
    [Fact]
    public void NatResponse_FromAnyoneButTheIntroducer_SendsNothing()
    {
        var server = new LiteNetLib.NetManager(new LiteNetLib.EventBasedNetListener()) { NatPunchEnabled = true, UnconnectedMessagesEnabled = true };
        var attacker = new LiteNetLib.NetManager(new LiteNetLib.EventBasedNetListener()) { NatPunchEnabled = true };
        using var victim = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        try
        {
            Assert.True(server.Start(IPAddress.Loopback, IPAddress.IPv6Loopback, 0));
            Assert.True(attacker.Start(IPAddress.Loopback, IPAddress.IPv6Loopback, 0));
            var victimEndPoint = (IPEndPoint)victim.Client.LocalEndPoint!;
            var serverEndPoint = new IPEndPoint(IPAddress.Loopback, server.LocalPort);
            attacker.NatPunchModule.NatIntroduce(victimEndPoint, victimEndPoint, 20, serverEndPoint, serverEndPoint, 20, "token");
            int received = 0;
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 500)
            {
                server.PollEvents();
                attacker.PollEvents();
                while (victim.Available > 0)
                {
                    IPEndPoint? from = null;
                    victim.Receive(ref from);
                    if (from.Port == server.LocalPort) received++;
                }
                Thread.Sleep(10);
            }
            Assert.Equal(0, received);
        }
        finally
        {
            attacker.Stop();
            server.Stop();
        }
    }
    [Fact]
    public void Disconnect_OfAPeerThatNeverAnswers_IsForgottenAfterTheLinger()
    {
        var serverListener = new LiteNetLib.EventBasedNetListener();
        var server = new LiteNetLib.NetManager(serverListener) { DisconnectTimeout = 30000, ShutdownLingerMs = 400 };
        LiteNetLib.NetPeer? accepted = null;
        serverListener.ConnectionRequestEvent += request => accepted = request.Accept();
        using var attacker = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        try
        {
            Assert.True(server.Start(IPAddress.Loopback, IPAddress.IPv6Loopback, 0));
            var target = new IPEndPoint(IPAddress.Loopback, server.LocalPort);
            LiteNetLib.NetPacket packet = LiteNetLib.NetConnectRequestPacket.Make(new LiteNetLib.Utils.NetDataWriter(), target.Serialize(), DateTime.UtcNow.Ticks, 0);
            attacker.Send(packet.RawData, packet.Size, target);
            Assert.True(Poll(server, () => accepted != null, 2000));
            server.DisconnectPeer(accepted!);
            Assert.True(Poll(server, () => server.GetPeersCount(LiteNetLib.ConnectionState.Any) == 0, 3000));
        }
        finally
        {
            server.Stop();
        }
    }
    [Theory]
    [InlineData(16 * 1024, false)]
    [InlineData(4 * 1024 * 1024, true)]
    public void Fragments_OverThePeerBudget_DisconnectInsteadOfBuffering(int budget, bool delivered)
    {
        var serverListener = new LiteNetLib.EventBasedNetListener();
        var server = new LiteNetLib.NetManager(serverListener) { MaxHeldFragmentBytesPerPeer = budget };
        bool received = false, dropped = false;
        LiteNetLib.NetPeer? serverSide = null;
        serverListener.ConnectionRequestEvent += request => request.Accept();
        serverListener.PeerConnectedEvent += peer => serverSide = peer;
        serverListener.PeerDisconnectedEvent += (peer, info) => dropped = true;
        serverListener.NetworkReceiveEvent += (peer, reader, channel, method) => { received = reader.AvailableBytes == 64 * 1024; reader.Recycle(); };
        var client = new LiteNetLib.NetManager(new LiteNetLib.EventBasedNetListener());
        try
        {
            Assert.True(server.Start(IPAddress.Loopback, IPAddress.IPv6Loopback, 0));
            Assert.True(client.Start());
            LiteNetLib.NetPeer clientSide = client.Connect(new IPEndPoint(IPAddress.Loopback, server.LocalPort), ReadOnlySpan<byte>.Empty);
            Assert.True(Poll(server, () => { client.PollEvents(); return serverSide != null && clientSide.ConnectionState == LiteNetLib.ConnectionState.Connected; }, 3000));
            clientSide.Send(new byte[64 * 1024], LiteNetLib.DeliveryMethod.ReliableOrdered);
            Poll(server, () => { client.PollEvents(); return received || dropped; }, 3000);
            Assert.Equal(delivered, received);
            Assert.Equal(!delivered, dropped);
        }
        finally
        {
            client.Stop();
            server.Stop();
        }
    }
    [Fact]
    public void PendingLogins_AreCappedPerAddressAndInTotal()
    {
        BasisPendingLoginGate.Reset();
        try
        {
            IPAddress a = IPAddress.Parse("203.0.113.10"), b = IPAddress.Parse("203.0.113.11");
            Assert.True(BasisPendingLoginGate.TryReserve(a, 3, 2, out ulong a1));
            Assert.True(BasisPendingLoginGate.TryReserve(a, 3, 2, out ulong a2));
            Assert.False(BasisPendingLoginGate.TryReserve(a, 3, 2, out _));
            Assert.True(BasisPendingLoginGate.TryReserve(b, 3, 2, out ulong b1));
            Assert.False(BasisPendingLoginGate.TryReserve(IPAddress.Parse("203.0.113.12"), 3, 2, out _));
            Assert.True(BasisPendingLoginGate.TryReserve(IPAddress.Loopback, 3, 2, out _));
            var peer = new FakeNetPeer(7, "203.0.113.10");
            BasisPendingLoginGate.Bind(7, peer, a1);
            BasisPendingLoginGate.Release(7, new FakeNetPeer(7, "203.0.113.10"));
            Assert.Equal(3, BasisPendingLoginGate.Pending);
            BasisPendingLoginGate.Release(7, peer);
            Assert.Equal(2, BasisPendingLoginGate.Pending);
            Assert.True(BasisPendingLoginGate.TryReserve(a, 3, 2, out _));
            BasisPendingLoginGate.Cancel(a2);
            BasisPendingLoginGate.Cancel(b1);
        }
        finally
        {
            BasisPendingLoginGate.Reset();
        }
    }
    private static bool Poll(LiteNetLib.NetManager server, Func<bool> done, int timeoutMs)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            server.PollEvents();
            if (done()) return true;
            Thread.Sleep(10);
        }
        return done();
    }
}
