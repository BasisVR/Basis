using Basis.Network.Core.Compression;
using Xunit;
using BitQuality = Basis.Network.Core.Compression.BasisAvatarBitPacking.BitQuality;
using S = BasisServerTests.DeltaTestSupport;

namespace BasisServerTests;

/// <summary>
/// Defensive behavior: bad inputs return sentinel values (never throw), corrupt/truncated deltas are
/// rejected rather than misapplied, and the baseline is never mutated. Includes fuzzing.
/// </summary>
public class AvatarDeltaRobustnessTests
{
    private static byte[] Dst(BitQuality q) => new byte[BasisAvatarDeltaCompression.MaxDeltaSize(q)];

    [Fact]
    public void BuildDelta_NullOrUndersized_ReturnsMinusOne()
    {
        var q = BitQuality.High;
        int size = S.PayloadSize(q);
        var good = new byte[size];
        Assert.Equal(-1, BasisAvatarDeltaCompression.BuildDelta(null!, good, q, Dst(q), 0));
        Assert.Equal(-1, BasisAvatarDeltaCompression.BuildDelta(good, null!, q, Dst(q), 0));
        Assert.Equal(-1, BasisAvatarDeltaCompression.BuildDelta(good, good, q, null!, 0));
        Assert.Equal(-1, BasisAvatarDeltaCompression.BuildDelta(new byte[size - 1], good, q, Dst(q), 0));
        Assert.Equal(-1, BasisAvatarDeltaCompression.BuildDelta(good, new byte[size - 1], q, Dst(q), 0));
        // dst too small for the worst case.
        Assert.Equal(-1, BasisAvatarDeltaCompression.BuildDelta(good, good, q, new byte[10], 0));
    }

    [Fact]
    public void TryApplyDelta_NullOrUndersized_ReturnsFalse()
    {
        var q = BitQuality.Medium;
        int size = S.PayloadSize(q);
        var baseline = new byte[size];
        var dst = Dst(q);
        int len = BasisAvatarDeltaCompression.BuildDelta(baseline, baseline, q, dst, 0); // mask-only delta
        var outFull = new byte[size];

        Assert.False(BasisAvatarDeltaCompression.TryApplyDelta(null!, dst, 0, len, q, outFull));
        Assert.False(BasisAvatarDeltaCompression.TryApplyDelta(baseline, null!, 0, len, q, outFull));
        Assert.False(BasisAvatarDeltaCompression.TryApplyDelta(baseline, dst, 0, len, q, null!));
        Assert.False(BasisAvatarDeltaCompression.TryApplyDelta(new byte[size - 1], dst, 0, len, q, outFull));
        Assert.False(BasisAvatarDeltaCompression.TryApplyDelta(baseline, dst, 0, len, q, new byte[size - 1]));
        // Fewer bytes than even the dirty mask.
        Assert.False(BasisAvatarDeltaCompression.TryApplyDelta(baseline, dst, 0, BasisAvatarDeltaCompression.DirtyMaskBytes - 1, q, outFull));
    }

    [Theory]
    [InlineData(BitQuality.VeryLow)]
    [InlineData(BitQuality.Low)]
    [InlineData(BitQuality.Medium)]
    [InlineData(BitQuality.High)]
    public void TryApplyDelta_WrongLength_IsRejected(BitQuality q)
    {
        var rng = new Random((int)q + 77);
        byte[] kf = S.MakePayload(q, rng);
        byte[] cur = S.MakePayload(q, rng);
        var dst = Dst(q);
        int len = BasisAvatarDeltaCompression.BuildDelta(kf, cur, q, dst, 0);
        var outFull = new byte[S.PayloadSize(q)];
        // One byte short and one byte long must both fail (mask says exactly `len`).
        Assert.False(BasisAvatarDeltaCompression.TryApplyDelta(kf, dst, 0, len - 1, q, outFull));
        Assert.False(BasisAvatarDeltaCompression.TryApplyDelta(kf, dst, 0, len + 1, q, outFull));
    }

    [Fact]
    public void TryApplyDelta_OutOfRangeWindow_IsRejected()
    {
        var q = BitQuality.Low;
        var rng = new Random(5);
        byte[] kf = S.MakePayload(q, rng);
        byte[] cur = S.MakePayload(q, rng);
        var dst = Dst(q);
        int len = BasisAvatarDeltaCompression.BuildDelta(kf, cur, q, dst, 0);
        var outFull = new byte[S.PayloadSize(q)];
        Assert.False(BasisAvatarDeltaCompression.TryApplyDelta(kf, dst, -1, len, q, outFull));
        Assert.False(BasisAvatarDeltaCompression.TryApplyDelta(kf, dst, dst.Length - 2, len, q, outFull));
    }

    [Theory]
    [InlineData(BitQuality.VeryLow)]
    [InlineData(BitQuality.Low)]
    [InlineData(BitQuality.Medium)]
    [InlineData(BitQuality.High)]
    public void TryApplyDelta_DoesNotMutateBaseline(BitQuality q)
    {
        var rng = new Random((int)q + 321);
        for (int i = 0; i < 50; i++)
        {
            byte[] kf = S.MakePayload(q, rng);
            byte[] baselineCopy = (byte[])kf.Clone();
            byte[] cur = S.MakePayload(q, rng);
            var dst = Dst(q);
            int len = BasisAvatarDeltaCompression.BuildDelta(kf, cur, q, dst, 0);
            var outFull = new byte[S.PayloadSize(q)];
            Assert.True(BasisAvatarDeltaCompression.TryApplyDelta(kf, dst, 0, len, q, outFull));
            Assert.Equal(baselineCopy, kf); // baseline untouched
        }
    }

    [Fact]
    public void DeltaBodyLength_InsufficientData_ReturnsMinusOne()
    {
        var q = BitQuality.High;
        var buf = new byte[BasisAvatarDeltaCompression.DirtyMaskBytes];
        Assert.Equal(-1, BasisAvatarDeltaCompression.DeltaBodyLength(buf, 0, BasisAvatarDeltaCompression.DirtyMaskBytes - 1, q));
        Assert.Equal(-1, BasisAvatarDeltaCompression.DeltaBodyLength(null!, 0, 100, q));
    }

    [Theory]
    [InlineData(BitQuality.VeryLow)]
    [InlineData(BitQuality.Low)]
    [InlineData(BitQuality.Medium)]
    [InlineData(BitQuality.High)]
    public void Fuzz_GarbageDelta_NeverThrows(BitQuality q)
    {
        var rng = new Random((int)q + 8888);
        byte[] baseline = S.MakePayload(q, rng);
        var outFull = new byte[S.PayloadSize(q)];
        for (int i = 0; i < 20000; i++)
        {
            var garbage = new byte[rng.Next(0, S.PayloadSize(q) + 20)];
            rng.NextBytes(garbage);
            // Neither call may throw regardless of content; correctness is that they reject bad data.
            int probe = BasisAvatarDeltaCompression.DeltaBodyLength(garbage, 0, garbage.Length, q);
            bool applied = BasisAvatarDeltaCompression.TryApplyDelta(baseline, garbage, 0, garbage.Length, q, outFull);
            if (applied)
            {
                // If it accepted the bytes, the claimed length must have matched the mask.
                Assert.Equal(garbage.Length, probe);
            }
        }
    }

    [Theory]
    [InlineData(BitQuality.VeryLow)]
    [InlineData(BitQuality.Low)]
    [InlineData(BitQuality.Medium)]
    [InlineData(BitQuality.High)]
    public void Fuzz_TruncatedValidDelta_NeverThrows(BitQuality q)
    {
        var rng = new Random((int)q + 9999);
        byte[] baseline = S.MakePayload(q, rng);
        var outFull = new byte[S.PayloadSize(q)];
        var dst = Dst(q);
        for (int i = 0; i < 2000; i++)
        {
            int len = BasisAvatarDeltaCompression.BuildDelta(baseline, S.MakePayload(q, rng), q, dst, 0);
            int truncated = rng.Next(0, len + 1);
            // Truncation should be rejected (length mismatch) but must never throw.
            BasisAvatarDeltaCompression.TryApplyDelta(baseline, dst, 0, truncated, q, outFull);
        }
    }
}
