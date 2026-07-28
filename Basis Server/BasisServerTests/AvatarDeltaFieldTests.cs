using Basis.Network.Core.Compression;
using Xunit;
using BitQuality = Basis.Network.Core.Compression.BasisAvatarBitPacking.BitQuality;
using S = BasisServerTests.DeltaTestSupport;

namespace BasisServerTests;

/// <summary>
/// Per-field dirty-mask coverage: changing exactly one field must send exactly that field (plus the
/// 7-byte mask), and every field / combination must round-trip.
/// </summary>
public class AvatarDeltaFieldTests
{
    private const int Mask = 8;   // DirtyMaskBytes (57 fields incl. end-effector)

    [Theory]
    [InlineData(BitQuality.VeryLow)]
    [InlineData(BitQuality.Low)]
    [InlineData(BitQuality.Medium)]
    [InlineData(BitQuality.High)]
    public void NoChange_SendsMaskOnly(BitQuality q)
    {
        var rng = new Random((int)q + 1);
        byte[] kf = S.MakeRealisticPayload(q, rng);
        byte[] cur = (byte[])kf.Clone();
        var (len, recon) = S.BuildApply(kf, cur, q);
        Assert.Equal(Mask, len);
        Assert.Equal(cur, recon);
    }

    [Theory]
    [InlineData(BitQuality.VeryLow)]
    [InlineData(BitQuality.Low)]
    [InlineData(BitQuality.Medium)]
    [InlineData(BitQuality.High)]
    public void PositionOnly(BitQuality q) => AssertByteFieldOnly(q, 0, BasisAvatarBitPacking.PositionBytes(q));

    [Theory]
    [InlineData(BitQuality.VeryLow)]
    [InlineData(BitQuality.Low)]
    [InlineData(BitQuality.Medium)]
    [InlineData(BitQuality.High)]
    public void ScaleOnly(BitQuality q) => AssertByteFieldOnly(q, S.ScaleOffset(q), BasisBoneRotationCompression.WriteScale);

    [Theory]
    [InlineData(BitQuality.VeryLow)]
    [InlineData(BitQuality.Low)]
    [InlineData(BitQuality.Medium)]
    [InlineData(BitQuality.High)]
    public void BodyRotationOnly(BitQuality q) => AssertByteFieldOnly(q, S.BodyRotOffset(q), BasisBoneRotationCompression.WriteRotation);

    [Theory]
    [InlineData(BitQuality.VeryLow)]
    [InlineData(BitQuality.Low)]
    [InlineData(BitQuality.Medium)]
    [InlineData(BitQuality.High)]
    public void HipsDeltaOnly(BitQuality q) => AssertByteFieldOnly(q, S.HipsDeltaOffset(q), BasisBoneRotationCompression.WriteHipsDelta);

    [Theory]
    [InlineData(BitQuality.VeryLow)]
    [InlineData(BitQuality.Low)]
    [InlineData(BitQuality.Medium)]
    [InlineData(BitQuality.High)]
    public void HipsRotationOnly(BitQuality q) => AssertByteFieldOnly(q, S.HipsRotOffset(q), BasisBoneRotationCompression.WriteHipsRotation);

    private static void AssertByteFieldOnly(BitQuality q, int fieldOffset, int fieldBytes)
    {
        var rng = new Random(fieldOffset * 31 + (int)q);
        byte[] kf = S.MakeRealisticPayload(q, rng);
        byte[] cur = (byte[])kf.Clone();
        cur[fieldOffset] ^= 0xFF; // flip one byte inside the field
        var (len, recon) = S.BuildApply(kf, cur, q);
        Assert.Equal(Mask + fieldBytes, len);
        Assert.Equal(cur, recon);
    }

    [Fact]
    public void EverySingleBone_AllQualities_ExactSizeAndRoundTrip()
    {
        var rng = new Random(9001);
        foreach (var q in S.AllQualities)
        {
            for (int slot = 0; slot < S.BoneCount; slot++)
            {
                byte[] kf = S.MakeRealisticPayload(q, rng);
                byte[] cur = (byte[])kf.Clone();
                S.FlipBone(cur, q, slot);
                var (len, recon) = S.BuildApply(kf, cur, q);
                int width = S.BoneWidth(q, slot);
                Assert.Equal(Mask + (width + 7) / 8, len);
                Assert.Equal(cur, recon);
            }
        }
    }

    [Theory]
    [InlineData(BitQuality.VeryLow)]
    [InlineData(BitQuality.Low)]
    [InlineData(BitQuality.Medium)]
    [InlineData(BitQuality.High)]
    public void AllBonesChanged_TailStable(BitQuality q)
    {
        var rng = new Random(4242 + (int)q);
        byte[] kf = S.MakeRealisticPayload(q, rng);
        byte[] cur = (byte[])kf.Clone();
        for (int s = 0; s < S.BoneCount; s++) S.FlipBone(cur, q, s);
        var (len, recon) = S.BuildApply(kf, cur, q);
        // All 51 bones packed contiguously == the whole rotation bitstream, plus the mask.
        Assert.Equal(Mask + S.RotBytes(q), len);
        Assert.Equal(cur, recon);
    }

    [Theory]
    [InlineData(BitQuality.VeryLow)]
    [InlineData(BitQuality.Low)]
    [InlineData(BitQuality.Medium)]
    [InlineData(BitQuality.High)]
    public void AllByteFieldsChanged_BonesStable(BitQuality q)
    {
        var rng = new Random(4343 + (int)q);
        byte[] kf = S.MakeRealisticPayload(q, rng);
        byte[] cur = (byte[])kf.Clone();
        cur[0] ^= 0xFF;                       // position
        cur[S.ScaleOffset(q)] ^= 0xFF;        // scale
        cur[S.BodyRotOffset(q)] ^= 0xFF;      // body rot
        cur[S.HipsDeltaOffset(q)] ^= 0xFF;    // hips delta
        cur[S.HipsRotOffset(q)] ^= 0xFF;      // hips rot
        if (S.EndEffectorBytes(q) > 0) cur[S.EndEffectorOffset(q)] ^= 0xFF;   // effector block (High only)
        var (len, recon) = S.BuildApply(kf, cur, q);
        Assert.Equal(Mask + S.PosBytes(q) + 2 + 7 + 6 + 7 + S.EndEffectorBytes(q), len);
        Assert.Equal(cur, recon);
    }

    [Theory]
    [InlineData(BitQuality.VeryLow)]
    [InlineData(BitQuality.Low)]
    [InlineData(BitQuality.Medium)]
    [InlineData(BitQuality.High)]
    public void EverythingChanged_EqualsMaskPlusPayload(BitQuality q)
    {
        var rng = new Random(4444 + (int)q);
        byte[] kf = S.MakeRealisticPayload(q, rng);
        byte[] cur = (byte[])kf.Clone();
        cur[0] ^= 0xFF;
        cur[S.ScaleOffset(q)] ^= 0xFF;
        cur[S.BodyRotOffset(q)] ^= 0xFF;
        cur[S.HipsDeltaOffset(q)] ^= 0xFF;
        cur[S.HipsRotOffset(q)] ^= 0xFF;
        for (int s = 0; s < S.BoneCount; s++) S.FlipBone(cur, q, s);
        if (S.EndEffectorBytes(q) > 0) S.FlipEndEffector(cur, q);
        var (len, recon) = S.BuildApply(kf, cur, q);
        Assert.Equal(Mask + S.PayloadSize(q), len);
        Assert.Equal(cur, recon);
    }

    [Theory]
    [InlineData(BitQuality.High, 1)]
    [InlineData(BitQuality.High, 3)]
    [InlineData(BitQuality.High, 7)]
    [InlineData(BitQuality.Medium, 5)]
    [InlineData(BitQuality.Low, 10)]
    [InlineData(BitQuality.VeryLow, 20)]
    public void KBonesChanged_ContiguousPacking(BitQuality q, int k)
    {
        var rng = new Random(k * 101 + (int)q);
        byte[] kf = S.MakeRealisticPayload(q, rng);
        byte[] cur = (byte[])kf.Clone();
        // Pick k distinct slots.
        var slots = new HashSet<int>();
        while (slots.Count < k) slots.Add(rng.Next(S.BoneCount));
        int bits = 0;
        foreach (int s in slots) { S.FlipBone(cur, q, s); bits += S.BoneWidth(q, s); }
        var (len, recon) = S.BuildApply(kf, cur, q);
        // Contiguous packing: mask + ceil(sum of changed bone widths / 8).
        Assert.Equal(Mask + (bits + 7) / 8, len);
        Assert.Equal(cur, recon);
    }
}
