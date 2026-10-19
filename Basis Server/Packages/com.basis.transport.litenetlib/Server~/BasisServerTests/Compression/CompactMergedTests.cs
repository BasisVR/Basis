using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using LiteNetLib;
using LiteNetLib.Layers;
using Xunit;

namespace BasisServerTests;

public sealed class CompactMergedTests
{
    private const byte UnreliableProperty = 0;
    private const byte ChanneledProperty = 1;
    private const byte AckProperty = 2;
    private const byte MergedProperty = 12;
    private const byte CompactMergedProperty = 18;

    [Fact]
    public void MixedCompact_EncodesBoundaryLengthsAndChannels()
    {
        using var pair = new LoopbackPair(mergeHoldMs: 20);
        pair.ClientLayer.ClearOutbound();

        byte[] shortPayload = Filled(255, 0x31);
        byte[] extendedPayload = Filled(256, 0x42);
        pair.ClientPeer.Send(shortPayload, 63, DeliveryMethod.Unreliable);
        pair.ClientPeer.Send(extendedPayload, 62, DeliveryMethod.Unreliable);

        byte[] datagram = pair.ClientLayer.WaitForOutbound(CompactMergedProperty);
        Assert.Equal(CompactMergedProperty, Property(datagram));

        int offset = 1;
        Assert.Equal(63, datagram[offset]);
        Assert.Equal(255, datagram[offset + 1]);
        offset += 2;
        Assert.Equal(shortPayload, datagram.AsSpan(offset, shortPayload.Length).ToArray());
        offset += shortPayload.Length;

        Assert.Equal((byte)(0x80 | 62), datagram[offset]);
        Assert.Equal(0, datagram[offset + 1]);
        Assert.Equal(1, datagram[offset + 2]);
        offset += 3;
        Assert.Equal(extendedPayload, datagram.AsSpan(offset, extendedPayload.Length).ToArray());
        offset += extendedPayload.Length;

        Assert.Equal(datagram.Length, offset);
        Assert.True(ParseCompactFully(datagram));
        pair.WaitForServerReceives(2);
        Assert.Contains(pair.ServerReceives, r => r.Channel == 63 && r.Data.SequenceEqual(shortPayload));
        Assert.Contains(pair.ServerReceives, r => r.Channel == 62 && r.Data.SequenceEqual(extendedPayload));
    }

    [Fact]
    public void ZeroLengthEntries_AreValidAndNotTerminators()
    {
        using var pair = new LoopbackPair(mergeHoldMs: 20);
        pair.ClientLayer.ClearOutbound();

        pair.ClientPeer.Send(Array.Empty<byte>(), 3, DeliveryMethod.Unreliable);
        pair.ClientPeer.Send(Array.Empty<byte>(), 4, DeliveryMethod.Unreliable);

        byte[] datagram = pair.ClientLayer.WaitForOutbound(CompactMergedProperty);
        Assert.Equal(new byte[] { 3, 0, 4, 0 }, datagram.AsSpan(1).ToArray());
        Assert.True(ParseCompactFully(datagram));
        pair.WaitForServerReceives(2);
        Assert.Contains(pair.ServerReceives, r => r.Channel == 3 && r.Data.Length == 0);
        Assert.Contains(pair.ServerReceives, r => r.Channel == 4 && r.Data.Length == 0);
    }

    [Fact]
    public void SingleEntryCompactBatch_RewritesToNormalUnreliableWithoutExtraByte()
    {
        using var pair = new LoopbackPair(mergeHoldMs: 10);

        pair.ClientLayer.ClearOutbound();
        byte[] shortPayload = Filled(10, 0x51);
        pair.ClientPeer.Send(shortPayload, 63, DeliveryMethod.Unreliable);
        byte[] shortDatagram = pair.ClientLayer.WaitForOutbound(UnreliableProperty);
        Assert.Equal(2 + shortPayload.Length, shortDatagram.Length);
        Assert.Equal(63, shortDatagram[1]);
        Assert.Equal(shortPayload, shortDatagram.AsSpan(2).ToArray());

        pair.ClientLayer.ClearOutbound();
        byte[] extendedPayload = Filled(256, 0x62);
        pair.ClientPeer.Send(extendedPayload, 63, DeliveryMethod.Unreliable);
        byte[] extendedDatagram = pair.ClientLayer.WaitForOutbound(UnreliableProperty);
        Assert.Equal(2 + extendedPayload.Length, extendedDatagram.Length);
        Assert.Equal(63, extendedDatagram[1]);
        Assert.Equal(extendedPayload, extendedDatagram.AsSpan(2).ToArray());
    }

    [Fact]
    public void SingleRawEntry_SendsOriginalChanneledPacketWithoutOuterOverhead()
    {
        using var pair = new LoopbackPair(mergeHoldMs: 10);
        pair.ClientLayer.ClearOutbound();

        byte[] payload = { 0x61, 0x62, 0x63 };
        pair.ClientPeer.Send(payload, 2, DeliveryMethod.ReliableOrdered);

        byte[] datagram = pair.ClientLayer.WaitForOutbound(ChanneledProperty);
        Assert.Equal(4 + payload.Length, datagram.Length);
        Assert.Equal(payload, datagram.AsSpan(4).ToArray());
        pair.WaitForServerReceives(1);
        Assert.Contains(pair.ServerReceives, r => r.Channel == 2 && r.Data.SequenceEqual(payload));
    }

    [Fact]
    public void CompactMerged_ExactMtuFitFlushesBeforeNextEntry()
    {
        using var pair = new LoopbackPair(mergeHoldMs: 20);
        pair.ClientLayer.ClearOutbound();

        pair.ClientPeer.Send(Filled(596, 0x21), 6, DeliveryMethod.Unreliable);
        pair.ClientPeer.Send(Filled(597, 0x22), 7, DeliveryMethod.Unreliable);
        pair.ClientPeer.Send(new byte[] { 0x23 }, 8, DeliveryMethod.Unreliable);

        byte[] datagram = pair.ClientLayer.WaitForOutbound(CompactMergedProperty);
        Assert.Equal(1200, datagram.Length);
        Assert.True(ParseCompactFully(datagram));
        pair.WaitForServerReceives(3);
    }

    [Fact]
    public void CompactMerged_DirectThresholdUsesCompactRepresentation()
    {
        using var pair = new LoopbackPair(mergeHoldMs: 10);

        pair.ClientPeer.Send(Filled(1175, 0x33), 9, DeliveryMethod.Unreliable);
        byte[] optimized = pair.ClientLayer.WaitForOutbound(UnreliableProperty);
        Assert.Equal(1177, optimized.Length);

        pair.ClientLayer.ClearOutbound();
        pair.ClientPeer.Send(Filled(1176, 0x34), 9, DeliveryMethod.Unreliable);
        byte[] direct = pair.ClientLayer.WaitForOutbound(UnreliableProperty);
        Assert.Equal(1178, direct.Length);
    }

    [Fact]
    public void OutOfRangeUnreliable_DoesNotFlushPendingCompactBuffer()
    {
        using var pair = new LoopbackPair(mergeHoldMs: 250);
        pair.ClientLayer.ClearOutbound();

        pair.ClientPeer.Send(new byte[] { 0x41 }, 1, DeliveryMethod.Unreliable);
        Thread.Sleep(25);
        Assert.DoesNotContain(
            pair.ClientLayer.OutboundSnapshot(),
            p => Property(p) == UnreliableProperty && p.Length > 1 && p[1] == 1);

        pair.ClientPeer.Send(new byte[] { 0x42 }, 64, DeliveryMethod.Unreliable);
        WaitUntil(
            () => pair.ClientLayer.OutboundSnapshot().Any(
                p => Property(p) == UnreliableProperty && p.Length == 3 && p[1] == 64),
            "out-of-range direct unreliable send");

        Thread.Sleep(50);
        Assert.DoesNotContain(
            pair.ClientLayer.OutboundSnapshot(),
            p => Property(p) == UnreliableProperty && p.Length > 1 && p[1] == 1);

        WaitUntil(
            () => pair.ClientLayer.OutboundSnapshot().Any(
                p => Property(p) == UnreliableProperty && p.Length == 3 && p[1] == 1),
            "held compact unreliable send");
        pair.WaitForServerReceives(2);
    }

    [Theory]
    [InlineData((byte)64)]
    [InlineData((byte)127)]
    [InlineData((byte)128)]
    public void ChannelsAbove63_BypassCompactWithoutMasking(byte channel)
    {
        using var pair = new LoopbackPair(mergeHoldMs: 20);
        pair.ClientLayer.ClearOutbound();

        pair.ClientPeer.Send(new byte[] { 0x71 }, channel, DeliveryMethod.Unreliable);
        byte[] datagram = pair.ClientLayer.WaitForOutbound(UnreliableProperty);

        Assert.Equal(3, datagram.Length);
        Assert.Equal(channel, datagram[1]);
        Assert.Equal(0x71, datagram[2]);
        Assert.DoesNotContain(pair.ClientLayer.OutboundSnapshot(), p => Property(p) == MergedProperty);
    }

    [Fact]
    public void AckAndChanneled_AreRawEntriesInsideCompactMerged()
    {
        using var pair = new LoopbackPair(mergeHoldMs: 100);
        pair.ClientLayer.ClearOutbound();
        pair.ServerLayer.ClearOutbound();

        pair.ClientPeer.Send(new byte[] { 0xC1 }, 2, DeliveryMethod.ReliableOrdered);
        pair.ClientPeer.Send(new byte[] { 0xA1 }, 1, DeliveryMethod.Unreliable);

        WaitUntil(
            () => pair.ServerReceives.Any(r => r.Channel == 2 && r.Method == DeliveryMethod.ReliableOrdered),
            "server reliable receive");
        pair.ServerPeer.Send(new byte[] { 0xB1 }, 4, DeliveryMethod.Unreliable);

        Thread.Sleep(130);

        byte[][] clientDatagrams = pair.ClientLayer.OutboundSnapshot();
        Assert.DoesNotContain(clientDatagrams, p => Property(p) == MergedProperty);
        CompactEntry[] clientEntries = clientDatagrams
            .Where(p => Property(p) == CompactMergedProperty)
            .SelectMany(ParseCompactEntries)
            .ToArray();
        Assert.Contains(clientEntries, e => e.IsRaw && Property(e.Data) == ChanneledProperty);
        Assert.Contains(clientEntries, e => !e.IsRaw && e.Channel == 1 && e.Data.SequenceEqual(new byte[] { 0xA1 }));

        byte[][] serverDatagrams = pair.ServerLayer.OutboundSnapshot();
        Assert.DoesNotContain(serverDatagrams, p => Property(p) == MergedProperty);
        CompactEntry[] serverEntries = serverDatagrams
            .Where(p => Property(p) == CompactMergedProperty)
            .SelectMany(ParseCompactEntries)
            .ToArray();
        Assert.Contains(serverEntries, e => e.IsRaw && Property(e.Data) == AckProperty);
        Assert.Contains(serverEntries, e => !e.IsRaw && e.Channel == 4 && e.Data.SequenceEqual(new byte[] { 0xB1 }));
    }

    [Fact]
    public void RawChanneled_ExtendedEntryRoundTrips()
    {
        using var pair = new LoopbackPair(mergeHoldMs: 100);
        pair.ClientLayer.ClearOutbound();

        byte[] reliablePayload = Filled(300, 0x5A);
        pair.ClientPeer.Send(reliablePayload, 2, DeliveryMethod.ReliableOrdered);
        pair.ClientPeer.Send(new byte[] { 0x22 }, 1, DeliveryMethod.Unreliable);

        byte[] datagram = pair.ClientLayer.WaitForOutbound(CompactMergedProperty);
        CompactEntry[] entries = ParseCompactEntries(datagram);
        Assert.Contains(entries, e => e.IsRaw && e.Data.Length == 304 && Property(e.Data) == ChanneledProperty);
        pair.WaitForServerReceives(2);
        Assert.Contains(pair.ServerReceives, r => r.Channel == 2 && r.Data.SequenceEqual(reliablePayload));
    }

    [Fact]
    public void FragmentedChanneled_FinalFragmentCanTravelAsRawCompactEntry()
    {
        using var pair = new LoopbackPair(mergeHoldMs: 20);
        pair.ClientLayer.ClearOutbound();

        byte[] reliablePayload = Filled(1300, 0x6A);
        pair.ClientPeer.Send(reliablePayload, 2, DeliveryMethod.ReliableOrdered);
        pair.ClientPeer.Send(new byte[] { 0x7E }, 1, DeliveryMethod.Unreliable);

        pair.WaitForServerReceives(2);
        Assert.Contains(
            pair.ServerReceives,
            r => r.Channel == 2 && r.Method == DeliveryMethod.ReliableOrdered && r.Data.SequenceEqual(reliablePayload));
        Assert.Contains(
            pair.ServerReceives,
            r => r.Channel == 1 && r.Method == DeliveryMethod.Unreliable && r.Data.SequenceEqual(new byte[] { 0x7E }));

        byte[][] compactDatagrams = pair.ClientLayer.OutboundSnapshot()
            .Where(p => Property(p) == CompactMergedProperty)
            .ToArray();
        Assert.Contains(
            compactDatagrams.SelectMany(ParseCompactEntries),
            e => e.IsRaw && Property(e.Data) == ChanneledProperty && (e.Data[0] & 0x80) != 0);
    }

    [Fact]
    public void OversizedChanneled_FlushesEarlierBufferedEntryBeforeDirectSend()
    {
        using var pair = new LoopbackPair(mergeHoldMs: 500);
        pair.ClientLayer.ClearOutbound();

        pair.ClientPeer.Send(new byte[] { 0x31 }, 1, DeliveryMethod.Unreliable);
        Thread.Sleep(20);
        pair.ClientPeer.Send(Filled(1197, 0x6B), 2, DeliveryMethod.ReliableOrdered);

        WaitUntil(
            () => pair.ClientLayer.OutboundSnapshot().Any(p => Property(p) == ChanneledProperty),
            "oversized direct channeled send");
        byte[][] datagrams = pair.ClientLayer.OutboundSnapshot();
        int bufferedIndex = Array.FindIndex(
            datagrams,
            p => Property(p) == UnreliableProperty && p.Length == 3 && p[1] == 1 && p[2] == 0x31);
        int directIndex = Array.FindIndex(datagrams, p => Property(p) == ChanneledProperty);
        Assert.True(bufferedIndex >= 0);
        Assert.True(directIndex > bufferedIndex);
    }

    [Theory]
    [MemberData(nameof(MalformedCompactBodies))]
    public void MalformedCompactMerged_IsDroppedWithoutBreakingPeer(byte[] compactBody)
    {
        using var pair = new LoopbackPair(mergeHoldMs: 0);
        int receivedBefore = pair.ServerReceives.Count;
        int poolBefore = pair.Server.PoolCount;

        pair.ServerLayer.ReplaceNextInboundUnreliableWithCompact(compactBody);
        pair.ClientPeer.Send(new byte[] { 0xEE }, 0, DeliveryMethod.Unreliable);
        Thread.Sleep(40);
        Assert.Equal(receivedBefore, pair.ServerReceives.Count);

        byte[] valid = { 0xAB, 0xCD };
        pair.ClientPeer.Send(valid, 0, DeliveryMethod.Unreliable);
        pair.WaitForServerReceives(receivedBefore + 1);
        Assert.Equal(valid, pair.ServerReceives.Last().Data);
        Assert.True(pair.Server.PoolCount >= poolBefore - 2);
    }

    [Fact]
    public void EmptyCompactMergedOuter_IsValidAndRecycled()
    {
        using var pair = new LoopbackPair(mergeHoldMs: 0);
        int receivedBefore = pair.ServerReceives.Count;
        int poolBefore = pair.Server.PoolCount;

        pair.ServerLayer.ReplaceNextInboundUnreliableWithCompact(Array.Empty<byte>());
        pair.ClientPeer.Send(new byte[] { 0xEE }, 0, DeliveryMethod.Unreliable);
        Thread.Sleep(40);

        Assert.Equal(receivedBefore, pair.ServerReceives.Count);
        Assert.True(pair.Server.PoolCount >= poolBefore - 2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(62)]
    [InlineData(63)]
    public void CompactMerged_SixBitChannels_RoundTrip(byte channel)
    {
        using var pair = new LoopbackPair(mergeHoldMs: 20);
        pair.ClientLayer.ClearOutbound();

        pair.ClientPeer.Send(new byte[] { 0x91 }, channel, DeliveryMethod.Unreliable);
        pair.ClientPeer.Send(new byte[] { 0x92 }, channel, DeliveryMethod.Unreliable);

        byte[] datagram = pair.ClientLayer.WaitForOutbound(CompactMergedProperty);
        Assert.Equal(channel, datagram[1]);
        Assert.True(ParseCompactFully(datagram));
        pair.WaitForServerReceives(2);
        Assert.All(pair.ServerReceives, received => Assert.Equal(channel, received.Channel));
    }

    [Fact]
    public void CompactMerged_MaxChannelAndZeroPayload_RoundTrip()
    {
        using var pair = new LoopbackPair(mergeHoldMs: 0);
        int before = pair.ServerReceives.Count;

        pair.ServerLayer.ReplaceNextInboundUnreliableWithCompact(new byte[] { 63, 0 });
        pair.ClientPeer.Send(new byte[] { 0xEE }, 0, DeliveryMethod.Unreliable);
        pair.WaitForServerReceives(before + 1);

        Received received = pair.ServerReceives.Last();
        Assert.Equal(63, received.Channel);
        Assert.Empty(received.Data);
    }

    [Fact]
    public void MalformedCompactMerged_StopsBeforeLaterEntries()
    {
        using var pair = new LoopbackPair(mergeHoldMs: 0);
        int before = pair.ServerReceives.Count;

        pair.ServerLayer.ReplaceNextInboundUnreliableWithCompact(
            new byte[] { 3, 1, 0xA1, 4, 5, 0xB1, 5, 1, 0xC1 });
        pair.ClientPeer.Send(new byte[] { 0xEE }, 0, DeliveryMethod.Unreliable);
        pair.WaitForServerReceives(before + 1);
        Thread.Sleep(30);

        Received[] received = pair.ServerReceives.Skip(before).ToArray();
        Assert.Single(received);
        Assert.Equal(3, received[0].Channel);
        Assert.Equal(new byte[] { 0xA1 }, received[0].Data);
    }

    [Fact]
    public void OversizedReconstructedPacket_IsRejectedBeforeInnerPacketRent()
    {
        using var pair = new LoopbackPair(mergeHoldMs: 0);
        int before = pair.ServerReceives.Count;
        int poolBefore = pair.Server.PoolCount;

        byte[] body = new byte[3 + ushort.MaxValue];
        body[0] = 0x80;
        body[1] = 0xFF;
        body[2] = 0xFF;
        pair.ServerLayer.ReplaceNextInboundUnreliableWithCompact(body);
        pair.ClientPeer.Send(new byte[] { 0xEE }, 0, DeliveryMethod.Unreliable);
        Thread.Sleep(40);

        Assert.Equal(before, pair.ServerReceives.Count);
        Assert.True(pair.Server.PoolCount >= poolBefore - 2);

        pair.ClientPeer.Send(new byte[] { 0x7A }, 0, DeliveryMethod.Unreliable);
        pair.WaitForServerReceives(before + 1);
    }

    public static IEnumerable<object[]> MalformedCompactBodies()
    {
        yield return new object[] { new byte[] { 5 } };
        yield return new object[] { new byte[] { 0x85, 0x01 } };
        yield return new object[] { new byte[] { 5, 3, 0x10 } };
        yield return new object[] { new byte[] { 0x85, 0x00, 0x01, 0x10 } };
        yield return new object[] { new byte[] { 0x85, 1, 0, 0x10 } };
        yield return new object[] { new byte[] { 0x41, 4, 2, 0, 0, 0 } };
        yield return new object[] { new byte[] { 0x40, 1, 2 } };
        yield return new object[] { new byte[] { 0x40, 4, 0, 0, 0, 0 } };
        yield return new object[] { new byte[] { 0x40, 4, 18, 0, 0, 0 } };
    }

    private static byte[] Filled(int length, byte value)
    {
        byte[] result = new byte[length];
        Array.Fill(result, value);
        return result;
    }

    private static byte Property(ReadOnlySpan<byte> datagram) => (byte)(datagram[0] & 0x1F);

    private sealed record CompactEntry(bool IsRaw, byte Channel, byte[] Data);

    private static CompactEntry[] ParseCompactEntries(byte[] datagram)
    {
        if (datagram.Length < 1 || Property(datagram) != CompactMergedProperty)
            throw new InvalidOperationException("Not a CompactMerged datagram.");

        var entries = new List<CompactEntry>();
        int offset = 1;
        while (offset < datagram.Length)
        {
            if (datagram.Length - offset < 2)
                throw new InvalidOperationException("Truncated CompactMerged entry header.");

            byte channelAndFlags = datagram[offset];
            bool extended = (channelAndFlags & 0x80) != 0;
            bool isRaw = (channelAndFlags & 0x40) != 0;
            byte channel = (byte)(channelAndFlags & 0x3F);
            if (isRaw && channel != 0)
                throw new InvalidOperationException("Raw entry has nonzero channel bits.");

            int header = extended ? 3 : 2;
            if (datagram.Length - offset < header)
                throw new InvalidOperationException("Truncated CompactMerged extended header.");

            int length = extended
                ? datagram[offset + 1] | (datagram[offset + 2] << 8)
                : datagram[offset + 1];
            if (extended && length <= 255)
                throw new InvalidOperationException("Non-canonical extended CompactMerged length.");

            offset += header;
            if (length > datagram.Length - offset)
                throw new InvalidOperationException("CompactMerged entry exceeds datagram.");

            byte[] data = datagram.AsSpan(offset, length).ToArray();
            if (isRaw)
            {
                if (data.Length < 4 || Property(data) is not (AckProperty or ChanneledProperty))
                    throw new InvalidOperationException("Invalid raw CompactMerged entry.");
            }

            entries.Add(new CompactEntry(isRaw, channel, data));
            offset += length;
        }

        return entries.ToArray();
    }

    private static bool ParseCompactFully(byte[] datagram)
    {
        try
        {
            _ = ParseCompactEntries(datagram);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void WaitUntil(Func<bool> condition, string description, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds >= timeoutMs)
                throw new TimeoutException($"Timed out waiting for {description}.");
            Thread.Sleep(2);
        }
    }

    private sealed record Received(byte Channel, DeliveryMethod Method, byte[] Data);

    private sealed class CaptureLayer : PacketLayerBase
    {
        private readonly ConcurrentQueue<byte[]> _outbound = new();
        private byte[]? _compactReplacementBody;

        public CaptureLayer() : base(0)
        {
        }

        public void ClearOutbound()
        {
            while (_outbound.TryDequeue(out _)) { }
        }

        public byte[][] OutboundSnapshot() => _outbound.ToArray();

        public byte[] WaitForOutbound(byte property, int timeoutMs = 3000)
        {
            byte[]? found = null;
            WaitUntil(() =>
            {
                foreach (byte[] packet in _outbound)
                {
                    if (Property(packet) == property)
                    {
                        found = packet;
                        return true;
                    }
                }
                return false;
            }, $"outbound packet property {property}", timeoutMs);
            return found!;
        }

        public void ReplaceNextInboundUnreliableWithCompact(byte[] compactBody)
        {
            Interlocked.Exchange(ref _compactReplacementBody, compactBody.ToArray());
        }

        public override void ProcessOutBoundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int offset, ref int length)
        {
            byte[] copy = new byte[length];
            Buffer.BlockCopy(data, offset, copy, 0, length);
            _outbound.Enqueue(copy);
        }

        public override void ProcessInboundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int length)
        {
            if (length < 1 || Property(data.AsSpan(0, length)) != UnreliableProperty)
                return;

            byte[]? body = Interlocked.Exchange(ref _compactReplacementBody, null);
            if (body == null)
                return;

            byte[] replacement = new byte[1 + body.Length];
            replacement[0] = (byte)((data[0] & 0xE0) | CompactMergedProperty);
            body.CopyTo(replacement, 1);
            data = replacement;
            length = replacement.Length;
        }
    }

    private sealed class LoopbackPair : IDisposable
    {
        private readonly ManualResetEventSlim _clientConnected = new(false);
        private readonly ManualResetEventSlim _serverConnected = new(false);

        public CaptureLayer ServerLayer { get; } = new();
        public CaptureLayer ClientLayer { get; } = new();
        public NetManager Server { get; }
        public NetManager Client { get; }
        public NetPeer ServerPeer { get; private set; } = null!;
        public NetPeer ClientPeer { get; private set; } = null!;
        public ConcurrentQueue<Received> ServerReceives { get; } = new();

        public LoopbackPair(float mergeHoldMs)
        {
            var serverListener = new EventBasedNetListener();
            var clientListener = new EventBasedNetListener();

            serverListener.ConnectionRequestEvent += request => request.AcceptIfKey("compact-test");
            serverListener.PeerConnectedEvent += peer =>
            {
                ServerPeer = peer;
                _serverConnected.Set();
            };
            clientListener.PeerConnectedEvent += peer =>
            {
                ClientPeer = peer;
                _clientConnected.Set();
            };
            serverListener.NetworkReceiveEvent += (_, reader, channel, method) =>
                ServerReceives.Enqueue(new Received(channel, method, reader.GetRemainingBytes()));

            Server = CreateManager(serverListener, ServerLayer, mergeHoldMs);
            Client = CreateManager(clientListener, ClientLayer, mergeHoldMs);

            Assert.True(Server.Start(0));
            Assert.True(Client.Start(0));
            Client.Connect("127.0.0.1", Server.LocalPort, "compact-test");
            Assert.True(_serverConnected.Wait(3000), "Server peer did not connect.");
            Assert.True(_clientConnected.Wait(3000), "Client peer did not connect.");
            Thread.Sleep(15);
        }

        private static NetManager CreateManager(EventBasedNetListener listener, CaptureLayer layer, float mergeHoldMs)
        {
            return new NetManager(listener, layer)
            {
                UnsyncedEvents = true,
                AutoRecycle = true,
                ChannelsCount = 64,
                UpdateTime = 2,
                MtuDiscovery = false,
                MtuOverride = 1200,
                MergeHoldMs = mergeHoldMs,
                CompactMergeEnabled = true,
                EnableStatistics = true
            };
        }

        public void WaitForServerReceives(int expected, int timeoutMs = 3000) =>
            WaitUntil(() => ServerReceives.Count >= expected, $"{expected} server receive events", timeoutMs);

        public void Dispose()
        {
            Client.Stop(false);
            Server.Stop(false);
            _clientConnected.Dispose();
            _serverConnected.Dispose();
        }
    }
}
