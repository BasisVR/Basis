namespace Basis.Network.Core.Compression
{
    public static class BasisAvatarBitPacking
    {
        public const int FloatSize = sizeof(float);
        public const int UShortSize = sizeof(ushort);
        public const int Vector3Size = 3 * FloatSize;
        public const float MinScale = 0.005f;
        public const float MaxScale = 150f;
        public const float ComputedRange = MaxScale - MinScale;

        public const int WritePosition = 12;
        public const int WriteScale = 2;
        public const int WriteRotation = 7;

        public const int TailBytes = WriteScale + WriteRotation; // 9

        // Expanded ladder (anchors preserved: Low/Medium/High)
        public enum BitQuality : byte
        {
            VeryLow = 0,
            Low = 1,
            Medium = 2,
            High = 3,
        }
        public static bool IsValidQuality(BitQuality q) => q == BitQuality.VeryLow || q == BitQuality.Low || q == BitQuality.Medium || q == BitQuality.High;

        public static byte[] GetBitsPerSlot(BitQuality q) => q switch
        {
            BitQuality.High => BITS_PER_SLOT_HIGH,
            BitQuality.Medium => BITS_PER_SLOT_MEDIUM,
            BitQuality.Low => BITS_PER_SLOT_LOW,
            BitQuality.VeryLow => BITS_PER_SLOT_VERY_LOW,
            _ => BITS_PER_SLOT_MEDIUM
        };
        public static int MuscleBytes(BitQuality q) => SumBitsPerSlotBytes(GetBitsPerSlot(q));

        public static int ConvertToSize(BitQuality q)
        {
            // Position (12) + Muscles (variable) + Scale (2) + Rotation (16)
            return WritePosition + MuscleBytes(q) + TailBytes;
        }
        // --------------------------
        // Internal helpers
        // --------------------------
        private static int SumBitsPerSlotBytes(byte[] bitsPerSlot)
        {
            int totalBits = 0;
            for (int i = 0; i < bitsPerSlot.Length; i++)
                totalBits += bitsPerSlot[i];

            return (totalBits + 7) >> 3;
        }
        // --------------------------
        // slot -> muscle index (unchanged)
        // --------------------------
        public static readonly int[] WRITE_ORDER = new int[]
        {
            0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,
            21,22,23,24,25,26,27,28,
            29,30,31,32,33,34,35,36,
            37,38,39,40,41,42,43,44,45,
            46,47,48,49,50,51,52,53,54,
            55,56,57,58,59,60,61,62,63,64,65,66,67,68,69,70,71,72,73,74,
            75,76,77,78,79,80,81,82,83,84,85,86,87,88,89,90,91,92,93,94,
        };
        public static readonly byte[] BITS_PER_SLOT_HIGH = new byte[]
{
            // Spine/Chest/Head
            17,17,17,
            17,17,17,
            16,16,16,
            17,17,17,
            17,17,17,

            // Left Leg
            17,17,17,
            17,18,17,
            15,15,

            // Right Leg
            17,17,17,
            17,18,17,
            15,15,

            // Left Arm
            14,14,
            18,18,18,
            17,18,
            17,16,

            // Right Arm
            14,14,
            18,18,18,
            17,18,
            17,16,

            // Left Hand Fingers (49..68 -> muscles 55..74)
            8,13,8,8,
            8,13,8,8,
            8,13,8,8,
            8,13,8,8,
            8,13,8,8,

            // Right Hand Fingers (69..88 -> muscles 75..94)
            8,13,8,8,
            8,13,8,8,
            8,13,8,8,
            8,13,8,8,
            8,13,8,8,
};
        // ---------------------------------------------------------------------
        // Anchors (YOUR EXISTING TABLES): keep exactly as authored.
        // ---------------------------------------------------------------------
        public static readonly byte[] BITS_PER_SLOT_MEDIUM = new byte[]
        {
            // Spine/Chest/Head (0..14)
            15,15,15,
            15,15,15,
            14,14,14,
            15,15,15,
            15,15,15,

            // Left Leg (15..22 -> muscles 21..28)
            15,15,15,
            15,16,15,
            13,8,

            // Right Leg (23..30 -> muscles 29..36)
            15,15,15,
            15,16,15,
            13,8,

            // Left Arm (31..39 -> muscles 37..45)
            12,12,
            16,16,16,
            15,16,
            15,14,

            // Right Arm (40..48 -> muscles 46..54)
            12,12,
            16,16,16,
            15,16,
            15,14,

            // Left Hand Fingers (49..68 -> muscles 55..74)
            8,12,8,8,
            8,11,8,8,
            8,10,8,8,
            8,10,8,8,
            8,11,8,8,

            // Right Hand Fingers (69..88 -> muscles 75..94)
            8,12,8,8,
            8,11,8,8,
            8,10,8,8,
            8,10,8,8,
            8,11,8,8,
        };

        public static readonly byte[] BITS_PER_SLOT_LOW = new byte[]
        {
            // Spine/Chest/Head
            12,12,12,
            12,12,12,
            11,11,11,
            12,12,12,
            12,12,12,

            // Left Leg
            12,12,12,
            12,12,11,
            10,11,

            // Right Leg
            12,12,12,
            12,12,11,
            10,11,

            // Left Arm
            9,9,
            12,12,12,
            11,12,
            11,10,

            // Right Arm
            9,9,
            12,12,12,
            11,12,
            11,10,

            // Left Hand Fingers (49..68 -> muscles 55..74)
            8,9,8,8,
            8,9,8,8,
            8,9,8,8,
            8,9,8,8,
            8,9,8,8,

            // Right Hand Fingers (69..88 -> muscles 75..94)
            8,9,8,8,
            8,9,8,8,
            8,9,8,8,
            8,9,8,8,
            8,9,8,8,
        };
        public static readonly byte[] BITS_PER_SLOT_VERY_LOW = new byte[]
        {
    // Spine / Chest / Head (0..14)
    9,10,9,
    9,10,9,
    9,10,9,
    9,10,9,
    9,10,9,

    // Left Leg (15..22)
    9,9,9,
    9,10,9,
    9,10,

    // Right Leg (23..30)
    9,9,9,
    9,10,9,
    9,10,

    // Left Arm (31..39)
    9,9,
    9,9,9,
    9,9,
    9,9,

    // Right Arm (40..48)
    9,9,
    9,9,9,
    9,9,
    9,9,

// Left Hand Fingers (49..68)
8,8,8,8,
8,8,8,8,
8,8,8,8,
8,8,8,8,
8,8,8,8,

// Right Hand Fingers (69..88)
8,8,8,8,
8,8,8,8,
8,8,8,8,
8,8,8,8,
8,8,8,8,
        };
        public static int TotalMuscles = 95;
        public const ushort UShortMin = ushort.MinValue;
        public const ushort UShortMax = ushort.MaxValue;
        public const ushort UShortRangeDifference = UShortMax - UShortMin;
        public static float[] MinMuscle = new float[]
        {
        -40f, -40f, -40f, -40f, -40f, -40f,
        -20f, -20f, -20f,
        -40f, -40f, -40f, -40f, -40f, -40f,
        -10f, -20f, -10f, -20f, -10f, -10f,
        -90f, -60f, -60f, -80f, -90f, -50f, -30f, -20f,
        -90f, -60f, -60f, -80f, -90f, -50f, -30f, -20f,
        -15f, -15f,
        -60f, -100f, -90f, -80f, -90f, -80f, -40f,
        -15f, -15f,
        -60f, -100f, -90f, -80f, -90f, -80f, -40f,
        -20f, -25f, -40f, -40f, -50f, -20f,
        -45f, -45f, -50f, -7.5f,
        -45f, -45f, -50f, -7.5f,
        -45f, -45f, -50f, -20f,
        -45f, -45f,
        -20f, -25f, -40f, -40f, -50f, -20f,
        -45f, -45f, -50f, -7.5f,
        -45f, -45f, -50f, -7.5f,
        -45f, -45f, -50f, -20f,
        -45f, -45f
        };

        public static float[] MaxMuscle = new float[]
        {
        40f, 40f, 40f, 40f, 40f, 40f,
        20f, 20f, 20f,
        40f, 40f, 40f, 40f, 40f, 40f,
        15f, 20f, 15f, 20f, 10f, 10f,
        50f, 60f, 60f, 80f, 90f, 50f, 30f, 20f,
        50f, 60f, 60f, 80f, 90f, 50f, 30f, 20f,
        30f, 15f,
        100f, 100f, 90f, 80f, 90f, 80f, 40f,
        30f, 15f,
        100f, 100f, 90f, 80f, 90f, 80f, 40f,
        20f, 25f, 35f, 35f, 50f, 20f,
        45f, 45f, 50f, 7.5f,
        45f, 45f, 50f, 7.5f,
        45f, 45f, 50f, 20f,
        45f, 45f,
        20f, 25f, 35f, 35f, 50f, 20f,
        45f, 45f, 50f, 7.5f,
        45f, 45f, 50f, 7.5f,
        45f, 45f, 50f, 20f,
        45f, 45f
        };

        public static float[] RangeMuscle = new float[]
        {
        80f, 80f, 80f, 80f, 80f, 80f,
        40f, 40f, 40f,
        80f, 80f, 80f, 80f, 80f, 80f,
        25f, 40f, 25f, 40f, 20f, 20f,
        140f, 120f, 120f, 160f, 180f, 100f, 60f, 40f,
        140f, 120f, 120f, 160f, 180f, 100f, 60f, 40f,
        45f, 30f,
        160f, 200f, 180f, 160f, 180f, 160f, 80f,
        45f, 30f,
        160f, 200f, 180f, 160f, 180f, 160f, 80f,
        40f, 50f, 75f, 75f, 100f, 40f,
        90f, 90f, 100f, 15f,
        90f, 90f, 100f, 15f,
        90f, 90f, 100f, 40f,
        90f, 90f,
        40f, 50f, 75f, 75f, 100f, 40f,
        90f, 90f, 100f, 15f,
        90f, 90f, 100f, 15f,
        90f, 90f, 100f, 40f,
        90f, 90f
        };
        public static uint ReadBits(byte[] src, ref int bitPos, int bitCount)
        {
            int bytePos = bitPos >> 3;
            int bitInByte = bitPos & 7;

            uint outV = 0;
            int outShift = 0;

            int bitsLeft = bitCount;
            while (bitsLeft > 0)
            {
                int room = 8 - bitInByte;
                int take = bitsLeft < room ? bitsLeft : room;

                uint mask = (uint)((1 << take) - 1);
                uint chunk = (uint)(src[bytePos] >> bitInByte) & mask;

                outV |= (chunk << outShift);

                outShift += take;
                bitsLeft -= take;
                bytePos++;
                bitInByte = 0;
            }

            bitPos += bitCount;
            return outV;
        }
    }
}
