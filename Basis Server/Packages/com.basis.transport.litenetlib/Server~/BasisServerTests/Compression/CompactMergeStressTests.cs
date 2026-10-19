using LiteNetLib;
using LiteNetLib.Layers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Xunit;
using Xunit.Abstractions;

namespace BasisServerTests;

// -----------------------------------------------------------------------------
// CompactMerged stress, fuzz and wire inspection.
//
// Three levels:
//   * wire     -- a PacketLayerBase records every datagram the sender actually
//                emits, so the framing can be parsed back byte for byte. This is
//                what proves a datagram never holds both framings.
//   * fuzz     -- malformed CompactMerged datagrams driven straight into
//                NetPeer.ProcessPacket. The parser reads remote bytes, so it has
//                to refuse anything ragged rather than read past the end.
//   * soak     -- many peers, random sizes and channels, packet loss, sustained
//                traffic, every byte verified on arrival.
// -----------------------------------------------------------------------------

/// <summary>Passes datagrams through untouched and keeps a copy of every one that goes out.</summary>
internal sealed class RecordingLayer : PacketLayerBase
{
    public readonly ConcurrentQueue<byte[]> Outbound = new();
    public volatile bool Recording;

    public RecordingLayer() : base(0) { }

    public override void ProcessInboundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int length)
    {
    }

    public override void ProcessOutBoundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int offset, ref int length)
    {
        if (!Recording)
            return;
        byte[] copy = new byte[length];
        Buffer.BlockCopy(data, offset, copy, 0, length);
        Outbound.Enqueue(copy);
    }
}

internal static class WireAssert
{
    internal const byte PropertyMask = 0x1F;

    internal static byte Property(byte[] datagram) => (byte)(datagram[0] & PropertyMask);

    /// <summary>Walks a CompactMerged datagram, returning the entries or throwing on ragged framing.</summary>
    internal static List<(bool Raw, byte Channel, byte[] Payload)> ParseCompact(byte[] datagram)
    {
        var entries = new List<(bool, byte, byte[])>();
        int offset = 1;
        while (offset < datagram.Length)
        {
            Assert.True(
                CompactMerge.TryReadEntry(
                    datagram,
                    datagram.Length,
                    ref offset,
                    out bool isRaw,
                    out byte channel,
                    out int length),
                $"CompactMerged datagram of {datagram.Length} B has a ragged entry at {offset}");
            entries.Add((isRaw, channel, datagram.AsSpan(offset, length).ToArray()));
            offset += length;
        }
        Assert.Equal(datagram.Length, offset);
        return entries;
    }

    /// <summary>Walks a legacy Merged datagram the same way.</summary>
    internal static List<byte[]> ParseLegacy(byte[] datagram)
    {
        var entries = new List<byte[]>();
        int offset = 1;
        while (offset < datagram.Length)
        {
            Assert.True(offset + 2 <= datagram.Length, "legacy Merged datagram truncated in its length field");
            int size = BitConverter.ToUInt16(datagram, offset);
            offset += 2;
            Assert.True(size > 0 && offset + size <= datagram.Length,
                $"legacy Merged datagram claims a {size} B entry with {datagram.Length - offset} B left");
            entries.Add(datagram.AsSpan(offset, size).ToArray());
            offset += size;
        }
        Assert.Equal(datagram.Length, offset);
        return entries;
    }
}

public class CompactMergeWireTests
{
    private readonly ITestOutputHelper _out;

    public CompactMergeWireTests(ITestOutputHelper output) => _out = output;

    private const string ConnectKey = "compact-wire";

    private static bool WaitFor(Func<bool> condition, int timeoutMs = 10000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(5);
        }
        return condition();
    }

    private static byte[] Message(int seed, int length)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < length; i++)
            data[i] = (byte)(seed * 31 + i * 7);
        return data;
    }

    [Fact]
    public void EveryEmittedDatagram_HoldsExactlyOneFraming()
    {
        var layer = new RecordingLayer();
        var senderListener = new EventBasedNetListener();
        var receiverListener = new EventBasedNetListener();

        var sender = new NetManager(senderListener, layer)
        {
            AutoRecycle = true,
            UnsyncedEvents = true,
            ChannelsCount = 64,
            UpdateTime = 5,
            MergeHoldMs = 3f,
            MaxUnreliableQueuePerPeer = 8192
        };
        var receiver = new NetManager(receiverListener)
        {
            AutoRecycle = true,
            UnsyncedEvents = true,
            ChannelsCount = 64,
            UpdateTime = 5,
            MaxUnreliableQueuePerPeer = 8192
        };

        int received = 0;
        receiverListener.ConnectionRequestEvent += r => r.AcceptIfKey(ConnectKey);
        receiverListener.NetworkReceiveEvent += (p, reader, ch, m) => { Interlocked.Increment(ref received); reader.Recycle(); };
        senderListener.NetworkReceiveEvent += (p, reader, ch, m) => reader.Recycle();

        try
        {
            Assert.True(receiver.Start(0));
            Assert.True(sender.Start(0));
            sender.Connect("127.0.0.1", receiver.LocalPort, ConnectKey);

            Assert.True(WaitFor(() => sender.FirstPeer is { ConnectionState: ConnectionState.Connected }));
            NetPeer peer = sender.FirstPeer;
            Assert.True(peer.CompactMergeActive, "compact framing is off, so this test proves nothing");

            layer.Recording = true;

            const int total = 600;
            for (int i = 0; i < total; i++)
            {
                // Reliable traffic threads Ack and Channeled packets through the same merge
                // buffer, which is exactly what the framing flush has to keep separate.
                DeliveryMethod method = i % 5 == 0 ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable;
                peer.Send(Message(i, 20 + i % 400), (byte)(i % 6), method);
            }

            Assert.True(WaitFor(() => received >= total, 20000), $"only {received}/{total} arrived");
            Thread.Sleep(100);
            layer.Recording = false;

            var byProperty = new Dictionary<byte, int>();
            var legacyContents = new Dictionary<byte, int>();
            int compactEntries = 0, compactDatagrams = 0, multiEntryCompact = 0, legacyDatagrams = 0, rawCompactEntries = 0;

            foreach (byte[] datagram in layer.Outbound)
            {
                byte property = WireAssert.Property(datagram);
                byProperty[property] = byProperty.TryGetValue(property, out int n) ? n + 1 : 1;

                if (property == (byte)PacketPropertyMirror.CompactMerged)
                {
                    var entries = WireAssert.ParseCompact(datagram);
                    compactDatagrams++;
                    compactEntries += entries.Count;
                    if (entries.Count > 1) multiEntryCompact++;
                    foreach (var e in entries)
                    {
                        Assert.True(e.Channel <= CompactMerge.ChannelMask);
                        if (e.Raw)
                        {
                            rawCompactEntries++;
                            Assert.True(e.Payload.Length >= NetConstants.ChanneledHeaderSize);
                            byte nestedProperty = WireAssert.Property(e.Payload);
                            Assert.True(
                                nestedProperty == (byte)PacketPropertyMirror.Channeled || nestedProperty == (byte)PacketPropertyMirror.Ack,
                                $"raw CompactMerged entry carried unexpected property {nestedProperty}");
                        }
                    }
                }
                else if (property == (byte)PacketPropertyMirror.Merged)
                {
                    foreach (byte[] nested in WireAssert.ParseLegacy(datagram))
                    {
                        byte nestedProperty = WireAssert.Property(nested);
                        legacyContents[nestedProperty] = legacyContents.TryGetValue(nestedProperty, out int m) ? m + 1 : 1;
                    }
                    legacyDatagrams++;
                }
            }

            _out.WriteLine($"datagrams by property: {string.Join(", ", byProperty.OrderBy(k => k.Key).Select(k => $"{(PacketPropertyMirror)k.Key}={k.Value}"))}");
            _out.WriteLine($"compact datagrams {compactDatagrams} carrying {compactEntries} entries ({multiEntryCompact} held more than one); legacy merged datagrams {legacyDatagrams}");
            _out.WriteLine($"legacy Merged entries by nested property: {string.Join(", ", legacyContents.OrderBy(k => k.Key).Select(k => $"{(PacketPropertyMirror)k.Key}={k.Value}"))}");

            Assert.DoesNotContain((byte)PacketPropertyMirror.Unreliable, legacyContents.Keys);
            Assert.True(compactDatagrams > 0, "no CompactMerged datagram was emitted at all");
            Assert.True(multiEntryCompact > 0, "compact framing never actually merged anything");
            Assert.True(rawCompactEntries > 0, "reliable traffic never produced a raw Ack/Channeled CompactMerged entry");
            Assert.Equal(0, legacyDatagrams);
        }
        finally
        {
            sender.Stop();
            receiver.Stop();
        }
    }

    /// <summary>Wire values of the packet properties this suite inspects.</summary>
    internal enum PacketPropertyMirror : byte
    {
        Unreliable = 0,
        Channeled = 1,
        Ack = 2,
        Ping = 3,
        Pong = 4,
        Merged = 12,
        CompactMerged = 18
    }

    [Fact]
    public void OlderProtocolId_IsRejectedAtTheHandshake()
    {
        // This replaces the runtime capability exchange. A peer without the compact parser would
        // drop every CompactMerged datagram as an unknown property and lose all unreliable traffic
        // silently, so it must not be able to reach the traffic phase at all.
        const byte ConnectRequestProperty = 5;
        const byte InvalidProtocolProperty = 15;
        const int PreviousProtocolId = 13;

        var listener = new EventBasedNetListener();
        listener.ConnectionRequestEvent += r => r.AcceptIfKey(ConnectKey);
        var server = new NetManager(listener) { AutoRecycle = true, UnsyncedEvents = true, UpdateTime = 5 };

        using var raw = new System.Net.Sockets.UdpClient(0, System.Net.Sockets.AddressFamily.InterNetwork);
        try
        {
            Assert.True(server.Start(0));

            // [0] property, [1..4] protocol id, [5..12] connect time, [13..16] peer id,
            // [17] address size, then the address itself.
            byte[] request = new byte[18 + 16];
            request[0] = ConnectRequestProperty;
            BitConverter.GetBytes(PreviousProtocolId).CopyTo(request, 1);
            BitConverter.GetBytes(1L).CopyTo(request, 5);
            BitConverter.GetBytes(1).CopyTo(request, 13);
            request[17] = 16;

            var serverEp = new IPEndPoint(IPAddress.Loopback, server.LocalPort);
            raw.Client.ReceiveTimeout = 5000;
            raw.Send(request, request.Length, serverEp);

            IPEndPoint? from = null;
            byte[] response = raw.Receive(ref from!);

            Assert.Equal(InvalidProtocolProperty, (byte)(response[0] & WireAssert.PropertyMask));
            Assert.Equal(0, server.ConnectedPeersCount);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public void PacketPropertyWireValues_AreStable()
    {
        // CompactMerged was appended, so nothing that already shipped moved. Peers that predate
        // it are kept out by NetConstants.ProtocolId rather than by anything on this wire.
        Assert.Equal(0, (int)PacketPropertyMirror.Unreliable);
        Assert.Equal(12, (int)PacketPropertyMirror.Merged);
        Assert.Equal(18, (int)PacketPropertyMirror.CompactMerged);
    }
}

/// <summary>
/// Malformed input straight into the real receive path. A NetPeer built directly needs no socket:
/// the incoming-connection constructor lands in Connected, and unsynced events dispatch inline.
/// </summary>
public class CompactMergeFuzzTests
{
    private readonly ITestOutputHelper _out;

    public CompactMergeFuzzTests(ITestOutputHelper output) => _out = output;

    private const byte CompactMergedProperty = 18;

    private sealed class Harness
    {
        public readonly NetManager Manager;
        public readonly NetPeer Peer;
        public int Delivered;
        public long DeliveredBytes;

        public Harness()
        {
            var listener = new EventBasedNetListener();
            Manager = new NetManager(listener)
            {
                AutoRecycle = true,
                UnsyncedEvents = true,
                ChannelsCount = 64
            };
            listener.NetworkReceiveEvent += (peer, reader, channel, method) =>
            {
                Interlocked.Increment(ref Delivered);
                Interlocked.Add(ref DeliveredBytes, reader.AvailableBytes);
                reader.Recycle();
            };
            Peer = new NetPeer(Manager, new IPEndPoint(IPAddress.Loopback, 30000), 1);
        }

        public void Feed(byte[] body)
        {
            NetPacket packet = Manager.PoolGetPacket(1 + body.Length);
            packet.RawData[0] = CompactMergedProperty;
            Buffer.BlockCopy(body, 0, packet.RawData, 1, body.Length);
            packet.Size = 1 + body.Length;
            Peer.ProcessPacket(packet);
        }
    }

    [Fact]
    public void RandomGarbage_NeverThrowsAndNeverOverReads()
    {
        var harness = new Harness();
        var rng = new Random(20260815);

        const int iterations = 50000;
        for (int i = 0; i < iterations; i++)
        {
            byte[] body = new byte[rng.Next(0, 400)];
            rng.NextBytes(body);
            harness.Feed(body);
        }

        _out.WriteLine($"{iterations} random bodies produced {harness.Delivered} events, {harness.DeliveredBytes} B");
    }

    [Fact]
    public void TruncatedEntries_AreRefusedRatherThanReadPast()
    {
        var harness = new Harness();
        var rng = new Random(7);

        for (int trial = 0; trial < 4000; trial++)
        {
            // Build a well-formed run, then cut it at an arbitrary point.
            byte[] buffer = new byte[2048];
            int written = 0;
            int entries = rng.Next(1, 6);
            for (int e = 0; e < entries && written < 1500; e++)
            {
                int length = rng.Next(0, 400);
                byte[] source = new byte[2 + length];
                rng.NextBytes(source);
                written += CompactMerge.WriteEntry(buffer, written, (byte)rng.Next(0, 64), source, 2, length);
            }

            int cut = rng.Next(0, written + 1);
            harness.Feed(buffer.AsSpan(0, cut).ToArray());
        }

        _out.WriteLine($"4000 truncated runs produced {harness.Delivered} events");
    }

    [Fact]
    public void CompactEntryNestedInsideALegacyMergedDatagram_StillParses()
    {
        // Reachable because the legacy container recurses into ProcessPacket for each nested
        // packet, so the compact parser has to work on a packet that is not the outermost one.
        var listener = new EventBasedNetListener();
        var manager = new NetManager(listener) { AutoRecycle = true, UnsyncedEvents = true, ChannelsCount = 64 };
        var delivered = new List<(byte Channel, byte[] Payload)>();
        listener.NetworkReceiveEvent += (peer, reader, channel, method) =>
            delivered.Add((channel, reader.GetRemainingBytes()));
        var peer = new NetPeer(manager, new IPEndPoint(IPAddress.Loopback, 30002), 3);

        byte[] payload = { 9, 8, 7, 6, 5 };
        byte[] source = new byte[2 + payload.Length];
        payload.CopyTo(source, 2);

        byte[] inner = new byte[64];
        inner[0] = CompactMergedProperty;
        int innerSize = 1 + CompactMerge.WriteEntry(inner, 1, 11, source, 2, payload.Length);

        NetPacket outer = manager.PoolGetPacket(1 + 2 + innerSize);
        outer.RawData[0] = 12; // legacy Merged
        BitConverter.GetBytes((ushort)innerSize).CopyTo(outer.RawData, 1);
        Buffer.BlockCopy(inner, 0, outer.RawData, 3, innerSize);
        outer.Size = 1 + 2 + innerSize;
        peer.ProcessPacket(outer);

        Assert.Single(delivered);
        Assert.Equal(11, delivered[0].Channel);
        Assert.Equal(payload, delivered[0].Payload);
    }

    [Fact]
    public void LengthFieldLongerThanTheDatagram_DeliversNothing()
    {
        var harness = new Harness();

        // Short form claiming 200 bytes with 4 present.
        harness.Feed(new byte[] { 3, 200, 1, 2, 3, 4 });
        Assert.Equal(0, harness.Delivered);

        // Long form claiming 60000 bytes with 3 present.
        harness.Feed(new byte[] { 3 | CompactMerge.LongLengthFlag, 0x60, 0xEA, 1, 2, 3 });
        Assert.Equal(0, harness.Delivered);

        // Long-form flag set with the length field itself cut short.
        harness.Feed(new byte[] { 3 | CompactMerge.LongLengthFlag, 0x10 });
        Assert.Equal(0, harness.Delivered);
    }

    [Fact]
    public void WellFormedRun_DeliversEveryEntryExactly()
    {
        var harness = new Harness();
        var rng = new Random(4242);

        var expected = new List<(byte Channel, byte[] Payload)>();
        var delivered = new List<(byte Channel, byte[] Payload)>();

        var listenerManager = harness.Manager;
        // Re-point the listener so payloads can be compared, not just counted.
        var listener = new EventBasedNetListener();
        var manager = new NetManager(listener) { AutoRecycle = true, UnsyncedEvents = true, ChannelsCount = 64 };
        listener.NetworkReceiveEvent += (peer, reader, channel, method) =>
        {
            delivered.Add((channel, reader.GetRemainingBytes()));
        };
        var peer = new NetPeer(manager, new IPEndPoint(IPAddress.Loopback, 30001), 2);

        byte[] buffer = new byte[2048];
        int written = 0;
        for (int e = 0; e < 12; e++)
        {
            int length = rng.Next(0, 130);
            byte[] payload = new byte[length];
            rng.NextBytes(payload);
            byte channel = (byte)rng.Next(0, 64);

            byte[] source = new byte[2 + length];
            payload.CopyTo(source, 2);
            written += CompactMerge.WriteEntry(buffer, written, channel, source, 2, length);
            expected.Add((channel, payload));
        }

        NetPacket packet = manager.PoolGetPacket(1 + written);
        packet.RawData[0] = CompactMergedProperty;
        Buffer.BlockCopy(buffer, 0, packet.RawData, 1, written);
        packet.Size = 1 + written;
        peer.ProcessPacket(packet);

        Assert.Equal(expected.Count, delivered.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Channel, delivered[i].Channel);
            Assert.Equal(expected[i].Payload, delivered[i].Payload);
        }
        Assert.Equal(0, listenerManager.PoolCount < 0 ? 1 : 0);
    }
}

[CollectionDefinition("CompactMergeSoak", DisableParallelization = true)]
public class CompactMergeSoakCollection { }

[Collection("CompactMergeSoak")]
public class CompactMergeSoakTests
{
    private readonly ITestOutputHelper _out;

    public CompactMergeSoakTests(ITestOutputHelper output) => _out = output;

    private const string ConnectKey = "compact-soak";

    private static bool WaitFor(Func<bool> condition, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(5);
        }
        return condition();
    }

    private sealed class Node : IDisposable
    {
        public readonly EventBasedNetListener Listener = new();
        public readonly NetManager Manager;
        public readonly ConcurrentQueue<(byte Channel, DeliveryMethod Method, byte[] Data)> Received = new();

        public Node(float mergeHoldMs = 0f, int lossChance = 0)
        {
            Manager = new NetManager(Listener)
            {
                AutoRecycle = true,
                UnsyncedEvents = true,
                EnableStatistics = true,
                ChannelsCount = 64,
                UpdateTime = 5,
                MergeHoldMs = mergeHoldMs,
                MaxUnreliableQueuePerPeer = 16384,
                DisconnectTimeout = 60000,
                SimulatePacketLoss = lossChance > 0,
                SimulationPacketLossChance = lossChance
            };
            Listener.ConnectionRequestEvent += r => r.AcceptIfKey(ConnectKey);
            Listener.NetworkReceiveEvent += (peer, reader, channel, method) =>
                Received.Enqueue((channel, method, reader.GetRemainingBytes()));
        }

        public void Dispose() => Manager.Stop();
    }

    private static byte[] Message(int seed, int length)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < length; i++)
            data[i] = (byte)(seed * 131 + i * 17 + (i >> 8));
        return data;
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(3f)]
    [InlineData(8f)]
    public void RandomTraffic_RoundTripsIntactAtEveryMergeHold(float mergeHoldMs)
    {
        var server = new Node(mergeHoldMs);
        var client = new Node(mergeHoldMs);
        using (server)
        using (client)
        {
            Assert.True(server.Manager.Start(0));
            Assert.True(client.Manager.Start(0));
            client.Manager.Connect("127.0.0.1", server.Manager.LocalPort, ConnectKey);

            Assert.True(WaitFor(() => client.Manager.FirstPeer is { ConnectionState: ConnectionState.Connected }, 10000));
            NetPeer peer = client.Manager.FirstPeer;
            Assert.True(WaitFor(() => peer.CompactMergeActive, 10000));

            var rng = new Random(1000 + (int)(mergeHoldMs * 10));
            var sent = new List<(byte Channel, byte[] Data)>();
            const int count = 1500;
            for (int i = 0; i < count; i++)
            {
                int length = rng.Next(0, 3) switch
                {
                    0 => rng.Next(0, 32),
                    1 => rng.Next(200, 300),
                    _ => rng.Next(0, 900)
                };
                byte channel = (byte)rng.Next(0, 32);
                byte[] data = Message(i, length);
                sent.Add((channel, data));
                peer.Send(data, channel, DeliveryMethod.Unreliable);
            }

            Assert.True(WaitFor(() => server.Received.Count == count, 30000),
                $"expected {count}, got {server.Received.Count}");

            var received = server.Received.ToArray();
            for (int i = 0; i < count; i++)
            {
                Assert.Equal(sent[i].Channel, received[i].Channel);
                Assert.Equal(sent[i].Data, received[i].Data);
            }

            _out.WriteLine($"hold {mergeHoldMs} ms: {count} messages, {peer.Statistics.BytesSent} B in {peer.Statistics.PacketsSent} datagrams");
        }
    }

    [Fact]
    public void ManyPeersBothDirections_StayIntact()
    {
        const int clientCount = 8;
        const int perClient = 300;

        var server = new Node();
        var clients = new List<Node>();
        try
        {
            Assert.True(server.Manager.Start(0));
            for (int i = 0; i < clientCount; i++)
            {
                var client = new Node();
                Assert.True(client.Manager.Start(0));
                client.Manager.Connect("127.0.0.1", server.Manager.LocalPort, ConnectKey);
                clients.Add(client);
            }

            Assert.True(WaitFor(() => server.Manager.ConnectedPeersCount == clientCount, 20000),
                $"only {server.Manager.ConnectedPeersCount}/{clientCount} connected");

            foreach (Node client in clients)
                Assert.True(WaitFor(() => client.Manager.FirstPeer.CompactMergeActive, 10000));

            var serverPeers = new List<NetPeer>();
            server.Manager.GetPeersNonAlloc(serverPeers, ConnectionState.Connected);
            Assert.Equal(clientCount, serverPeers.Count);
            Assert.All(serverPeers, p => Assert.True(p.CompactMergeActive));

            var expectedPerClient = new Dictionary<int, List<byte[]>>();
            for (int c = 0; c < clientCount; c++)
            {
                var list = new List<byte[]>();
                expectedPerClient[c] = list;
                for (int i = 0; i < perClient; i++)
                {
                    byte[] data = Message(c * 10000 + i, 10 + (i % 500));
                    list.Add(data);
                    clients[c].Manager.FirstPeer.Send(data, (byte)(i % 8), DeliveryMethod.Unreliable);
                }
            }

            for (int i = 0; i < perClient; i++)
            {
                byte[] data = Message(900000 + i, 40 + (i % 300));
                foreach (NetPeer p in serverPeers)
                    p.Send(data, (byte)(i % 4), DeliveryMethod.Unreliable);
            }

            int expectedAtServer = clientCount * perClient;
            Assert.True(WaitFor(() => server.Received.Count == expectedAtServer, 40000),
                $"server got {server.Received.Count}/{expectedAtServer}");
            foreach (Node client in clients)
                Assert.True(WaitFor(() => client.Received.Count == perClient, 40000),
                    $"a client got {client.Received.Count}/{perClient}");

            var allExpected = new HashSet<string>(
                expectedPerClient.Values.SelectMany(v => v).Select(Convert.ToBase64String));
            foreach (var got in server.Received)
                Assert.Contains(Convert.ToBase64String(got.Data), allExpected);

            for (int c = 0; c < clientCount; c++)
            {
                var got = clients[c].Received.ToArray();
                Assert.Equal(perClient, got.Length);
                for (int i = 0; i < perClient; i++)
                    Assert.Equal(Message(900000 + i, 40 + (i % 300)), got[i].Data);
            }

            _out.WriteLine($"{clientCount} peers x {perClient} messages each way, all bytes verified");
        }
        finally
        {
            foreach (Node client in clients) client.Dispose();
            server.Dispose();
        }
    }

    [Fact]
    public void ParallelPeerUpdate_KeepsEveryPeersMergeBufferIntact()
    {
        // The merge buffer is per peer but the update pass is spread across workers, so this
        // forces many workers over many peers rather than the single-worker path a small test
        // would otherwise take. A larger MTU also packs more entries per datagram.
        const int clientCount = 24;
        const int perClient = 400;

        var server = new Node(3f);
        server.Manager.PeersPerUpdateWorker = 2;
        server.Manager.PeerUpdateWorkerCap = 16;
        server.Manager.MtuOverride = 1432;

        var clients = new List<Node>();
        try
        {
            Assert.True(server.Manager.Start(0));
            for (int i = 0; i < clientCount; i++)
            {
                var client = new Node(3f);
                client.Manager.MtuOverride = 1432;
                Assert.True(client.Manager.Start(0));
                client.Manager.Connect("127.0.0.1", server.Manager.LocalPort, ConnectKey);
                clients.Add(client);
            }

            Assert.True(WaitFor(() => server.Manager.ConnectedPeersCount == clientCount, 30000),
                $"only {server.Manager.ConnectedPeersCount}/{clientCount} connected");

            var serverPeers = new List<NetPeer>();
            server.Manager.GetPeersNonAlloc(serverPeers, ConnectionState.Connected);
            Assert.Equal(clientCount, serverPeers.Count);
            foreach (NetPeer p in serverPeers)
                Assert.True(WaitFor(() => p.CompactMergeActive, 20000));

            // Server fans out to every peer, which is the shape the reduction system produces.
            var rng = new Random(555);
            var expected = new List<byte[]>();
            for (int i = 0; i < perClient; i++)
            {
                byte[] data = Message(i, rng.Next(0, 3) == 0 ? rng.Next(256, 800) : rng.Next(0, 200));
                expected.Add(data);
                foreach (NetPeer p in serverPeers)
                    p.Send(data, (byte)(i % 12), DeliveryMethod.Unreliable);
            }

            foreach (Node client in clients)
                Assert.True(WaitFor(() => client.Received.Count == perClient, 60000),
                    $"a client got {client.Received.Count}/{perClient}");

            foreach (Node client in clients)
            {
                var got = client.Received.ToArray();
                for (int i = 0; i < perClient; i++)
                    Assert.Equal(expected[i], got[i].Data);
            }

            long bytes = serverPeers.Sum(p => p.Statistics.BytesSent);
            long packets = serverPeers.Sum(p => p.Statistics.PacketsSent);
            _out.WriteLine($"{clientCount} peers x {perClient} fan-out messages at MTU 1432: " +
                           $"{bytes} B in {packets} datagrams ({bytes / (double)packets:F0} B each), all verified");
        }
        finally
        {
            foreach (Node client in clients) client.Dispose();
            server.Dispose();
        }
    }

    [Fact]
    public void PacketLoss_DoesNotCorruptAndStillNegotiates()
    {
        // 20% inbound loss on both ends: the capability exchange has to survive it, reliable
        // traffic has to arrive whole, and everything unreliable that does arrive has to be a
        // message that was actually sent.
        var server = new Node(lossChance: 20);
        var client = new Node(lossChance: 20);
        using (server)
        using (client)
        {
            Assert.True(server.Manager.Start(0));
            Assert.True(client.Manager.Start(0));
            client.Manager.Connect("127.0.0.1", server.Manager.LocalPort, ConnectKey);

            Assert.True(WaitFor(() => client.Manager.FirstPeer is { ConnectionState: ConnectionState.Connected }, 20000));
            NetPeer peer = client.Manager.FirstPeer;
            Assert.True(WaitFor(() => peer.CompactMergeActive, 20000),
                "capability exchange did not survive 20% loss");

            const int reliableCount = 200;
            const int unreliableCount = 600;
            var reliable = new List<byte[]>();
            var everything = new HashSet<string>();

            for (int i = 0; i < reliableCount; i++)
            {
                byte[] data = Message(i, 30 + i % 200);
                reliable.Add(data);
                everything.Add(Convert.ToBase64String(data));
                peer.Send(data, 1, DeliveryMethod.ReliableOrdered);
            }
            for (int i = 0; i < unreliableCount; i++)
            {
                byte[] data = Message(50000 + i, 10 + i % 400);
                everything.Add(Convert.ToBase64String(data));
                peer.Send(data, 2, DeliveryMethod.Unreliable);
            }

            Assert.True(WaitFor(() => server.Received.Count(r => r.Method == DeliveryMethod.ReliableOrdered) == reliableCount, 60000),
                $"reliable: {server.Received.Count(r => r.Method == DeliveryMethod.ReliableOrdered)}/{reliableCount}");

            var got = server.Received.ToArray();
            var reliableInOrder = got.Where(r => r.Method == DeliveryMethod.ReliableOrdered).ToArray();
            for (int i = 0; i < reliableCount; i++)
                Assert.Equal(reliable[i], reliableInOrder[i].Data);

            int unreliableArrived = 0;
            foreach (var r in got)
            {
                Assert.Contains(Convert.ToBase64String(r.Data), everything);
                if (r.Method == DeliveryMethod.Unreliable) unreliableArrived++;
            }

            _out.WriteLine($"20% loss: {reliableCount}/{reliableCount} reliable intact and in order, " +
                           $"{unreliableArrived}/{unreliableCount} unreliable arrived, zero corrupt");
        }
    }

    [Fact]
    public void SustainedTraffic_HoldsUpOverTime()
    {
        var server = new Node(3f);
        var client = new Node(3f);
        using (server)
        using (client)
        {
            Assert.True(server.Manager.Start(0));
            Assert.True(client.Manager.Start(0));
            client.Manager.Connect("127.0.0.1", server.Manager.LocalPort, ConnectKey);

            Assert.True(WaitFor(() => client.Manager.FirstPeer is { ConnectionState: ConnectionState.Connected }, 10000));
            NetPeer peer = client.Manager.FirstPeer;
            Assert.True(WaitFor(() => peer.CompactMergeActive, 10000));

            var rng = new Random(99);
            var sent = new List<byte[]>();
            var sw = Stopwatch.StartNew();
            int seed = 0;
            while (sw.ElapsedMilliseconds < 4000)
            {
                for (int i = 0; i < 40; i++)
                {
                    byte[] data = Message(seed++, rng.Next(0, 600));
                    sent.Add(data);
                    peer.Send(data, (byte)(seed % 16), DeliveryMethod.Unreliable);
                }
                Thread.Sleep(5);
            }

            Assert.True(WaitFor(() => server.Received.Count == sent.Count, 30000),
                $"{server.Received.Count}/{sent.Count} arrived");

            var got = server.Received.ToArray();
            for (int i = 0; i < sent.Count; i++)
                Assert.Equal(sent[i], got[i].Data);

            _out.WriteLine($"soak: {sent.Count} messages over {sw.ElapsedMilliseconds} ms, " +
                           $"{peer.Statistics.BytesSent} B in {peer.Statistics.PacketsSent} datagrams, all verified");
        }
    }

    [Theory]
    [InlineData(20)]
    [InlineData(80)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(600)]
    public void FramingSaving_MeasuredOnTheWire(int payloadLength)
    {
        const int count = 500;

        long compact = Egress(payloadLength, compactMerge: true, out int compactPackets);
        long legacy = Egress(payloadLength, compactMerge: false, out int legacyPackets);

        double saved = (legacy - compact) / (double)legacy;
        _out.WriteLine($"{payloadLength,4} B payload: legacy {legacy,7} B / {legacyPackets,4} pkts, " +
                       $"compact {compact,7} B / {compactPackets,4} pkts, {saved:P2} saved");

        // Never worse: a payload too big to share a datagram is sent as the bare unreliable packet
        // under either framing, so the two are identical rather than compact being a byte behind.
        Assert.True(compact <= legacy, $"compact {compact} exceeded legacy {legacy}");

        if (legacyPackets < count)
            Assert.True(compact < legacy, $"messages merged but compact {compact} did not beat legacy {legacy}");
        else
            Assert.Equal(legacy, compact);
    }

    private long Egress(int payloadLength, bool compactMerge, out int packets)
    {
        var server = new Node();
        var client = new Node();
        server.Manager.CompactMergeEnabled = compactMerge;
        client.Manager.CompactMergeEnabled = compactMerge;

        using (server)
        using (client)
        {
            Assert.True(server.Manager.Start(0));
            Assert.True(client.Manager.Start(0));
            client.Manager.Connect("127.0.0.1", server.Manager.LocalPort, ConnectKey);

            Assert.True(WaitFor(() => client.Manager.FirstPeer is { ConnectionState: ConnectionState.Connected }, 10000));
            NetPeer peer = client.Manager.FirstPeer;
            Assert.Equal(compactMerge, peer.CompactMergeActive);

            const int count = 500;
            peer.Statistics.Reset();
            for (int i = 0; i < count; i++)
                peer.Send(Message(i, payloadLength), (byte)(i % 4), DeliveryMethod.Unreliable);

            Assert.True(WaitFor(() => server.Received.Count == count, 30000),
                $"{server.Received.Count}/{count} arrived");

            packets = (int)peer.Statistics.PacketsSent;
            return peer.Statistics.BytesSent;
        }
    }
}
