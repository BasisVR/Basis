using Basis.Network.Core;
using Basis.Scripts.Networking.Compression;
using Basis.Scripts.Networking.Transmitters;
using Basis.Scripts.Profiler;
using LiteNetLib;
using System;
using System.Linq;
using Unity.Burst;
using Unity.Mathematics;
using UnityEngine;
using static Basis.Scripts.Networking.Transmitters.BasisNetworkTransmitter;
using static SerializableBasis;

namespace Basis.Scripts.Networking.NetworkedAvatar
{
    public static class BasisNetworkAvatarCompressor
    {
        public static void Compress(BasisNetworkTransmitter transmitter, Animator animator)
        {
            EnsureTransmitterIsInitialized(transmitter, animator);

            // Get current pose from Animator
            transmitter.PoseHandler.GetHumanPose(ref transmitter.HumanPose);
            CompressAvatarData(transmitter.storedAvatarData, transmitter.HumanPose, animator);
            transmitter.storedAvatarData.LASM.AdditionalAvatarDatas = transmitter.SendingOutAvatarData.Count == 0 ? null : transmitter.SendingOutAvatarData.Values.ToArray();
            transmitter.storedAvatarData.LASM.Serialize(transmitter.AvatarSendWriter);
            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.LocalAvatarSync, transmitter.AvatarSendWriter.Length);
            BasisNetworkConnection.LocalPlayerPeer.Send(transmitter.AvatarSendWriter, BasisNetworkCommons.PlayerAvatarChannel, DeliveryMethod.Sequenced);
            transmitter.AvatarSendWriter.Reset();
            transmitter.ClearAdditional();
        }

        public static void InitalAvatarData(Animator animator, out StoredAvatarData StoredAvatarData)
        {
            var poseHandler = new HumanPoseHandler(animator.avatar, animator.transform);
            var humanPose = new HumanPose();
            poseHandler.GetHumanPose(ref humanPose);
            StoredAvatarData = new StoredAvatarData();
            CompressAvatarData(StoredAvatarData, humanPose, animator);
        }
        [BurstCompile]
        public static void CompressAvatarData(StoredAvatarData AvatarData, HumanPose pose, Animator animator)
        {
            int offset = 0;
            // Compress Position 3*4 = 12 bytes
            BasisUnityBitPackerExtensionsUnsafe.WritePosition(animator.bodyPosition, ref AvatarData.LASM.array, ref offset);

            // Compress Rotation 3*4 = 12 + 2 = 14 bytes
            BasisUnityBitPackerExtensionsUnsafe.WriteQuaternionToBytes(animator.bodyRotation, ref AvatarData.LASM.array, ref offset, BasisNetworkPlayer.RotationCompression);

            // Compress Muscles totals 153 bytes
            CompressAvatarMuscles_NoLoop(ref pose.muscles, ref AvatarData.LASM, ref offset);

            // Compress Scale 2 bytes
            CompressScale(animator.transform.localScale.y, ref AvatarData.LASM, ref offset);
        }
        const int muscleCount = 95 - 6;//we remove the stuff we dont even put on the network first.
        public static byte[][] CBytes = new byte[muscleCount][];

        public static void CompressAvatarMuscles_NoLoop(ref float[] floatArray, ref LocalAvatarSyncMessage message, ref int offset)
        {
            // Sectioned, no loops in the write mapping (mirrors original order exactly)
            CompressSpineChestHead(ref floatArray);

            //CompressEyesJaw(ref floatArray); // intentionally skipped like original

            CompressLeftLeg(ref floatArray);
            CompressRightLeg(ref floatArray);

            CompressLeftArm(ref floatArray);
            CompressRightArm(ref floatArray);

            CompressLeftHandFingers(ref floatArray);
            CompressRightHandFingers(ref floatArray);

            // Combine and write to message
            int totalLength = 0;
            for (int i = 0; i < muscleCount; i++)
                totalLength += CBytes[i].Length;

            byte[] combined = new byte[totalLength];
            int pos = 0;
            for (int i = 0; i < muscleCount; i++)
            {
                Array.Copy(CBytes[i], 0, combined, pos, CBytes[i].Length);
                pos += CBytes[i].Length;
            }

            Array.Copy(combined, 0, message.array, offset, combined.Length);
            offset += combined.Length;
        }

        // ----------------------
        // Section: Spine/Chest/Head (indices 0..14 map 1:1 to compressed slots 0..14)
        // ----------------------
        private static void CompressSpineChestHead(ref float[] floatArray)
        {
            SetCompressedUshort(ref CBytes, ref floatArray, 0, 0, false); // Spine Front-Back: Range 80
            SetCompressedUshort(ref CBytes, ref floatArray, 1, 1, false); // Spine Left-Right: Range 80
            SetCompressedUshort(ref CBytes, ref floatArray, 2, 2, false); // Spine Twist Left-Right: Range 80
            SetCompressedUshort(ref CBytes, ref floatArray, 3, 3, false); // Chest Front-Back: Range 80
            SetCompressedUshort(ref CBytes, ref floatArray, 4, 4, false); // Chest Left-Right: Range 80
            SetCompressedUshort(ref CBytes, ref floatArray, 5, 5, false); // Chest Twist Left-Right: Range 80
            SetCompressedUshort(ref CBytes, ref floatArray, 6, 6, true); // UpperChest Front-Back: Range 40
            SetCompressedUshort(ref CBytes, ref floatArray, 7, 7, true); // UpperChest Left-Right: Range 40
            SetCompressedUshort(ref CBytes, ref floatArray, 8, 8, true); // UpperChest Twist Left-Right: Range 40
            SetCompressedUshort(ref CBytes, ref floatArray, 9, 9, false); // Neck Nod Down-Up: Range 80
            SetCompressedUshort(ref CBytes, ref floatArray, 10, 10, false); // Neck Tilt Left-Right: Range 80
            SetCompressedUshort(ref CBytes, ref floatArray, 11, 11, false); // Neck Turn Left-Right: Range 80
            SetCompressedUshort(ref CBytes, ref floatArray, 12, 12, false); // Head Nod Down-Up: Range 80
            SetCompressedUshort(ref CBytes, ref floatArray, 13, 13, false); // Head Tilt Left-Right: Range 80
            SetCompressedUshort(ref CBytes, ref floatArray, 14, 14, false); // Head Turn Left-Right: Range 80
        }

        // ----------------------
        // Section: Eyes/Jaw (Skipped like original)
        // ----------------------
        private static void CompressEyesJaw(ref float[] floatArray)
        {
            // Not sent over the network in the original code:
            // SetCompressedUshort(ref CBytes, ref floatArray, 15,  ?, true ); // Left Eye Down-Up: Range 25 byteable
            // SetCompressedUshort(ref CBytes, ref floatArray, 16,  ?, true ); // Left Eye In-Out: Range 40 byteable
            // SetCompressedUshort(ref CBytes, ref floatArray, 17,  ?, true ); // Right Eye Down-Up: Range 25 byteable
            // SetCompressedUshort(ref CBytes, ref floatArray, 18,  ?, true ); // Right Eye In-Out: Range 40 byteable
            // SetCompressedUshort(ref CBytes, ref floatArray, 19,  ?, true ); // Jaw Close: Range 20 byteable
            // SetCompressedUshort(ref CBytes, ref floatArray, 20,  ?, true ); // Jaw Left-Right: Range 20 byteable
        }

        // ----------------------
        // Section: Left Leg (indices 21..28 map to compressed slots 15..22)
        // ----------------------
        private static void CompressLeftLeg(ref float[] floatArray)
        {
            SetCompressedUshort(ref CBytes, ref floatArray, 21, 15, false); // Left Upper Leg Front-Back: Range 140
            SetCompressedUshort(ref CBytes, ref floatArray, 22, 16, false); // Left Upper Leg In-Out: Range 120
            SetCompressedUshort(ref CBytes, ref floatArray, 23, 17, false); // Left Upper Leg Twist In-Out: Range 120
            SetCompressedUshort(ref CBytes, ref floatArray, 24, 18, false); // Left Lower Leg Stretch: Range 160
            SetCompressedUshort(ref CBytes, ref floatArray, 25, 19, false); // Left Lower Leg Twist In-Out: Range 180
            SetCompressedUshort(ref CBytes, ref floatArray, 26, 20, false); // Left Foot Up-Down: Range 100
            SetCompressedUshort(ref CBytes, ref floatArray, 27, 21, true); // Left Foot Twist In-Out: Range 60 byteable
            SetCompressedUshort(ref CBytes, ref floatArray, 28, 22, false); // Left Toes Up-Down: Range 100 byteable
        }

        // ----------------------
        // Section: Right Leg (indices 29..36 map to compressed slots 23..30)
        // ----------------------
        private static void CompressRightLeg(ref float[] floatArray)
        {
            SetCompressedUshort(ref CBytes, ref floatArray, 29, 23, false); // Right Upper Leg Front-Back: Range 140
            SetCompressedUshort(ref CBytes, ref floatArray, 30, 24, false); // Right Upper Leg In-Out: Range 120
            SetCompressedUshort(ref CBytes, ref floatArray, 31, 25, false); // Right Upper Leg Twist In-Out: Range 120
            SetCompressedUshort(ref CBytes, ref floatArray, 32, 26, false); // Right Lower Leg Stretch: Range 160
            SetCompressedUshort(ref CBytes, ref floatArray, 33, 27, false); // Right Lower Leg Twist In-Out: Range 180
            SetCompressedUshort(ref CBytes, ref floatArray, 34, 28, false); // Right Foot Up-Down: Range 100
            SetCompressedUshort(ref CBytes, ref floatArray, 35, 29, true); // Right Foot Twist In-Out: Range 60 byteable
            SetCompressedUshort(ref CBytes, ref floatArray, 36, 30, false); // Right Toes Up-Down: Range 100 byteable
        }

        // ----------------------
        // Section: Left Arm (indices 37..45 map to compressed slots 31..39)
        // ----------------------
        private static void CompressLeftArm(ref float[] floatArray)
        {
            SetCompressedUshort(ref CBytes, ref floatArray, 37, 31, false); // Left Shoulder Down-Up: Range 45 byteable
            SetCompressedUshort(ref CBytes, ref floatArray, 38, 32, false); // Left Shoulder Front-Back: Range 30 byteable
            SetCompressedUshort(ref CBytes, ref floatArray, 39, 33, false); // Left Arm Down-Up: Range 160
            SetCompressedUshort(ref CBytes, ref floatArray, 40, 34, false); // Left Arm Front-Back: Range 200
            SetCompressedUshort(ref CBytes, ref floatArray, 41, 35, false); // Left Arm Twist In-Out: Range 180
            SetCompressedUshort(ref CBytes, ref floatArray, 42, 36, false); // Left Forearm Stretch: Range 160
            SetCompressedUshort(ref CBytes, ref floatArray, 43, 37, false); // Left Forearm Twist In-Out: Range 180
            SetCompressedUshort(ref CBytes, ref floatArray, 44, 38, false); // Left Hand Down-Up: Range 160
            SetCompressedUshort(ref CBytes, ref floatArray, 45, 39, false); // Left Hand In-Out: Range 80
        }

        // ----------------------
        // Section: Right Arm (indices 46..54 map to compressed slots 40..48)
        // ----------------------
        private static void CompressRightArm(ref float[] floatArray)
        {
            SetCompressedUshort(ref CBytes, ref floatArray, 46, 40, false); // Right Shoulder Down-Up: Range 45 byteable
            SetCompressedUshort(ref CBytes, ref floatArray, 47, 41, false); // Right Shoulder Front-Back: Range 30 byteable
            SetCompressedUshort(ref CBytes, ref floatArray, 48, 42, false); // Right Arm Down-Up: Range 160
            SetCompressedUshort(ref CBytes, ref floatArray, 49, 43, false); // Right Arm Front-Back: Range 200
            SetCompressedUshort(ref CBytes, ref floatArray, 50, 44, false); // Right Arm Twist In-Out: Range 180
            SetCompressedUshort(ref CBytes, ref floatArray, 51, 45, false); // Right Forearm Stretch: Range 160
            SetCompressedUshort(ref CBytes, ref floatArray, 52, 46, false); // Right Forearm Twist In-Out: Range 180
            SetCompressedUshort(ref CBytes, ref floatArray, 53, 47, false); // Right Hand Down-Up: Range 160
            SetCompressedUshort(ref CBytes, ref floatArray, 54, 48, false); // Right Hand In-Out: Range 80
        }

        // ----------------------
        // Section: Left Hand Fingers (indices 55..74 map to compressed slots 49..68)
        // ----------------------
        private static void CompressLeftHandFingers(ref float[] floatArray)
        {
            SetCompressedUshort(ref CBytes, ref floatArray, 55, 49, false); // Left Thumb 1 Stretched: Range 40 byteable
            SetCompressedUshort(ref CBytes, ref floatArray, 56, 50, false); // Left Thumb Spread: Range 50 byteable
            SetCompressedUshort(ref CBytes, ref floatArray, 57, 51, true); // Left Thumb 2 Stretched: Range 75 byteable
            SetCompressedUshort(ref CBytes, ref floatArray, 58, 52, true); // Left Thumb 3 Stretched: Range 75 byteable

            SetCompressedUshort(ref CBytes, ref floatArray, 59, 53, false); // Left Index 1 Stretched: Range 100 byteable
            SetCompressedUshort(ref CBytes, ref floatArray, 60, 54, true); // Left Index Spread: Range 40 byteable
            SetCompressedUshort(ref CBytes, ref floatArray, 61, 55, true); // Left Index 2 Stretched: Range 90 byteable
            SetCompressedUshort(ref CBytes, ref floatArray, 62, 56, true); // Left Index 3 Stretched: Range 90 byteable

            SetCompressedUshort(ref CBytes, ref floatArray, 63, 57, false); // Left Middle 1 Stretched: Range 100 byteable
            SetCompressedUshort(ref CBytes, ref floatArray, 64, 58, true); // Left Middle Spread: Range 15 byteable
            SetCompressedUshort(ref CBytes, ref floatArray, 65, 59, true); // Left Middle 2 Stretched: Range 90 byteable
            SetCompressedUshort(ref CBytes, ref floatArray, 66, 60, true); // Left Middle 3 Stretched: Range 90 byteable

            SetCompressedUshort(ref CBytes, ref floatArray, 67, 61, false); // Left Ring 1 Stretched: Range 100 byteable
            SetCompressedUshort(ref CBytes, ref floatArray, 68, 62, true); // Left Ring Spread: Range 15 byteable
            SetCompressedUshort(ref CBytes, ref floatArray, 69, 63, true); // Left Ring 2 Stretched: Range 90 byteable
            SetCompressedUshort(ref CBytes, ref floatArray, 70, 64, true); // Left Ring 3 Stretched: Range 90 byteable

            SetCompressedUshort(ref CBytes, ref floatArray, 71, 65, false); // Left Little 1 Stretched: Range 100 byteable
            SetCompressedUshort(ref CBytes, ref floatArray, 72, 66, true); // Left Little Spread: Range 40 byteable
            SetCompressedUshort(ref CBytes, ref floatArray, 73, 67, true); // Left Little 2 Stretched: Range 90 byteable
            SetCompressedUshort(ref CBytes, ref floatArray, 74, 68, true); // Left Little 3 Stretched: Range 90 byteable
        }

        // ----------------------
        // Section: Right Hand Fingers (indices 75..94 map to compressed slots 69..88)
        // ----------------------
        private static void CompressRightHandFingers(ref float[] floatArray)
        {
            SetCompressedUshort(ref CBytes, ref floatArray, 75, 69, false); // Right Thumb 1 Stretched: Range 40 byteable
            SetCompressedUshort(ref CBytes, ref floatArray, 76, 70, false); // Right Thumb Spread: Range 50 byteable
            SetCompressedUshort(ref CBytes, ref floatArray, 77, 71, true); // Right Thumb 2 Stretched: Range 75 byteable
            SetCompressedUshort(ref CBytes, ref floatArray, 78, 72, true); // Right Thumb 3 Stretched: Range 75 byteable

            SetCompressedUshort(ref CBytes, ref floatArray, 79, 73, false); // Right Index 1 Stretched: Range 100 byteable
            SetCompressedUshort(ref CBytes, ref floatArray, 80, 74, true); // Right Index Spread: Range 40 byteable
            SetCompressedUshort(ref CBytes, ref floatArray, 81, 75, true); // Right Index 2 Stretched: Range 90 byteable
            SetCompressedUshort(ref CBytes, ref floatArray, 82, 76, true); // Right Index 3 Stretched: Range 90 byteable

            SetCompressedUshort(ref CBytes, ref floatArray, 83, 77, false); // Right Middle 1 Stretched: Range 100 byteable
            SetCompressedUshort(ref CBytes, ref floatArray, 84, 78, true); // Right Middle Spread: Range 15 byteable
            SetCompressedUshort(ref CBytes, ref floatArray, 85, 79, true); // Right Middle 2 Stretched: Range 90 byteable
            SetCompressedUshort(ref CBytes, ref floatArray, 86, 80, true); // Right Middle 3 Stretched: Range 90 byteable

            SetCompressedUshort(ref CBytes, ref floatArray, 87, 81, false); // Right Ring 1 Stretched: Range 100 byteable
            SetCompressedUshort(ref CBytes, ref floatArray, 88, 82, true); // Right Ring Spread: Range 15 byteable
            SetCompressedUshort(ref CBytes, ref floatArray, 89, 83, true); // Right Ring 2 Stretched: Range 90 byteable
            SetCompressedUshort(ref CBytes, ref floatArray, 90, 84, true); // Right Ring 3 Stretched: Range 90 byteable

            SetCompressedUshort(ref CBytes, ref floatArray, 91, 85, false); // Right Little 1 Stretched: Range 100 byteable
            SetCompressedUshort(ref CBytes, ref floatArray, 92, 86, true); // Right Little Spread: Range 40 byteable
            SetCompressedUshort(ref CBytes, ref floatArray, 93, 87, true); // Right Little 2 Stretched: Range 90 byteable
            SetCompressedUshort(ref CBytes, ref floatArray, 94, 88, true); // Right Little 3 Stretched: Range 90 byteable
        }

        // ----------------------
        // Common writer (shared by all sections)
        // ----------------------
        public static void SetCompressedUshort(ref byte[][] compressedBytes, ref float[] value, int index, int compressedIndex, bool asByte)
        {
            float min = BasisMuscleRange.MinMuscle[index];
            float max = BasisMuscleRange.MaxMuscle[index];
            float range = BasisMuscleRange.RangeMuscle[index];

            float clamped = math.clamp(value[index], min, max);
            float normalized = (range == 0f) ? 0f : (clamped - min) / range;

            int requiredLength = asByte ? 1 : 2;

            if (compressedBytes[compressedIndex] == null || compressedBytes[compressedIndex].Length != requiredLength)
                compressedBytes[compressedIndex] = new byte[requiredLength];

            if (asByte)
            {
                byte compressed = (byte)math.round(normalized * 255f);
                compressedBytes[compressedIndex][0] = compressed;
            }
            else
            {
                ushort compressed = (ushort)math.round(normalized * 65535f);
                compressedBytes[compressedIndex][0] = (byte)(compressed & 0xFF);
                compressedBytes[compressedIndex][1] = (byte)(compressed >> 8);
            }
        }
        public static void CompressScale(float scale, ref LocalAvatarSyncMessage message, ref int offset)
        {
            const float Min = 0.005f;
            const float Max = 150f;
            const float range = Max - Min;

            float clamped = math.clamp(scale, Min, Max);
            float normalized = (clamped - Min) / range;

            ushort compressed = (ushort)(normalized * BasisMuscleRange.UShortRangeDifference);
            BasisUnityBitPackerExtensionsUnsafe.WriteUShort(compressed, ref message.array, ref offset);
        }

        private static void EnsureTransmitterIsInitialized(BasisNetworkTransmitter transmitter, Animator animator)
        {

            if (transmitter.PoseHandler == null)
                transmitter.PoseHandler = new HumanPoseHandler(animator.avatar, animator.transform);
        }
    }
}
