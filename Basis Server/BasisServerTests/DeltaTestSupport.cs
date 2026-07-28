using Basis.Network.Core.Compression;
using Xunit;
using BitQuality = Basis.Network.Core.Compression.BasisAvatarBitPacking.BitQuality;

namespace BasisServerTests;

/// <summary>
/// Shared helpers for the avatar delta codec tests: payload construction (random and realistic),
/// field/bone geometry accessors that mirror <see cref="BasisAvatarDeltaCompression"/>'s private
/// layout, and build/apply round-trip assertions.
/// </summary>
public static class DeltaTestSupport
{
    public static readonly BitQuality[] AllQualities =
        { BitQuality.VeryLow, BitQuality.Low, BitQuality.Medium, BitQuality.High };

    public const int BoneCount = BasisBoneRotationCompression.SyncBoneCount; // 51
    public static int PosBytes(BitQuality q) => BasisAvatarBitPacking.PositionBytes(q);
    public static int BoneBaseBit(BitQuality q) => PosBytes(q) * 8;

    public static int PayloadSize(BitQuality q) => BasisAvatarBitPacking.ConvertToSize(q);
    public static int RotBytes(BitQuality q) => BasisBoneRotationCompression.RotationBytes(q);
    public static int TailStart(BitQuality q) => PosBytes(q) + RotBytes(q);
    public static int ScaleOffset(BitQuality q) => TailStart(q);
    public static int BodyRotOffset(BitQuality q) => TailStart(q) + BasisBoneRotationCompression.WriteScale;
    public static int HipsDeltaOffset(BitQuality q) => BodyRotOffset(q) + BasisBoneRotationCompression.WriteRotation;
    public static int HipsRotOffset(BitQuality q) => HipsDeltaOffset(q) + BasisBoneRotationCompression.WriteHipsDelta;
    public static int EndEffectorOffset(BitQuality q) => TailStart(q) + BasisBoneRotationCompression.TailBytes;
    public static int EndEffectorBytes(BitQuality q) => BasisBoneRotationCompression.EndEffectorBytes(q);

    /// <summary>Flip every byte of the end-effector block (High only), guaranteeing it differs.</summary>
    public static void FlipEndEffector(byte[] payload, BitQuality q)
    {
        int off = EndEffectorOffset(q), n = EndEffectorBytes(q);
        for (int i = 0; i < n; i++) payload[off + i] ^= 0xFF;
    }

    public static byte[] Bpc(BitQuality q) => BasisBoneRotationCompression.GetBpcTable(q);
    public static int BoneWidth(BitQuality q, int slot) => 2 + 3 * Bpc(q)[slot];

    public static int[] BoneBitOffsets(BitQuality q)
    {
        var bpc = Bpc(q);
        var offs = new int[bpc.Length];
        BasisBoneRotationCompression.ComputeBitOffsets(bpc, offs);
        return offs;
    }

    public static ulong GetBone(byte[] payload, BitQuality q, int slot)
    {
        int pos = BoneBaseBit(q) + BoneBitOffsets(q)[slot];
        return BasisBoneRotationCompression.ReadBits(payload, ref pos, BoneWidth(q, slot));
    }

    public static void SetBone(byte[] payload, BitQuality q, int slot, ulong value)
    {
        int offset = BoneBaseBit(q) + BoneBitOffsets(q)[slot];
        int width = BoneWidth(q, slot);
        for (int i = 0; i < width; i++)
        {
            int b = offset + i, bytePos = b >> 3, bit = b & 7;
            if (((value >> i) & 1UL) != 0) payload[bytePos] |= (byte)(1 << bit);
            else payload[bytePos] &= (byte)~(1 << bit);
        }
    }

    /// <summary>Flip every bit of a bone's field, guaranteeing it differs from its current value.</summary>
    public static void FlipBone(byte[] payload, BitQuality q, int slot)
    {
        ulong maxv = (1UL << BoneWidth(q, slot)) - 1UL;
        SetBone(payload, q, slot, GetBone(payload, q, slot) ^ maxv);
    }

    /// <summary>Valid quantized payload: random position/tail bytes + random bone bits (padding stays 0).</summary>
    public static byte[] MakePayload(BitQuality q, Random rng)
    {
        int size = PayloadSize(q);
        var arr = new byte[size];
        rng.NextBytes(new Span<byte>(arr, 0, PosBytes(q)));
        rng.NextBytes(new Span<byte>(arr, TailStart(q), BasisBoneRotationCompression.TailBytes));
        if (EndEffectorBytes(q) > 0) rng.NextBytes(new Span<byte>(arr, EndEffectorOffset(q), EndEffectorBytes(q)));
        var bpc = Bpc(q);
        var offs = BoneBitOffsets(q);
        for (int s = 0; s < bpc.Length; s++)
        {
            int width = 2 + 3 * bpc[s];
            ulong maxv = (1UL << width) - 1UL;
            BasisBoneRotationCompression.WriteBits(arr, BoneBaseBit(q) + offs[s], (ulong)rng.NextInt64() & maxv, width);
        }
        return arr;
    }

    /// <summary>Realistic payload: bones are true smallest-three encodings of random unit quaternions.</summary>
    public static byte[] MakeRealisticPayload(BitQuality q, Random rng)
    {
        int size = PayloadSize(q);
        var arr = new byte[size];
        rng.NextBytes(new Span<byte>(arr, 0, PosBytes(q)));
        rng.NextBytes(new Span<byte>(arr, TailStart(q), BasisBoneRotationCompression.TailBytes));
        if (EndEffectorBytes(q) > 0) rng.NextBytes(new Span<byte>(arr, EndEffectorOffset(q), EndEffectorBytes(q)));
        var bpc = Bpc(q);
        var offs = BoneBitOffsets(q);
        for (int s = 0; s < bpc.Length; s++)
        {
            var (x, y, z, w) = RandomQuat(rng);
            ulong packed = BasisBoneRotationCompression.EncodeSmallestThree(x, y, z, w, bpc[s], BasisBoneRotationCompression.MAX_COMPONENT[s]);
            BasisBoneRotationCompression.WriteBits(arr, BoneBaseBit(q) + offs[s], packed, 2 + 3 * bpc[s]);
        }
        return arr;
    }

    public static (float x, float y, float z, float w) RandomQuat(Random rng)
    {
        float x = (float)(rng.NextDouble() * 2 - 1);
        float y = (float)(rng.NextDouble() * 2 - 1);
        float z = (float)(rng.NextDouble() * 2 - 1);
        float w = (float)(rng.NextDouble() * 2 - 1);
        float len = MathF.Sqrt(x * x + y * y + z * z + w * w);
        return len < 1e-6f ? (0f, 0f, 0f, 1f) : (x / len, y / len, z / len, w / len);
    }

    /// <summary>Builds a delta, verifies the length probe agrees and the bound holds, then applies it.</summary>
    public static (int len, byte[] recon) BuildApply(byte[] kf, byte[] cur, BitQuality q)
    {
        var dst = new byte[BasisAvatarDeltaCompression.MaxDeltaSize(q)];
        int len = BasisAvatarDeltaCompression.BuildDelta(kf, cur, q, dst, 0);
        Assert.True(len > 0, "BuildDelta returned non-positive length");
        Assert.True(len <= BasisAvatarDeltaCompression.MaxDeltaSize(q), "delta exceeded MaxDeltaSize");
        Assert.Equal(len, BasisAvatarDeltaCompression.DeltaBodyLength(dst, 0, len, q));
        var recon = new byte[PayloadSize(q)];
        Assert.True(BasisAvatarDeltaCompression.TryApplyDelta(kf, dst, 0, len, q, recon), "TryApplyDelta rejected a valid delta");
        return (len, recon);
    }

    public static void AssertRoundTrip(byte[] kf, byte[] cur, BitQuality q)
    {
        var (_, recon) = BuildApply(kf, cur, q);
        Assert.Equal(cur.AsSpan(0, PayloadSize(q)).ToArray(), recon.AsSpan(0, PayloadSize(q)).ToArray());
    }
}
