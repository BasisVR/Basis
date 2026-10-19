using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using BasisNetworkClient;
using Basis.Contrib.Auth.DecentralizedIds;
using Basis.Contrib.Auth.DecentralizedIds.Newtypes;
using Basis.Contrib.Crypto;
using Basis.Network.Core;
using BasisNetworkCore.Security;
using BasisNetworkServer.Security;
using BasisServerHandle;
using Xunit;
using static Basis.Network.Core.Compression.BasisAvatarBitPacking;
using CryptoRng = System.Security.Cryptography.RandomNumberGenerator;

namespace BasisServerTests;

[Collection("LiteNetLib global logger")]
public class LiteNetLibDidLoginTests
{
    private static bool WaitFor(Func<bool> done, int timeoutMs = 10000)
    {
        var watch = Stopwatch.StartNew();
        while (!done())
        {
            if (watch.ElapsedMilliseconds > timeoutMs) return false;
            Thread.Sleep(10);
        }
        return true;
    }

    [Fact]
    public void RealClientLogsIn_AndItsSealedDataReachesTheServer()
    {
        using var scope = new ServerStaticsScope();
        BasisLiteNetLibTransport.Register();
        int port = TransportTestSupport.FreeTcpAndUdpPort();
        NetworkServer.Configuration = new Configuration
        {
            PeerLimit = 100,
            UseAuth = false,
            UseAuthIdentity = true,
            HasFileSupport = false,
            BasisUserRestrictionMode = BasisUserRestrictionMode.Normal,
        };
        NetworkServer.Auth = new FakeAuth { Result = true };
        NetworkServer.AllowList = new BasisAllowList();
        NetworkServer.BanList = new BasisBanList();
        NetworkServer.HighQualityLength = ConvertToSize(BitQuality.High);
        var identity = new BasisDidLink.BasisDIDAuthIdentity();
        NetworkServer.AuthIdentity = identity;

        var received = new ConcurrentQueue<(NetPeer Peer, byte[] Data)>();
        var serverListener = new EventBasedNetListener();
        serverListener.ConnectionRequestEvent += BasisServerHandleEvents.HandleConnectionRequest;
        serverListener.NetworkReceiveEvent += (peer, reader, channel, method) =>
        {
            if (channel == BasisNetworkCommons.AuthIdentityChannel)
            {
                BasisServerHandleEvents.HandleAuth(reader, peer);
                return;
            }
            if (channel == BasisNetworkCommons.ChatChannel) received.Enqueue((peer, reader.GetRemainingBytes()));
            reader.Recycle();
        };
        NetManager server = BasisNetworkStackRegistry.Create(BasisNetworkStackRegistry.LiteNetLibId, serverListener, NetworkServer.Configuration);
        NetworkServer.Server = server;
        server.Start(IPAddress.Loopback, IPAddress.IPv6Loopback, port);

        byte[] privateBytes = new byte[Ed25519.PrivkeySize];
        CryptoRng.Fill(privateBytes);
        var playerKey = new PrivKey(privateBytes);
        Did player = DidKeyResolver.EncodePubkeyAsDid(Ed25519.ConvertPrivkeyToPubkey(playerKey)!);
        var client = new NetworkClient();
        int answered = 0;
        try
        {
            NetPeer clientPeer = client.StartClient("127.0.0.1", port, LifecycleSupport.MakeReady(player.V, "Connie"), new byte[] { 1 },
                BasisNetworkApplication.DefaultCompanyName, BasisNetworkApplication.DefaultProductName,
                new Configuration { NetworkStackId = BasisNetworkStackRegistry.LiteNetLibId, HasFileSupport = false });
            client.listener.NetworkReceiveEvent += (peer, reader, channel, method) =>
            {
                if (channel == BasisNetworkCommons.AuthIdentityChannel &&
                    BasisDIDAuthIdentityClient.RespondToChallenge(client, playerKey, player.V, reader, out NetDataWriter writer))
                {
                    Interlocked.Increment(ref answered);
                    peer.Send(writer, BasisNetworkCommons.AuthIdentityChannel, DeliveryMethod.ReliableOrdered);
                }
                reader.Recycle();
            };

            Assert.True(WaitFor(() => identity.UUIDToNetID(player.V, out _)));
            Assert.Equal(1, Volatile.Read(ref answered));
            Assert.True(identity.UUIDToNetID(player.V, out int peerId));
            Assert.True(NetworkServer.AuthenticatedPeers.TryGetValue(peerId, out NetPeer serverPeer));
            Assert.True(identity.NetIDToUUID(serverPeer, out string uuid));
            Assert.Equal(player.V, uuid);

            clientPeer.Send(new byte[] { 4, 5, 6 }, BasisNetworkCommons.ChatChannel, DeliveryMethod.ReliableOrdered);
            Assert.True(WaitFor(() => !received.IsEmpty));
            Assert.True(received.TryDequeue(out var message));
            Assert.Equal(new byte[] { 4, 5, 6 }, message.Data);
            Assert.Equal(peerId, message.Peer.Id);
        }
        finally
        {
            client.Disconnect();
            server.Stop();
            identity.DeInitialize();
        }
    }
}
