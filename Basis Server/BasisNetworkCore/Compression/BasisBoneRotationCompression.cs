using System;
using System.Runtime.CompilerServices;

namespace Basis.Network.Core.Compression
{
    /// <summary>
    /// Bone rotation compression using "smallest three" quaternion encoding.
    /// Pure C# — no Unity dependencies. Can run on the server.
    ///
    /// Each bone is assigned a bits-per-component (BPC) value based on its DOF:
    ///   3-DOF body joints: 10 BPC (32 bits total)
    ///   2-DOF limb joints: 8 BPC (26 bits total)
    ///   2-DOF extremities: 7 BPC (23 bits total)
    ///   1-2 DOF toes/eyes/jaw: 5 BPC (17 bits total)
    ///   2-DOF finger proximal: 6 BPC (20 bits total)
    ///   1-DOF finger mid/distal: 4 BPC (14 bits total)
    /// </summary>
    public static class BasisBoneRotationCompression
    {
        /// <summary>
        /// Number of bones synced. Excludes:
        ///   Hips (0) — sent as body rotation in the packet tail
        ///   LeftEye (21), RightEye (22), Jaw (23) — driven locally by BasisRemoteFaceManagement
        /// </summary>
        public const int SyncBoneCount = 51;

        /// <summary>Inverse of sqrt(2), the max magnitude of any non-dropped smallest-three component.</summary>
        public const float InvSqrt2 = 0.70710678118f;

        // Reuse position/scale/rotation sizes from BasisAvatarBitPacking
        public const int WritePosition = BasisAvatarBitPacking.WritePosition;   // 12
        public const int WriteScale    = BasisAvatarBitPacking.WriteScale;      // 2
        public const int WriteRotation = BasisAvatarBitPacking.WriteRotation;   // 7
        public const int WriteHipsDelta = BasisAvatarBitPacking.WriteHipsDelta; // 6
        public const int WriteHipsRotation = BasisAvatarBitPacking.WriteHipsRotation; // 7
        public const int TailBytes     = BasisAvatarBitPacking.TailBytes;       // 22

        // ────────────────────────────────────────────────────────────
        //  Bone write order: HumanBodyBones enum values (excluding Hips=0)
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// Maps slot index (0..50) to HumanBodyBones enum value.
        /// Excludes Hips(0), LeftEye(21), RightEye(22), Jaw(23).
        /// Grouped: 3-DOF body → 2-DOF limbs → 2-DOF extremities → toes → finger proximal → finger mid/distal.
        /// </summary>
        public static readonly int[] BONE_WRITE_ORDER = new int[]
        {
            // 3-DOF body (9 bones): Spine, Chest, UpperChest, Neck, Head, UpperArms, UpperLegs
            7, 8, 54, 9, 10, 13, 14, 1, 2,
            // 2-DOF limbs (4 bones): LowerArms, LowerLegs
            15, 16, 3, 4,
            // 2-DOF extremities (6 bones): Shoulders, Hands, Feet
            11, 12, 17, 18, 5, 6,
            // toes (2 bones) — eyes/jaw excluded (driven by face system)
            19, 20,
            // 2-DOF finger proximal (10 bones)
            24, 27, 30, 33, 36, 39, 42, 45, 48, 51,
            // 1-DOF finger intermediate (10 bones)
            25, 28, 31, 34, 37, 40, 43, 46, 49, 52,
            // 1-DOF finger distal (10 bones)
            26, 29, 32, 35, 38, 41, 44, 47, 50, 53,
        };

        /// <summary>
        /// Reverse lookup: HumanBodyBones enum value → slot index.
        /// Index 0 (Hips) = -1. Bones 1..54 map to slots 0..53.
        /// </summary>
        public static readonly int[] BONE_TO_SLOT;

        static BasisBoneRotationCompression()
        {
            BONE_TO_SLOT = new int[55];
            for (int i = 0; i < 55; i++) BONE_TO_SLOT[i] = -1;
            for (int slot = 0; slot < SyncBoneCount; slot++)
                BONE_TO_SLOT[BONE_WRITE_ORDER[slot]] = slot;
        }

        // ────────────────────────────────────────────────────────────
        //  Bits-per-component tables (per quality level)
        //  Total bits per bone = 2 (index) + 3 * BPC
        // ────────────────────────────────────────────────────────────

        /// <summary>HIGH quality. 1182 bits = 148 rotation bytes. Packet = 169 bytes.
        /// Per-finger priority: thumb/index get more bits (most expressive).
        /// Proximal gets more than intermediate/distal (carries spread motion).</summary>
        public static readonly byte[] BPC_HIGH = new byte[]
        {
            // 3-DOF body (9): spine, chest, upperchest, neck, head, upper arms, upper legs
            // 12 bits (was 10): halves the per-joint quant step twice over vs 10-bit, cutting the
            // slow-motion limb SHIMMER ~4x. Long-lever/proximal joints dominate hand/foot shimmer.
            12,12,12,12,12,12,12,12,12,
            // 2-DOF limbs (4): lower arms, lower legs
            12,12,12,12,
            // 2-DOF extremities (6): shoulders(2), hands(2), feet(2)
            12,12, 12,12, 12,12,
            // toes (2)
            5,5,
            // finger proximal (10): L-Thumb,L-Index,L-Mid,L-Ring,L-Little, R-same
            6,6,6,6,5,  6,6,6,6,5,
            // finger intermediate (10): Thumb/Index=6, Mid/Ring/Little=5
            6,6,5,5,5,  6,6,5,5,5,
            // finger distal (10): all 5
            5,5,5,5,5,  5,5,5,5,5,
        };

        /// <summary>MEDIUM quality. 972 bits = 122 rotation bytes. Packet = 143 bytes.</summary>
        public static readonly byte[] BPC_MEDIUM = new byte[]
        {
            8,8,8,8,8,8,8,8,8,
            8,8,8,8,
            8,8, 8,8, 6,6,
            3,3,
            6,6,5,5,4,  6,6,5,5,4,
            5,5,4,4,4,  5,5,4,4,4,
            4,4,4,4,4,  4,4,4,4,4,
        };

        /// <summary>LOW quality. 774 bits = 97 rotation bytes. Packet = 118 bytes.</summary>
        public static readonly byte[] BPC_LOW = new byte[]
        {
            6,6,6,6,6,6,6,6,6,
            6,6,6,6,
            6,6, 6,6, 5,5,
            3,3,
            5,5,4,4,3,  5,5,4,4,3,
            4,4,3,3,3,  4,4,3,3,3,
            3,3,3,3,3,  3,3,3,3,3,
        };

        /// <summary>VERY LOW quality. 621 bits = 78 rotation bytes. Packet = 99 bytes.</summary>
        public static readonly byte[] BPC_VERY_LOW = new byte[]
        {
            5,5,5,5,5,5,5,5,5,
            5,5,5,5,
            5,5, 5,5, 4,4,
            2,2,
            4,4,3,3,2,  4,4,3,3,2,
            3,3,2,2,2,  3,3,2,2,2,
            2,2,2,2,2,  2,2,2,2,2,
        };

        // ────────────────────────────────────────────────────────────
        //  Per-bone max component range (joint limits)
        //  maxComp = sin(maxAngle/2) with ~15% safety margin, capped at InvSqrt2.
        //  Tighter range → more precision at the same bit count.
        //  Precision multiplier = InvSqrt2 / maxComp.
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// Maximum quaternion component magnitude per bone slot.
        /// Components are quantized within [-maxComp, maxComp] instead of full [-0.707, 0.707].
        ///
        /// DESIGN: most joints use full InvSqrt2 range to support ALL human poses
        /// (dancing, gymnastics, sleeping, backbends, splits, etc.).
        /// Only joints that are physically incapable of large rotation get tighter ranges:
        ///   - Eyes: ~35° max look direction (anatomical limit of extraocular muscles)
        ///   - Jaw: ~40° max open + sideways (TMJ limit)
        ///   - Toes: ~55° max curl (metatarsal limit)
        ///   - UpperChest: ~50° max (thoracic vertebrae are fused/limited)
        ///
        /// Hips orientation is sent separately as a full-precision compressed quaternion,
        /// so upside-down, sideways, etc. are unaffected by these limits.
        /// </summary>
        /// <summary>
        /// Maximum quaternion component magnitude per bone slot.
        /// After dropping the largest component in smallest-three, the remaining 3
        /// are quantized within [-maxComp, maxComp].
        /// Tighter range = better precision at the same BPC.
        ///
        /// Values derived from max anatomical rotation, computing sin(maxAngle/2)
        /// for the largest possible remaining component, plus safety margin.
        /// Full InvSqrt2 used for any joint that can approach or exceed 90° from T-pose.
        /// </summary>
        public static readonly float[] MAX_COMPONENT = new float[]
        {
            // 3-DOF body (9): Spine, Chest, UpperChest, Neck, Head, UpperArms, UpperLegs
            InvSqrt2,               // Spine         full (deep backbend/fold can exceed 90° combined)
            InvSqrt2,               // Chest         full
            0.50f,                  // UpperChest    thoracic limit ~58° → 1.41x
            InvSqrt2,               // Neck          full (extreme head tilt)
            InvSqrt2,               // Head          full
            InvSqrt2, InvSqrt2,     // UpperArms     full (shoulder has ~180° ROM)
            InvSqrt2, InvSqrt2,     // UpperLegs     full (splits, deep squat)

            // 2-DOF limbs (4): LowerArms, LowerLegs
            InvSqrt2, InvSqrt2,     // LowerArms     full (elbow 150° + pronation 90°)
            InvSqrt2, InvSqrt2,     // LowerLegs     full (knee 150°)

            // 2-DOF extremities (6): Shoulders, Hands, Feet
            0.50f, 0.50f,           // Shoulders     clavicle max ~58° (shrug+protract) → 1.41x
            InvSqrt2, InvSqrt2,     // Hands         full (wrist can circle ~90°)
            0.60f, 0.60f,           // Feet          ankle max ~70° combined → 1.18x

            // toes (2) — eyes/jaw excluded (driven by face system)
            0.50f, 0.50f,           // Toes          ~58° curl → 1.41x

            // finger proximal (10): curl ~90° + spread ~25° → combined ~95°
            // At 95°: axis=0.74, w=0.68. After dropping axis, remaining max=0.68
            0.68f, 0.68f, 0.68f, 0.68f, 0.68f,
            0.68f, 0.68f, 0.68f, 0.68f, 0.68f,

            // finger intermediate (10): curl only, max ~110°
            // At 110°: axis=0.82, w=0.57. After dropping axis, remaining max=0.57
            0.58f, 0.58f, 0.58f, 0.58f, 0.58f,
            0.58f, 0.58f, 0.58f, 0.58f, 0.58f,

            // finger distal (10): curl only, max ~80°
            // At 80°: w=0.77, axis=0.64. After dropping w, remaining max=0.64
            0.65f, 0.65f, 0.65f, 0.65f, 0.65f,
            0.65f, 0.65f, 0.65f, 0.65f, 0.65f,
        };

        public static byte[] GetBpcTable(BasisAvatarBitPacking.BitQuality q) => q switch
        {
            BasisAvatarBitPacking.BitQuality.High     => BPC_HIGH,
            BasisAvatarBitPacking.BitQuality.Medium   => BPC_MEDIUM,
            BasisAvatarBitPacking.BitQuality.Low      => BPC_LOW,
            BasisAvatarBitPacking.BitQuality.VeryLow  => BPC_VERY_LOW,
            _ => BPC_HIGH
        };

        // ────────────────────────────────────────────────────────────
        //  Size calculations
        // ────────────────────────────────────────────────────────────

        public static int RotationBytes(BasisAvatarBitPacking.BitQuality q)
        {
            byte[] bpc = GetBpcTable(q);
            int totalBits = 0;
            for (int i = 0; i < bpc.Length; i++)
                totalBits += 2 + 3 * bpc[i];
            return (totalBits + 7) >> 3;
        }

        // End-effector anchoring block (hand/foot world targets), High quality only —
        // near players get precise planting; far players are repacked to lower quality without it.
        public const int EndEffectorBlockBytes = 35;
        public static int EndEffectorBytes(BasisAvatarBitPacking.BitQuality q)
            => q == BasisAvatarBitPacking.BitQuality.High ? EndEffectorBlockBytes : 0;

        public static int ConvertToSize(BasisAvatarBitPacking.BitQuality q)
        {
            return BasisAvatarBitPacking.PositionBytes(q) + RotationBytes(q) + TailBytes + EndEffectorBytes(q);
        }

        public static int ComputeBitOffsets(byte[] bpc, int[] outBitOffsets)
        {
            int pos = 0;
            for (int i = 0; i < bpc.Length; i++)
            {
                outBitOffsets[i] = pos;
                pos += 2 + 3 * bpc[i];
            }
            return pos;
        }

        // ────────────────────────────────────────────────────────────
        //  Smallest-Three Encode / Decode (pure floats, no Unity types)
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// Encodes a unit quaternion (x,y,z,w) using "smallest three" compression.
        /// Components are quantized within [-maxRange, maxRange] for better precision
        /// on joints with limited rotation. Use InvSqrt2 for full-range joints.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong EncodeSmallestThree(float qx, float qy, float qz, float qw, int bpc, float maxRange = InvSqrt2)
        {
            float ax = Math.Abs(qx), ay = Math.Abs(qy), az = Math.Abs(qz), aw = Math.Abs(qw);

            // Find largest absolute component
            int maxIdx = 0;
            float maxVal = ax;
            if (ay > maxVal) { maxIdx = 1; maxVal = ay; }
            if (az > maxVal) { maxIdx = 2; maxVal = az; }
            if (aw > maxVal) { maxIdx = 3; }

            // Negate quaternion if largest is negative
            float sign = 1f;
            switch (maxIdx)
            {
                case 0: if (qx < 0f) sign = -1f; break;
                case 1: if (qy < 0f) sign = -1f; break;
                case 2: if (qz < 0f) sign = -1f; break;
                case 3: if (qw < 0f) sign = -1f; break;
            }
            qx *= sign; qy *= sign; qz *= sign; qw *= sign;

            // Extract the 3 remaining components
            float a, b, c;
            switch (maxIdx)
            {
                case 0:  a = qy; b = qz; c = qw; break;
                case 1:  a = qx; b = qz; c = qw; break;
                case 2:  a = qx; b = qy; c = qw; break;
                default: a = qx; b = qy; c = qz; break;
            }

            // Quantize within [-maxRange, maxRange] (clamped for edge cases)
            float invRange = 1f / maxRange;
            uint maxQ = (uint)((1 << bpc) - 1);
            uint qa = Clamp((uint)Math.Round((ClampF(a * invRange, -1f, 1f) * 0.5f + 0.5f) * maxQ), 0, maxQ);
            uint qA = Clamp((uint)Math.Round((ClampF(b * invRange, -1f, 1f) * 0.5f + 0.5f) * maxQ), 0, maxQ);
            uint qC = Clamp((uint)Math.Round((ClampF(c * invRange, -1f, 1f) * 0.5f + 0.5f) * maxQ), 0, maxQ);

            return (ulong)maxIdx | ((ulong)qa << 2) | ((ulong)qA << (2 + bpc)) | ((ulong)qC << (2 + 2 * bpc));
        }

        /// <summary>
        /// Decodes a "smallest three" compressed quaternion into (x,y,z,w).
        /// maxRange must match the value used during encoding.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void DecodeSmallestThree(ulong packed, int bpc, out float qx, out float qy, out float qz, out float qw, float maxRange = InvSqrt2)
        {
            uint mask = (uint)((1 << bpc) - 1);
            int maxIdx = (int)(packed & 3UL);
            uint qa = (uint)((packed >> 2) & mask);
            uint qb = (uint)((packed >> (2 + bpc)) & mask);
            uint qc = (uint)((packed >> (2 + 2 * bpc)) & mask);

            float fMax = (float)mask;
            float a = (qa / fMax * 2f - 1f) * maxRange;
            float b = (qb / fMax * 2f - 1f) * maxRange;
            float c = (qc / fMax * 2f - 1f) * maxRange;

            float d2 = 1f - a * a - b * b - c * c;
            float d = d2 > 0f ? (float)Math.Sqrt(d2) : 0f;

            switch (maxIdx)
            {
                case 0:  qx = d; qy = a; qz = b; qw = c; break;
                case 1:  qx = a; qy = d; qz = b; qw = c; break;
                case 2:  qx = a; qy = b; qz = d; qw = c; break;
                default: qx = a; qy = b; qz = c; qw = d; break;
            }

            // Normalize
            float len = (float)Math.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
            if (len > 1e-8f)
            {
                float inv = 1f / len;
                qx *= inv; qy *= inv; qz *= inv; qw *= inv;
            }
            else
            {
                qx = 0f; qy = 0f; qz = 0f; qw = 1f;
            }
        }

        // ────────────────────────────────────────────────────────────
        //  Bitstream read/write (pure C#)
        // ────────────────────────────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteBits(byte[] dst, int bitPos, ulong value, int bitCount)
        {
            int bytePos = bitPos >> 3;
            int bitInByte = bitPos & 7;
            ulong v = value;
            int bitsLeft = bitCount;

            while (bitsLeft > 0)
            {
                int room = 8 - bitInByte;
                int take = bitsLeft < room ? bitsLeft : room;
                ulong maskVal = (1UL << take) - 1UL;
                byte chunk = (byte)(v & maskVal);
                dst[bytePos] = (byte)(dst[bytePos] | (chunk << bitInByte));
                v >>= take;
                bitsLeft -= take;
                bytePos++;
                bitInByte = 0;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong ReadBits(byte[] src, ref int bitPos, int bitCount)
        {
            int bytePos = bitPos >> 3;
            int bitInByte = bitPos & 7;
            ulong outV = 0;
            int outShift = 0;
            int bitsLeft = bitCount;

            while (bitsLeft > 0)
            {
                int room = 8 - bitInByte;
                int take = bitsLeft < room ? bitsLeft : room;
                ulong maskVal = (1UL << take) - 1UL;
                ulong chunk = ((ulong)src[bytePos] >> bitInByte) & maskVal;
                outV |= chunk << outShift;
                outShift += take;
                bitsLeft -= take;
                bytePos++;
                bitInByte = 0;
            }

            bitPos += bitCount;
            return outV;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static uint Clamp(uint v, uint min, uint max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float ClampF(float v, float min, float max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }
    }
}
