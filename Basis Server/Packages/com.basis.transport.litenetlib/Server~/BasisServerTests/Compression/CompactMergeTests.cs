using LiteNetLib;
using LiteNetLib.Utils;
using System.Collections.Concurrent;
using System.Diagnostics;
using Xunit;

namespace BasisServerTests;

// -----------------------------------------------------------------------------
// CompactMerged: the merged-datagram framing for unreliable traffic.
//
// A legacy Merged entry is [ushort size][property][channel][payload] -- four bytes
// of framing per message. A CompactMerged entry is [channel|flag][length][payload]:
// two bytes up to a 255-byte payload, three above it, because the container already
// says everything inside it is unreliable.
//
// The framing tests drive the codec directly. The transport tests run two real
// NetManagers over loopback, because the thing that can only go wrong end to end is
// the mixing of framings inside one datagram -- Ack and Channeled packets share the
// merge buffer, and letting both framings into it was what corrupted the first
// version of this.
// -----------------------------------------------------------------------------

public class CompactMergeFramingTests
{
    /// <summary>ushort nested length + the nested packet's own property and channel bytes.</summary>
    private const int LegacyEntryOverhead = 4;

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    [InlineData(200, 2)]
    [InlineData(255, 2)]
    [InlineData(256, 3)]
    [InlineData(1200, 3)]
    public void EntryOverhead_IsTwoBytesUpTo255_ThreeAbove(int payloadLength, int expectedOverhead)
    {
        Assert.Equal(expectedOverhead, CompactMerge.EntryOverhead(payloadLength));
        Assert.Equal(payloadLength + expectedOverhead, CompactMerge.EntrySize(payloadLength));
    }

    [Fact]
    public void EntryOverhead_BeatsLegacyFramingByHalfThenAQuarter()
    {
        Assert.Equal(LegacyEntryOverhead / 2, CompactMerge.EntryOverhead(200));
        Assert.Equal(LegacyEntryOverhead - 1, CompactMerge.EntryOverhead(900));
    }

    [Fact]
    public void CanCarryChannel_StopsAtTheBitTheLengthFlagOwns()
    {
        Assert.True(CompactMerge.CanCarryChannel(0));
        Assert.True(CompactMerge.CanCarryChannel(63));
        Assert.True(CompactMerge.CanCarryChannel(CompactMerge.ChannelMask));
        Assert.False(CompactMerge.CanCarryChannel(128));
        Assert.False(CompactMerge.CanCarryChannel(255));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(63, 32)]
    [InlineData(62, 255)]
    [InlineData(5, 256)]
    [InlineData(1, 1200)]
    public void WriteEntry_RoundTripsThroughTryReadEntry(byte channel, int payloadLength)
    {
        byte[] payload = Payload(payloadLength);

        // Written from a packet body, so the source offset is past the unreliable header.
        byte[] source = new byte[2 + payloadLength];
        payload.CopyTo(source, 2);

        byte[] buffer = new byte[CompactMerge.EntrySize(payloadLength) + 8];
        int written = CompactMerge.WriteEntry(buffer, 0, channel, source, 2, payloadLength);
        Assert.Equal(CompactMerge.EntrySize(payloadLength), written);

        int offset = 0;
        Assert.True(CompactMerge.TryReadEntry(buffer, written, ref offset, out byte readChannel, out int readLength));
        Assert.Equal(channel, readChannel);
        Assert.Equal(payloadLength, readLength);
        Assert.Equal(payload, buffer.AsSpan(offset, readLength).ToArray());
    }

    [Fact]
    public void WriteEntry_PacksEntriesBackToBack()
    {
        var lengths = new[] { 1, 300, 0, 40 };
        var channels = new byte[] { 0, 7, 63, 2 };

        byte[] buffer = new byte[4096];
        int written = 0;
        for (int i = 0; i < lengths.Length; i++)
        {
            byte[] source = new byte[2 + lengths[i]];
            Payload(lengths[i]).CopyTo(source, 2);
            written += CompactMerge.WriteEntry(buffer, written, channels[i], source, 2, lengths[i]);
        }

        int offset = 0;
        for (int i = 0; i < lengths.Length; i++)
        {
            Assert.True(CompactMerge.TryReadEntry(buffer, written, ref offset, out byte channel, out int length));
            Assert.Equal(channels[i], channel);
            Assert.Equal(lengths[i], length);
            Assert.Equal(Payload(lengths[i]), buffer.AsSpan(offset, length).ToArray());
            offset += length;
        }
        Assert.Equal(written, offset);
    }

    [Fact]
    public void TryReadEntry_RejectsTruncatedEntries()
    {
        byte[] source = new byte[2 + 300];
        Payload(300).CopyTo(source, 2);

        byte[] buffer = new byte[512];
        int written = CompactMerge.WriteEntry(buffer, 0, 9, source, 2, 300);

        // Every prefix short of the whole entry has to be refused rather than read past.
        for (int size = 0; size < written; size++)
        {
            int offset = 0;
            Assert.False(CompactMerge.TryReadEntry(buffer, size, ref offset, out _, out _));
        }

        int wholeOffset = 0;
        Assert.True(CompactMerge.TryReadEntry(buffer, written, ref wholeOffset, out _, out int length));
        Assert.Equal(300, length);
    }

    private static byte[] Payload(int length)
    {
        byte[] payload = new byte[length];
        for (int i = 0; i < length; i++)
            payload[i] = (byte)(i * 7 + 3);
        return payload;
    }
}

/// <summary>
/// Two real NetManagers on loopback. Every wait is polled against a deadline rather than slept
/// through, so the suite costs about as long as the handshake actually takes.
/// </summary>
public class CompactMergeTransportTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public CompactMergeTransportTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    private const string ConnectKey = "compact-merge-test";
    private const int HandshakeTimeoutMs = 10000;
    private const int DeliveryTimeoutMs = 10000;

    private sealed class Endpoint : IDisposable
    {
        public readonly EventBasedNetListener Listener = new();
        public readonly NetManager Manager;
        public readonly ConcurrentQueue<(byte Channel, DeliveryMethod Method, byte[] Data)> Received = new();

        public Endpoint(bool compactMerge)
        {
            Manager = new NetManager(Listener)
            {
                AutoRecycle = true,
                UnsyncedEvents = true,
                EnableStatistics = true,
                CompactMergeEnabled = compactMerge,
                ChannelsCount = 64,
                UpdateTime = 5,
                MergeHoldMs = 0f,
                // The suite sends in bursts; the default bound would shed them as backlog.
                MaxUnreliableQueuePerPeer = 8192,
                DisconnectTimeout = 60000
            };

            Listener.ConnectionRequestEvent += request => request.AcceptIfKey(ConnectKey);
            Listener.NetworkReceiveEvent += (peer, reader, channel, method) =>
            {
                Received.Enqueue((channel, method, reader.GetRemainingBytes()));
            };
        }

        public void Dispose() => Manager.Stop();
    }

    private static (Endpoint Server, Endpoint Client, NetPeer ClientPeer) Connect(bool serverCompact, bool clientCompact)
    {
        var server = new Endpoint(serverCompact);
        var client = new Endpoint(clientCompact);

        Assert.True(server.Manager.Start(0));
        Assert.True(client.Manager.Start(0));

        client.Manager.Connect("127.0.0.1", server.Manager.LocalPort, ConnectKey);

        NetPeer? peer = null;
        Assert.True(
            WaitFor(() =>
            {
                peer = client.Manager.FirstPeer;
                return peer is { ConnectionState: ConnectionState.Connected }
                       && server.Manager.FirstPeer is { ConnectionState: ConnectionState.Connected };
            }, HandshakeTimeoutMs),
            "peers never reached Connected");

        return (server, client, peer!);
    }

    private static bool WaitFor(Func<bool> condition, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
                return true;
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
    public void CompactFraming_IsActiveFromTheFirstPacket()
    {
        // No handshake to wait out: the protocol id guarantees the far end can decode it, so the
        // very first merged datagram of a connection is already compact.
        var (server, client, clientPeer) = Connect(serverCompact: true, clientCompact: true);
        using (server)
        using (client)
        {
            Assert.True(clientPeer.CompactMergeActive);
            Assert.True(server.Manager.FirstPeer.CompactMergeActive);
        }
    }

    [Fact]
    public void DisablingItOnOneEndOnly_StillRoundTripsBothWays()
    {
        // The switch is send-side; both framings are always decoded, so a mismatched pair is a
        // supported configuration rather than a broken one.
        var (server, client, clientPeer) = Connect(serverCompact: true, clientCompact: false);
        using (server)
        using (client)
        {
            NetPeer serverPeer = server.Manager.FirstPeer;
            Assert.True(serverPeer.CompactMergeActive);
            Assert.False(clientPeer.CompactMergeActive);

            byte[] up = Message(1, 120);
            clientPeer.Send(up, 3, DeliveryMethod.Unreliable);
            Assert.True(WaitFor(() => server.Received.Count == 1, DeliveryTimeoutMs));
            Assert.True(server.Received.TryDequeue(out var gotUp));
            Assert.Equal(3, gotUp.Channel);
            Assert.Equal(up, gotUp.Data);

            byte[] down = Message(2, 130);
            serverPeer.Send(down, 4, DeliveryMethod.Unreliable);
            Assert.True(WaitFor(() => client.Received.Count == 1, DeliveryTimeoutMs));
            Assert.True(client.Received.TryDequeue(out var gotDown));
            Assert.Equal(4, gotDown.Channel);
            Assert.Equal(down, gotDown.Data);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnreliableMessages_SurviveBothFramings(bool compact)
    {
        var (server, client, clientPeer) = Connect(serverCompact: compact, clientCompact: compact);
        using (server)
        using (client)
        {
            Assert.Equal(compact, clientPeer.CompactMergeActive);

            // Lengths straddle the one-byte length boundary and channels straddle nothing in
            // particular -- both framings have to carry all of it.
            var sent = new List<(byte Channel, byte[] Data)>();
            int[] lengths = { 1, 8, 64, 200, 255, 256, 300, 700 };
            for (int i = 0; i < 120; i++)
            {
                byte channel = (byte)(i % 8);
                byte[] data = Message(i, lengths[i % lengths.Length]);
                sent.Add((channel, data));
                clientPeer.Send(data, channel, DeliveryMethod.Unreliable);
            }

            Assert.True(WaitFor(() => server.Received.Count == sent.Count, DeliveryTimeoutMs),
                $"expected {sent.Count} messages, got {server.Received.Count}");

            var received = server.Received.ToArray();
            for (int i = 0; i < sent.Count; i++)
            {
                Assert.Equal(DeliveryMethod.Unreliable, received[i].Method);
                Assert.Equal(sent[i].Channel, received[i].Channel);
                Assert.Equal(sent[i].Data, received[i].Data);
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(200)]
    [InlineData(700)]
    public void LoneCompactEntry_IsUnwrappedBackIntoAPlainUnreliablePacket(int payloadLength)
    {
        // A single compact entry costs a byte more than the packet it was built from, so the flush
        // rebuilds that packet instead of sending a one-entry container.
        var (server, client, clientPeer) = Connect(serverCompact: true, clientCompact: true);
        using (server)
        using (client)
        {
            Assert.True(clientPeer.CompactMergeActive);

            byte[] payload = Message(11, payloadLength);
            clientPeer.Send(payload, 6, DeliveryMethod.Unreliable);

            Assert.True(WaitFor(() => server.Received.Count == 1, DeliveryTimeoutMs));
            Assert.True(server.Received.TryDequeue(out var got));
            Assert.Equal(6, got.Channel);
            Assert.Equal(DeliveryMethod.Unreliable, got.Method);
            Assert.Equal(payload, got.Data);
        }
    }

    [Fact]
    public void ChannelTooHighForCompactFraming_FallsBackWithoutCorruptingNeighbours()
    {
        // Bit 6 is now the raw Ack/Channeled marker, so unreliable channels above 63 cannot be
        // compacted and drop back to legacy framing mid-stream.
        var (server, client, clientPeer) = Connect(serverCompact: true, clientCompact: true);
        using (server)
        using (client)
        {
            Assert.True(clientPeer.CompactMergeActive);

            var sent = new List<(byte Channel, byte[] Data)>();
            for (int i = 0; i < 60; i++)
            {
                byte channel = i % 2 == 0 ? (byte)3 : (byte)200;
                byte[] data = Message(i + 900, 30 + i);
                sent.Add((channel, data));
                clientPeer.Send(data, channel, DeliveryMethod.Unreliable);
            }

            Assert.True(WaitFor(() => server.Received.Count == sent.Count, DeliveryTimeoutMs),
                $"expected {sent.Count} messages, got {server.Received.Count}");

            // Unreliable delivery is intentionally unordered. Out-of-range channels bypass the
            // compact accumulator directly, so they can overtake a held compact entry. Validate
            // exact channel/content preservation without imposing an ordering guarantee.
            var outstanding = sent
                .Select(s => (s.Channel, Key: Convert.ToBase64String(s.Data)))
                .ToList();
            foreach (var got in server.Received)
            {
                string key = Convert.ToBase64String(got.Data);
                int index = outstanding.FindIndex(o => o.Channel == got.Channel && o.Key == key);
                Assert.True(index >= 0, $"received an unexpected unreliable message on channel {got.Channel}");
                outstanding.RemoveAt(index);
            }
            Assert.Empty(outstanding);
        }
    }

    [Fact]
    public void MixedReliableAndUnreliable_DoNotCorruptEachOther()
    {
        // The regression: Ack and Channeled packets go through the same merge buffer, so a
        // datagram that held both framings deserialised as garbage on the far side.
        var (server, client, clientPeer) = Connect(serverCompact: true, clientCompact: true);
        using (server)
        using (client)
        {
            Assert.True(clientPeer.CompactMergeActive);

            var expected = new List<(byte Channel, DeliveryMethod Method, byte[] Data)>();
            for (int i = 0; i < 200; i++)
            {
                byte channel = (byte)(i % 4);
                DeliveryMethod method = i % 3 == 0 ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable;
                byte[] data = Message(i + 500, 20 + (i % 260));
                expected.Add((channel, method, data));
                clientPeer.Send(data, channel, method);
            }

            Assert.True(WaitFor(() => server.Received.Count == expected.Count, DeliveryTimeoutMs),
                $"expected {expected.Count} messages, got {server.Received.Count}");

            // Reliable ordering only holds within a channel, and the two methods interleave, so
            // match on content rather than on arrival order.
            var outstanding = expected
                .Select(e => (e.Channel, e.Method, Key: Convert.ToBase64String(e.Data)))
                .ToList();

            foreach (var got in server.Received)
            {
                string key = Convert.ToBase64String(got.Data);
                int index = outstanding.FindIndex(o => o.Key == key && o.Channel == got.Channel && o.Method == got.Method);
                Assert.True(index >= 0, $"received a message nobody sent: channel {got.Channel}, {got.Data.Length} bytes");
                outstanding.RemoveAt(index);
            }
            Assert.Empty(outstanding);
        }
    }

    [Fact]
    public void CompactFraming_PutsFewerBytesOnTheWire()
    {
        long compactBytes = MeasureEgress(compact: true);
        long legacyBytes = MeasureEgress(compact: false);

        _out.WriteLine($"300 x 80 B unreliable: legacy {legacyBytes} B, compact {compactBytes} B " +
                       $"({(legacyBytes - compactBytes) / (double)legacyBytes:P2} saved)");

        Assert.True(compactBytes < legacyBytes,
            $"compact framing sent {compactBytes} bytes, legacy sent {legacyBytes}");
    }

    private static long MeasureEgress(bool compact)
    {
        var (server, client, clientPeer) = Connect(serverCompact: compact, clientCompact: compact);
        using (server)
        using (client)
        {
            Assert.Equal(compact, clientPeer.CompactMergeActive);

            const int count = 300;
            const int payloadLength = 80;
            clientPeer.Statistics.Reset();
            for (int i = 0; i < count; i++)
                clientPeer.Send(Message(i, payloadLength), (byte)(i % 4), DeliveryMethod.Unreliable);

            Assert.True(WaitFor(() => server.Received.Count == count, DeliveryTimeoutMs),
                $"expected {count} messages, got {server.Received.Count}");

            return clientPeer.Statistics.BytesSent;
        }
    }
}
