using Basis.Network.Core;
using Basis.Network.Core.Compression;
using Xunit;
using BitQuality = Basis.Network.Core.Compression.BasisAvatarBitPacking.BitQuality;
using S = BasisServerTests.DeltaTestSupport;

namespace BasisServerTests;

/// <summary>
/// Wire-level tests: replicate the server's delta frame assembly (BasisServerReductionSystemEvents
/// .PreSerializeDelta) and the client's parse (BasisNetworkHandleAvatarDelta) and confirm they agree,
/// including the DeltaAvatarChannel header helpers, the per-receiver interval-patch offset, and the
/// body / trailing-additional-data split.
/// </summary>
public class AvatarDeltaFrameTests
{
    [Fact]
    public void DeltaHeader_RoundTrips_AllCombinations()
    {
        foreach (var q in S.AllQualities)
            foreach (bool add in new[] { false, true })
                foreach (bool large in new[] { false, true })
                {
                    byte h = BasisNetworkCommons.BuildDeltaHeader((int)q, add, large);
                    Assert.Equal((byte)q, BasisNetworkCommons.DeltaHeaderQuality(h));
                    Assert.Equal(add, BasisNetworkCommons.DeltaHeaderHasAdditionalData(h));
                    Assert.Equal(large, BasisNetworkCommons.DeltaHeaderLargeId(h));
                }
    }

    [Fact]
    public void DeltaChannel_IsThirty() => Assert.Equal(30, BasisNetworkCommons.DeltaAvatarChannel);

    // Frame layout: [header:1][playerId:1|2][interval:1][sequence:1][baseSeq:1][delta body][additional?]
    private static (byte[] frame, int len) Assemble(byte[] kf, byte[] cur, BitQuality q, ushort playerId,
        bool largeId, bool hasAdditional, byte interval, byte seq, byte baseSeq, byte[]? additional)
    {
        int idSize = largeId ? 2 : 1;
        int addLen = hasAdditional && additional != null ? additional.Length : 0;
        var frame = new byte[1 + idSize + 3 + BasisAvatarDeltaCompression.MaxDeltaSize(q) + addLen];
        int o = 0;
        frame[o++] = BasisNetworkCommons.BuildDeltaHeader((int)q, hasAdditional, largeId);
        if (largeId) { frame[o++] = (byte)(playerId & 0xFF); frame[o++] = (byte)((playerId >> 8) & 0xFF); }
        else frame[o++] = (byte)playerId;
        frame[o++] = interval;
        frame[o++] = seq;
        frame[o++] = baseSeq;
        int bodyLen = BasisAvatarDeltaCompression.BuildDelta(kf, cur, q, frame, o);
        Assert.True(bodyLen > 0);
        o += bodyLen;
        if (addLen > 0) { Buffer.BlockCopy(additional!, 0, frame, o, addLen); o += addLen; }
        return (frame, o);
    }

    private sealed record Parsed(BitQuality Q, bool HasAdditional, bool LargeId, ushort PlayerId,
        byte Interval, byte Seq, byte BaseSeq, bool Ok, byte[] Recon, int AdditionalStart, int AdditionalLen);

    private static Parsed Parse(byte[] frame, int totalLen, byte[] baseline)
    {
        int o = 0;
        byte header = frame[o++];
        var q = (BitQuality)BasisNetworkCommons.DeltaHeaderQuality(header);
        bool hasAdd = BasisNetworkCommons.DeltaHeaderHasAdditionalData(header);
        bool large = BasisNetworkCommons.DeltaHeaderLargeId(header);
        ushort playerId;
        if (large) { playerId = (ushort)(frame[o] | (frame[o + 1] << 8)); o += 2; }
        else { playerId = frame[o]; o += 1; }
        byte interval = frame[o++];
        byte seq = frame[o++];
        byte baseSeq = frame[o++];

        int avail = totalLen - o;
        int bodyLen = BasisAvatarDeltaCompression.DeltaBodyLength(frame, o, avail, q);
        Assert.True(bodyLen >= 0 && bodyLen <= avail);
        var recon = new byte[S.PayloadSize(q)];
        bool ok = BasisAvatarDeltaCompression.TryApplyDelta(baseline, frame, o, bodyLen, q, recon);
        int addStart = o + bodyLen;
        return new Parsed(q, hasAdd, large, playerId, interval, seq, baseSeq, ok, recon, addStart, totalLen - addStart);
    }

    [Fact]
    public void FrameRoundTrip_AllCombinations()
    {
        var rng = new Random(20240607);
        foreach (var q in S.AllQualities)
            foreach (bool large in new[] { false, true })
                foreach (bool hasAdd in new[] { false, true })
                    for (int iter = 0; iter < 40; iter++)
                    {
                        byte[] kf = S.MakeRealisticPayload(q, rng);
                        byte[] cur = S.MakeRealisticPayload(q, rng);
                        ushort playerId = large ? (ushort)(300 + rng.Next(60000)) : (byte)rng.Next(256);
                        byte interval = (byte)rng.Next(256);
                        byte seq = (byte)rng.Next(256);
                        byte baseSeq = (byte)rng.Next(256);
                        byte[]? add = hasAdd ? RandomBytes(rng, 1 + rng.Next(40)) : null;

                        var (frame, len) = Assemble(kf, cur, q, playerId, large, hasAdd, interval, seq, baseSeq, add);
                        var p = Parse(frame, len, kf);

                        Assert.True(p.Ok);
                        Assert.Equal(q, p.Q);
                        Assert.Equal(hasAdd, p.HasAdditional);
                        Assert.Equal(large, p.LargeId);
                        Assert.Equal(playerId, p.PlayerId);
                        Assert.Equal(interval, p.Interval);
                        Assert.Equal(seq, p.Seq);
                        Assert.Equal(baseSeq, p.BaseSeq);
                        Assert.Equal(cur, p.Recon);
                        // Trailing additional-data bytes survive the body/additional split intact.
                        if (hasAdd)
                        {
                            Assert.Equal(add!.Length, p.AdditionalLen);
                            Assert.Equal(add, frame.AsSpan(p.AdditionalStart, p.AdditionalLen).ToArray());
                        }
                        else
                        {
                            Assert.Equal(0, p.AdditionalLen);
                        }
                    }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IntervalByte_AtExpectedOffset_AndPatchable(bool large)
    {
        var rng = new Random(large ? 1 : 2);
        var q = BitQuality.High;
        byte[] kf = S.MakeRealisticPayload(q, rng);
        byte[] cur = S.MakeRealisticPayload(q, rng);
        ushort playerId = large ? (ushort)4000 : (byte)42;
        var (frame, len) = Assemble(kf, cur, q, playerId, large, false, interval: 0, seq: 5, baseSeq: 9, additional: null);

        // The send loop patches the interval per-receiver at offset (1 + idSize): header + playerId.
        int idSize = large ? 2 : 1;
        int intervalOffset = 1 + idSize;
        Assert.Equal(0, frame[intervalOffset]);

        frame[intervalOffset] = 123; // simulate the per-receiver patch
        var p = Parse(frame, len, kf);
        Assert.True(p.Ok);
        Assert.Equal((byte)123, p.Interval);
        Assert.Equal(cur, p.Recon); // patching interval must not disturb the body
    }

    [Fact]
    public void DeltaBeforeKeyframe_IsDroppableByBaselineMismatch()
    {
        // The client keys a baseline by (quality, baseSeq). A delta whose baseSeq/quality the receiver
        // does not hold cannot be applied — modelled here as "no baseline" -> reconstruction refused.
        var rng = new Random(31);
        var q = BitQuality.Medium;
        byte[] kf = S.MakeRealisticPayload(q, rng);
        byte[] cur = S.MakeRealisticPayload(q, rng);
        var (frame, len) = Assemble(kf, cur, q, 7, false, false, 0, 3, baseSeq: 88, additional: null);
        // A receiver with a different-size / absent baseline must not silently apply.
        var wrongSizeBaseline = new byte[S.PayloadSize(q) - 1];
        int o = 1 + 1 + 3; // header + id + interval + seq + baseSeq
        int bodyLen = BasisAvatarDeltaCompression.DeltaBodyLength(frame, o, len - o, q);
        var recon = new byte[S.PayloadSize(q)];
        Assert.False(BasisAvatarDeltaCompression.TryApplyDelta(wrongSizeBaseline, frame, o, bodyLen, q, recon));
    }

    private static byte[] RandomBytes(Random rng, int n)
    {
        var b = new byte[n];
        rng.NextBytes(b);
        return b;
    }
}
