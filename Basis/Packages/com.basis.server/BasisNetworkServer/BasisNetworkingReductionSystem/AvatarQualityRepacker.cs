using Basis.Network.Core.Compression;
using System;
using static Basis.Network.Core.Compression.BasisAvatarBitPacking;

namespace BasisNetworkServer.BasisNetworkingReductionSystem
{
    /// <summary>
    /// Server-side repacker that converts HIGH quality bone rotation data
    /// into medium/low/very-low quality by re-quantizing each bone's
    /// smallest-three components at a lower bits-per-component (BPC).
    /// </summary>
    public static class AvatarQualityRepacker
    {
        static readonly int Slots = BasisBoneRotationCompression.SyncBoneCount; // 51

        // Cache BPC tables for each quality
        static readonly byte[] HighBpc  = BasisBoneRotationCompression.BPC_HIGH;
        static readonly byte[] MedBpc   = BasisBoneRotationCompression.BPC_MEDIUM;
        static readonly byte[] LowBpc   = BasisBoneRotationCompression.BPC_LOW;
        static readonly byte[] VLowBpc  = BasisBoneRotationCompression.BPC_VERY_LOW;

        // Cache byte counts (via MuscleBytes which now routes to RotationBytes)
        static readonly int HighRotBytes = MuscleBytes(BitQuality.High);
        static readonly int MedRotBytes  = MuscleBytes(BitQuality.Medium);
        static readonly int LowRotBytes  = MuscleBytes(BitQuality.Low);
        static readonly int VLowRotBytes = MuscleBytes(BitQuality.VeryLow);

        // Cache payload sizes. High reads float32 position (12B); lower tiers write int24-mm (9B).
        static readonly int LowerPosBytes = PositionBytes(BitQuality.Medium);
        static readonly int HighPayloadSize = WritePosition + HighRotBytes + TailBytes;
        static readonly int MedPayloadSize  = LowerPosBytes + MedRotBytes  + TailBytes;
        static readonly int LowPayloadSize  = LowerPosBytes + LowRotBytes  + TailBytes;
        static readonly int VLowPayloadSize = LowerPosBytes + VLowRotBytes + TailBytes;

        // Cache per-bone bit offsets for each quality
        static readonly int[] HighOffs = BuildBitOffsets(HighBpc);
        static readonly int[] MedOffs  = BuildBitOffsets(MedBpc);
        static readonly int[] LowOffs  = BuildBitOffsets(LowBpc);
        static readonly int[] VLowOffs = BuildBitOffsets(VLowBpc);

        static int[] BuildBitOffsets(byte[] bpc)
        {
            var offs = new int[Slots];
            int bit = 0;
            for (int i = 0; i < Slots; i++)
            {
                offs[i] = bit;
                bit += 2 + 3 * bpc[i]; // 2 index bits + 3 components
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

            // Position: float32 in the High source, int24-mm in every lower tier
            TranscodePositionToQuantized(srcHigh.array, medium.array);
            Buffer.BlockCopy(medium.array, 0, low.array, 0, LowerPosBytes);
            Buffer.BlockCopy(medium.array, 0, veryLow.array, 0, LowerPosBytes);

            int srcRotBase = WritePosition;
            int rotBase = LowerPosBytes;

            // Clear rotation regions (BitWriter ORs into bytes)
            Array.Clear(medium.array, rotBase, MedRotBytes);
            Array.Clear(low.array, rotBase, LowRotBytes);
            Array.Clear(veryLow.array, rotBase, VLowRotBytes);

            // Repack each bone: read smallest-three at HIGH BPC, rescale components to lower BPC
            for (int slot = 0; slot < Slots; slot++)
            {
                int bpcSrc = HighBpc[slot];
                int totalBitsSrc = 2 + 3 * bpcSrc;

                // Read the full packed bone (index + 3 components) as raw bits
                ulong raw = BitReader.ReadBitsU64(srcHigh.array, srcRotBase, HighOffs[slot], totalBitsSrc);

                // Extract the 2-bit index (which component was dropped)
                uint idx = (uint)(raw & 3UL);

                // Extract 3 components at source BPC
                uint maskSrc = (uint)((1 << bpcSrc) - 1);
                uint qa = (uint)((raw >> 2) & maskSrc);
                uint qb = (uint)((raw >> (2 + bpcSrc)) & maskSrc);
                uint qc = (uint)((raw >> (2 + 2 * bpcSrc)) & maskSrc);

                // Rescale and write for each target quality
                RepackBone(medium.array, rotBase, MedOffs[slot], MedBpc[slot], idx, qa, qb, qc, bpcSrc);
                RepackBone(low.array, rotBase, LowOffs[slot], LowBpc[slot], idx, qa, qb, qc, bpcSrc);
                RepackBone(veryLow.array, rotBase, VLowOffs[slot], VLowBpc[slot], idx, qa, qb, qc, bpcSrc);
            }

            // Copy tail (scale + body rotation)
            int srcTailOffset = WritePosition + HighRotBytes;
            Buffer.BlockCopy(srcHigh.array, srcTailOffset, medium.array, rotBase + MedRotBytes, TailBytes);
            Buffer.BlockCopy(srcHigh.array, srcTailOffset, low.array, rotBase + LowRotBytes, TailBytes);
            Buffer.BlockCopy(srcHigh.array, srcTailOffset, veryLow.array, rotBase + VLowRotBytes, TailBytes);
        }

        static void RepackBone(byte[] dst, int baseByteOffset, int bitOffset, int bpcDst,
            uint idx, uint qa, uint qb, uint qc, int bpcSrc)
        {
            // Rescale each component from source BPC to destination BPC
            uint da = RescaleQuant(qa, bpcSrc, bpcDst);
            uint db = RescaleQuant(qb, bpcSrc, bpcDst);
            uint dc = RescaleQuant(qc, bpcSrc, bpcDst);

            // Pack: [idx:2][da:bpcDst][db:bpcDst][dc:bpcDst]
            ulong packed = (ulong)idx
                | ((ulong)da << 2)
                | ((ulong)db << (2 + bpcDst))
                | ((ulong)dc << (2 + 2 * bpcDst));

            int totalBits = 2 + 3 * bpcDst;
            BitWriter.WriteBitsU64(dst, baseByteOffset, bitOffset, packed, totalBits);
        }

        static void EnsureBuffer(ref SerializableBasis.LocalAvatarSyncMessage msg, BitQuality q, int size)
        {
            msg.DataQualityLevel = (byte)q;
            if (msg.array != null && msg.array.Length >= size)
                return;
            msg.array = new byte[size];
        }

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
            public static ulong ReadBitsU64(byte[] src, int baseByteOffset, int bitPos, int bitCount)
            {
                int bytePos = baseByteOffset + (bitPos >> 3);
                int bitInByte = bitPos & 7;
                ulong result = 0;
                int outShift = 0;
                int bitsLeft = bitCount;

                while (bitsLeft > 0)
                {
                    int room = 8 - bitInByte;
                    int take = bitsLeft < room ? bitsLeft : room;
                    ulong cur = src[bytePos];
                    cur >>= bitInByte;
                    ulong mask = (1UL << take) - 1UL;
                    ulong chunk = cur & mask;
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
            public static void WriteBitsU64(byte[] dst, int baseByteOffset, int bitPos, ulong value, int bitCount)
            {
                int bytePos = baseByteOffset + (bitPos >> 3);
                int bitInByte = bitPos & 7;
                ulong v = value;
                int bitsLeft = bitCount;

                while (bitsLeft > 0)
                {
                    int room = 8 - bitInByte;
                    int take = bitsLeft < room ? bitsLeft : room;
                    ulong mask = (1UL << take) - 1UL;
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
