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
    [Fact]
    public void Cookie_AlwaysOn_SpoofedRequestGetsOneSmallReplyAndNoState()
    {
        var serverListener = new LiteNetLib.EventBasedNetListener();
        var server = new LiteNetLib.NetManager(serverListener) { ConnectCookieMode = 2 };
        int requests = 0;
        serverListener.ConnectionRequestEvent += request => { requests++; request.Accept(); };
        using var attacker = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        try
        {
            Assert.True(server.Start(IPAddress.Loopback, IPAddress.IPv6Loopback, 0));
            var target = new IPEndPoint(IPAddress.Loopback, server.LocalPort);
            var data = new LiteNetLib.Utils.NetDataWriter();
            data.Put(new byte[64]);
            LiteNetLib.NetPacket packet = LiteNetLib.NetConnectRequestPacket.Make(data, target.Serialize(), DateTime.UtcNow.Ticks, 0);
            attacker.Send(packet.RawData, packet.Size, target);
            attacker.Client.ReceiveTimeout = 2000;
            IPEndPoint? from = null;
            byte[] reply = attacker.Receive(ref from);
            Poll(server, () => false, 200);
            Assert.Equal(0, requests);
            Assert.Equal(0, server.GetPeersCount(LiteNetLib.ConnectionState.Any));
            Assert.True(reply.Length <= packet.Size, $"reply {reply.Length} bytes for a {packet.Size} byte request");
            Assert.Equal(1, server.ConnectCookieChallenges);
        }
        finally
        {
            server.Stop();
        }
    }
    [Theory]
    [InlineData(2)]
    [InlineData(1)]
    public void Cookie_RealClientStillConnects(int mode)
    {
        var serverListener = new LiteNetLib.EventBasedNetListener();
        var server = new LiteNetLib.NetManager(serverListener) { ConnectCookieMode = mode, ConnectCookieAutoRequestsPerSecond = 0, ConnectCookiePressure = () => true };
        serverListener.ConnectionRequestEvent += request => request.Accept();
        var client = new LiteNetLib.NetManager(new LiteNetLib.EventBasedNetListener());
        try
        {
            Assert.True(server.Start(IPAddress.Loopback, IPAddress.IPv6Loopback, 0));
            Assert.True(client.Start());
            LiteNetLib.NetPeer clientSide = client.Connect(new IPEndPoint(IPAddress.Loopback, server.LocalPort), ReadOnlySpan<byte>.Empty);
            Assert.True(Poll(server, () => { client.PollEvents(); return clientSide.ConnectionState == LiteNetLib.ConnectionState.Connected; }, 3000));
            Assert.True(server.ConnectCookieChallenges >= 1);
            Assert.True(server.ConnectCookieActive);
        }
        finally
        {
            client.Stop();
            server.Stop();
        }
    }
    [Fact]
    public void Cookie_AutoMode_TurnsOnOnlyAfterTheThreshold()
    {
        var serverListener = new LiteNetLib.EventBasedNetListener();
        var server = new LiteNetLib.NetManager(serverListener) { ConnectCookieMode = 1, ConnectCookieAutoRequestsPerSecond = 3 };
        int requests = 0;
        serverListener.ConnectionRequestEvent += request => { requests++; request.Reject(Array.Empty<byte>(), 0, 0, true); };
        var attackers = new List<UdpClient>();
        try
        {
            Assert.True(server.Start(IPAddress.Loopback, IPAddress.IPv6Loopback, 0));
            Assert.False(server.ConnectCookieActive);
            var target = new IPEndPoint(IPAddress.Loopback, server.LocalPort);
            for (int i = 0; i < 20; i++)
            {
                var attacker = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                attackers.Add(attacker);
                LiteNetLib.NetPacket packet = LiteNetLib.NetConnectRequestPacket.Make(new LiteNetLib.Utils.NetDataWriter(), target.Serialize(), DateTime.UtcNow.Ticks + i, 0);
                attacker.Send(packet.RawData, packet.Size, target);
            }
            Poll(server, () => false, 300);
            Assert.InRange(requests, 1, 3);
            Assert.True(server.ConnectCookieActive);
        }
        finally
        {
            foreach (var attacker in attackers) attacker.Dispose();
            server.Stop();
        }
    }
    [Fact]
    public void ReliableQueue_ForAPeerThatNeverAcks_DisconnectsAtTheBudget()
    {
        var serverListener = new LiteNetLib.EventBasedNetListener();
        var server = new LiteNetLib.NetManager(serverListener) { MaxQueuedChannelBytesPerPeer = 32 * 1024, DisconnectTimeout = 30000 };
        LiteNetLib.NetPeer? accepted = null;
        bool dropped = false;
        serverListener.ConnectionRequestEvent += request => accepted = request.Accept();
        serverListener.PeerDisconnectedEvent += (peer, info) => dropped = true;
        using var attacker = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        try
        {
            Assert.True(server.Start(IPAddress.Loopback, IPAddress.IPv6Loopback, 0));
            var target = new IPEndPoint(IPAddress.Loopback, server.LocalPort);
            LiteNetLib.NetPacket packet = LiteNetLib.NetConnectRequestPacket.Make(new LiteNetLib.Utils.NetDataWriter(), target.Serialize(), DateTime.UtcNow.Ticks, 0);
            attacker.Send(packet.RawData, packet.Size, target);
            Assert.True(Poll(server, () => accepted != null, 2000));
            for (int i = 0; i < 200; i++) accepted!.Send(new byte[1000], LiteNetLib.DeliveryMethod.ReliableOrdered);
            Assert.True(Poll(server, () => dropped, 3000));
        }
        finally
        {
            server.Stop();
        }
    }
    [Fact]
    public void ProtectionSettings_ClampRoundAndApplyThroughTheRegistry()
    {
        var holder = new Configuration { MaxPendingLogins = 512 };
        BasisProtectionSettings.RegisterField(() => holder, typeof(Configuration), nameof(Configuration.MaxPendingLogins), "Test", 0, 1000);
        Assert.True(BasisProtectionSettings.TrySet(nameof(Configuration.MaxPendingLogins), 5000.7, out double clamped));
        Assert.Equal(1000, clamped);
        Assert.Equal(1000, holder.MaxPendingLogins);
        Assert.True(BasisProtectionSettings.TrySet(nameof(Configuration.MaxPendingLogins), 12.6, out double rounded));
        Assert.Equal(13, holder.MaxPendingLogins);
        Assert.False(BasisProtectionSettings.TrySet("NoSuchSetting", 1, out _));
        Assert.Equal("Max Pending Logins Per Address", BasisProtectionSettings.Humanize("MaxPendingLoginsPerAddress"));
        var writer = new NetDataWriter();
        BasisProtectionSettings.Write(writer);
        Assert.True(BasisProtectionSettings.TryRead(new NetDataReader(writer.CopyData()), out List<BasisProtectionSettings.Info> infos));
        BasisProtectionSettings.Info info = infos.Single(i => i.Key == nameof(Configuration.MaxPendingLogins));
        Assert.Equal(13, info.Value);
        Assert.True(info.WholeNumber);
        Assert.False(string.IsNullOrEmpty(info.Description));
        var changes = new List<KeyValuePair<string, double>> { new("A", 1.5), new("B", 2) };
        var changeWriter = new NetDataWriter();
        BasisProtectionSettings.WriteChanges(changeWriter, changes);
        Assert.True(BasisProtectionSettings.TryReadChanges(new NetDataReader(changeWriter.CopyData()), out var read));
        Assert.Equal(changes, read);
        BasisProtectionSettings.RegisterField(() => NetworkServer.Configuration, typeof(Configuration), nameof(Configuration.MaxPendingLogins), BasisServerProtectionSettings.Logins, 0, 65535);
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
