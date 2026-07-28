using Basis.Network.Core.Compression;
using Basis.Scripts.Networking.Compression;
using BasisNetworkCore;
using Xunit;
using BitQuality = Basis.Network.Core.Compression.BasisAvatarBitPacking.BitQuality;

namespace BasisServerTests;

/// <summary>
/// Tests for the core wire primitives: smallest-three quaternion compression and its bitstream,
/// the ranged ushort float codec, MathExtensions, packet sequence validation, and the raw
/// position read/write extensions. Avatar bit-packing/delta codecs are covered elsewhere.
/// </summary>
public class CorePrimitiveCompressionTests
{
    private static readonly BitQuality[] AllQualities =
        { BitQuality.VeryLow, BitQuality.Low, BitQuality.Medium, BitQuality.High };

    // ────────────────────────────────────────────────────────────
    //  Smallest-three quaternion compression
    // ────────────────────────────────────────────────────────────

    private const float S2 = 0.70710678f;   // sin/cos 45°
    private const float S225 = 0.38268343f; // sin 22.5°
    private const float C225 = 0.92387953f; // cos 22.5°
    private const float S60 = 0.8660254f;   // sin 60°

    private static (float x, float y, float z, float w)[] CanonicalQuats() => new[]
    {
        (0f, 0f, 0f, 1f),
        (0f, 0f, 0f, -1f),
        (1f, 0f, 0f, 0f),
        (0f, 1f, 0f, 0f),
        (0f, 0f, 1f, 0f),
        (-1f, 0f, 0f, 0f),
        (0f, -1f, 0f, 0f),
        (0f, 0f, -1f, 0f),
        (S2, 0f, 0f, S2),
        (0f, S2, 0f, S2),
        (0f, 0f, S2, S2),
        (-S2, 0f, 0f, S2),
        (S2, 0f, 0f, -S2),
        (S2, -S2, 0f, 0f),
        (S225, 0f, 0f, C225),
        (0f, -S225, 0f, C225),
        (S60, 0f, 0f, 0.5f),
        (0.5f, 0.5f, 0.5f, 0.5f),
        (-0.5f, -0.5f, -0.5f, -0.5f),
        (0.5f, -0.5f, 0.5f, -0.5f),
        Normalize(0.7072f, 0.7070f, 0f, 0f),
        Normalize(0.01f, -0.01f, 0.01f, 0.9999f),
        Normalize(0.577f, 0.577f, 0.577f, 0.05f),
    };

    private static (float x, float y, float z, float w) Normalize(float x, float y, float z, float w)
    {
        float len = MathF.Sqrt(x * x + y * y + z * z + w * w);
        return (x / len, y / len, z / len, w / len);
    }

    private static double OneMinusAbsDot(
        (float x, float y, float z, float w) a,
        float bx, float by, float bz, float bw)
    {
        double dot = (double)a.x * bx + (double)a.y * by + (double)a.z * bz + (double)a.w * bw;
        return 1.0 - Math.Abs(dot);
    }

    /// <summary>Worst-case 1-|dot| bound for quantization at half-step h, with float slop.</summary>
    private static double Tolerance(int bpc, float maxRange)
    {
        double h = maxRange / ((1 << bpc) - 1);
        return 12.0 * h * h + 2e-6;
    }

    private static void AssertEncodeDecodeClose(
        (float x, float y, float z, float w) q, int bpc, float maxRange)
    {
        ulong packed = BasisBoneRotationCompression.EncodeSmallestThree(q.x, q.y, q.z, q.w, bpc, maxRange);
        BasisBoneRotationCompression.DecodeSmallestThree(packed, bpc, out float dx, out float dy, out float dz, out float dw, maxRange);

        double norm = Math.Sqrt((double)dx * dx + (double)dy * dy + (double)dz * dz + (double)dw * dw);
        Assert.True(Math.Abs(norm - 1.0) < 1e-3, $"decoded quaternion not unit length: {norm}");

        double err = OneMinusAbsDot(q, dx, dy, dz, dw);
        double tol = Tolerance(bpc, maxRange);
        Assert.True(err <= tol, $"bpc={bpc} q=({q.x},{q.y},{q.z},{q.w}) err={err} tol={tol}");
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(12)]
    public void SmallestThree_RoundTrip_CanonicalQuaternions(int bpc)
    {
        foreach (var q in CanonicalQuats())
            AssertEncodeDecodeClose(q, bpc, BasisBoneRotationCompression.InvSqrt2);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(12)]
    public void SmallestThree_RoundTrip_RandomSweep(int bpc)
    {
        var rng = new Random(9000 + bpc);
        for (int i = 0; i < 400; i++)
        {
            var q = DeltaTestSupport.RandomQuat(rng);
            AssertEncodeDecodeClose(q, bpc, BasisBoneRotationCompression.InvSqrt2);
        }
    }

    [Fact]
    public void SmallestThree_HemisphereEquivalence_PackedBitsIdentical()
    {
        var rng = new Random(777);
        int[] bpcs = { 5, 8, 12 };
        foreach (int bpc in bpcs)
        {
            foreach (var q in CanonicalQuats())
            {
                ulong a = BasisBoneRotationCompression.EncodeSmallestThree(q.x, q.y, q.z, q.w, bpc);
                ulong b = BasisBoneRotationCompression.EncodeSmallestThree(-q.x, -q.y, -q.z, -q.w, bpc);
                Assert.Equal(a, b);
            }
            for (int i = 0; i < 200; i++)
            {
                var (x, y, z, w) = DeltaTestSupport.RandomQuat(rng);
                ulong a = BasisBoneRotationCompression.EncodeSmallestThree(x, y, z, w, bpc);
                ulong b = BasisBoneRotationCompression.EncodeSmallestThree(-x, -y, -z, -w, bpc);
                Assert.Equal(a, b);
            }
        }
    }

    [Fact]
    public void SmallestThree_RestrictedRange_RoundTripsSmallRotations()
    {
        const int bpc = 8;
        const float maxRange = 0.5f;
        var axes = new[]
        {
            (1f, 0f, 0f), (0f, 1f, 0f), (0f, 0f, 1f),
            (0.57735f, 0.57735f, 0.57735f), (S2, -S2, 0f), (0f, 0.6f, -0.8f),
        };
        for (int deg = 5; deg <= 50; deg += 5)
        {
            float half = deg * MathF.PI / 360f;
            float s = MathF.Sin(half);
            float c = MathF.Cos(half);
            foreach (var (ax, ay, az) in axes)
                AssertEncodeDecodeClose((ax * s, ay * s, az * s, c), bpc, maxRange);
        }
    }

    [Fact]
    public void SmallestThree_OutOfRangeComponents_ClampToMaxRange()
    {
        // 90° about X has non-dropped magnitude 0.7071, beyond the 0.5 range: the encoder
        // clamps, so the decode lands on the nearest representable pose (60° about X).
        const int bpc = 10;
        const float maxRange = 0.5f;
        ulong packed = BasisBoneRotationCompression.EncodeSmallestThree(S2, 0f, 0f, S2, bpc, maxRange);
        BasisBoneRotationCompression.DecodeSmallestThree(packed, bpc, out float x, out float y, out float z, out float w, maxRange);

        Assert.True(Math.Abs(x - S60) <= 1e-3, $"x={x}");
        Assert.True(Math.Abs(w - 0.5f) <= 1e-3, $"w={w}");
        Assert.True(Math.Abs(y) <= 5e-3, $"y={y}");
        Assert.True(Math.Abs(z) <= 5e-3, $"z={z}");
        double norm = Math.Sqrt((double)x * x + (double)y * y + (double)z * z + (double)w * w);
        Assert.True(Math.Abs(norm - 1.0) < 1e-3);
    }

    // ────────────────────────────────────────────────────────────
    //  Bitstream
    // ────────────────────────────────────────────────────────────

    private static ulong NextULong(Random rng)
    {
        ulong hi = (ulong)(rng.NextInt64() & 0xFFFFFFFFL);
        ulong lo = (ulong)(rng.NextInt64() & 0xFFFFFFFFL);
        return (hi << 32) | lo;
    }

    private static ulong WidthMask(int width) => width == 64 ? ulong.MaxValue : (1UL << width) - 1UL;

    [Fact]
    public void WriteBits_ReadBits_RandomVariableWidthFields_RoundTrip()
    {
        var rng = new Random(4242);
        const int fieldCount = 300;
        var widths = new int[fieldCount];
        var values = new ulong[fieldCount];
        var buffer = new byte[3000];

        int bitPos = 0;
        for (int i = 0; i < fieldCount; i++)
        {
            widths[i] = 1 + rng.Next(64);
            values[i] = NextULong(rng) & WidthMask(widths[i]);
            BasisBoneRotationCompression.WriteBits(buffer, bitPos, values[i], widths[i]);
            bitPos += widths[i];
        }
        Assert.True(bitPos <= buffer.Length * 8);

        int readPos = 0;
        for (int i = 0; i < fieldCount; i++)
        {
            ulong got = BasisBoneRotationCompression.ReadBits(buffer, ref readPos, widths[i]);
            Assert.Equal(values[i], got);
        }
        Assert.Equal(bitPos, readPos);
    }

    [Fact]
    public void WriteBits_IsLsbFirst_AndLeavesNeighborsUntouched()
    {
        var buf = new byte[4];
        BasisBoneRotationCompression.WriteBits(buf, 6, 0b101UL, 3);
        Assert.Equal((byte)0x40, buf[0]);
        Assert.Equal((byte)0x01, buf[1]);
        Assert.Equal((byte)0x00, buf[2]);
        Assert.Equal((byte)0x00, buf[3]);
        int pos = 6;
        Assert.Equal(0b101UL, BasisBoneRotationCompression.ReadBits(buf, ref pos, 3));
        Assert.Equal(9, pos);

        var wide = new byte[16];
        BasisBoneRotationCompression.WriteBits(wide, 5, ulong.MaxValue, 64);
        Assert.Equal((byte)0xE0, wide[0]);
        for (int i = 1; i <= 7; i++) Assert.Equal((byte)0xFF, wide[i]);
        Assert.Equal((byte)0x1F, wide[8]);
        for (int i = 9; i < wide.Length; i++) Assert.Equal((byte)0x00, wide[i]);
        int widePos = 5;
        Assert.Equal(ulong.MaxValue, BasisBoneRotationCompression.ReadBits(wide, ref widePos, 64));
        Assert.Equal(69, widePos);
    }

    // ────────────────────────────────────────────────────────────
    //  Bone tables and packet sizing
    // ────────────────────────────────────────────────────────────

    [Fact]
    public void ComputeBitOffsets_MatchesRotationBytes_ForAllQualities()
    {
        foreach (var q in AllQualities)
        {
            byte[] bpc = BasisBoneRotationCompression.GetBpcTable(q);
            var offsets = new int[bpc.Length];
            int totalBits = BasisBoneRotationCompression.ComputeBitOffsets(bpc, offsets);

            Assert.Equal(0, offsets[0]);
            int expected = 0;
            for (int i = 0; i < bpc.Length; i++)
            {
                Assert.Equal(expected, offsets[i]);
                expected += 2 + 3 * bpc[i];
            }
            Assert.Equal(expected, totalBits);
            Assert.Equal((totalBits + 7) >> 3, BasisBoneRotationCompression.RotationBytes(q));
        }
    }

    [Fact]
    public void BoneOrderTables_AreConsistentInverses()
    {
        int[] order = BasisBoneRotationCompression.BONE_WRITE_ORDER;
        int[] toSlot = BasisBoneRotationCompression.BONE_TO_SLOT;

        Assert.Equal(BasisBoneRotationCompression.SyncBoneCount, order.Length);
        Assert.Equal(51, order.Length);
        Assert.Equal(55, toSlot.Length);

        int[] expectedBones = Enumerable.Range(1, 54).Where(v => v != 21 && v != 22 && v != 23).ToArray();
        Assert.Equal(expectedBones, order.OrderBy(v => v).ToArray());

        for (int slot = 0; slot < order.Length; slot++)
            Assert.Equal(slot, toSlot[order[slot]]);
        Assert.Equal(-1, toSlot[0]);
        Assert.Equal(-1, toSlot[21]);
        Assert.Equal(-1, toSlot[22]);
        Assert.Equal(-1, toSlot[23]);
    }

    [Fact]
    public void QualityTables_LengthsAndRanges_AreValid()
    {
        byte[][] tables =
        {
            BasisBoneRotationCompression.BPC_HIGH,
            BasisBoneRotationCompression.BPC_MEDIUM,
            BasisBoneRotationCompression.BPC_LOW,
            BasisBoneRotationCompression.BPC_VERY_LOW,
        };
        foreach (byte[] table in tables)
        {
            Assert.Equal(BasisBoneRotationCompression.SyncBoneCount, table.Length);
            Assert.All(table, b => Assert.InRange(b, (byte)2, (byte)12));
        }

        float[] maxComp = BasisBoneRotationCompression.MAX_COMPONENT;
        Assert.Equal(BasisBoneRotationCompression.SyncBoneCount, maxComp.Length);
        Assert.All(maxComp, m => Assert.InRange(m, 1e-3f, BasisBoneRotationCompression.InvSqrt2 + 1e-6f));

        Assert.Same(BasisBoneRotationCompression.BPC_HIGH, BasisBoneRotationCompression.GetBpcTable(BitQuality.High));
        Assert.Same(BasisBoneRotationCompression.BPC_MEDIUM, BasisBoneRotationCompression.GetBpcTable(BitQuality.Medium));
        Assert.Same(BasisBoneRotationCompression.BPC_LOW, BasisBoneRotationCompression.GetBpcTable(BitQuality.Low));
        Assert.Same(BasisBoneRotationCompression.BPC_VERY_LOW, BasisBoneRotationCompression.GetBpcTable(BitQuality.VeryLow));
    }

    [Fact]
    public void PacketSizes_ArePinned_WireCompatibility()
    {
        // Current v42 wire sizes; a change here is a protocol break and must be deliberate.
        Assert.Equal(78, BasisBoneRotationCompression.RotationBytes(BitQuality.VeryLow));
        Assert.Equal(97, BasisBoneRotationCompression.RotationBytes(BitQuality.Low));
        Assert.Equal(122, BasisBoneRotationCompression.RotationBytes(BitQuality.Medium));
        Assert.Equal(163, BasisBoneRotationCompression.RotationBytes(BitQuality.High));

        Assert.Equal(0, BasisBoneRotationCompression.EndEffectorBytes(BitQuality.VeryLow));
        Assert.Equal(0, BasisBoneRotationCompression.EndEffectorBytes(BitQuality.Low));
        Assert.Equal(0, BasisBoneRotationCompression.EndEffectorBytes(BitQuality.Medium));
        Assert.Equal(BasisBoneRotationCompression.EndEffectorBlockBytes,
            BasisBoneRotationCompression.EndEffectorBytes(BitQuality.High));

        foreach (var q in AllQualities)
        {
            int expected = BasisAvatarBitPacking.PositionBytes(q)
                + BasisBoneRotationCompression.RotationBytes(q)
                + BasisBoneRotationCompression.TailBytes
                + BasisBoneRotationCompression.EndEffectorBytes(q);
            Assert.Equal(expected, BasisBoneRotationCompression.ConvertToSize(q));
        }
        Assert.Equal(109, BasisBoneRotationCompression.ConvertToSize(BitQuality.VeryLow));
        Assert.Equal(128, BasisBoneRotationCompression.ConvertToSize(BitQuality.Low));
        Assert.Equal(153, BasisBoneRotationCompression.ConvertToSize(BitQuality.Medium));
        Assert.Equal(232, BasisBoneRotationCompression.ConvertToSize(BitQuality.High));
    }

    // ────────────────────────────────────────────────────────────
    //  BasisRangedUshortFloatData
    // ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(-1f, 1f, 0.001f)]
    [InlineData(0f, 1f, 0.01f)]
    [InlineData(-3.1415927f, 3.1415927f, 0.001f)]
    [InlineData(0f, 10f, 1f)]
    [InlineData(-50f, 50f, 0.1f)]
    public void RangedFloat_RoundTrips_WithinHalfPrecision(float min, float max, float precision)
    {
        var codec = new BasisNetworkPrimitiveCompression.BasisRangedUshortFloatData(min, max, precision);
        float tol = 0.5f * precision + 0.011f * precision;
        for (int i = 0; i <= 1000; i++)
        {
            float v = min + (max - min) * (i / 1000f);
            ushort compressed = codec.Compress(v);
            Assert.True(compressed <= codec.Mask);
            float back = codec.Decompress(compressed);
            Assert.True(back >= min && back <= max, $"decompressed {back} escaped [{min},{max}]");
            Assert.True(Math.Abs(back - v) <= tol, $"v={v} back={back} tol={tol}");
        }
        Assert.Equal(min, codec.Decompress(codec.Compress(min)));
    }

    [Fact]
    public void RangedFloat_OutOfRangeInputs_ClampToBounds()
    {
        var codec = new BasisNetworkPrimitiveCompression.BasisRangedUshortFloatData(-1f, 1f, 0.001f);
        Assert.Equal(codec.Compress(-1f), codec.Compress(-100f));
        Assert.Equal(codec.Compress(1f), codec.Compress(100f));
        Assert.Equal((ushort)0, codec.Compress(float.NegativeInfinity));
        Assert.Equal(codec.Compress(1f), codec.Compress(float.PositiveInfinity));

        Assert.Equal(-1f, codec.Decompress(0));
        Assert.True(codec.Decompress(codec.Mask) <= 1f);
        Assert.True(codec.Decompress(ushort.MaxValue) <= 1f);
        float prev = codec.Decompress(0);
        for (ushort code = 1; code < 100; code++)
        {
            float cur = codec.Decompress(code);
            Assert.True(cur > prev, "Decompress should be monotonic over in-range codes");
            prev = cur;
        }
    }

    [Theory]
    [InlineData(-1f, 1f, 0.001f, 11, (ushort)2047)]
    [InlineData(0f, 1f, 0.01f, 7, (ushort)127)]
    [InlineData(0f, 10f, 1f, 4, (ushort)15)]
    [InlineData(0f, 16f, 1f, 5, (ushort)31)]
    public void RangedFloat_RequiredBitsAndMask_Pinned(float min, float max, float precision, int bits, ushort mask)
    {
        var codec = new BasisNetworkPrimitiveCompression.BasisRangedUshortFloatData(min, max, precision);
        Assert.Equal(bits, codec.RequiredBits);
        Assert.Equal(mask, codec.Mask);
    }

    [Theory]
    [InlineData(0u, 0)]
    [InlineData(1u, 0)]
    [InlineData(2u, 1)]
    [InlineData(3u, 1)]
    [InlineData(4u, 2)]
    [InlineData(7u, 2)]
    [InlineData(8u, 3)]
    [InlineData(255u, 7)]
    [InlineData(256u, 8)]
    [InlineData(1023u, 9)]
    [InlineData(1024u, 10)]
    [InlineData(65535u, 15)]
    [InlineData(65536u, 16)]
    [InlineData(2147483648u, 31)]
    [InlineData(4294967295u, 31)]
    public void FastLog2_Pins(uint value, int expected)
    {
        Assert.Equal(expected, BasisNetworkPrimitiveCompression.BasisRangedUshortFloatData.FastLog2(value));
    }

    // ────────────────────────────────────────────────────────────
    //  MathExtensions and support structs
    // ────────────────────────────────────────────────────────────

    [Fact]
    public void Clamp_Float_Edges()
    {
        Assert.Equal(5f, MathExtensions.Clamp(5f, 0f, 10f));
        Assert.Equal(0f, MathExtensions.Clamp(-5f, 0f, 10f));
        Assert.Equal(10f, MathExtensions.Clamp(15f, 0f, 10f));
        Assert.Equal(0f, MathExtensions.Clamp(0f, 0f, 10f));
        Assert.Equal(10f, MathExtensions.Clamp(10f, 0f, 10f));
        Assert.Equal(7f, MathExtensions.Clamp(3f, 7f, 7f));
        Assert.Equal(10f, MathExtensions.Clamp(float.PositiveInfinity, 0f, 10f));
        Assert.Equal(0f, MathExtensions.Clamp(float.NegativeInfinity, 0f, 10f));
        Assert.True(float.IsNaN(MathExtensions.Clamp(float.NaN, 0f, 10f)));
        Assert.Equal(-2f, MathExtensions.Clamp(-2f, -3f, -1f));
    }

    [Fact]
    public void Clamp_Int_Edges()
    {
        Assert.Equal(5, MathExtensions.Clamp(5, 0, 10));
        Assert.Equal(0, MathExtensions.Clamp(-5, 0, 10));
        Assert.Equal(10, MathExtensions.Clamp(15, 0, 10));
        Assert.Equal(0, MathExtensions.Clamp(0, 0, 10));
        Assert.Equal(10, MathExtensions.Clamp(10, 0, 10));
        Assert.Equal(7, MathExtensions.Clamp(int.MinValue, 7, 7));
        Assert.Equal(int.MaxValue, MathExtensions.Clamp(int.MaxValue, int.MinValue, int.MaxValue));
        Assert.Equal(-1, MathExtensions.Clamp(int.MaxValue, -3, -1));
    }

    [Fact]
    public void Clamp_Double_Edges()
    {
        Assert.Equal(5.0, MathExtensions.Clamp(5.0, 0.0, 10.0));
        Assert.Equal(0.0, MathExtensions.Clamp(-5.0, 0.0, 10.0));
        Assert.Equal(10.0, MathExtensions.Clamp(15.0, 0.0, 10.0));
        Assert.Equal(10.0, MathExtensions.Clamp(double.PositiveInfinity, 0.0, 10.0));
        Assert.Equal(0.0, MathExtensions.Clamp(double.NegativeInfinity, 0.0, 10.0));
        Assert.True(double.IsNaN(MathExtensions.Clamp(double.NaN, 0.0, 10.0)));
    }

    [Fact]
    public void Vector3_Operators_And_SquaredMagnitude()
    {
        var a = new Vector3(1f, 2f, 3f);
        var b = new Vector3(-4f, 0.5f, 10f);

        var sum = a + b;
        Assert.Equal(-3f, sum.x);
        Assert.Equal(2.5f, sum.y);
        Assert.Equal(13f, sum.z);

        var diff = a - b;
        Assert.Equal(5f, diff.x);
        Assert.Equal(1.5f, diff.y);
        Assert.Equal(-7f, diff.z);

        Assert.Equal(14f, a.SquaredMagnitude());
        Assert.Equal(0f, new Vector3(0f, 0f, 0f).SquaredMagnitude());
    }

    [Fact]
    public void Quaternion_Constructor_SetsComponents()
    {
        var q = new Quaternion(0.1f, -0.2f, 0.3f, -0.4f);
        Assert.Equal(0.1f, q.value.x);
        Assert.Equal(-0.2f, q.value.y);
        Assert.Equal(0.3f, q.value.z);
        Assert.Equal(-0.4f, q.value.w);
    }

    // ────────────────────────────────────────────────────────────
    //  BasisPacketUtil sequence validation
    // ────────────────────────────────────────────────────────────

    [Fact]
    public void IsNewer_HalfWindowSemantics_WithWraparound()
    {
        Assert.True(BasisPacketUtil.IsNewer(5, 4));
        Assert.False(BasisPacketUtil.IsNewer(4, 5));
        Assert.True(BasisPacketUtil.IsNewer(0, 255));
        Assert.False(BasisPacketUtil.IsNewer(255, 0));
        Assert.True(BasisPacketUtil.IsNewer(127, 0));
        Assert.False(BasisPacketUtil.IsNewer(128, 0));
        Assert.True(BasisPacketUtil.IsNewer(1, 200));
        Assert.False(BasisPacketUtil.IsNewer(200, 1));
        // Exactly opposite sequence numbers are mutually "not newer".
        Assert.False(BasisPacketUtil.IsNewer(0, 128));
        Assert.False(BasisPacketUtil.IsNewer(129, 1));
        Assert.False(BasisPacketUtil.IsNewer(1, 129));
        // Equal sequences count as "newer" here; ValidatePacket adds the inequality check.
        Assert.True(BasisPacketUtil.IsNewer(42, 42));
        Assert.False(BasisPacketUtil.ValidatePacket(42, 42));
    }

    [Fact]
    public void ValidatePacket_Exhaustive_MatchesHalfWindowModel()
    {
        for (int oldSeq = 0; oldSeq <= 255; oldSeq++)
        {
            for (int newSeq = 0; newSeq <= 255; newSeq++)
            {
                byte delta = (byte)(newSeq - oldSeq);
                bool expected = delta >= 1 && delta <= 127;
                Assert.Equal(expected, BasisPacketUtil.ValidatePacket((byte)newSeq, (byte)oldSeq));
            }
        }
    }

    // ────────────────────────────────────────────────────────────
    //  BasisNetworkCompressionExtensions
    // ────────────────────────────────────────────────────────────

    [Fact]
    public void WritePosition_ReadPosition_RoundTripAtOffsetZero()
    {
        var buffer = new byte[12];
        int offset = 0;
        var pos = new Vector3(1.5f, -2.25f, 3.75f);
        BasisNetworkCompressionExtensions.WritePosition(pos, ref buffer, ref offset);
        Assert.Equal(12, offset);

        Vector3 back = BasisNetworkCompressionExtensions.ReadPosition(ref buffer);
        Assert.Equal(pos.x, back.x);
        Assert.Equal(pos.y, back.y);
        Assert.Equal(pos.z, back.z);
    }

    [Fact]
    public void WritePosition_AdvancesOffset_ReadPosition_AlwaysReadsFromStart()
    {
        var buffer = new byte[24];
        int offset = 0;
        var first = new Vector3(10f, 20f, 30f);
        var second = new Vector3(-1f, -2f, -3f);
        BasisNetworkCompressionExtensions.WritePosition(first, ref buffer, ref offset);
        Assert.Equal(12, offset);
        BasisNetworkCompressionExtensions.WritePosition(second, ref buffer, ref offset);
        Assert.Equal(24, offset);

        Assert.Equal(second.x, BitConverter.ToSingle(buffer, 12));
        Assert.Equal(second.y, BitConverter.ToSingle(buffer, 16));
        Assert.Equal(second.z, BitConverter.ToSingle(buffer, 20));

        // ReadPosition has no offset parameter: it always decodes the vector at buffer start.
        Vector3 back = BasisNetworkCompressionExtensions.ReadPosition(ref buffer);
        Assert.Equal(first.x, back.x);
        Assert.Equal(first.y, back.y);
        Assert.Equal(first.z, back.z);
    }

    [Fact]
    public void WritePosition_PreservesSpecialFloatBits()
    {
        var buffer = new byte[12];
        int offset = 0;
        var pos = new Vector3(-0f, float.NaN, float.PositiveInfinity);
        BasisNetworkCompressionExtensions.WritePosition(pos, ref buffer, ref offset);
        Vector3 back = BasisNetworkCompressionExtensions.ReadPosition(ref buffer);
        Assert.Equal(BitConverter.SingleToInt32Bits(-0f), BitConverter.SingleToInt32Bits(back.x));
        Assert.Equal(BitConverter.SingleToInt32Bits(float.NaN), BitConverter.SingleToInt32Bits(back.y));
        Assert.Equal(BitConverter.SingleToInt32Bits(float.PositiveInfinity), BitConverter.SingleToInt32Bits(back.z));
    }
}
