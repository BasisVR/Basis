namespace Basis.Network.Core.Compression
{
    /// <summary>
    /// Avatar payload geometry (position / scale / rotation / hips tail sizes) and the quality
    /// ladder. The actual bone-rotation bitstream is produced by <see cref="BasisBoneRotationCompression"/>;
    /// the size helpers here delegate to it. (The legacy per-muscle quantization tables that used to
    /// live here were removed — the smallest-three quaternion codec superseded them.)
    /// </summary>
    public static class BasisAvatarBitPacking
    {
        public const int WritePosition = 12;
        // Medium/Low/VeryLow carry the hips world position as 3 × signed int24 millimetres
        // (±8388 m, 1 mm steps — invisible at the ≥10 m view distances those tiers serve).
        // High keeps 3 × float32 so the client uplink and P2P frames are untouched.
        public const int WritePositionQuantized = 9;
        private const int PositionMmLimit = (1 << 23) - 1;
        public const int WriteScale = 2;
        public const int WriteRotation = 7;
        // Hips local-position delta vs TPose, sent so seated/IK-driven hips poses
        // reach remotes (3 ushorts at fixed range, see HipsDeltaRange below).
        public const int WriteHipsDelta = 6;
        // Hips local-rotation delta vs TPose. Hips is excluded from the bone
        // packet (BONE_WRITE_ORDER), so without this slot the remote hips would
        // sit at calibration rotation forever. 7 bytes = same smallest-three
        // encoding used for the root rotation.
        public const int WriteHipsRotation = 7;
        // Per-axis ±1m envelope. With ushort precision this is ≈30 µm/axis,
        // far below visual jitter and large enough to cover squat/seated
        // overrides without clipping.
        public const float HipsDeltaRange = 1f;

        public const int TailBytes = WriteScale + WriteRotation + WriteHipsDelta + WriteHipsRotation; // 22

        // Expanded ladder (anchors preserved: Low/Medium/High)
        public enum BitQuality : byte
        {
            VeryLow = 0,
            Low = 1,
            Medium = 2,
            High = 3,
        }
        public static bool IsValidQuality(BitQuality q) => q == BitQuality.VeryLow || q == BitQuality.Low || q == BitQuality.Medium || q == BitQuality.High;

        /// <summary>
        /// Returns the byte count for the bone rotation bitstream at the given quality.
        /// Named MuscleBytes for backward compatibility with server code.
        /// </summary>
        public static int MuscleBytes(BitQuality q) => BasisBoneRotationCompression.RotationBytes(q);

        /// <summary>Position field size: High = 3 × float32, lower tiers = 3 × int24 mm.</summary>
        public static int PositionBytes(BitQuality q)
            => q == BitQuality.High ? WritePosition : WritePositionQuantized;

        public static int ConvertToSize(BitQuality q)
        {
            // Position (12 or 9) + BoneRotations (variable) + Posit16 Scale (2) + Rotation (7) + hips tail.
            return BasisBoneRotationCompression.ConvertToSize(q);
        }

        /// <summary>Encodes one world-space axis value (metres) as signed int24 millimetres, little-endian.</summary>
        public static void EncodeAxisMm(float meters, byte[] dst, int offset)
        {
            float mmF = meters * 1000f;
            int mm = float.IsNaN(mmF) ? 0
                : mmF >= PositionMmLimit ? PositionMmLimit
                : mmF <= -PositionMmLimit ? -PositionMmLimit
                : (int)System.Math.Round(mmF);
            dst[offset] = (byte)mm;
            dst[offset + 1] = (byte)(mm >> 8);
            dst[offset + 2] = (byte)(mm >> 16);
        }

        /// <summary>Decodes one signed int24 millimetre axis back to metres.</summary>
        public static float DecodeAxisMm(byte[] src, int offset)
        {
            int mm = src[offset] | (src[offset + 1] << 8) | (src[offset + 2] << 16);
            // Sign-extend from 24 bits.
            mm = (mm << 8) >> 8;
            return mm * 0.001f;
        }

        /// <summary>
        /// Transcodes the 12-byte float32 position at the start of a High payload into the
        /// 9-byte int24-mm form at the start of a lower-quality payload.
        /// </summary>
        public static void TranscodePositionToQuantized(byte[] srcHigh, byte[] dstLower)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                float v = System.BitConverter.ToSingle(srcHigh, axis * 4);
                EncodeAxisMm(v, dstLower, axis * 3);
            }
        }
    }
}
