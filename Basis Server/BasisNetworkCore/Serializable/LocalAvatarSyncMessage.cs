using LiteNetLib.Utils;
using System;
using System.Collections.Generic;
public static partial class SerializableBasis
{
    public struct LocalAvatarSyncMessage
    {
        public byte[] array;
        public const int AvatarSyncSize = 204;
        public const int StoredBones = 89;
        public AdditionalAvatarData[] AdditionalAvatarDatas;
        public bool hasAdditionalAvatarData;
        public bool[] boolArray;// = new bool[8];
        public void Deserialize(NetDataReader Writer, bool AttemptAdditionalData)
        {
            int Bytes = Writer.AvailableBytes;
            if (Writer.TryGetByte(out byte MuscleLoadOut) == false)
            {
                BNL.LogError($"Unable to read Remaing bytes where {Bytes}");
                hasAdditionalAvatarData = false;
                return;
            }
            ConvertByteToBools(MuscleLoadOut);
            int newSize = ComputeNewSize();
            int ReducedAmount = newSize * 2; //conversion to ushort (2 bytes)
            int NeededSize = AvatarSyncSize - ReducedAmount;
            if (Bytes - 1 >= NeededSize)
            {
                //89 * 2 = 178 + 12 + 14 = 204
                //now 178 for muscles, 3*4 for position 12, 4*4 for rotation 16-2 (W is half) = 204
                if (array == null || array.Length != NeededSize)
                {
                    array = new byte[NeededSize];
                }
                Writer.GetBytes(array, NeededSize);
                if (AttemptAdditionalData)
                {
                    if (Writer.EndOfData)
                    {
                        AdditionalAvatarDatas = null;
                        hasAdditionalAvatarData = false;
                    }
                    else
                    {
                        List<AdditionalAvatarData> list = new List<AdditionalAvatarData>();
                        while (Writer.AvailableBytes != 0)
                        {
                            // BNL.Log("Deserialize AAD");
                            AdditionalAvatarData AAD = new AdditionalAvatarData();
                            AAD.Deserialize(Writer);
                            list.Add(AAD);
                        }
                        AdditionalAvatarDatas = list.ToArray();
                        hasAdditionalAvatarData = true;
                    }
                }
            }
            else
            {
                BNL.LogError($"Unable to read Remaing bytes where {Bytes}");
                hasAdditionalAvatarData = false;
                return;
            }
        }
        public void Serialize(NetDataWriter Writer, bool AttemptAdditionalData)
        {
            Writer.Put(ConvertBoolsToByte());

            if (array == null)
            {
                BNL.LogError("array was null!!");
            }
            else
            {
                Writer.Put(array);
            }
            if (AttemptAdditionalData && hasAdditionalAvatarData)
            {
                // BNL.Log("Serialize AAD");
                int count = AdditionalAvatarDatas.Length;
                for (int Index = 0; Index < count; Index++)
                {
                    AdditionalAvatarData AAD = AdditionalAvatarDatas[Index];
                    AAD.Serialize(Writer);
                }
            }
        }
        public byte ConvertBoolsToByte()
        {
            if (boolArray == null)
            {
                boolArray = new bool[8];
            }
            byte byteValue = 0;

            for (int i = 0; i < 8; i++)
            {
                if (boolArray[i])
                {
                    byteValue |= (byte)(1 << i);
                }
            }

            return byteValue;
        }
        public void ConvertByteToBools(byte byteValue)
        {
            if (boolArray == null)
            {
                boolArray = new bool[8];
            }
            for (int i = 0; i < 8; i++)
            {
                // Check if the i-th bit is set
                boolArray[i] = (byteValue & (1 << i)) != 0;
            }
        }
        public void UpdateFlagsBasedOnData(float[] dataArray, out float[] Value, out int newSize)
        {
            boolArray[0] = IsSectionZero(69, 20, dataArray); // Right Hand Fingers
            boolArray[1] = IsSectionZero(49, 20, dataArray); // Left Hand Fingers
            boolArray[2] = IsSectionZero(40, 9, dataArray);  // Right Arm
            boolArray[3] = IsSectionZero(31, 9, dataArray);  // Left Arm
            boolArray[4] = IsSectionZero(23, 8, dataArray);  // Right Leg
            boolArray[5] = IsSectionZero(15, 8, dataArray);  // Left Leg
            boolArray[6] = IsSectionZero(6, 3, dataArray);   // Chest
            boolArray[7] = IsSectionZero(3, 3, dataArray);   // Upper Chest

            // Precompute the new size directly here
            newSize = ComputeNewSize();
            Value = FilterDataBasedOnFlags(dataArray, newSize);
        }
        private static readonly int[] SectionStartIndices = { 69, 49, 40, 31, 23, 15, 6, 3 };
        private static readonly int[] SectionSizes = { 20, 20, 9, 9, 8, 8, 3, 3 };
        private const float Epsilon = 1e-6f;
        private bool IsSectionZero(int startIndex, int count, float[] dataArray)
        {
            for (int i = startIndex; i < startIndex + count; i++)
            {
                if (Math.Abs(dataArray[i]) >= Epsilon)
                {
                    return false;
                }
            }
            return true;
        }

        private int ComputeNewSize()
        {
            int newSize = 0;
            for (int i = 0; i < 8; i++)
            {
                if (!boolArray[i])
                {
                    newSize += SectionSizes[i];
                }
            }
            return newSize;
        }

        private float[] FilterDataBasedOnFlags(float[] dataArray, int newSize)
        {
            if (newSize == dataArray.Length)
            {
                return dataArray; // No filtering needed
            }

            float[] filteredData = new float[newSize];
            int newIndex = 0;

            for (int i = 0; i < 8; i++)
            {
                if (!boolArray[i])
                {
                    int startIndex = SectionStartIndices[i];
                    int count = SectionSizes[i];

                    Array.Copy(dataArray, startIndex, filteredData, newIndex, count);
                    newIndex += count;
                }
            }

            return filteredData;
        }
    }
}
/*
// Spine
SpineFrontBack = 0,
SpineLeftRight = 1,
SpineTwistLeftRight = 2,

// Chest
ChestFrontBack = 3,
ChestLeftRight = 4,
ChestTwistLeftRight = 5,

// Upper Chest
UpperChestFrontBack = 6,
UpperChestLeftRight = 7,
UpperChestTwistLeftRight = 8,

// Neck 8
NeckNodDownUp = 9,
NeckTiltLeftRight = 10,
NeckTurnLeftRight = 11,

// Head 7
HeadNodDownUp = 12,
HeadTiltLeftRight = 13,
HeadTurnLeftRight = 14,

// Left Leg 6 8
LeftUpperLegFrontBack = 15,
LeftUpperLegInOut = 16,
LeftUpperLegTwistInOut = 17,
LeftLowerLegStretch = 18,
LeftLowerLegTwistInOut = 19,
LeftFootUpDown = 20,
LeftFootTwistInOut = 21,
LeftToesUpDown = 22,

// Right Leg 5 8
RightUpperLegFrontBack = 23,
RightUpperLegInOut = 24,
RightUpperLegTwistInOut = 25,
RightLowerLegStretch = 26,
RightLowerLegTwistInOut = 27,
RightFootUpDown = 28,
RightFootTwistInOut = 29,
RightToesUpDown = 30,

// Left Arm 4 9
LeftShoulderDownUp = 31,
LeftShoulderFrontBack = 32,
LeftArmDownUp = 33,
LeftArmFrontBack = 34,
LeftArmTwistInOut = 35,
LeftForearmStretch = 36,
LeftForearmTwistInOut = 37,
LeftHandDownUp = 38,
LeftHandInOut = 39,

// Right Arm 3 9
RightShoulderDownUp = 40,
RightShoulderFrontBack = 41,
RightArmDownUp = 42,
RightArmFrontBack = 43,
RightArmTwistInOut = 44,
RightForearmStretch = 45,
RightForearmTwistInOut = 46,
RightHandDownUp = 47,
RightHandInOut = 48,

// Left Hand Fingers 20 2
LeftThumb1Stretched = 49,
LeftThumbSpread = 50,
LeftThumb2Stretched = 51,
LeftThumb3Stretched = 52,
LeftIndex1Stretched = 53,
LeftIndexSpread = 54,
LeftIndex2Stretched = 55,
LeftIndex3Stretched = 56,
LeftMiddle1Stretched = 57,
LeftMiddleSpread = 58,
LeftMiddle2Stretched = 59,
LeftMiddle3Stretched = 60,
LeftRing1Stretched = 61,
LeftRingSpread = 62,
LeftRing2Stretched = 63,
LeftRing3Stretched = 64,
LeftLittle1Stretched = 65,
LeftLittleSpread = 66,
LeftLittle2Stretched = 67,
LeftLittle3Stretched = 68,

// Right Hand Fingers 20 1
RightThumb1Stretched = 69,
RightThumbSpread = 70,
RightThumb2Stretched = 71,
RightThumb3Stretched = 72,
RightIndex1Stretched = 73,
RightIndexSpread = 74,
RightIndex2Stretched = 75,
RightIndex3Stretched = 76,
RightMiddle1Stretched = 77,
RightMiddleSpread = 78,
RightMiddle2Stretched = 79,
RightMiddle3Stretched = 80,
RightRing1Stretched = 81,
RightRingSpread = 82,
RightRing2Stretched = 83,
RightRing3Stretched = 84,
RightLittle1Stretched = 85,
RightLittleSpread = 86,
RightLittle2Stretched = 87,
RightLittle3Stretched = 88,
*/
