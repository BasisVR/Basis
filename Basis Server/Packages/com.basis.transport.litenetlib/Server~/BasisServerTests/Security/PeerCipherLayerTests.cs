using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Basis.Contrib.Auth.DecentralizedIds;
using Basis.Contrib.Auth.DecentralizedIds.Newtypes;
using Basis.Contrib.Crypto;
using Basis.Network.Core;
using BasisNetworkCore.Security;
using Xunit;
using CryptoRng = System.Security.Cryptography.RandomNumberGenerator;

namespace BasisServerTests;

[Collection("LiteNetLib global logger")]
public class PeerCipherLayerTests
{
    private const byte HeaderUnreliable = 0x00;
    private const byte HeaderChanneled = 0x01;
    private const byte HeaderAck = 0x02;
    private const byte HeaderPing = 0x03;
    private const byte HeaderMtuCheck = 0x09;
    private const byte HeaderMtuOk = 0x0A;
    private const byte HeaderShutdownOk = 0x0D;
    private const byte HeaderPeerNotFound = 0x0E;

    private sealed class Side
    {
        public byte[] Private = null!;
        public byte[] Public = null!;

        public static Side Create()
        {
            var side = new Side();
            BasisCryptoHandshake.GenerateKeyPair(out side.Private, out side.Public);
            return side;
        }
    }

    private static byte[] Packet(byte header, int payloadSize)
    {
        byte[] packet = new byte[1 + payloadSize + BasisCryptoLayer.Overhead];
        packet[0] = header;
        for (int i = 0; i < payloadSize; i++) packet[1 + i] = (byte)(3 + i * 7);
        return packet;
    }

    private static int Seal(BasisPeerCipherLayer layer, byte[] packet, int length)
    {
        IPEndPoint endPoint = null!;
        int offset = 0;
        layer.ProcessOutBoundPacket(ref endPoint, ref packet, ref offset, ref length);
        return length;
    }

    private static int Open(BasisPeerCipherLayer layer, byte[] packet, int length)
    {
        IPEndPoint endPoint = null!;
        layer.ProcessInboundPacket(ref endPoint, ref packet, ref length);
        return length;
    }

    private static byte[] ClientKey(Side client, Side server)
    {
        Assert.True(BasisCryptoHandshake.DeriveClientKey(client.Private, client.Public, server.Public, out byte[] key));
        return key;
    }

    private static byte[] ServerKey(Side client, Side server)
    {
        Assert.True(BasisCryptoHandshake.DeriveServerKey(server.Private, client.Public, server.Public, out byte[] key));
        return key;
    }

    private static (PrivKey Private, Did Did) NewPlayer()
    {
        byte[] privateBytes = new byte[Ed25519.PrivkeySize];
        CryptoRng.Fill(privateBytes);
        var privateKey = new PrivKey(privateBytes);
        PubKey publicKey = Ed25519.ConvertPrivkeyToPubkey(privateKey)!;
        return (privateKey, DidKeyResolver.EncodePubkeyAsDid(publicKey));
    }

    private static Signature Sign(PrivKey key, byte[] transcript)
    {
        Assert.True(Ed25519.Sign(key, new Payload(transcript), out Signature? sig));
        return sig!;
    }

    private static async Task<bool> Verifies(Did player, byte[] nonce, byte[] transcript, Signature signature)
    {
        var didAuth = new DidAuthentication(new Config());
        var challenge = new Challenge(player, new Nonce(nonce));
        var response = new Response(signature, new DidUrlFragment(string.Empty));
        return (await didAuth.VerifyResponse(response, challenge, new Payload(transcript))).IsOk;
    }

    private static byte[] Nonce()
    {
        byte[] nonce = new byte[BasisDidLoginTranscript.MinNonceSize];
        CryptoRng.Fill(nonce);
        return nonce;
    }

    private static byte[] PayloadHash()
    {
        byte[] hash = new byte[BasisDidLoginTranscript.PayloadHashSize];
        CryptoRng.Fill(hash);
        return hash;
    }

    [Fact]
    public void PeerLayer_SealedUserDataOpensToTheOriginalBytes()
    {
        byte[] key = ClientKey(Side.Create(), Side.Create());
        var sender = new BasisPeerCipherLayer(key);
        var receiver = new BasisPeerCipherLayer(key);

        byte[] packet = Packet(HeaderChanneled, 40);
        byte[] original = packet.AsSpan(0, 41).ToArray();
        int sealedLength = Seal(sender, packet, 41);
        Assert.Equal(41 + BasisCryptoLayer.Overhead, sealedLength);
        Assert.False(packet.AsSpan(1, 40).SequenceEqual(original.AsSpan(1)));

        Assert.Equal(41, Open(receiver, packet, sealedLength));
        Assert.Equal(original, packet.AsSpan(0, 41).ToArray());
    }

    [Theory]
    [InlineData(HeaderUnreliable)]
    [InlineData(HeaderChanneled)]
    public void PeerLayer_DropsPlaintextUserData(byte header)
    {
        var receiver = new BasisPeerCipherLayer(ClientKey(Side.Create(), Side.Create()));
        byte[] packet = Packet(header, 60);
        Assert.Equal(0, Open(receiver, packet, packet.Length));
    }

    [Fact]
    public void PeerLayer_DropsUserDataSealedWithAnotherKey()
    {
        var sender = new BasisPeerCipherLayer(ClientKey(Side.Create(), Side.Create()));
        var receiver = new BasisPeerCipherLayer(ClientKey(Side.Create(), Side.Create()));
        byte[] packet = Packet(HeaderChanneled, 20);
        int sealedLength = Seal(sender, packet, 21);
        Assert.Equal(0, Open(receiver, packet, sealedLength));
    }

    [Fact]
    public void PeerLayer_SealsConnectionControlPacketsToo()
    {
        byte[] key = ClientKey(Side.Create(), Side.Create());
        var sender = new BasisPeerCipherLayer(key);
        var receiver = new BasisPeerCipherLayer(key);
        byte[] packet = Packet(HeaderAck, 8);
        byte[] original = packet.AsSpan(0, 9).ToArray();

        int sealedLength = Seal(sender, packet, 9);
        Assert.Equal(9 + BasisCryptoLayer.Overhead, sealedLength);
        Assert.Equal(9, Open(receiver, packet, sealedLength));
        Assert.Equal(original, packet.AsSpan(0, 9).ToArray());
    }

    [Fact]
    public void PeerLayer_TakesUnsealedControlOnlyUntilTheFirstSealedPacket()
    {
        byte[] key = ClientKey(Side.Create(), Side.Create());
        var sender = new BasisPeerCipherLayer(key);
        var receiver = new BasisPeerCipherLayer(key);

        Assert.Equal(9, Open(receiver, Packet(HeaderPing, 8), 9));
        Assert.False(receiver.Proven);
        Assert.False(receiver.AllowsEndPointChange);

        byte[] packet = Packet(HeaderChanneled, 16);
        Assert.Equal(17, Open(receiver, packet, Seal(sender, packet, 17)));
        Assert.True(receiver.Proven);
        Assert.True(receiver.AllowsEndPointChange);

        Assert.Equal(0, Open(receiver, Packet(HeaderPing, 8), 9));
        Assert.Equal(0, Open(receiver, Packet(HeaderAck, 8), 9));
        Assert.Equal(1, Open(receiver, Packet(HeaderShutdownOk, 0), 1));
        byte[] ping = Packet(HeaderPing, 8);
        Assert.Equal(9, Open(receiver, ping, Seal(sender, ping, 9)));
    }

    [Fact]
    public void PeerLayer_WithoutAKey_HoldsBackUserDataUntilOneArrives()
    {
        var waiting = new BasisPeerCipherLayer();
        Assert.False(waiting.HasKey);
        Assert.Equal(0, Open(waiting, Packet(HeaderChanneled, 16), 17));
        Assert.Equal(0, Open(waiting, Packet(HeaderUnreliable, 16), 17));
        Assert.Equal(9, Open(waiting, Packet(HeaderPing, 8), 9));
        Assert.False(waiting.AllowsEndPointChange);

        byte[] key = ClientKey(Side.Create(), Side.Create());
        waiting.SetKey(key);
        byte[] packet = Packet(HeaderChanneled, 16);
        Assert.Equal(17, Open(waiting, packet, Seal(new BasisPeerCipherLayer(key), packet, 17)));
        Assert.Equal(0, Open(waiting, Packet(HeaderChanneled, 16), 17));
    }

    [Fact]
    public void PeerLayer_WithoutAKey_SendsUnchangedButReservesRoomForTheSeal()
    {
        var reserved = new BasisPeerCipherLayer();
        Assert.Equal(BasisCryptoLayer.Overhead, reserved.ExtraPacketSizeForLayer);
        Assert.Equal(0, reserved.ExtraSizeFor(HeaderMtuCheck));
        Assert.Equal(17, Seal(reserved, Packet(HeaderChanneled, 16), 17));

        reserved.SetKey(ClientKey(Side.Create(), Side.Create()));
        Assert.Equal(BasisCryptoLayer.Overhead, reserved.ExtraSizeFor(HeaderMtuCheck));
        Assert.Equal(17 + BasisCryptoLayer.Overhead, Seal(reserved, Packet(HeaderChanneled, 16), 17));
    }

    [Fact]
    public void PeerLayer_LeavesTheNetworkChangedReplyReadableBeforeThePeerIsKnown()
    {
        byte[] key = ClientKey(Side.Create(), Side.Create());
        var sender = new BasisPeerCipherLayer(key);
        byte[] reply = Packet(HeaderPeerNotFound, 14);
        byte[] original = reply.AsSpan(0, 15).ToArray();

        Assert.Equal(15, Seal(sender, reply, 15));
        Assert.Equal(original, reply.AsSpan(0, 15).ToArray());
        Assert.Equal(0, sender.ExtraSizeFor(HeaderPeerNotFound));
        Assert.Equal(BasisCryptoLayer.Overhead, sender.ExtraSizeFor(HeaderPing));
        Assert.Equal(1 + BasisCryptoLayer.Overhead, Seal(sender, Packet(HeaderPing, 0), 1));
    }

    [Fact]
    public void PeerLayer_CountsTheSealOnEchoesOnlyOnceTheRemoteHasSealed()
    {
        byte[] key = ClientKey(Side.Create(), Side.Create());
        var receiver = new BasisPeerCipherLayer(key);
        Assert.Equal(0, new BasisPeerCipherLayer().InboundExtraSizeFor(HeaderMtuOk));
        Assert.Equal(0, receiver.InboundExtraSizeFor(HeaderMtuOk));

        byte[] packet = Packet(HeaderChanneled, 16);
        Assert.Equal(17, Open(receiver, packet, Seal(new BasisPeerCipherLayer(key), packet, 17)));
        Assert.Equal(BasisCryptoLayer.Overhead, receiver.InboundExtraSizeFor(HeaderMtuOk));
        Assert.Equal(0, receiver.InboundExtraSizeFor(HeaderPeerNotFound));
    }

    private static byte[] SealedCopy(BasisPeerCipherLayer sender, int payloadSize)
    {
        byte[] packet = Packet(HeaderUnreliable, payloadSize);
        int length = Seal(sender, packet, 1 + payloadSize);
        return packet.AsSpan(0, length).ToArray();
    }

    private static int OpenCopy(BasisPeerCipherLayer receiver, byte[] sealedDatagram)
    {
        byte[] buffer = (byte[])sealedDatagram.Clone();
        return Open(receiver, buffer, buffer.Length);
    }

    [Fact]
    public void ReplayWindow_DropsADatagramSeenBefore()
    {
        byte[] key = ClientKey(Side.Create(), Side.Create());
        var sender = new BasisPeerCipherLayer(key);
        var receiver = new BasisPeerCipherLayer(key);
        byte[] datagram = SealedCopy(sender, 16);

        Assert.Equal(17, OpenCopy(receiver, datagram));
        Assert.Equal(0, OpenCopy(receiver, datagram));
    }

    [Fact]
    public void ReplayWindow_AcceptsReorderingInsideTheWindow()
    {
        byte[] key = ClientKey(Side.Create(), Side.Create());
        var sender = new BasisPeerCipherLayer(key);
        var receiver = new BasisPeerCipherLayer(key);
        byte[] first = SealedCopy(sender, 8);
        byte[] second = SealedCopy(sender, 8);
        byte[] third = SealedCopy(sender, 8);

        Assert.Equal(9, OpenCopy(receiver, third));
        Assert.Equal(9, OpenCopy(receiver, first));
        Assert.Equal(9, OpenCopy(receiver, second));
        Assert.Equal(0, OpenCopy(receiver, first));
    }

    [Fact]
    public void ReplayWindow_DropsADatagramOlderThanTheWindow()
    {
        byte[] key = ClientKey(Side.Create(), Side.Create());
        var sender = new BasisPeerCipherLayer(key);
        var receiver = new BasisPeerCipherLayer(key);
        byte[] held = SealedCopy(sender, 8);
        for (int i = 0; i < BasisPeerCipherLayer.ReplayWindowSize; i++)
        {
            Assert.Equal(9, OpenCopy(receiver, SealedCopy(sender, 8)));
        }

        Assert.Equal(0, OpenCopy(receiver, held));
    }

    [Fact]
    public void ReplayWindow_AJumpAheadStillRejectsWhatWasSeen()
    {
        byte[] key = ClientKey(Side.Create(), Side.Create());
        var sender = new BasisPeerCipherLayer(key);
        var receiver = new BasisPeerCipherLayer(key);
        byte[] early = SealedCopy(sender, 8);
        Assert.Equal(9, OpenCopy(receiver, early));
        for (int i = 0; i < 200; i++) SealedCopy(sender, 8);
        byte[] late = SealedCopy(sender, 8);

        Assert.Equal(9, OpenCopy(receiver, late));
        Assert.Equal(0, OpenCopy(receiver, early));
        Assert.Equal(0, OpenCopy(receiver, late));
    }

    [Fact]
    public void ReplayWindow_AJumpPastTheWholeWindowForgetsEverySlot()
    {
        byte[] key = ClientKey(Side.Create(), Side.Create());
        var sender = new BasisPeerCipherLayer(key);
        var receiver = new BasisPeerCipherLayer(key);
        var held = new List<byte[]>();
        for (int i = 0; i < 5; i++) held.Add(SealedCopy(sender, 8));
        Assert.Equal(9, OpenCopy(receiver, held[4]));
        for (int i = 0; i < BasisPeerCipherLayer.ReplayWindowSize * 3; i++) SealedCopy(sender, 8);
        var recent = new List<byte[]>();
        for (int i = 0; i < 70; i++) recent.Add(SealedCopy(sender, 8));

        Assert.Equal(9, OpenCopy(receiver, recent[^1]));
        for (int i = 0; i < recent.Count - 1; i++) Assert.Equal(9, OpenCopy(receiver, recent[i]));
        foreach (byte[] old in held) Assert.Equal(0, OpenCopy(receiver, old));
    }

    [Fact]
    public async Task Relay_ForwardingThePlayersConnectionKey_LeavesTheRelayUnableToSend()
    {
        // A forwards everything unchanged, so B accepts the login, but B now only takes user data
        // sealed with a key derived from the player's private connection key.
        var (key, did) = NewPlayer();
        Side player = Side.Create();
        Side relay = Side.Create();
        Side serverB = Side.Create();
        byte[] nonceB = Nonce();

        byte[] transcript = BasisDidLoginTranscript.Build(did.V, "did:key:server-b", nonceB, player.Public, serverB.Public, PayloadHash());
        Assert.True(await Verifies(did, nonceB, transcript, Sign(key, transcript)));

        var serverBInbound = new BasisPeerCipherLayer(ServerKey(player, serverB));

        // Anything A can derive uses its own private key, never the player's.
        Assert.True(BasisCryptoHandshake.DeriveClientKey(relay.Private, player.Public, serverB.Public, out byte[] relayGuess));
        byte[] forged = Packet(HeaderChanneled, 32);
        int forgedLength = Seal(new BasisPeerCipherLayer(relayGuess), forged, 33);
        Assert.Equal(0, Open(serverBInbound, forged, forgedLength));

        byte[] plaintext = Packet(HeaderChanneled, 32);
        Assert.Equal(0, Open(serverBInbound, plaintext, 33));

        // The player's own packets still get through.
        byte[] genuine = Packet(HeaderChanneled, 32);
        int genuineLength = Seal(new BasisPeerCipherLayer(ClientKey(player, serverB)), genuine, 33);
        Assert.Equal(33, Open(serverBInbound, genuine, genuineLength));
        Assert.Equal(0, Open(serverBInbound, Packet(HeaderPing, 8), 9));
    }

    [Fact]
    public void Relay_ResendingThePlayersSealedPackets_IsDropped()
    {
        // A relay that passes traffic through sees every sealed datagram the player sends and can
        // send any of them to the server again.
        Side player = Side.Create();
        Side serverB = Side.Create();
        var playerOutbound = new BasisPeerCipherLayer(ClientKey(player, serverB));
        var serverBInbound = new BasisPeerCipherLayer(ServerKey(player, serverB));

        byte[] command = SealedCopy(playerOutbound, 24);
        Assert.Equal(25, OpenCopy(serverBInbound, command));
        for (int i = 0; i < 10; i++) Assert.Equal(25, OpenCopy(serverBInbound, SealedCopy(playerOutbound, 24)));

        Assert.Equal(0, OpenCopy(serverBInbound, command));
    }

    [Fact]
    public void LiteNetLib_InboundPeerLayer_DropsPlaintextAndAcceptsSealedData()
    {
        byte[] key = ClientKey(Side.Create(), Side.Create());

        var serverListener = new LiteNetLib.EventBasedNetListener();
        var clientListener = new LiteNetLib.EventBasedNetListener();
        var server = new LiteNetLib.NetManager(serverListener);
        var client = new LiteNetLib.NetManager(clientListener);
        var received = new List<string>();
        LiteNetLib.NetPeer? serverSide = null;

        serverListener.ConnectionRequestEvent += request => request.Accept();
        serverListener.PeerConnectedEvent += peer => serverSide = peer;
        serverListener.NetworkReceiveEvent += (peer, reader, channel, method) =>
        {
            received.Add(System.Text.Encoding.UTF8.GetString(reader.GetRemainingBytes()));
            reader.Recycle();
        };

        try
        {
            Assert.True(server.Start(IPAddress.Loopback, IPAddress.IPv6Loopback, 0));
            Assert.True(client.Start());
            LiteNetLib.NetPeer clientSide = client.Connect(new IPEndPoint(IPAddress.Loopback, server.LocalPort), ReadOnlySpan<byte>.Empty);

            Assert.True(PollUntil(server, client, () => serverSide != null && clientSide.ConnectionState == LiteNetLib.ConnectionState.Connected));
            serverSide!.SetInboundPacketLayer(new BasisPeerCipherLayer(key));

            clientSide.Send(System.Text.Encoding.UTF8.GetBytes("plaintext"), LiteNetLib.DeliveryMethod.Unreliable);
            PollUntil(server, client, () => received.Count > 0, 500);
            Assert.Empty(received);

            clientSide.SetOutboundPacketLayer(new BasisPeerCipherLayer(key));
            clientSide.Send(System.Text.Encoding.UTF8.GetBytes("sealed"), LiteNetLib.DeliveryMethod.ReliableOrdered);
            Assert.True(PollUntil(server, client, () => received.Count > 0));
            Assert.Equal(new[] { "sealed" }, received);
        }
        finally
        {
            client.Stop();
            server.Stop();
        }
    }

    [Fact]
    public void LiteNetLib_AcceptAwaitingSeal_HoldsBackDataSentBeforeTheKey()
    {
        byte[] key = ClientKey(Side.Create(), Side.Create());

        var serverListener = new LiteNetLib.EventBasedNetListener();
        var clientListener = new LiteNetLib.EventBasedNetListener();
        var server = new LiteNetLib.NetManager(serverListener);
        var client = new LiteNetLib.NetManager(clientListener);
        var received = new List<string>();
        BasisPeerCipherLayer? pending = null;
        LiteNetLib.NetPeer? serverSide = null;
        bool layerInPlaceWhenConnected = false;

        serverListener.ConnectionRequestEvent += request =>
        {
            pending = new BasisPeerCipherLayer();
            request.Accept(pending);
        };
        serverListener.PeerConnectedEvent += peer =>
        {
            serverSide = peer;
            layerInPlaceWhenConnected = ReferenceEquals(peer.InboundPacketLayer, pending);
        };
        serverListener.NetworkReceiveEvent += (peer, reader, channel, method) =>
        {
            received.Add(System.Text.Encoding.UTF8.GetString(reader.GetRemainingBytes()));
            reader.Recycle();
        };

        try
        {
            Assert.True(server.Start(IPAddress.Loopback, IPAddress.IPv6Loopback, 0));
            Assert.True(client.Start());
            LiteNetLib.NetPeer clientSide = client.Connect(new IPEndPoint(IPAddress.Loopback, server.LocalPort), ReadOnlySpan<byte>.Empty);
            Assert.True(PollUntil(server, client, () => serverSide != null && clientSide.ConnectionState == LiteNetLib.ConnectionState.Connected));
            Assert.True(layerInPlaceWhenConnected);

            clientSide.Send(System.Text.Encoding.UTF8.GetBytes("early"), LiteNetLib.DeliveryMethod.Unreliable);
            PollUntil(server, client, () => received.Count > 0, 500);
            Assert.Empty(received);

            pending!.SetKey(key);
            clientSide.SetOutboundPacketLayer(new BasisPeerCipherLayer(key));
            clientSide.Send(System.Text.Encoding.UTF8.GetBytes("sealed"), LiteNetLib.DeliveryMethod.ReliableOrdered);
            Assert.True(PollUntil(server, client, () => received.Count > 0));
            Assert.Equal(new[] { "sealed" }, received);
            Assert.Equal(LiteNetLib.ConnectionState.Connected, serverSide!.ConnectionState);
        }
        finally
        {
            client.Stop();
            server.Stop();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LiteNetLib_OutboundPeerLayer_ReservesItsOverheadInTheMtu(bool keyed)
    {
        var listener = new LiteNetLib.EventBasedNetListener();
        var serverListener = new LiteNetLib.EventBasedNetListener();
        var server = new LiteNetLib.NetManager(serverListener);
        var client = new LiteNetLib.NetManager(listener);
        serverListener.ConnectionRequestEvent += request => request.Accept();
        try
        {
            Assert.True(server.Start(IPAddress.Loopback, IPAddress.IPv6Loopback, 0));
            Assert.True(client.Start());
            LiteNetLib.NetPeer peer = client.Connect(new IPEndPoint(IPAddress.Loopback, server.LocalPort), ReadOnlySpan<byte>.Empty);
            int before = peer.Mtu;
            peer.SetOutboundPacketLayer(keyed ? new BasisPeerCipherLayer(new byte[BasisCryptoHandshake.KeySize]) : new BasisPeerCipherLayer());
            Assert.Equal(before - BasisCryptoLayer.Overhead, peer.Mtu);
        }
        finally
        {
            client.Stop();
            server.Stop();
        }
    }

    private sealed class MtuProbeRecorder : LiteNetLib.Layers.PacketLayerBase
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<int> Probes = new();

        public MtuProbeRecorder() : base(0)
        {
        }

        public override void ProcessInboundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int length)
        {
            if (length > 0 && (data[0] & 0x1F) == HeaderMtuCheck) Probes.Enqueue(length);
        }

        public override void ProcessOutBoundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int offset, ref int length)
        {
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LiteNetLib_MtuProbe_IsAsLargeOnTheWireAsTheDataItClears(bool keyed)
    {
        byte[] key = ClientKey(Side.Create(), Side.Create());
        var recorder = new MtuProbeRecorder();
        var serverListener = new LiteNetLib.EventBasedNetListener();
        var server = new LiteNetLib.NetManager(serverListener, recorder);
        var client = new LiteNetLib.NetManager(new LiteNetLib.EventBasedNetListener()) { MtuDiscovery = true };
        serverListener.ConnectionRequestEvent += request => request.Accept(new BasisPeerCipherLayer(key));
        try
        {
            Assert.True(server.Start(IPAddress.Loopback, IPAddress.IPv6Loopback, 0));
            Assert.True(client.Start());
            LiteNetLib.NetPeer peer = client.Connect(new IPEndPoint(IPAddress.Loopback, server.LocalPort), ReadOnlySpan<byte>.Empty);
            peer.SetOutboundPacketLayer(keyed ? new BasisPeerCipherLayer(key) : new BasisPeerCipherLayer());
            int dataMtuBefore = peer.Mtu;

            Assert.True(PollUntil(server, client, () => !recorder.Probes.IsEmpty && peer.Mtu > dataMtuBefore, 6000));
            Assert.True(recorder.Probes.TryPeek(out int wire));
            Assert.Contains(wire, LiteNetLib.NetConstants.PossibleMtu);
            Assert.Equal(wire - BasisCryptoLayer.Overhead, peer.Mtu);
        }
        finally
        {
            client.Stop();
            server.Stop();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LiteNetLib_MtuDiscovery_ReachesTheLargestSizeOnBothEnds(bool sealedSession)
    {
        byte[] key = ClientKey(Side.Create(), Side.Create());
        var serverListener = new LiteNetLib.EventBasedNetListener();
        var clientListener = new LiteNetLib.EventBasedNetListener();
        var server = new LiteNetLib.NetManager(serverListener) { MtuDiscovery = true };
        var client = new LiteNetLib.NetManager(clientListener) { MtuDiscovery = true };
        var received = new List<string>();
        LiteNetLib.NetPeer? serverSide = null;
        serverListener.ConnectionRequestEvent += request =>
        {
            if (sealedSession) request.Accept(new BasisPeerCipherLayer(key));
            else request.Accept();
        };
        serverListener.PeerConnectedEvent += peer => serverSide = peer;
        serverListener.NetworkReceiveEvent += (peer, reader, channel, method) =>
        {
            received.Add(Encoding.UTF8.GetString(reader.GetRemainingBytes()));
            reader.Recycle();
        };
        try
        {
            Assert.True(server.Start(IPAddress.Loopback, IPAddress.IPv6Loopback, 0));
            Assert.True(client.Start());
            LiteNetLib.NetPeer clientSide = client.Connect(new IPEndPoint(IPAddress.Loopback, server.LocalPort), ReadOnlySpan<byte>.Empty);
            if (sealedSession) clientSide.SetOutboundPacketLayer(new BasisPeerCipherLayer(key));
            Assert.True(PollUntil(server, client, () => serverSide != null && clientSide.ConnectionState == LiteNetLib.ConnectionState.Connected));
            clientSide.Send(Encoding.UTF8.GetBytes("hello"), LiteNetLib.DeliveryMethod.ReliableOrdered);
            Assert.True(PollUntil(server, client, () => received.Count > 0));

            int clientTop = LiteNetLib.NetConstants.MaxPacketSize - (sealedSession ? BasisCryptoLayer.Overhead : 0);
            Assert.True(PollUntil(server, client, () => serverSide!.Mtu == LiteNetLib.NetConstants.MaxPacketSize && clientSide.Mtu == clientTop, 15000), $"server MTU {serverSide!.Mtu}, client MTU {clientSide.Mtu}");
        }
        finally
        {
            client.Stop();
            server.Stop();
        }
    }

    [Fact]
    public void LiteNetLib_SealedSession_SurvivesTheClientChangingAddress()
    {
        byte[] key = ClientKey(Side.Create(), Side.Create());
        var serverListener = new LiteNetLib.EventBasedNetListener();
        var clientListener = new LiteNetLib.EventBasedNetListener();
        var server = new LiteNetLib.NetManager(serverListener) { AllowPeerAddressChange = true, DisconnectTimeout = 15000 };
        var client = new LiteNetLib.NetManager(clientListener) { DisconnectTimeout = 15000 };
        var received = new System.Collections.Concurrent.ConcurrentQueue<string>();
        LiteNetLib.NetPeer? serverSide = null;
        LiteNetLib.DisconnectReason? clientLost = null;
        serverListener.ConnectionRequestEvent += request => request.Accept(new BasisPeerCipherLayer(key));
        serverListener.PeerConnectedEvent += peer => serverSide = peer;
        serverListener.NetworkReceiveEvent += (peer, reader, channel, method) =>
        {
            received.Enqueue(Encoding.UTF8.GetString(reader.GetRemainingBytes()));
            reader.Recycle();
        };
        clientListener.PeerDisconnectedEvent += (peer, info) => clientLost = info.Reason;
        try
        {
            Assert.True(server.Start(IPAddress.Loopback, IPAddress.IPv6Loopback, 0));
            using var relay = new UdpRelay(server.LocalPort);
            Assert.True(client.Start());
            LiteNetLib.NetPeer clientSide = client.Connect(new IPEndPoint(IPAddress.Loopback, relay.Port), ReadOnlySpan<byte>.Empty);
            Assert.True(PollUntil(server, client, () => serverSide != null && clientSide.ConnectionState == LiteNetLib.ConnectionState.Connected));
            clientSide.SetOutboundPacketLayer(new BasisPeerCipherLayer(key));
            clientSide.Send(Encoding.UTF8.GetBytes("before"), LiteNetLib.DeliveryMethod.ReliableOrdered);
            Assert.True(PollUntil(server, client, () => received.Contains("before")));
            Assert.Equal(relay.ServerFacingPort, serverSide!.Port);

            relay.ChangeAddress();
            clientSide.Send(Encoding.UTF8.GetBytes("after"), LiteNetLib.DeliveryMethod.ReliableOrdered);
            Assert.True(PollUntil(server, client, () => received.Contains("after") || clientLost != null, 10000), "the message sent after the address change never arrived");
            Assert.Null(clientLost);
            Assert.Equal(relay.ServerFacingPort, serverSide.Port);
            Assert.Equal(LiteNetLib.ConnectionState.Connected, clientSide.ConnectionState);
        }
        finally
        {
            client.Stop();
            server.Stop();
        }
    }

    private sealed class UdpRelay : IDisposable
    {
        private readonly Socket front = Bound();
        private readonly IPEndPoint server;
        private readonly object gate = new object();
        private Socket back = Bound();
        private EndPoint? client;

        public UdpRelay(int serverPort)
        {
            server = new IPEndPoint(IPAddress.Loopback, serverPort);
            Socket first = back;
            Start(FromClient);
            Start(() => FromServer(first));
        }

        public int Port => ((IPEndPoint)front.LocalEndPoint!).Port;

        public int ServerFacingPort
        {
            get
            {
                lock (gate) return ((IPEndPoint)back.LocalEndPoint!).Port;
            }
        }

        public void ChangeAddress()
        {
            Socket fresh = Bound();
            Socket old;
            lock (gate)
            {
                old = back;
                back = fresh;
            }
            Start(() => FromServer(fresh));
            old.Dispose();
        }

        public void Dispose()
        {
            front.Dispose();
            lock (gate) back.Dispose();
        }

        private static Socket Bound()
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return socket;
        }

        private static void Start(Action pump) => new Thread(() => pump()) { IsBackground = true }.Start();

        private void FromClient()
        {
            byte[] buffer = new byte[4096];
            while (true)
            {
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int length;
                Socket target;
                try
                {
                    length = front.ReceiveFrom(buffer, ref from);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
                {
                    continue;
                }
                catch (Exception)
                {
                    return;
                }
                lock (gate)
                {
                    client = from;
                    target = back;
                }
                try
                {
                    target.SendTo(buffer, 0, length, SocketFlags.None, server);
                }
                catch (Exception)
                {
                }
            }
        }

        private void FromServer(Socket socket)
        {
            byte[] buffer = new byte[4096];
            while (true)
            {
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int length;
                EndPoint? to;
                try
                {
                    length = socket.ReceiveFrom(buffer, ref from);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
                {
                    continue;
                }
                catch (Exception)
                {
                    return;
                }
                lock (gate) to = client;
                if (to == null) continue;
                try
                {
                    front.SendTo(buffer, 0, length, SocketFlags.None, to);
                }
                catch (Exception)
                {
                }
            }
        }
    }

    private static bool PollUntil(LiteNetLib.NetManager server, LiteNetLib.NetManager client, Func<bool> done, int timeoutMs = 5000)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            server.PollEvents();
            client.PollEvents();
            if (done()) return true;
            Thread.Sleep(10);
        }
        return done();
    }
}
