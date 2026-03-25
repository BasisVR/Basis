using System;

namespace Basis.Network.Core.Compression
{
    /// <summary>
    /// Delta-compressed blendshape encoding/decoding shared between client and server.
    /// Wire format:
    ///   Byte 0 - Header: [7:6] Mode (00=Sparse, 01=Dense, 10=Silent) | [5] Keyframe | [4:0] Reserved
    ///   Byte 1 - Count of synced blendshapes (0-255)
    ///   Sparse: [bitmask: ceil(N/8) bytes] [changed values: 8-bit quantized]
    ///   Dense:  [all values: N bytes, 8-bit quantized]
    ///   Silent: no additional bytes
    ///
    /// Quantization: Unity weight [0,100] → byte [0,255] (~0.4% precision, visually indistinguishable)
    /// Only face-tracking blendshapes are synced. Viseme/blink/laughter are excluded
    /// because they are reconstructed remotely by uLipSync and BasisRemoteFaceManagement.
    /// </summary>
    public static class BasisBlendShapePacking
    {
        public const byte ModeSparse = 0x00;
        public const byte ModeDense = 0x40;
        public const byte ModeSilent = 0x80;
        public const byte ModeMask = 0xC0;
        public const byte KeyframeBit = 0x20;

        public const int DefaultKeyframeInterval = 32;

        /// <summary>Quantize Unity blendshape weight [0, 100] → byte [0, 255].</summary>
        public static byte Quantize(float weight)
        {
            int q = (int)(weight * 2.55f + 0.5f);
            if (q < 0) return 0;
            if (q > 255) return 255;
            return (byte)q;
        }

        /// <summary>Dequantize byte [0, 255] → Unity blendshape weight [0, 100].</summary>
        public static float Dequantize(byte q)
        {
            return q * (100f / 255f);
        }

        /// <summary>Maximum possible encoded size for a given count.</summary>
        public static int MaxEncodedSize(int count)
        {
            return 2 + ((count + 7) >> 3) + count;
        }

        /// <summary>
        /// Encode blendshape state using delta compression.
        /// Returns bytes written, or 0 if nothing to send.
        /// Uses optimal sparse/dense crossover: sparse only when it's actually smaller.
        /// </summary>
        public static int Encode(byte[] dst, int dstOffset, byte[] current, byte[] previous, int count, bool isKeyframe)
        {
            if (count == 0)
            {
                dst[dstOffset] = (byte)(ModeSilent | (isKeyframe ? KeyframeBit : 0));
                dst[dstOffset + 1] = 0;
                return 2;
            }

            int changedCount = 0;
            bool allZero = true;
            for (int i = 0; i < count; i++)
            {
                if (current[i] != previous[i])
                    changedCount++;
                if (current[i] != 0)
                    allZero = false;
            }

            if (changedCount == 0 && !isKeyframe)
                return 0;

            if (allZero)
            {
                dst[dstOffset] = (byte)(ModeSilent | (isKeyframe ? KeyframeBit : 0));
                dst[dstOffset + 1] = 0;
                return 2;
            }

            // Optimal crossover: sparse costs 2 + bitmask + changed, dense costs 2 + count.
            // Use dense when sparse would be larger or on keyframe.
            int bitmaskBytes = (count + 7) >> 3;
            bool useDense = isKeyframe || changedCount + bitmaskBytes >= count;

            if (useDense)
                return EncodeDense(dst, dstOffset, current, count, isKeyframe);
            else
                return EncodeSparse(dst, dstOffset, current, previous, count, bitmaskBytes, isKeyframe);
        }

        private static int EncodeSparse(byte[] dst, int offset, byte[] current, byte[] previous, int count, int bitmaskBytes, bool isKeyframe)
        {
            int pos = offset;
            dst[pos++] = (byte)(ModeSparse | (isKeyframe ? KeyframeBit : 0));
            dst[pos++] = (byte)count;

            int bitmaskStart = pos;
            for (int i = 0; i < bitmaskBytes; i++)
                dst[bitmaskStart + i] = 0;
            pos += bitmaskBytes;

            for (int i = 0; i < count; i++)
            {
                if (current[i] != previous[i])
                {
                    dst[bitmaskStart + (i >> 3)] |= (byte)(1 << (i & 7));
                    dst[pos++] = current[i];
                }
            }

            return pos - offset;
        }

        private static int EncodeDense(byte[] dst, int offset, byte[] current, int count, bool isKeyframe)
        {
            int pos = offset;
            dst[pos++] = (byte)(ModeDense | (isKeyframe ? KeyframeBit : 0));
            dst[pos++] = (byte)count;
            Buffer.BlockCopy(current, 0, dst, pos, count);
            return 2 + count;
        }

        /// <summary>
        /// Decode into persistent receiver state array.
        /// Sparse: only changed values updated. Dense: all replaced. Silent+keyframe: cleared.
        /// </summary>
        public static bool Decode(byte[] src, int srcOffset, int srcLength, byte[] outState, int outStateLength, out int decodedCount, out bool isKeyframe)
        {
            decodedCount = 0;
            isKeyframe = false;

            if (src == null || srcLength < 2)
                return false;

            byte header = src[srcOffset];
            byte mode = (byte)(header & ModeMask);
            isKeyframe = (header & KeyframeBit) != 0;
            decodedCount = src[srcOffset + 1];

            if (mode == ModeSilent || decodedCount == 0)
            {
                if (isKeyframe)
                {
                    int clearLen = Math.Min(decodedCount > 0 ? decodedCount : outStateLength, outStateLength);
                    Array.Clear(outState, 0, clearLen);
                }
                decodedCount = 0;
                return true;
            }

            int applyCount = Math.Min(decodedCount, outStateLength);
            int pos = srcOffset + 2;

            if (mode == ModeDense)
            {
                if (srcLength < 2 + decodedCount)
                    return false;
                Buffer.BlockCopy(src, pos, outState, 0, applyCount);
                return true;
            }

            if (mode == ModeSparse)
            {
                int bitmaskBytes = (decodedCount + 7) >> 3;
                if (srcLength < 2 + bitmaskBytes)
                    return false;

                int bitmaskStart = pos;
                pos += bitmaskBytes;

                for (int i = 0; i < decodedCount; i++)
                {
                    if ((src[bitmaskStart + (i >> 3)] & (1 << (i & 7))) != 0)
                    {
                        if (pos >= srcOffset + srcLength)
                            return false;
                        if (i < outStateLength)
                            outState[i] = src[pos];
                        pos++;
                    }
                }
                return true;
            }

            return false;
        }
    }
}
