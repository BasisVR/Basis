namespace Basis.Network.Core.Compression
{
    public static class BasisBitPackingConstants
    {
        public const int FloatSize = sizeof(float);
        public const int UShortSize = sizeof(ushort);
        public const int Vector3Size = 3 * FloatSize;
        private static int avatarSyncSize;
        private static bool IsWired = false;
        public static int WritePosition = 12;
        public static int WriteRotation = 16;
        public static int WriteScale = 2;
        public static int AvatarSyncSize
        {
            get
            {
                if (IsWired == false)
                {
                    IsWired = true;
                    //156 = 12 + 8 + 2 + 134
                    avatarSyncSize = WritePosition + WriteRotation + WriteScale + SumBitsPerSlot();
                }
                return avatarSyncSize;
            }
        }
        private static int SumBitsPerSlot()
        {
            int totalBits = 0;

            for (int i = 0; i < BITS_PER_SLOT.Length; i++)
            {
                totalBits += BITS_PER_SLOT[i];
            }

            // Convert bits → bytes, rounding up
            return (totalBits + 7) >> 3;
        }
        // slot -> muscle index (exactly your existing order, skipping 15..20)
        public static readonly int[] WRITE_ORDER = new int[]
        {
        // 0..14
        0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,

        // Left Leg
        21,22,23,24,25,26,27,28,

        // Right Leg
        29,30,31,32,33,34,35,36,

        // Left Arm
        37,38,39,40,41,42,43,44,45,

        // Right Arm
        46,47,48,49,50,51,52,53,54,

        // Left Hand Fingers
        55,56,57,58,59,60,61,62,63,64,65,66,67,68,69,70,71,72,73,74,

        // Right Hand Fingers
        75,76,77,78,79,80,81,82,83,84,85,86,87,88,89,90,91,92,93,94,
        };
        public static readonly byte[] BITS_PER_SLOT = new byte[]
        {
    // ----------------------
    // Spine/Chest/Head (slots 0..14 -> muscles 0..14)
    // Range 80 -> 15 bits (step ~0.0024)
    // Range 40 -> 14 bits (step ~0.0024)
    // ----------------------
    15,15,15,   // 0 Spine FB, 1 Spine LR, 2 Spine Twist (80)
    15,15,15,   // 3 Chest FB, 4 Chest LR, 5 Chest Twist (80)
    14,14,14,   // 6 UpperChest FB, 7 UpperChest LR, 8 UpperChest Twist (40)
    15,15,15,   // 9 Neck Nod, 10 Neck Tilt, 11 Neck Turn (80)
    15,15,15,   // 12 Head Nod, 13 Head Tilt, 14 Head Turn (80)

    // ----------------------
    // Left Leg (slots 15..22 -> muscles 21..28)
    // Big leg joints: keep fine-ish.
    // ----------------------
    15,15,15,   // 21 UpperLeg FB (140), 22 UpperLeg InOut (120), 23 UpperLeg Twist (120)
    15,16,15,   // 24 LowerLeg Stretch (160), 25 LowerLeg Twist (180), 26 Foot UpDown (100)
    13,8,      // 27 Foot Twist (60)  -> 13 (step ~0.0073) MUCH better than 8-bit
                // 28 Toes UpDown (100) -> 15 (step ~0.0031)

    // ----------------------
    // Right Leg (slots 23..30 -> muscles 29..36)
    // ----------------------

    15,15,15,   // 29 UpperLeg FB (140), 30 UpperLeg InOut (120), 31 UpperLeg Twist (120)
    15,16,15,   // 32 LowerLeg Stretch (160), 33 LowerLeg Twist (180), 34 Foot UpDown (100)
    13,8,      // 35 Foot Twist (60) -> 13, 36 Toes UpDown (100) -> 15

    // ----------------------
    // Left Arm (slots 31..39 -> muscles 37..45)
    // Arms have some huge ranges (160..200). Keep those higher.
    // Shoulder ranges are smaller (45/30) so we can drop.
    // ----------------------
    12,12,      // 37 Shoulder DownUp (45), 38 Shoulder FrontBack (30)
    16,16,16,   // 39 Arm DownUp (160), 40 Arm FrontBack (200), 41 Arm Twist (180)
    15,16,      // 42 Forearm Stretch (160), 43 Forearm Twist (180)
    15,14,      // 44 Hand DownUp (160), 45 Hand InOut (80)

    // ----------------------
    // Right Arm (slots 40..48 -> muscles 46..54)
    // ----------------------
    12,12,      // 46 Shoulder DownUp (45), 47 Shoulder FrontBack (30)
    16,16,16,   // 48 Arm DownUp (160), 49 Arm FrontBack (200), 50 Arm Twist (180)
    15,16,      // 51 Forearm Stretch (160), 52 Forearm Twist (180)
    15,14,      // 53 Hand DownUp (160), 54 Hand InOut (80)

    // ----------------------
    // Left Hand Fingers (slots 49..68 -> muscles 55..74)
    //
    // especially on fast networked motion. Here we push bends to 13–14 and spreads to 11–12.
    //
    // Thumb: ranges 40,50,75,75
    // Index/Middle/Ring/Little: 1 stretch ~100, spreads 15 or 40, 2/3 stretches ~90
    // ----------------------
    8,13,8,8,    // 55 Thumb1 (40), 56 ThumbSpread (50), 57 Thumb2 (75), 58 Thumb3 (75)
    8,12,8,8,    // 59 Index1 (100), 60 IndexSpread (40), 61 Index2 (90), 62 Index3 (90)
    8,11,8,8,    // 63 Middle1 (100), 64 MiddleSpread (15), 65 Middle2 (90), 66 Middle3 (90)
    8,11,8,8,    // 67 Ring1 (100), 68 RingSpread (15), 69 Ring2 (90), 70 Ring3 (90)
    8,12,8,8,    // 71 Little1 (100), 72 LittleSpread (40), 73 Little2 (90), 74 Little3 (90)

    // ----------------------
    // Right Hand Fingers (slots 69..88 -> muscles 75..94)
    // Mirror of left.
    // ----------------------
    8,13,8,8,    // 75 Thumb1 (40), 76 ThumbSpread (50), 77 Thumb2 (75), 78 Thumb3 (75)
    8,12,8,8,    // 79 Index1 (100), 80 IndexSpread (40), 81 Index2 (90), 82 Index3 (90)
    8,11,8,8,    // 83 Middle1 (100), 84 MiddleSpread (15), 85 Middle2 (90), 86 Middle3 (90)
    8,11,8,8,    // 87 Ring1 (100), 88 RingSpread (15), 89 Ring2 (90), 90 Ring3 (90)
    8,12,8,8,    // 91 Little1 (100), 92 LittleSpread (40), 93 Little2 (90), 94 Little3 (90)
        };
    }
}
