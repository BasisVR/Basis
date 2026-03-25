using Basis.Network.Core.Compression;
using System;
using static Basis.Network.Core.Compression.BasisAvatarBitPacking;

namespace BasisNetworkServer.BasisNetworkingReductionSystem
{
    public static class AvatarQualityRepacker
    {
        // Cache bits per slot for all qualities we care about
        static readonly byte[] HighBits = GetBitsPerSlot(BitQuality.High);
        static readonly byte[] MedBits = GetBitsPerSlot(BitQuality.Medium);
        static readonly byte[] LowBits = GetBitsPerSlot(BitQuality.Low);
        static readonly byte[] VLowBits = GetBitsPerSlot(BitQuality.VeryLow);

        static readonly int Slots = WRITE_ORDER.Length;

        // Cache muscle byte counts
        static readonly int HighMuscleBytes = MuscleBytes(BitQuality.High);
        static readonly int MedMuscleBytes = MuscleBytes(BitQuality.Medium);
        static readonly int LowMuscleBytes = MuscleBytes(BitQuality.Low);
        static readonly int VLowMuscleBytes = MuscleBytes(BitQuality.VeryLow);

        // Cache payload sizes
        static readonly int HighPayloadSize = BasisAvatarBitPacking.WritePosition + HighMuscleBytes + TailBytes;
        static readonly int MedPayloadSize = BasisAvatarBitPacking.WritePosition + MedMuscleBytes + TailBytes;
        static readonly int LowPayloadSize = BasisAvatarBitPacking.WritePosition + LowMuscleBytes + TailBytes;
        static readonly int VLowPayloadSize = BasisAvatarBitPacking.WritePosition + VLowMuscleBytes + TailBytes;

        // Cache bit offsets for each quality (no per-call allocations)
        static readonly int[] HighOffs = BuildBitOffsets(HighBits);
        static readonly int[] MedOffs = BuildBitOffsets(MedBits);
        static readonly int[] LowOffs = BuildBitOffsets(LowBits);
        static readonly int[] VLowOffs = BuildBitOffsets(VLowBits);

        static int[] BuildBitOffsets(byte[] bits)
        {
            var offs = new int[Slots];
            int bit = 0;
            for (int i = 0; i < Slots; i++)
            {
                offs[i] = bit;
                bit += bits[i];
            }
            return offs;
        }
        public static void BuildAllLowerFromHighInto(
            in SerializableBasis.LocalAvatarSyncMessage srcHigh,
            ref SerializableBasis.LocalAvatarSyncMessage medium,
            ref SerializableBasis.LocalAvatarSyncMessage low,
            ref SerializableBasis.LocalAvatarSyncMessage veryLow)
        {
            if (srcHigh.array == null)
                throw new ArgumentNullException(nameof(srcHigh.array));

            if (srcHigh.array.Length < HighPayloadSize)
                throw new ArgumentException($"High payload too small. Need >= {HighPayloadSize}, got {srcHigh.array.Length}");

            EnsureBuffer(ref medium, BitQuality.Medium, MedPayloadSize);
            EnsureBuffer(ref low, BitQuality.Low, LowPayloadSize);
            EnsureBuffer(ref veryLow, BitQuality.VeryLow, VLowPayloadSize);

            // Copy position
            Buffer.BlockCopy(srcHigh.array, 0, medium.array, 0, BasisAvatarBitPacking.WritePosition);
            Buffer.BlockCopy(srcHigh.array, 0, low.array, 0, BasisAvatarBitPacking.WritePosition);
            Buffer.BlockCopy(srcHigh.array, 0, veryLow.array, 0, BasisAvatarBitPacking.WritePosition);

            int srcMuscleBase = BasisAvatarBitPacking.WritePosition;

            int medMuscleBase = BasisAvatarBitPacking.WritePosition;
            int lowMuscleBase = BasisAvatarBitPacking.WritePosition;
            int vlowMuscleBase = BasisAvatarBitPacking.WritePosition;

            // Clear only muscle regions because BitWriter ORs into bytes
            Array.Clear(medium.array, medMuscleBase, MedMuscleBytes);
            Array.Clear(low.array, lowMuscleBase, LowMuscleBytes);
            Array.Clear(veryLow.array, vlowMuscleBase, VLowMuscleBytes);

            // Repack in one pass
            for (int slot = 0; slot < Slots; slot++)
            {
                int bSrc = HighBits[slot];
                uint qSrc = BitReader.ReadBits(srcHigh.array, srcMuscleBase, HighOffs[slot], bSrc);

                int bMed = MedBits[slot];
                if (bMed > 0)
                    BitWriter.WriteBits(medium.array, medMuscleBase, MedOffs[slot], RescaleQuant(qSrc, bSrc, bMed), bMed);

                int bLow = LowBits[slot];
                if (bLow > 0)
                    BitWriter.WriteBits(low.array, lowMuscleBase, LowOffs[slot], RescaleQuant(qSrc, bSrc, bLow), bLow);

                int bVLow = VLowBits[slot];
                if (bVLow > 0)
                    BitWriter.WriteBits(veryLow.array, vlowMuscleBase, VLowOffs[slot], RescaleQuant(qSrc, bSrc, bVLow), bVLow);
            }

            // Copy tail
            int srcTailOffset = BasisAvatarBitPacking.WritePosition + HighMuscleBytes;

            Buffer.BlockCopy(srcHigh.array, srcTailOffset, medium.array, BasisAvatarBitPacking.WritePosition + MedMuscleBytes, TailBytes);
            Buffer.BlockCopy(srcHigh.array, srcTailOffset, low.array, BasisAvatarBitPacking.WritePosition + LowMuscleBytes, TailBytes);
            Buffer.BlockCopy(srcHigh.array, srcTailOffset, veryLow.array, BasisAvatarBitPacking.WritePosition + VLowMuscleBytes, TailBytes);
        }

        static void EnsureBuffer(ref SerializableBasis.LocalAvatarSyncMessage msg, BitQuality q, int size)
        {
            msg.DataQualityLevel = (byte)q;

            // Fast path: already allocated and correct size
            if (msg.array != null && msg.array.Length == size)
                return;

            // If you can tolerate ">= size" (e.g. pooled arrays), use >=
            // but if you serialize based on Length, you want exact size.
            msg.array = new byte[size];
        }

        // --------- Convenience wrapper: returns tuple (allocates arrays) ---------
        // This will still allocate 3 arrays per call, but no int[] anymore.
        public static (SerializableBasis.LocalAvatarSyncMessage medium,
                       SerializableBasis.LocalAvatarSyncMessage low,
                       SerializableBasis.LocalAvatarSyncMessage veryLow)
            BuildAllLowerFromHigh(in SerializableBasis.LocalAvatarSyncMessage srcHigh)
        {
            var med = new SerializableBasis.LocalAvatarSyncMessage();
            var low = new SerializableBasis.LocalAvatarSyncMessage();
            var vlow = new SerializableBasis.LocalAvatarSyncMessage();

            BuildAllLowerFromHighInto(srcHigh, ref med, ref low, ref vlow);
            return (med, low, vlow);
        }

        // Same Rescale/BitReader/BitWriter as you already have...
        static uint RescaleQuant(uint qSrc, int bSrc, int bDst)
        {
            if (bSrc == bDst) return qSrc;
            if (bDst <= 0) return 0;

            ulong maxSrc = ((ulong)1 << bSrc) - 1UL;
            ulong maxDst = ((ulong)1 << bDst) - 1UL;

            ulong num = (ulong)qSrc * maxDst + (maxSrc >> 1);
            return (uint)(num / maxSrc);
        }

        static class BitReader
        {
            public static uint ReadBits(byte[] src, int baseByteOffset, int bitPos, int bitCount)
            {
                int bytePos = baseByteOffset + (bitPos >> 3);
                int bitInByte = bitPos & 7;

                uint result = 0;
                int outShift = 0;
                int bitsLeft = bitCount;

                while (bitsLeft > 0)
                {
                    int room = 8 - bitInByte;
                    int take = bitsLeft < room ? bitsLeft : room;

                    uint cur = src[bytePos];
                    cur >>= bitInByte;

                    uint mask = (uint)((1u << take) - 1u);
                    uint chunk = cur & mask;

                    result |= (chunk << outShift);

                    outShift += take;
                    bitsLeft -= take;

                    bytePos++;
                    bitInByte = 0;
                }
                return result;
            }
        }

        static class BitWriter
        {
            public static void WriteBits(byte[] dst, int baseByteOffset, int bitPos, uint value, int bitCount)
            {
                int bytePos = baseByteOffset + (bitPos >> 3);
                int bitInByte = bitPos & 7;

                uint v = value;
                int bitsLeft = bitCount;

                while (bitsLeft > 0)
                {
                    int room = 8 - bitInByte;
                    int take = bitsLeft < room ? bitsLeft : room;

                    uint mask = (uint)((1u << take) - 1u);
                    byte chunk = (byte)(v & mask);

                    dst[bytePos] = (byte)(dst[bytePos] | (chunk << bitInByte));

                    v >>= take;
                    bitsLeft -= take;

                    bytePos++;
                    bitInByte = 0;
                }
            }
        }
    }
}
