using System.Net;
using System.Text;
using Basis.Contrib.Crypto;
using Basis.Network.Core;
using Xunit;

namespace BasisServerTests;

/// <summary>
/// Transport-encryption tests: the X25519 + HKDF handshake (BasisCryptoHandshake)
/// and the per-endpoint ChaCha20-Poly1305 packet layer (BasisCryptoLayer).
/// Covers full two-sided key agreement, encrypt/decrypt round-trips across payload
/// sizes, wire-format layout, tampering/truncation/replay behavior, wrong-key and
/// malformed-handshake rejection, and session (endpoint) lifecycle.
/// </summary>
public class CryptoHandshakeAndLayerTests
{
    private static readonly IPEndPoint ClientAddress = new(IPAddress.Parse("192.0.2.10"), 41000);
    private static readonly IPEndPoint ServerAddress = new(IPAddress.Parse("192.0.2.20"), 42000);

    // Low five header bits mirror LiteNetLib.PacketProperty.
    private const byte HeaderUnreliable = 0x00;
    private const byte HeaderChanneled = 0x01;
    private const byte HeaderMerged = 0x0C;

    // ---------------------------------------------------------------- helpers

    private static byte[] SequentialKey(byte seed)
    {
        byte[] key = new byte[BasisCryptoHandshake.KeySize];
        for (int i = 0; i < key.Length; i++) key[i] = (byte)(seed + i);
        return key;
    }

    private static byte[] BuildPacket(byte header, int payloadSize)
    {
        byte[] packet = new byte[1 + payloadSize];
        packet[0] = header;
        for (int i = 0; i < payloadSize; i++) packet[1 + i] = (byte)(7 + i * 31);
        return packet;
    }

    /// Runs a packet through the outbound path exactly as LiteNetLib would:
    /// the buffer has ExtraPacketSizeForLayer spare bytes after the packet.
    private static byte[] Seal(BasisCryptoLayer layer, IPEndPoint remote, byte[] packet, int offset = 0)
    {
        byte[] buffer = new byte[offset + packet.Length + BasisCryptoLayer.Overhead];
        packet.CopyTo(buffer, offset);
        int off = offset;
        int length = packet.Length;
        IPEndPoint endpoint = remote;
        layer.ProcessOutBoundPacket(ref endpoint, ref buffer, ref off, ref length);
        byte[] wire = new byte[length];
        Array.Copy(buffer, off, wire, 0, length);
        return wire;
    }

    /// Runs a received datagram through the inbound path; returns the mutated
    /// buffer and resulting length (0 means the layer dropped the packet).
    private static (byte[] Buffer, int Length) OpenRaw(BasisCryptoLayer layer, IPEndPoint remote, byte[] wire)
    {
        byte[] buffer = (byte[])wire.Clone();
        int length = buffer.Length;
        IPEndPoint endpoint = remote;
        layer.ProcessInboundPacket(ref endpoint, ref buffer, ref length);
        return (buffer, length);
    }

    private static byte[]? Open(BasisCryptoLayer layer, IPEndPoint remote, byte[] wire)
    {
        (byte[] buffer, int length) = OpenRaw(layer, remote, wire);
        if (length == 0) return null;
        byte[] packet = new byte[length];
        Array.Copy(buffer, 0, packet, 0, length);
        return packet;
    }

    /// Little-endian nonce counter carried in the last 8 bytes of an encrypted datagram.
    private static long ReadCounter(byte[] wire)
    {
        ulong value = 0;
        for (int i = 0; i < BasisCryptoLayer.CounterSize; i++)
            value |= (ulong)wire[wire.Length - BasisCryptoLayer.CounterSize + i] << (8 * i);
        return (long)value;
    }

    /// Mirror of the handshake's public-key ordering (unsigned lexicographic, length tiebreak).
    private static int CompareKeys(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            int d = a[i] - b[i];
            if (d != 0) return d;
        }
        return a.Length - b.Length;
    }

    private static (BasisCryptoLayer Client, BasisCryptoLayer Server) NewFixedKeyPair(long initialSendCounter = 0)
    {
        byte[] keyAB = SequentialKey(0xA0);
        byte[] keyBA = SequentialKey(0x0B);
        var client = new BasisCryptoLayer();
        var server = new BasisCryptoLayer();
        client.SetEndpointKeys(ServerAddress, keyAB, keyBA, initialSendCounter);
        server.SetEndpointKeys(ClientAddress, keyBA, keyAB, initialSendCounter);
        return (client, server);
    }

    private static (BasisCryptoLayer Client, BasisCryptoLayer Server) NewHandshakePair()
    {
        BasisCryptoHandshake.GenerateKeyPair(out byte[] clientPrivate, out byte[] clientPublic);
        BasisCryptoHandshake.GenerateKeyPair(out byte[] serverPrivate, out byte[] serverPublic);
        Assert.True(BasisCryptoHandshake.DerivePeerKeys(clientPrivate, clientPublic, serverPublic, out byte[] clientSend, out byte[] clientRecv));
        Assert.True(BasisCryptoHandshake.DerivePeerKeys(serverPrivate, serverPublic, clientPublic, out byte[] serverSend, out byte[] serverRecv));
        var client = new BasisCryptoLayer();
        var server = new BasisCryptoLayer();
        client.SetEndpointKeys(ServerAddress, clientSend, clientRecv);
        server.SetEndpointKeys(ClientAddress, serverSend, serverRecv);
        return (client, server);
    }

    // ------------------------------------------------------------- handshake

    [Fact]
    public void GenerateKeyPair_ProducesDistinctWellFormedPairs()
    {
        BasisCryptoHandshake.GenerateKeyPair(out byte[] privateKey, out byte[] publicKey);
        Assert.Equal(BasisCryptoHandshake.PrivateKeySize, privateKey.Length);
        Assert.Equal(BasisCryptoHandshake.PublicKeySize, publicKey.Length);
        Assert.Equal(publicKey, BasisX25519.DerivePublicKey(privateKey));

        BasisCryptoHandshake.GenerateKeyPair(out byte[] secondPrivate, out byte[] secondPublic);
        Assert.NotEqual(privateKey, secondPrivate);
        Assert.NotEqual(publicKey, secondPublic);
    }

    [Fact]
    public void DerivePeerKeys_BothSides_DeriveComplementaryDirectionalKeys()
    {
        BasisCryptoHandshake.GenerateKeyPair(out byte[] clientPrivate, out byte[] clientPublic);
        BasisCryptoHandshake.GenerateKeyPair(out byte[] serverPrivate, out byte[] serverPublic);

        Assert.True(BasisCryptoHandshake.DerivePeerKeys(clientPrivate, clientPublic, serverPublic, out byte[] clientSend, out byte[] clientRecv));
        Assert.True(BasisCryptoHandshake.DerivePeerKeys(serverPrivate, serverPublic, clientPublic, out byte[] serverSend, out byte[] serverRecv));

        Assert.Equal(BasisCryptoHandshake.KeySize, clientSend.Length);
        Assert.Equal(BasisCryptoHandshake.KeySize, clientRecv.Length);
        // Each side's send key is the other side's receive key.
        Assert.Equal(clientSend, serverRecv);
        Assert.Equal(clientRecv, serverSend);
        // Directions use independent keys.
        Assert.NotEqual(clientSend, clientRecv);
    }

    [Fact]
    public void DerivePeerKeys_IsDeterministic()
    {
        BasisCryptoHandshake.GenerateKeyPair(out byte[] privateKey, out byte[] publicKey);
        BasisCryptoHandshake.GenerateKeyPair(out _, out byte[] peerPublic);

        Assert.True(BasisCryptoHandshake.DerivePeerKeys(privateKey, publicKey, peerPublic, out byte[] send1, out byte[] recv1));
        Assert.True(BasisCryptoHandshake.DerivePeerKeys(privateKey, publicKey, peerPublic, out byte[] send2, out byte[] recv2));

        Assert.Equal(send1, send2);
        Assert.Equal(recv1, recv2);
    }

    [Fact]
    public void DerivePeerKeys_MatchesDocumentedHkdfConstruction()
    {
        BasisCryptoHandshake.GenerateKeyPair(out byte[] privateA, out byte[] publicA);
        BasisCryptoHandshake.GenerateKeyPair(out byte[] privateB, out byte[] publicB);

        // Recompute the spec by hand: ECDH secret, transcript salt = lowPub || highPub,
        // HKDF-SHA256 with the two directional info strings; the lower public key is "A".
        byte[] shared = BasisX25519.Agree(privateA, publicB);
        bool aIsLow = CompareKeys(publicA, publicB) < 0;
        byte[] lowPublic = aIsLow ? publicA : publicB;
        byte[] highPublic = aIsLow ? publicB : publicA;
        byte[] salt = new byte[lowPublic.Length + highPublic.Length];
        lowPublic.CopyTo(salt, 0);
        highPublic.CopyTo(salt, lowPublic.Length);
        byte[] keyLowToHigh = BasisHkdf.DeriveKey(shared, salt, Encoding.ASCII.GetBytes("basis-crypto-v1-ab"), BasisCryptoHandshake.KeySize);
        byte[] keyHighToLow = BasisHkdf.DeriveKey(shared, salt, Encoding.ASCII.GetBytes("basis-crypto-v1-ba"), BasisCryptoHandshake.KeySize);

        Assert.True(BasisCryptoHandshake.DerivePeerKeys(privateA, publicA, publicB, out byte[] sendA, out byte[] recvA));
        Assert.True(BasisCryptoHandshake.DerivePeerKeys(privateB, publicB, publicA, out byte[] sendB, out byte[] recvB));

        Assert.Equal(aIsLow ? keyLowToHigh : keyHighToLow, sendA);
        Assert.Equal(aIsLow ? keyHighToLow : keyLowToHigh, recvA);
        Assert.Equal(aIsLow ? keyHighToLow : keyLowToHigh, sendB);
        Assert.Equal(aIsLow ? keyLowToHigh : keyHighToLow, recvB);
    }

    [Fact]
    public void DerivePeerKeys_IdenticalPublicKeys_ReturnsFalse()
    {
        BasisCryptoHandshake.GenerateKeyPair(out byte[] privateKey, out byte[] publicKey);
        Assert.False(BasisCryptoHandshake.DerivePeerKeys(privateKey, publicKey, publicKey, out byte[] send, out byte[] recv));
        Assert.Empty(send);
        Assert.Empty(recv);
    }

    [Fact]
    public void DerivePeerKeys_AllZeroPeerPublic_ReturnsFalse()
    {
        // The all-zero point yields an all-zero X25519 shared secret, which the
        // agreement rejects; the handshake must surface that as a clean false.
        BasisCryptoHandshake.GenerateKeyPair(out byte[] privateKey, out byte[] publicKey);
        byte[] zeroPublic = new byte[BasisCryptoHandshake.PublicKeySize];
        Assert.False(BasisCryptoHandshake.DerivePeerKeys(privateKey, publicKey, zeroPublic, out byte[] send, out byte[] recv));
        Assert.Empty(send);
        Assert.Empty(recv);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(31)]
    public void DerivePeerKeys_UndersizedPeerPublic_ReturnsFalse(int size)
    {
        BasisCryptoHandshake.GenerateKeyPair(out byte[] privateKey, out byte[] publicKey);
        byte[] malformed = new byte[size];
        for (int i = 0; i < size; i++) malformed[i] = (byte)(i + 1);
        Assert.False(BasisCryptoHandshake.DerivePeerKeys(privateKey, publicKey, malformed, out byte[] send, out byte[] recv));
        Assert.Empty(send);
        Assert.Empty(recv);
    }

    [Theory]
    [InlineData(33)]
    [InlineData(64)]
    public void DerivePeerKeys_OversizedPeerPublic_DoesNotThrow(int size)
    {
        BasisCryptoHandshake.GenerateKeyPair(out byte[] privateKey, out byte[] publicKey);
        byte[] oversized = new byte[size];
        for (int i = 0; i < size; i++) oversized[i] = (byte)(0x40 + i);

        // Success or failure is acceptable for garbage; escaping exceptions are not.
        bool ok = BasisCryptoHandshake.DerivePeerKeys(privateKey, publicKey, oversized, out byte[] send, out byte[] recv);
        if (ok)
        {
            Assert.Equal(BasisCryptoHandshake.KeySize, send.Length);
            Assert.Equal(BasisCryptoHandshake.KeySize, recv.Length);
        }
        else
        {
            Assert.Empty(send);
            Assert.Empty(recv);
        }
    }

    [Fact]
    public void DerivePeerKeys_UndersizedPrivateKey_ReturnsFalse()
    {
        BasisCryptoHandshake.GenerateKeyPair(out byte[] fullPrivate, out byte[] publicKey);
        BasisCryptoHandshake.GenerateKeyPair(out _, out byte[] peerPublic);
        byte[] truncatedPrivate = fullPrivate.AsSpan(0, 16).ToArray();
        Assert.False(BasisCryptoHandshake.DerivePeerKeys(truncatedPrivate, publicKey, peerPublic, out byte[] send, out byte[] recv));
        Assert.Empty(send);
        Assert.Empty(recv);
    }

    [Fact]
    public void DerivePeerKeys_DifferentPeers_ProduceDifferentKeys()
    {
        BasisCryptoHandshake.GenerateKeyPair(out byte[] privateA, out byte[] publicA);
        BasisCryptoHandshake.GenerateKeyPair(out _, out byte[] publicB);
        BasisCryptoHandshake.GenerateKeyPair(out _, out byte[] publicC);

        Assert.True(BasisCryptoHandshake.DerivePeerKeys(privateA, publicA, publicB, out byte[] sendAB, out byte[] recvAB));
        Assert.True(BasisCryptoHandshake.DerivePeerKeys(privateA, publicA, publicC, out byte[] sendAC, out byte[] recvAC));

        Assert.NotEqual(sendAB, sendAC);
        Assert.NotEqual(recvAB, recvAC);
    }

    // ------------------------------------------------- layer: round-trip & format

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(512)]
    [InlineData(1200)]
    [InlineData(16384)]
    public void RoundTrip_RecoversExactPacket_AcrossPayloadSizes(int payloadSize)
    {
        var (client, server) = NewFixedKeyPair();
        byte[] packet = BuildPacket(HeaderUnreliable, payloadSize);
        byte[] wire = Seal(client, ServerAddress, packet);
        Assert.Equal(packet.Length + BasisCryptoLayer.Overhead, wire.Length);
        Assert.Equal(packet, Open(server, ClientAddress, wire));
    }

    [Fact]
    public void FullHandshake_EncryptedTraffic_FlowsBothDirections()
    {
        var (client, server) = NewHandshakePair();
        foreach (int payloadSize in new[] { 0, 3, 200, 1200 })
        {
            byte[] request = BuildPacket(HeaderChanneled, payloadSize);
            Assert.Equal(request, Open(server, ClientAddress, Seal(client, ServerAddress, request)));

            byte[] response = BuildPacket(HeaderMerged, payloadSize);
            Assert.Equal(response, Open(client, ServerAddress, Seal(server, ClientAddress, response)));
        }
    }

    [Fact]
    public void Outbound_AddsExactOverhead_KeepsHeaderCleartext_HidesPayload()
    {
        Assert.Equal(BasisAeadCipher.TagSize + BasisCryptoLayer.CounterSize, BasisCryptoLayer.Overhead);
        Assert.Equal(BasisCryptoLayer.Overhead, new BasisCryptoLayer().ExtraPacketSizeForLayer);

        var (client, server) = NewFixedKeyPair();
        byte[] packet = BuildPacket(HeaderChanneled, 64);
        byte[] wire = Seal(client, ServerAddress, packet);

        Assert.Equal(packet.Length + BasisCryptoLayer.Overhead, wire.Length);
        Assert.Equal(HeaderChanneled, wire[0]);
        Assert.False(wire.AsSpan(1, 64).SequenceEqual(packet.AsSpan(1)));
        Assert.Equal(1L, ReadCounter(wire));
        Assert.Equal(packet, Open(server, ClientAddress, wire));
    }

    [Fact]
    public void Outbound_TrailerIsLittleEndianSendCounter()
    {
        const long InitialCounter = 0x0102030405060708;
        var client = new BasisCryptoLayer();
        client.SetEndpointKeys(ServerAddress, SequentialKey(0x2C), SequentialKey(0x9D), InitialCounter);

        byte[] packet = BuildPacket(HeaderUnreliable, 4);
        byte[] wire1 = Seal(client, ServerAddress, packet);
        byte[] trailer = wire1.AsSpan(wire1.Length - BasisCryptoLayer.CounterSize).ToArray();
        Assert.Equal(new byte[] { 0x09, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01 }, trailer);
        Assert.Equal(InitialCounter + 1, ReadCounter(wire1));

        byte[] wire2 = Seal(client, ServerAddress, packet);
        Assert.Equal(InitialCounter + 2, ReadCounter(wire2));
    }

    [Fact]
    public void Outbound_SamePlaintext_ProducesDifferentCiphertexts()
    {
        var (client, server) = NewFixedKeyPair();
        byte[] packet = BuildPacket(HeaderUnreliable, 32);
        byte[] wire1 = Seal(client, ServerAddress, packet);
        byte[] wire2 = Seal(client, ServerAddress, packet);

        Assert.False(wire1.AsSpan(1, 32).SequenceEqual(wire2.AsSpan(1, 32)));
        Assert.Equal(packet, Open(server, ClientAddress, wire1));
        Assert.Equal(packet, Open(server, ClientAddress, wire2));
    }

    [Fact]
    public void Outbound_IsDeterministic_ForSameKeysAndCounter()
    {
        byte[] keyAB = SequentialKey(0x3A);
        byte[] keyBA = SequentialKey(0xD4);
        var first = new BasisCryptoLayer();
        var second = new BasisCryptoLayer();
        first.SetEndpointKeys(ServerAddress, keyAB, keyBA);
        second.SetEndpointKeys(ServerAddress, keyAB, keyBA);

        byte[] packet = BuildPacket(HeaderMerged, 48);
        Assert.Equal(Seal(first, ServerAddress, packet), Seal(second, ServerAddress, packet));

        var offsetCounter = new BasisCryptoLayer();
        offsetCounter.SetEndpointKeys(ServerAddress, keyAB, keyBA, 5);
        Assert.NotEqual(Seal(second, ServerAddress, packet), Seal(offsetCounter, ServerAddress, packet));
    }

    [Fact]
    public void Outbound_RespectsNonZeroOffset()
    {
        var (client, server) = NewFixedKeyPair();
        byte[] packet = BuildPacket(HeaderUnreliable, 24);
        const int Offset = 5;
        byte[] buffer = new byte[Offset + packet.Length + BasisCryptoLayer.Overhead];
        for (int i = 0; i < Offset; i++) buffer[i] = 0xAA;
        packet.CopyTo(buffer, Offset);

        int offset = Offset;
        int length = packet.Length;
        IPEndPoint endpoint = ServerAddress;
        client.ProcessOutBoundPacket(ref endpoint, ref buffer, ref offset, ref length);

        Assert.Equal(Offset, offset);
        Assert.Equal(packet.Length + BasisCryptoLayer.Overhead, length);
        for (int i = 0; i < Offset; i++) Assert.Equal(0xAA, buffer[i]);

        byte[] wire = new byte[length];
        Array.Copy(buffer, offset, wire, 0, length);
        Assert.Equal(packet, Open(server, ClientAddress, wire));
    }

    [Fact]
    public void WireFormat_LayerOutput_OpensWithRawAeadCipher()
    {
        byte[] keyAB = SequentialKey(0x21);
        var client = new BasisCryptoLayer();
        client.SetEndpointKeys(ServerAddress, keyAB, SequentialKey(0x91));

        byte[] packet = BuildPacket(HeaderMerged, 32);
        byte[] wire = Seal(client, ServerAddress, packet);

        // Documented layout: [header][ciphertext][16B tag][8B LE counter];
        // nonce = counter bytes zero-padded to 12; AAD = header byte.
        byte[] nonce = new byte[BasisAeadCipher.NonceSize];
        Array.Copy(wire, wire.Length - BasisCryptoLayer.CounterSize, nonce, 0, BasisCryptoLayer.CounterSize);
        byte[] body = new byte[32];
        Array.Copy(wire, 1, body, 0, 32);

        using var cipher = new BasisAeadCipher(keyAB);
        Assert.True(cipher.Open(nonce, wire[0], body, 0, 32, wire, 1 + 32));
        Assert.Equal(packet.AsSpan(1).ToArray(), body);
    }

    [Fact]
    public void WireFormat_RawAeadConstructedDatagram_AcceptedByInbound()
    {
        byte[] recvKey = SequentialKey(0x55);
        var server = new BasisCryptoLayer();
        server.SetEndpointKeys(ClientAddress, SequentialKey(0x66), recvKey);

        byte[] packet = BuildPacket(HeaderChanneled, 16);
        const long Counter = 7;
        byte[] wire = new byte[packet.Length + BasisCryptoLayer.Overhead];
        packet.CopyTo(wire, 0);
        byte[] nonce = new byte[BasisAeadCipher.NonceSize];
        nonce[0] = (byte)Counter;
        using (var cipher = new BasisAeadCipher(recvKey))
        {
            cipher.Seal(nonce, packet[0], wire, 1, 16, wire, packet.Length);
        }
        wire[packet.Length + BasisAeadCipher.TagSize] = (byte)Counter;

        Assert.Equal(packet, Open(server, ClientAddress, wire));
    }

    // ------------------------------------------------------ tampering & replay

    [Fact]
    public void Inbound_AnyFlippedByte_IsDropped_WithoutPlaintextLeak()
    {
        var (client, server) = NewFixedKeyPair();
        byte[] packet = BuildPacket(HeaderUnreliable, 8);
        byte[] wire = Seal(client, ServerAddress, packet);
        Assert.Equal(packet.Length + BasisCryptoLayer.Overhead, wire.Length);

        // Exhaustive over ciphertext, tag and counter positions (header handled separately).
        for (int position = 1; position < wire.Length; position++)
        {
            byte[] tampered = (byte[])wire.Clone();
            tampered[position] ^= 0x01;
            (byte[] buffer, int length) = OpenRaw(server, ClientAddress, tampered);
            Assert.True(length == 0, $"tampered byte at {position} was not rejected");
            Assert.False(buffer.AsSpan(1, 8).SequenceEqual(packet.AsSpan(1)), $"plaintext leaked for tamper at {position}");
        }

        // Failed decrypts must not poison the session for the genuine datagram.
        Assert.Equal(packet, Open(server, ClientAddress, wire));
    }

    [Fact]
    public void Inbound_HeaderBitFlip_SameProperty_IsDropped()
    {
        var (client, server) = NewFixedKeyPair();
        byte[] wire = Seal(client, ServerAddress, BuildPacket(HeaderUnreliable, 16));

        // High header bits are outside the property mask, but the whole header
        // byte is authenticated as AAD, so the flip must break the tag.
        wire[0] ^= 0x80;
        (_, int length) = OpenRaw(server, ClientAddress, wire);
        Assert.Equal(0, length);
    }

    [Fact]
    public void Inbound_HeaderMorphedToNonEncryptable_BypassesDecryption()
    {
        var (client, server) = NewFixedKeyPair();
        byte[] packet = BuildPacket(HeaderUnreliable, 16);
        byte[] wire = Seal(client, ServerAddress, packet);

        // Property 0x02 is not an encryptable property, so the layer passes the
        // datagram through untouched (still ciphertext) for LiteNetLib to vet.
        wire[0] ^= 0x02;
        (byte[] buffer, int length) = OpenRaw(server, ClientAddress, wire);
        Assert.Equal(wire.Length, length);
        Assert.Equal(wire, buffer);
        Assert.False(buffer.AsSpan(1, 16).SequenceEqual(packet.AsSpan(1)));
    }

    [Fact]
    public void Inbound_ReplayedDatagram_IsAcceptedAgain_NoReplayWindow()
    {
        var (client, server) = NewFixedKeyPair();
        byte[] packet = BuildPacket(HeaderChanneled, 64);
        byte[] wire = Seal(client, ServerAddress, packet);

        // Pins current behavior: the layer carries the nonce in the datagram and
        // keeps no inbound sequence state, so byte-identical replays decrypt again.
        Assert.Equal(packet, Open(server, ClientAddress, wire));
        Assert.Equal(packet, Open(server, ClientAddress, wire));
    }

    [Fact]
    public void Inbound_OutOfOrderDelivery_Succeeds()
    {
        var (client, server) = NewFixedKeyPair();
        byte[] first = BuildPacket(HeaderUnreliable, 10);
        byte[] second = BuildPacket(HeaderUnreliable, 20);
        byte[] wire1 = Seal(client, ServerAddress, first);
        byte[] wire2 = Seal(client, ServerAddress, second);

        Assert.Equal(second, Open(server, ClientAddress, wire2));
        Assert.Equal(first, Open(server, ClientAddress, wire1));
    }

    [Fact]
    public void Inbound_WrongKeys_Dropped_WithoutPlaintextLeak()
    {
        var (client, _) = NewFixedKeyPair();
        var stranger = new BasisCryptoLayer();
        stranger.SetEndpointKeys(ClientAddress, SequentialKey(0x5A), SequentialKey(0xC3));

        byte[] packet = BuildPacket(HeaderUnreliable, 64);
        byte[] wire = Seal(client, ServerAddress, packet);
        (byte[] buffer, int length) = OpenRaw(stranger, ClientAddress, wire);

        Assert.Equal(0, length);
        Assert.False(buffer.AsSpan(1, 64).SequenceEqual(packet.AsSpan(1)));
    }

    [Fact]
    public void Inbound_SwappedDirectionalKeys_Dropped()
    {
        byte[] keyAB = SequentialKey(0x60);
        byte[] keyBA = SequentialKey(0xE7);
        var client = new BasisCryptoLayer();
        client.SetEndpointKeys(ServerAddress, keyAB, keyBA);
        // Misconfigured peer installs the same orientation instead of the mirror.
        var mirrored = new BasisCryptoLayer();
        mirrored.SetEndpointKeys(ClientAddress, keyAB, keyBA);

        byte[] wire = Seal(client, ServerAddress, BuildPacket(HeaderChanneled, 32));
        (_, int length) = OpenRaw(mirrored, ClientAddress, wire);
        Assert.Equal(0, length);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(10)]
    [InlineData(24)]
    [InlineData(25)]
    [InlineData(40)]
    [InlineData(56)]
    public void Inbound_TruncatedDatagram_IsDropped(int truncatedLength)
    {
        var (client, server) = NewFixedKeyPair();
        byte[] wire = Seal(client, ServerAddress, BuildPacket(HeaderUnreliable, 32));
        Assert.True(truncatedLength < wire.Length);

        byte[] truncated = wire.AsSpan(0, truncatedLength).ToArray();
        (_, int length) = OpenRaw(server, ClientAddress, truncated);
        Assert.Equal(0, length);
    }

    [Fact]
    public void Inbound_GarbageDatagram_IsDropped()
    {
        var (_, server) = NewFixedKeyPair();

        byte[] junk = new byte[100];
        new Random(1234).NextBytes(junk);
        junk[0] = HeaderUnreliable;
        (_, int length) = OpenRaw(server, ClientAddress, junk);
        Assert.Equal(0, length);

        // Minimum length that reaches the AEAD: zero payload, garbage tag/counter.
        byte[] minimal = new byte[1 + BasisCryptoLayer.Overhead];
        new Random(5678).NextBytes(minimal);
        minimal[0] = HeaderChanneled;
        (_, length) = OpenRaw(server, ClientAddress, minimal);
        Assert.Equal(0, length);
    }

    [Fact]
    public void Inbound_UsesLengthNotBufferSize()
    {
        // LiteNetLib hands the layer a reused oversized receive buffer.
        var (client, server) = NewFixedKeyPair();
        byte[] packet = BuildPacket(HeaderUnreliable, 32);
        byte[] wire = Seal(client, ServerAddress, packet);

        byte[] oversizedBuffer = new byte[2048];
        wire.CopyTo(oversizedBuffer, 0);
        int length = wire.Length;
        IPEndPoint endpoint = ClientAddress;
        server.ProcessInboundPacket(ref endpoint, ref oversizedBuffer, ref length);

        Assert.Equal(packet.Length, length);
        Assert.True(oversizedBuffer.AsSpan(0, length).SequenceEqual(packet));
    }

    // ------------------------------------------------- sessions & endpoints

    [Fact]
    public void NoSession_PacketsPassThroughUnmodified()
    {
        var layer = new BasisCryptoLayer();
        byte[] packet = BuildPacket(HeaderUnreliable, 32);

        Assert.Equal(packet, Seal(layer, ServerAddress, packet));

        (byte[] buffer, int length) = OpenRaw(layer, ClientAddress, packet);
        Assert.Equal(packet.Length, length);
        Assert.Equal(packet, buffer);
    }

    [Theory]
    [InlineData((byte)0x02)] // Ack
    [InlineData((byte)0x03)] // Ping
    [InlineData((byte)0x05)] // ConnectRequest
    [InlineData((byte)0x1F)] // highest property id
    public void NonEncryptableProperties_BypassEncryption_EvenWithSession(byte header)
    {
        var (client, server) = NewFixedKeyPair();
        byte[] packet = BuildPacket(header, 32);

        Assert.Equal(packet, Seal(client, ServerAddress, packet));

        (byte[] buffer, int length) = OpenRaw(server, ClientAddress, packet);
        Assert.Equal(packet.Length, length);
        Assert.Equal(packet, buffer);
    }

    [Theory]
    [InlineData(HeaderUnreliable)]
    [InlineData(HeaderChanneled)]
    [InlineData(HeaderMerged)]
    [InlineData((byte)0xE1)] // Channeled with high header bits set
    [InlineData((byte)0x8C)] // Merged with high header bit set
    public void EncryptableProperties_AreEncrypted_IncludingMaskedHeaderBits(byte header)
    {
        var (client, server) = NewFixedKeyPair();
        byte[] packet = BuildPacket(header, 40);
        byte[] wire = Seal(client, ServerAddress, packet);

        Assert.Equal(packet.Length + BasisCryptoLayer.Overhead, wire.Length);
        Assert.Equal(header, wire[0]);
        Assert.False(wire.AsSpan(1, 40).SequenceEqual(packet.AsSpan(1)));
        Assert.Equal(packet, Open(server, ClientAddress, wire));
    }

    [Fact]
    public void Endpoints_MatchByAddressAndPort_NotByInstance()
    {
        byte[] keyAB = SequentialKey(0x12);
        byte[] keyBA = SequentialKey(0x77);
        var client = new BasisCryptoLayer();
        var server = new BasisCryptoLayer();
        client.SetEndpointKeys(new IPEndPoint(IPAddress.Loopback, 7777), keyAB, keyBA);
        server.SetEndpointKeys(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 7777), keyBA, keyAB);

        Assert.True(client.HasEndpoint(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 7777)));
        Assert.False(client.HasEndpoint(new IPEndPoint(IPAddress.Loopback, 7778)));

        byte[] packet = BuildPacket(HeaderUnreliable, 16);
        byte[] wire = Seal(client, new IPEndPoint(IPAddress.Parse("127.0.0.1"), 7777), packet);
        Assert.Equal(packet.Length + BasisCryptoLayer.Overhead, wire.Length);
        Assert.Equal(packet, Open(server, new IPEndPoint(IPAddress.Loopback, 7777), wire));

        // A different port is a different session: passthrough.
        Assert.Equal(packet, Seal(client, new IPEndPoint(IPAddress.Loopback, 7778), packet));
    }

    [Fact]
    public void RemoveEndpoint_RevertsToPassthrough_AndKeyedPeerDropsCleartext()
    {
        var (client, server) = NewFixedKeyPair();
        byte[] packet = BuildPacket(HeaderUnreliable, 32);
        Assert.Equal(packet, Open(server, ClientAddress, Seal(client, ServerAddress, packet)));

        client.RemoveEndpoint(ServerAddress);
        Assert.False(client.HasEndpoint(ServerAddress));
        Assert.Equal(0, client.SessionCount);

        byte[] cleartext = Seal(client, ServerAddress, packet);
        Assert.Equal(packet, cleartext);

        // The still-keyed side refuses unauthenticated traffic instead of parsing it.
        (_, int length) = OpenRaw(server, ClientAddress, cleartext);
        Assert.Equal(0, length);
    }

    [Fact]
    public void RemapEndpoint_MovesSessionAndKeepsCounter()
    {
        var (client, server) = NewFixedKeyPair();
        byte[] packet = BuildPacket(HeaderUnreliable, 16);
        byte[] wire1 = Seal(client, ServerAddress, packet);
        Assert.Equal(1L, ReadCounter(wire1));

        var moved = new IPEndPoint(IPAddress.Parse("198.51.100.5"), 45678);
        client.RemapEndpoint(ServerAddress, moved);
        Assert.False(client.HasEndpoint(ServerAddress));
        Assert.True(client.HasEndpoint(moved));
        Assert.Equal(1, client.SessionCount);

        byte[] wire2 = Seal(client, moved, packet);
        Assert.Equal(2L, ReadCounter(wire2));
        Assert.Equal(packet, Open(server, ClientAddress, wire2));

        // The old endpoint no longer encrypts.
        Assert.Equal(packet, Seal(client, ServerAddress, packet));
    }

    [Fact]
    public void Reinstall_SameKeys_DefaultCounter_ReusesNonces()
    {
        byte[] keyAB = SequentialKey(0x40);
        byte[] keyBA = SequentialKey(0x8E);
        var client = new BasisCryptoLayer();
        client.SetEndpointKeys(ServerAddress, keyAB, keyBA);
        byte[] packet = BuildPacket(HeaderUnreliable, 32);

        byte[] wire1 = Seal(client, ServerAddress, packet);
        byte[] wire2 = Seal(client, ServerAddress, packet);
        Assert.NotEqual(wire1, wire2);

        // Pins the documented hazard: reinstalling the same keys with the default
        // initial counter restarts the nonce sequence, reproducing wire1 exactly.
        client.SetEndpointKeys(ServerAddress, keyAB, keyBA);
        byte[] wire3 = Seal(client, ServerAddress, packet);
        Assert.Equal(wire1, wire3);

        // Passing a fresh initial counter is the documented mitigation.
        client.SetEndpointKeys(ServerAddress, keyAB, keyBA, 1000);
        byte[] wire4 = Seal(client, ServerAddress, packet);
        Assert.Equal(1001L, ReadCounter(wire4));
    }

    [Fact]
    public void SessionCount_TracksInstallReplaceRemove()
    {
        var layer = new BasisCryptoLayer();
        Assert.Equal(0, layer.SessionCount);

        layer.SetEndpointKeys(ClientAddress, SequentialKey(1), SequentialKey(2));
        Assert.Equal(1, layer.SessionCount);
        layer.SetEndpointKeys(ServerAddress, SequentialKey(3), SequentialKey(4));
        Assert.Equal(2, layer.SessionCount);
        layer.SetEndpointKeys(ClientAddress, SequentialKey(5), SequentialKey(6));
        Assert.Equal(2, layer.SessionCount);

        layer.RemoveEndpoint(ClientAddress);
        Assert.Equal(1, layer.SessionCount);
        layer.RemoveEndpoint(ClientAddress);
        Assert.Equal(1, layer.SessionCount);
        layer.RemoveEndpoint(ServerAddress);
        Assert.Equal(0, layer.SessionCount);
    }

    [Fact]
    public void NullEndpoints_AreIgnoredEverywhere()
    {
        var layer = new BasisCryptoLayer();
        byte[] key = SequentialKey(0x10);

        layer.SetEndpointKeys(null!, key, key);
        Assert.Equal(0, layer.SessionCount);
        Assert.False(layer.HasEndpoint(null!));
        layer.RemoveEndpoint(null!);
        layer.RemapEndpoint(null!, ServerAddress);
        layer.RemapEndpoint(ServerAddress, null!);
        Assert.Equal(0, layer.SessionCount);

        byte[] packet = BuildPacket(HeaderUnreliable, 8);
        byte[] buffer = new byte[packet.Length + BasisCryptoLayer.Overhead];
        packet.CopyTo(buffer, 0);
        int offset = 0;
        int length = packet.Length;
        IPEndPoint nullOutbound = null!;
        layer.ProcessOutBoundPacket(ref nullOutbound, ref buffer, ref offset, ref length);
        Assert.Equal(packet.Length, length);

        byte[] inboundBuffer = (byte[])packet.Clone();
        int inboundLength = packet.Length;
        IPEndPoint nullInbound = null!;
        layer.ProcessInboundPacket(ref nullInbound, ref inboundBuffer, ref inboundLength);
        Assert.Equal(packet.Length, inboundLength);
        Assert.Equal(packet, inboundBuffer);
    }

    [Fact]
    public void SetEndpointKeys_RejectsWrongSizedKeys()
    {
        var layer = new BasisCryptoLayer();
        Assert.Throws<ArgumentException>(() => layer.SetEndpointKeys(ServerAddress, new byte[16], SequentialKey(0x01)));
        Assert.Throws<ArgumentException>(() => layer.SetEndpointKeys(ServerAddress, SequentialKey(0x01), new byte[33]));
        Assert.Throws<ArgumentException>(() => layer.SetEndpointKeys(ServerAddress, Array.Empty<byte>(), SequentialKey(0x01)));

        Assert.False(layer.HasEndpoint(ServerAddress));
        Assert.Equal(0, layer.SessionCount);
    }

    [Fact]
    public void ConcurrentSeals_ClaimUniqueCounters_AllDecrypt()
    {
        var (client, server) = NewFixedKeyPair();
        const int PacketCount = 256;
        byte[] template = BuildPacket(HeaderUnreliable, 48);
        byte[][] wires = new byte[PacketCount][];

        Parallel.For(0, PacketCount, i => wires[i] = Seal(client, ServerAddress, template));

        long[] counters = wires.Select(ReadCounter).OrderBy(c => c).ToArray();
        long[] expected = Enumerable.Range(1, PacketCount).Select(i => (long)i).ToArray();
        Assert.Equal(expected, counters);

        foreach (byte[] wire in wires)
            Assert.Equal(template, Open(server, ClientAddress, wire));
    }
}
