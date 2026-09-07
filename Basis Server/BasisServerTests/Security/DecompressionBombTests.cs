using Basis.Network.Core;
using BasisNetworkServer.BasisNetworking;
using System.IO.Compression;
using Xunit;
using static SerializableBasis;

namespace BasisServerTests;

/// <summary>
/// Inflation caps on every compressed payload a peer can hand the server or another client.
///
/// Deflate and Brotli both reach ratios in the thousands on repetitive input, so a few
/// kilobytes on the wire can ask the receiver for gigabytes of heap. The receiver cannot
/// know the real size in advance (the frame's declared length is the COMPRESSED length),
/// so the only defence is to stop copying the moment the output passes what the protocol
/// says a payload may be.
///
/// Each test here pins one of those caps from both sides: a payload at exactly the cap has
/// to survive, and a bomb has to be refused while it is still small.
/// </summary>
public class DecompressionBombTests
{
    private static byte[] Deflate(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, true))
        {
            deflate.Write(raw, 0, raw.Length);
        }
        return output.ToArray();
    }

    private static byte[] Brotli(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Optimal, true))
        {
            brotli.Write(raw, 0, raw.Length);
        }
        return output.ToArray();
    }

    private static NetDataReader Reader(NetDataWriter w) => new NetDataReader(w.CopyData());

    // ─────────────────────────────────────────────────────────────────────────
    // ServerReadyBatchMessage — the join fill, the largest compressed payload in the protocol
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ReadyBatch_CompressedBomb_IsRefused()
    {
        // 8 MB of zeros deflates to a couple of kilobytes. Without the cap the receiver
        // allocates the full 8 MB from a frame that cost the sender almost nothing.
        byte[] bomb = Deflate(new byte[8 * 1024 * 1024]);
        Assert.True(bomb.Length < 16 * 1024, "the bomb has to be small to be worth refusing");

        var w = new NetDataWriter();
        new ServerReadyBatchMessage { Count = 1 }.SerializePreCompressed(w, bomb, true);

        var received = default(ServerReadyBatchMessage);
        Assert.Throws<InvalidDataException>(() => received.Deserialize(Reader(w)));
    }

    [Fact]
    public void ReadyBatch_UncompressedPayloadOverTheCap_IsRefused()
    {
        // MaxInflatedBytes, not MaxPayloadBytes: the producer flushes AFTER appending the record
        // that crosses the nominal cap, so a real batch is always a little over it. See
        // JoinFillBatchScaleTests — pinning the refusal at MaxPayloadBytes rejects every full
        // join-fill batch.
        byte[] oversized = new byte[ServerReadyBatchMessage.MaxInflatedBytes + 1];
        var w = new NetDataWriter();
        new ServerReadyBatchMessage { Count = 1 }.SerializePreCompressed(w, oversized, false);

        var received = default(ServerReadyBatchMessage);
        Assert.Throws<ArgumentException>(() => received.Deserialize(Reader(w)));
    }

    [Fact]
    public void ReadyBatch_UncompressedPayloadAtExactlyTheCap_IsAccepted()
    {
        byte[] atCap = new byte[ServerReadyBatchMessage.MaxPayloadBytes];
        for (int i = 0; i < atCap.Length; i++) atCap[i] = (byte)i;

        var w = new NetDataWriter();
        new ServerReadyBatchMessage { Count = 7 }.SerializePreCompressed(w, atCap, false);

        var received = default(ServerReadyBatchMessage);
        received.Deserialize(Reader(w));
        Assert.Equal((ushort)7, received.Count);
        Assert.Equal(atCap, received.Payload);
    }

    [Fact]
    public void ReadyBatch_CompressedPayloadAtExactlyTheCap_RoundTrips()
    {
        // The boundary the join-fill producer has to respect: a batch may reach the cap
        // exactly, and inflating it must not trip the guard on the final chunk.
        byte[] atCap = new byte[ServerReadyBatchMessage.MaxPayloadBytes];
        var w = new NetDataWriter();
        var batch = new ServerReadyBatchMessage { Count = 11, Payload = atCap };
        batch.Serialize(w);
        Assert.True(batch.WasCompressed, "a 32 KB run of zeros must take the compressed path");

        var received = default(ServerReadyBatchMessage);
        received.Deserialize(Reader(w));
        Assert.Equal((ushort)11, received.Count);
        Assert.Equal(ServerReadyBatchMessage.MaxPayloadBytes, received.Payload.Length);
    }

    [Fact]
    public void ReadyBatch_CompressedPayloadOneByteOverTheInflationCeiling_IsRefused()
    {
        // Pins the boundary from the other side. The ceiling is MaxInflatedBytes because the
        // producers overshoot MaxPayloadBytes by design (append, then flush on >=), and
        // JoinBroadcast.Flush always emits at least one record whatever its size.
        byte[] overCap = new byte[ServerReadyBatchMessage.MaxInflatedBytes + 1];
        var w = new NetDataWriter();
        new ServerReadyBatchMessage { Count = 11, Payload = overCap }.Serialize(w);

        var received = default(ServerReadyBatchMessage);
        Assert.Throws<InvalidDataException>(() => received.Deserialize(Reader(w)));
    }

    [Fact]
    public void ReadyBatch_CompressedPayloadAtTheInflationCeiling_RoundTrips()
    {
        byte[] atCeiling = new byte[ServerReadyBatchMessage.MaxInflatedBytes];
        var w = new NetDataWriter();
        new ServerReadyBatchMessage { Count = 11, Payload = atCeiling }.Serialize(w);

        var received = default(ServerReadyBatchMessage);
        received.Deserialize(Reader(w));
        Assert.Equal(ServerReadyBatchMessage.MaxInflatedBytes, received.Payload.Length);
    }

    [Fact]
    public void ReadyBatch_DeclaredLengthBeyondTheFrame_IsRefusedBeforeAllocating()
    {
        // The declared length is attacker-chosen and int-wide; it must be checked against
        // what actually follows before it is used as an allocation size.
        var w = new NetDataWriter();
        w.Put((ushort)1);
        w.Put(true);
        w.Put(int.MaxValue);
        w.Put(new byte[] { 1, 2, 3, 4 });

        var received = default(ServerReadyBatchMessage);
        Assert.Throws<ArgumentException>(() => received.Deserialize(Reader(w)));
    }

    [Fact]
    public void ReadyBatch_NegativeDeclaredLength_IsRefused()
    {
        var w = new NetDataWriter();
        w.Put((ushort)1);
        w.Put(false);
        w.Put(-1);
        w.Put(new byte[] { 1, 2, 3, 4 });

        var received = default(ServerReadyBatchMessage);
        Assert.Throws<ArgumentException>(() => received.Deserialize(Reader(w)));
    }

    [Fact]
    public void ReadyBatch_CorruptDeflateStream_ThrowsRatherThanReturningPartialData()
    {
        byte[] good = Deflate(new byte[1024]);
        byte[] corrupt = (byte[])good.Clone();
        for (int i = 0; i < corrupt.Length; i++) corrupt[i] ^= 0x5A;

        var w = new NetDataWriter();
        new ServerReadyBatchMessage { Count = 1 }.SerializePreCompressed(w, corrupt, true);

        var received = default(ServerReadyBatchMessage);
        Assert.ThrowsAny<Exception>(() => received.Deserialize(Reader(w)));
    }

    [Fact]
    public void ReadyBatch_CompressionIsSkippedWhenItDoesNotPay()
    {
        // High-entropy bodies grow under Deflate. The flag has to report what actually
        // happened, or the receiver inflates something that was never deflated.
        var random = new Random(20260903);
        byte[] noise = new byte[4096];
        random.NextBytes(noise);

        byte[] framed = ServerReadyBatchMessage.Compress(noise, out bool compressed);
        Assert.False(compressed);
        Assert.Equal(noise, framed);

        byte[] tiny = new byte[ServerReadyBatchMessage.MinCompressBytes - 1];
        ServerReadyBatchMessage.Compress(tiny, out bool tinyCompressed);
        Assert.False(tinyCompressed);
    }

    [Fact]
    public void ReadyBatch_AmplificationOfTheRefusedBomb_StaysBounded()
    {
        // What the guard actually buys: the work done before the refusal is bounded by the
        // cap, not by what the sender asked for.
        byte[] bomb = Deflate(new byte[64 * 1024 * 1024]);
        var w = new NetDataWriter();
        new ServerReadyBatchMessage { Count = 1 }.SerializePreCompressed(w, bomb, true);
        byte[] frame = w.CopyData();

        var received = default(ServerReadyBatchMessage);
        Assert.Throws<InvalidDataException>(() => received.Deserialize(new NetDataReader(frame)));

        // The frame is orders of magnitude smaller than what it claimed to expand to, which
        // is exactly why the uncapped copy was worth exploiting.
        Assert.True(frame.Length * 100L < 64L * 1024 * 1024);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // BasisNetworkStatistics.Snapshot — Brotli, arrives from the console/rest side
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void StatisticsSnapshot_BrotliBomb_IsRefused()
    {
        byte[] bomb = Brotli(new byte[8 * 1024 * 1024]);
        Assert.True(bomb.Length < 16 * 1024);

        Assert.Throws<InvalidDataException>(() => BasisNetworkStatistics.Snapshot.Decode(bomb));
    }

    [Fact]
    public void StatisticsSnapshot_PayloadUnderTheCap_DecodesNormally()
    {
        bool wasRecording = BasisNetworkStatistics.IsRecordingData;
        try
        {
            BasisNetworkStatistics.IsRecordingData = true;
            BasisNetworkStatistics.RecordInbound(42, 128);
            BasisNetworkStatistics.RecordOutbound(43, 256);

            byte[] encoded = BasisNetworkStatistics.Snapshot.EncodeCurrent();
            BasisNetworkStatistics.Snapshot decoded = BasisNetworkStatistics.Snapshot.Decode(encoded);

            Assert.True(decoded.PerIndex.ContainsKey(42));
            Assert.True(decoded.OutPerIndex.ContainsKey(43));
            Assert.True(decoded.PerIndex[42].Bytes >= 128);
            Assert.True(decoded.OutPerIndex[43].Bytes >= 256);
        }
        finally
        {
            BasisNetworkStatistics.IsRecordingData = wasRecording;
        }
    }

    [Fact]
    public void StatisticsSnapshot_UncompressedPathIsUncapped_ButBoundedByTheFrame()
    {
        // compressed:false hands the bytes straight to the decoder, so the only bound is the
        // caller's own buffer. Pinned so the flag is not mistaken for a second cap.
        byte[] raw = new byte[] { 0, 0 };
        BasisNetworkStatistics.Snapshot decoded = BasisNetworkStatistics.Snapshot.Decode(raw, compressed: false);
        Assert.Empty(decoded.PerIndex);
        Assert.Empty(decoded.OutPerIndex);
    }

    [Fact]
    public void StatisticsSnapshot_TruncatedPayload_ThrowsRatherThanReadingOn()
    {
        // Map header claims one entry, then the stream ends.
        byte[] truncated = { 1 };
        Assert.Throws<EndOfStreamException>(() => BasisNetworkStatistics.Snapshot.Decode(truncated, compressed: false));
    }

    [Fact]
    public void StatisticsSnapshot_OverlongVarint_IsRefused()
    {
        // Ten continuation bytes would shift past 63 bits; the reader has to stop rather
        // than wrap the shift.
        byte[] overlong = { 1, 7, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80 };
        Assert.Throws<InvalidDataException>(() => BasisNetworkStatistics.Snapshot.Decode(overlong, compressed: false));
    }

    [Fact]
    public void StatisticsSnapshot_CorruptBrotliStream_Throws()
    {
        byte[] corrupt = { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 };
        Assert.ThrowsAny<Exception>(() => BasisNetworkStatistics.Snapshot.Decode(corrupt));
    }
}
