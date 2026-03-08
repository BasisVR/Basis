using System;
using Basis.Network.Core.Compression;
using Basis.Scripts.Networking.Compression;
using Xunit;

namespace BasisServerTests
{
    public class PrimitiveCompressionTests
    {
        [Fact]
        public void CompressDecompress_MidRange_ReturnsApproxValue()
        {
            var data = new BasisNetworkPrimitiveCompression.BasisRangedUshortFloatData(-1f, 1f, 0.001f);

            float original = 0.5f;
            ushort compressed = data.Compress(original);
            float decompressed = data.Decompress(compressed);

            Assert.InRange(decompressed, original - data.Precision, original + data.Precision);
        }

        [Fact]
        public void CompressDecompress_MinValue_ClampsCorrectly()
        {
            var data = new BasisNetworkPrimitiveCompression.BasisRangedUshortFloatData(0f, 100f, 0.1f);

            ushort compressed = data.Compress(-50f); // below min
            float decompressed = data.Decompress(compressed);

            Assert.Equal(0f, decompressed);
        }

        [Fact]
        public void CompressDecompress_MaxValue_ClampsCorrectly()
        {
            var data = new BasisNetworkPrimitiveCompression.BasisRangedUshortFloatData(0f, 100f, 0.1f);

            ushort compressed = data.Compress(200f); // above max
            float decompressed = data.Decompress(compressed);

            Assert.Equal(100f, decompressed);
        }

        [Fact]
        public void CompressDecompress_Zero_Works()
        {
            var data = new BasisNetworkPrimitiveCompression.BasisRangedUshortFloatData(-10f, 10f, 0.01f);

            ushort compressed = data.Compress(0f);
            float decompressed = data.Decompress(compressed);

            Assert.InRange(decompressed, -0.01f, 0.01f);
        }

        [Fact]
        public void RequiredBits_CalculatedCorrectly()
        {
            var data = new BasisNetworkPrimitiveCompression.BasisRangedUshortFloatData(0f, 1f, 0.001f);

            // Range=1, 1/0.001=1000, log2(1000)+1 = 10+1 = 11
            Assert.True(data.RequiredBits > 0);
            Assert.True(data.RequiredBits <= 16); // ushort max
        }

        [Fact]
        public void Mask_IsCorrectForRequiredBits()
        {
            var data = new BasisNetworkPrimitiveCompression.BasisRangedUshortFloatData(0f, 1f, 0.01f);

            int expectedMask = (1 << data.RequiredBits) - 1;
            Assert.Equal(expectedMask, data.Mask);
        }

        [Theory]
        [InlineData(1u, 0)]
        [InlineData(2u, 1)]
        [InlineData(4u, 2)]
        [InlineData(8u, 3)]
        [InlineData(16u, 4)]
        [InlineData(1024u, 10)]
        public void FastLog2_PowersOfTwo(uint value, int expected)
        {
            Assert.Equal(expected, BasisNetworkPrimitiveCompression.BasisRangedUshortFloatData.FastLog2(value));
        }

        [Fact]
        public void CompressDecompress_NegativeRange()
        {
            var data = new BasisNetworkPrimitiveCompression.BasisRangedUshortFloatData(-180f, 180f, 0.1f);

            float original = -90f;
            ushort compressed = data.Compress(original);
            float decompressed = data.Decompress(compressed);

            Assert.InRange(decompressed, original - data.Precision * 2, original + data.Precision * 2);
        }

        [Fact]
        public void InversePrecision_IsCorrect()
        {
            var data = new BasisNetworkPrimitiveCompression.BasisRangedUshortFloatData(0f, 1f, 0.01f);
            Assert.Equal(100f, data.InversePrecision);
        }
    }

    public class BasisAvatarBitPackingTests
    {
        [Theory]
        [InlineData(BasisAvatarBitPacking.BitQuality.VeryLow, true)]
        [InlineData(BasisAvatarBitPacking.BitQuality.Low, true)]
        [InlineData(BasisAvatarBitPacking.BitQuality.Medium, true)]
        [InlineData(BasisAvatarBitPacking.BitQuality.High, true)]
        [InlineData((BasisAvatarBitPacking.BitQuality)99, false)]
        public void IsValidQuality_CorrectResult(BasisAvatarBitPacking.BitQuality quality, bool expected)
        {
            Assert.Equal(expected, BasisAvatarBitPacking.IsValidQuality(quality));
        }

        [Fact]
        public void GetBitsPerSlot_ReturnsCorrectArray()
        {
            Assert.Same(BasisAvatarBitPacking.BITS_PER_SLOT_HIGH, BasisAvatarBitPacking.GetBitsPerSlot(BasisAvatarBitPacking.BitQuality.High));
            Assert.Same(BasisAvatarBitPacking.BITS_PER_SLOT_MEDIUM, BasisAvatarBitPacking.GetBitsPerSlot(BasisAvatarBitPacking.BitQuality.Medium));
            Assert.Same(BasisAvatarBitPacking.BITS_PER_SLOT_LOW, BasisAvatarBitPacking.GetBitsPerSlot(BasisAvatarBitPacking.BitQuality.Low));
            Assert.Same(BasisAvatarBitPacking.BITS_PER_SLOT_VERY_LOW, BasisAvatarBitPacking.GetBitsPerSlot(BasisAvatarBitPacking.BitQuality.VeryLow));
        }

        [Fact]
        public void ConvertToSize_IncludesPositionMuscleTail()
        {
            foreach (BasisAvatarBitPacking.BitQuality q in Enum.GetValues(typeof(BasisAvatarBitPacking.BitQuality)))
            {
                int size = BasisAvatarBitPacking.ConvertToSize(q);
                int expectedMin = BasisAvatarBitPacking.WritePosition + BasisAvatarBitPacking.TailBytes;
                Assert.True(size > expectedMin, $"Quality {q}: size {size} should be > {expectedMin}");
            }
        }

        [Fact]
        public void ConvertToSize_HighQuality_LargerThanLow()
        {
            int high = BasisAvatarBitPacking.ConvertToSize(BasisAvatarBitPacking.BitQuality.High);
            int medium = BasisAvatarBitPacking.ConvertToSize(BasisAvatarBitPacking.BitQuality.Medium);
            int low = BasisAvatarBitPacking.ConvertToSize(BasisAvatarBitPacking.BitQuality.Low);
            int veryLow = BasisAvatarBitPacking.ConvertToSize(BasisAvatarBitPacking.BitQuality.VeryLow);

            Assert.True(high > medium);
            Assert.True(medium > low);
            Assert.True(low > veryLow);
        }

        [Fact]
        public void AllBitsPerSlot_HaveConsistentLength()
        {
            // All quality tables must have the same number of entries
            int slotCount = BasisAvatarBitPacking.BITS_PER_SLOT_HIGH.Length;
            Assert.Equal(slotCount, BasisAvatarBitPacking.BITS_PER_SLOT_MEDIUM.Length);
            Assert.Equal(slotCount, BasisAvatarBitPacking.BITS_PER_SLOT_LOW.Length);
            Assert.Equal(slotCount, BasisAvatarBitPacking.BITS_PER_SLOT_VERY_LOW.Length);
            Assert.True(slotCount > 0);
        }

        [Fact]
        public void WriteOrder_MatchesBitSlotCount()
        {
            // WRITE_ORDER length should match the bit slot table length
            Assert.Equal(BasisAvatarBitPacking.BITS_PER_SLOT_HIGH.Length, BasisAvatarBitPacking.WRITE_ORDER.Length);
        }

        [Fact]
        public void MuscleArrays_HaveConsistentLength()
        {
            Assert.Equal(BasisAvatarBitPacking.TotalMuscles, BasisAvatarBitPacking.MinMuscle.Length);
            Assert.Equal(BasisAvatarBitPacking.TotalMuscles, BasisAvatarBitPacking.MaxMuscle.Length);
            Assert.Equal(BasisAvatarBitPacking.TotalMuscles, BasisAvatarBitPacking.RangeMuscle.Length);
        }

        [Fact]
        public void RangeMuscle_IsMaxMinusMin()
        {
            for (int i = 0; i < BasisAvatarBitPacking.TotalMuscles; i++)
            {
                float expectedRange = BasisAvatarBitPacking.MaxMuscle[i] - BasisAvatarBitPacking.MinMuscle[i];
                Assert.Equal(expectedRange, BasisAvatarBitPacking.RangeMuscle[i], 0.001f);
            }
        }

        [Fact]
        public void Constants_AreCorrect()
        {
            Assert.Equal(4, BasisAvatarBitPacking.FloatSize);
            Assert.Equal(2, BasisAvatarBitPacking.UShortSize);
            Assert.Equal(12, BasisAvatarBitPacking.Vector3Size);
            Assert.Equal(12, BasisAvatarBitPacking.WritePosition);
            Assert.Equal(2, BasisAvatarBitPacking.WriteScale);
            Assert.Equal(7, BasisAvatarBitPacking.WriteRotation);
            Assert.Equal(9, BasisAvatarBitPacking.TailBytes);
        }

        [Fact]
        public void ReadBits_SingleByte_CorrectBits()
        {
            byte[] src = { 0b10110101 };
            int bitPos = 0;

            // Read 4 bits: 0101 = 5
            uint val = BasisAvatarBitPacking.ReadBits(src, ref bitPos, 4);
            Assert.Equal(5u, val);
            Assert.Equal(4, bitPos);

            // Read next 4 bits: 1011 = 11
            val = BasisAvatarBitPacking.ReadBits(src, ref bitPos, 4);
            Assert.Equal(11u, val);
            Assert.Equal(8, bitPos);
        }

        [Fact]
        public void ReadBits_CrossByteBoundary()
        {
            byte[] src = { 0xFF, 0x00 };
            int bitPos = 4;

            // Starting at bit 4 of first byte (all 1s), reading 8 bits
            // Gets bits 4-7 of byte 0 (0xF => 4 ones) and bits 0-3 of byte 1 (0x0 => 4 zeros)
            uint val = BasisAvatarBitPacking.ReadBits(src, ref bitPos, 8);
            Assert.Equal(0x0Fu, val); // lower 4 bits are 1, upper 4 are 0
        }

        [Fact]
        public void ReadBits_FullByte()
        {
            byte[] src = { 0xAB };
            int bitPos = 0;
            uint val = BasisAvatarBitPacking.ReadBits(src, ref bitPos, 8);
            Assert.Equal(0xABu, val);
        }
    }

    public class MathExtensionsTests
    {
        [Theory]
        [InlineData(5f, 0f, 10f, 5f)]
        [InlineData(-5f, 0f, 10f, 0f)]
        [InlineData(15f, 0f, 10f, 10f)]
        [InlineData(0f, 0f, 0f, 0f)]
        public void Clamp_Float(float value, float min, float max, float expected)
        {
            Assert.Equal(expected, MathExtensions.Clamp(value, min, max));
        }

        [Theory]
        [InlineData(5, 0, 10, 5)]
        [InlineData(-5, 0, 10, 0)]
        [InlineData(15, 0, 10, 10)]
        public void Clamp_Int(int value, int min, int max, int expected)
        {
            Assert.Equal(expected, MathExtensions.Clamp(value, min, max));
        }

        [Theory]
        [InlineData(5.0, 0.0, 10.0, 5.0)]
        [InlineData(-5.0, 0.0, 10.0, 0.0)]
        [InlineData(15.0, 0.0, 10.0, 10.0)]
        public void Clamp_Double(double value, double min, double max, double expected)
        {
            Assert.Equal(expected, MathExtensions.Clamp(value, min, max));
        }

        [Fact]
        public void Vector3_Subtraction()
        {
            var a = new Vector3(3, 5, 7);
            var b = new Vector3(1, 2, 3);
            var result = a - b;

            Assert.Equal(2f, result.x);
            Assert.Equal(3f, result.y);
            Assert.Equal(4f, result.z);
        }

        [Fact]
        public void Vector3_Addition()
        {
            var a = new Vector3(1, 2, 3);
            var b = new Vector3(4, 5, 6);
            var result = a + b;

            Assert.Equal(5f, result.x);
            Assert.Equal(7f, result.y);
            Assert.Equal(9f, result.z);
        }

        [Fact]
        public void Vector3_SquaredMagnitude()
        {
            var v = new Vector3(3, 4, 0);
            Assert.Equal(25f, v.SquaredMagnitude());
        }

        [Fact]
        public void Vector3_SquaredMagnitude_Zero()
        {
            var v = new Vector3(0, 0, 0);
            Assert.Equal(0f, v.SquaredMagnitude());
        }
    }
}
