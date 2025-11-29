using Basis.Scripts.Networking.Compression;
using Basis.Scripts.Networking.Receivers;
using Basis.Scripts.Profiler;
using System;
using Unity.Collections;
using Unity.Mathematics;
using static SerializableBasis;
namespace Basis.Scripts.Networking.NetworkedAvatar
{
    public static class BasisNetworkAvatarDecompressor
    {
        private const float MinimumValueSupported = 0.005f;
        private const float MaximumValueSupported = 150f;
        private const ushort UShortMin = ushort.MinValue;
        private const ushort UShortMax = ushort.MaxValue;
        private const float FloatRangeDifference = UShortMax - UShortMin;
        public static void DecompressAndProcessAvatar(BasisNetworkReceiver baseReceiver, ServerSideSyncPlayerMessage syncMessage)
        {
            if (syncMessage.avatarSerialization.array == null)
            {
                throw new ArgumentException("Cannot serialize avatar data.");
            }
            byte[] data = syncMessage.avatarSerialization.array;
            int length = data.Length;
            if (length >= LocalAvatarSyncMessage.AvatarSyncSize)
            {
                int offset = 0;
                double Interval = (double)BasisNetworkManagement.ServerMetaDataMessage.SyncInterval;// Interval + syncMessage.interval
                BasisAvatarBuffer avatarBuffer = CreateAvatarBuffer(data, ref offset, (double)(Interval + (double)syncMessage.interval) / 1000f);
                EnqueueAndProcessAdditionalData(baseReceiver, avatarBuffer, syncMessage.avatarSerialization, length);
            }
            else
            {
                BasisDebug.LogError("Data did not have enough for AvatarsyncMessage", BasisDebug.LogTag.Networking);
            }
        }
        /// <summary>
        /// tied to initalization
        /// </summary>
        /// <param name="baseReceiver"></param>
        /// <param name="avatarSerialization"></param>
        /// <exception cref="ArgumentException"></exception>
        public static void DecompressAndProcessAvatar(BasisNetworkReceiver baseReceiver, LocalAvatarSyncMessage avatarSerialization)
        {
            if (avatarSerialization.array == null)
            {
                throw new ArgumentException("Cannot serialize inital avatar data.");
            }
            byte[] data = avatarSerialization.array;
            int length = data.Length;
            if (length >= LocalAvatarSyncMessage.AvatarSyncSize)
            {
                int offset = 0;
                BasisAvatarBuffer avatarBuffer = CreateAvatarBuffer(data, ref offset, 0.01f);
                EnqueueAndProcessAdditionalData(baseReceiver, avatarBuffer, avatarSerialization, length);
            }
            else
            {
                BasisDebug.LogError("Data did not have enough for AvatarsyncMessage", BasisDebug.LogTag.Networking);
            }
        }

        private static BasisAvatarBuffer CreateAvatarBuffer(byte[] data, ref int offset, double SecondsInterval)
        {
            BasisAvatarBuffer Buffer = BasisAvatarBufferPool.Get();
            Buffer.Position = BasisUnityBitPackerExtensionsUnsafe.ReadPosition(ref data, ref offset);
            if (!IsFinite(Buffer.Position))
            {
                // If position broke, keep last known good (0 if none)
                Buffer.Position = new Unity.Mathematics.float3(0, 0, 0);
            }
            Buffer.Rotation = SanitizeRotation(BasisUnityBitPackerExtensionsUnsafe.ReadQuaternionFromBytes(ref data, BasisNetworkPlayer.RotationCompression, ref offset));
            DecompressAvatarMuscles_NoLoop(data, ref Buffer.Muscles, ref offset);
            Buffer.Scale = MuscleDecompress(BasisUnityBitPackerExtensionsUnsafe.ReadUShort(ref data, ref offset), MinimumValueSupported, MaximumValueSupported);

            // Seconds interval — clamp to a sane minimum so interpolation works
            if (!math.isfinite((float)SecondsInterval) || SecondsInterval <= 0)
            {
                Buffer.SecondsInterval = 1.0 / 60.0;
            }
            else
            {
                Buffer.SecondsInterval = SecondsInterval;
            }
            return Buffer;
        }
        private static quaternion SanitizeRotation(quaternion q)
        {
            // If not finite or nearly zero length, use identity
            if (!math.isfinite(q.value.x) || !math.isfinite(q.value.y) || !math.isfinite(q.value.z) || !math.isfinite(q.value.w))
                return quaternion.identity;

            float magSq = q.value.x * q.value.x + q.value.y * q.value.y + q.value.z * q.value.z + q.value.w * q.value.w;
            if (magSq < 1e-8f) return quaternion.identity;
            return math.normalize(q);
        }
        private static bool IsFinite(Unity.Mathematics.float3 v) => math.isfinite(v.x) && math.isfinite(v.y) && math.isfinite(v.z);
        public static void DecompressAvatarMuscles_NoLoop(byte[] data, ref NativeArray<float> outputArray, ref int offset)
        {
            int dataPos = offset;

            float[] floatArray = outputArray.ToArray();
            // Sections in the same order as the original method
            DecompressSpineChestHead(data, ref dataPos, ref floatArray);

            // no need to put this data on the network! 6 in total (saves between 6 and 16 bytes)
            //DecompressEyesJaw(data, ref dataPos, floatArray); // (intentionally skipped as in original)

            DecompressLeftLeg(data, ref dataPos, floatArray);
            DecompressRightLeg(data, ref dataPos, floatArray);
            DecompressLeftArm(data, ref dataPos, floatArray);
            DecompressRightArm(data, ref dataPos, floatArray);
            DecompressLeftHandFingers(data, ref dataPos, floatArray);
            DecompressRightHandFingers(data, ref dataPos, floatArray);

            offset = dataPos;
            outputArray.CopyFrom(floatArray);
        }
        // ----------------------
        // Section: Spine/Chest/Head
        // ----------------------
        private static void DecompressSpineChestHead(byte[] data, ref int dataPos,ref float[] floatArray)
        {
            ReadCompressed(data, ref dataPos, 0, false, floatArray); // Spine Front-Back: Range 80
            ReadCompressed(data, ref dataPos, 1, false, floatArray); // Spine Left-Right: Range 80
            ReadCompressed(data, ref dataPos, 2, false, floatArray); // Spine Twist Left-Right: Range 80
            ReadCompressed(data, ref dataPos, 3, false, floatArray); // Chest Front-Back: Range 80
            ReadCompressed(data, ref dataPos, 4, false, floatArray); // Chest Left-Right: Range 80
            ReadCompressed(data, ref dataPos, 5, false, floatArray); // Chest Twist Left-Right: Range 80
            ReadCompressed(data, ref dataPos, 6, false, floatArray); // UpperChest Front-Back: Range 40
            ReadCompressed(data, ref dataPos, 7, false, floatArray); // UpperChest Left-Right: Range 40
            ReadCompressed(data, ref dataPos, 8, false, floatArray); // UpperChest Twist Left-Right: Range 40
            ReadCompressed(data, ref dataPos, 9, false, floatArray); // Neck Nod Down-Up: Range 80
            ReadCompressed(data, ref dataPos, 10, false, floatArray); // Neck Tilt Left-Right: Range 80
            ReadCompressed(data, ref dataPos, 11, false, floatArray); // Neck Turn Left-Right: Range 80
            ReadCompressed(data, ref dataPos, 12, false, floatArray); // Head Nod Down-Up: Range 80
            ReadCompressed(data, ref dataPos, 13, false, floatArray); // Head Tilt Left-Right: Range 80
            ReadCompressed(data, ref dataPos, 14, false, floatArray); // Head Turn Left-Right: Range 80
        }

        // ----------------------
        // Section: Eyes/Jaw (Skipped like original)
        // ----------------------
        private static void DecompressEyesJaw(byte[] data, ref int dataPos, float[] floatArray)
        {
            // no need to put this data on the network! 6 in total (saves between 6 and 16 bytes)
            // ReadCompressed(data, ref dataPos, 15, true,  floatArray); // Left Eye Down-Up: Range 25 byteable
            // ReadCompressed(data, ref dataPos, 16, true,  floatArray); // Left Eye In-Out: Range 40 byteable
            // ReadCompressed(data, ref dataPos, 17, true,  floatArray); // Right Eye Down-Up: Range 25 byteable
            // ReadCompressed(data, ref dataPos, 18, true,  floatArray); // Right Eye In-Out: Range 40 byteable
            // ReadCompressed(data, ref dataPos, 19, true,  floatArray); // Jaw Close: Range 20 byteable
            // ReadCompressed(data, ref dataPos, 20, true,  floatArray); // Jaw Left-Right: Range 20 byteable
        }

        // ----------------------
        // Section: Left Leg
        // ----------------------
        private static void DecompressLeftLeg(byte[] data, ref int dataPos, float[] floatArray)
        {
            ReadCompressed(data, ref dataPos, 21, false, floatArray); // Left Upper Leg Front-Back: Range 140
            ReadCompressed(data, ref dataPos, 22, false, floatArray); // Left Upper Leg In-Out: Range 120
            ReadCompressed(data, ref dataPos, 23, false, floatArray); // Left Upper Leg Twist In-Out: Range 120
            ReadCompressed(data, ref dataPos, 24, false, floatArray); // Left Lower Leg Stretch: Range 160
            ReadCompressed(data, ref dataPos, 25, false, floatArray); // Left Lower Leg Twist In-Out: Range 180
            ReadCompressed(data, ref dataPos, 26, false, floatArray); // Left Foot Up-Down: Range 100
            ReadCompressed(data, ref dataPos, 27, true, floatArray); // Left Foot Twist In-Out: Range 60 byteable
            ReadCompressed(data, ref dataPos, 28, false, floatArray); // Left Toes Up-Down: Range 100 byteable
        }

        // ----------------------
        // Section: Right Leg
        // ----------------------
        private static void DecompressRightLeg(byte[] data, ref int dataPos, float[] floatArray)
        {
            ReadCompressed(data, ref dataPos, 29, false, floatArray); // Right Upper Leg Front-Back: Range 140
            ReadCompressed(data, ref dataPos, 30, false, floatArray); // Right Upper Leg In-Out: Range 120
            ReadCompressed(data, ref dataPos, 31, false, floatArray); // Right Upper Leg Twist In-Out: Range 120
            ReadCompressed(data, ref dataPos, 32, false, floatArray); // Right Lower Leg Stretch: Range 160
            ReadCompressed(data, ref dataPos, 33, false, floatArray); // Right Lower Leg Twist In-Out: Range 180
            ReadCompressed(data, ref dataPos, 34, false, floatArray); // Right Foot Up-Down: Range 100
            ReadCompressed(data, ref dataPos, 35, true, floatArray); // Right Foot Twist In-Out: Range 60 byteable
            ReadCompressed(data, ref dataPos, 36, false, floatArray); // Right Toes Up-Down: Range 100 byteable
        }

        // ----------------------
        // Section: Left Arm
        // ----------------------
        private static void DecompressLeftArm(byte[] data, ref int dataPos, float[] floatArray)
        {
            ReadCompressed(data, ref dataPos, 37, false, floatArray); // Left Shoulder Down-Up: Range 45 byteable
            ReadCompressed(data, ref dataPos, 38, false, floatArray); // Left Shoulder Front-Back: Range 30 byteable
            ReadCompressed(data, ref dataPos, 39, false, floatArray); // Left Arm Down-Up: Range 160
            ReadCompressed(data, ref dataPos, 40, false, floatArray); // Left Arm Front-Back: Range 200
            ReadCompressed(data, ref dataPos, 41, false, floatArray); // Left Arm Twist In-Out: Range 180
            ReadCompressed(data, ref dataPos, 42, false, floatArray); // Left Forearm Stretch: Range 160
            ReadCompressed(data, ref dataPos, 43, false, floatArray); // Left Forearm Twist In-Out: Range 180
            ReadCompressed(data, ref dataPos, 44, false, floatArray); // Left Hand Down-Up: Range 160
            ReadCompressed(data, ref dataPos, 45, false, floatArray); // Left Hand In-Out: Range 80
        }

        // ----------------------
        // Section: Right Arm
        // ----------------------
        private static void DecompressRightArm(byte[] data, ref int dataPos, float[] floatArray)
        {
            ReadCompressed(data, ref dataPos, 46, false, floatArray); // Right Shoulder Down-Up: Range 45 byteable
            ReadCompressed(data, ref dataPos, 47, false, floatArray); // Right Shoulder Front-Back: Range 30 byteable
            ReadCompressed(data, ref dataPos, 48, false, floatArray); // Right Arm Down-Up: Range 160
            ReadCompressed(data, ref dataPos, 49, false, floatArray); // Right Arm Front-Back: Range 200
            ReadCompressed(data, ref dataPos, 50, false, floatArray); // Right Arm Twist In-Out: Range 180
            ReadCompressed(data, ref dataPos, 51, false, floatArray); // Right Forearm Stretch: Range 160
            ReadCompressed(data, ref dataPos, 52, false, floatArray); // Right Forearm Twist In-Out: Range 180
            ReadCompressed(data, ref dataPos, 53, false, floatArray); // Right Hand Down-Up: Range 160
            ReadCompressed(data, ref dataPos, 54, false, floatArray); // Right Hand In-Out: Range 80
        }

        // ----------------------
        // Section: Left Hand Fingers
        // ----------------------
        private static void DecompressLeftHandFingers(byte[] data, ref int dataPos, float[] floatArray)
        {
            ReadCompressed(data, ref dataPos, 55, false, floatArray); // Left Thumb 1 Stretched: Range 40 byteable
            ReadCompressed(data, ref dataPos, 56, false, floatArray); // Left Thumb Spread: Range 50 byteable
            ReadCompressed(data, ref dataPos, 57, true, floatArray); // Left Thumb 2 Stretched: Range 75 byteable
            ReadCompressed(data, ref dataPos, 58, true, floatArray); // Left Thumb 3 Stretched: Range 75 byteable

            ReadCompressed(data, ref dataPos, 59, false, floatArray); // Left Index 1 Stretched: Range 100 byteable
            ReadCompressed(data, ref dataPos, 60, true, floatArray); // Left Index Spread: Range 40 byteable
            ReadCompressed(data, ref dataPos, 61, true, floatArray); // Left Index 2 Stretched: Range 90 byteable
            ReadCompressed(data, ref dataPos, 62, true, floatArray); // Left Index 3 Stretched: Range 90 byteable

            ReadCompressed(data, ref dataPos, 63, false, floatArray); // Left Middle 1 Stretched: Range 100 byteable
            ReadCompressed(data, ref dataPos, 64, true, floatArray); // Left Middle Spread: Range 15 byteable
            ReadCompressed(data, ref dataPos, 65, true, floatArray); // Left Middle 2 Stretched: Range 90 byteable
            ReadCompressed(data, ref dataPos, 66, true, floatArray); // Left Middle 3 Stretched: Range 90 byteable

            ReadCompressed(data, ref dataPos, 67, false, floatArray); // Left Ring 1 Stretched: Range 100 byteable
            ReadCompressed(data, ref dataPos, 68, true, floatArray); // Left Ring Spread: Range 15 byteable
            ReadCompressed(data, ref dataPos, 69, true, floatArray); // Left Ring 2 Stretched: Range 90 byteable
            ReadCompressed(data, ref dataPos, 70, true, floatArray); // Left Ring 3 Stretched: Range 90 byteable

            ReadCompressed(data, ref dataPos, 71, false, floatArray); // Left Little 1 Stretched: Range 100 byteable
            ReadCompressed(data, ref dataPos, 72, true, floatArray); // Left Little Spread: Range 40 byteable
            ReadCompressed(data, ref dataPos, 73, true, floatArray); // Left Little 2 Stretched: Range 90 byteable
            ReadCompressed(data, ref dataPos, 74, true, floatArray); // Left Little 3 Stretched: Range 90 byteable
        }

        // ----------------------
        // Section: Right Hand Fingers
        // ----------------------
        private static void DecompressRightHandFingers(byte[] data, ref int dataPos, float[] floatArray)
        {
            ReadCompressed(data, ref dataPos, 75, false, floatArray); // Right Thumb 1 Stretched: Range 40 byteable
            ReadCompressed(data, ref dataPos, 76, false, floatArray); // Right Thumb Spread: Range 50 byteable
            ReadCompressed(data, ref dataPos, 77, true, floatArray); // Right Thumb 2 Stretched: Range 75 byteable
            ReadCompressed(data, ref dataPos, 78, true, floatArray); // Right Thumb 3 Stretched: Range 75 byteable

            ReadCompressed(data, ref dataPos, 79, false, floatArray); // Right Index 1 Stretched: Range 100 byteable
            ReadCompressed(data, ref dataPos, 80, true, floatArray); // Right Index Spread: Range 40 byteable
            ReadCompressed(data, ref dataPos, 81, true, floatArray); // Right Index 2 Stretched: Range 90 byteable
            ReadCompressed(data, ref dataPos, 82, true, floatArray); // Right Index 3 Stretched: Range 90 byteable

            ReadCompressed(data, ref dataPos, 83, false, floatArray); // Right Middle 1 Stretched: Range 100 byteable
            ReadCompressed(data, ref dataPos, 84, true, floatArray); // Right Middle Spread: Range 15 byteable
            ReadCompressed(data, ref dataPos, 85, true, floatArray); // Right Middle 2 Stretched: Range 90 byteable
            ReadCompressed(data, ref dataPos, 86, true, floatArray); // Right Middle 3 Stretched: Range 90 byteable

            ReadCompressed(data, ref dataPos, 87, false, floatArray); // Right Ring 1 Stretched: Range 100 byteable
            ReadCompressed(data, ref dataPos, 88, true, floatArray); // Right Ring Spread: Range 15 byteable
            ReadCompressed(data, ref dataPos, 89, true, floatArray); // Right Ring 2 Stretched: Range 90 byteable
            ReadCompressed(data, ref dataPos, 90, true, floatArray); // Right Ring 3 Stretched: Range 90 byteable

            ReadCompressed(data, ref dataPos, 91, false, floatArray); // Right Little 1 Stretched: Range 100 byteable
            ReadCompressed(data, ref dataPos, 92, true, floatArray); // Right Little Spread: Range 40 byteable
            ReadCompressed(data, ref dataPos, 93, true, floatArray); // Right Little 2 Stretched: Range 90 byteable
            ReadCompressed(data, ref dataPos, 94, true, floatArray); // Right Little 3 Stretched: Range 90 byteable
        }
        private static void ReadCompressed(byte[] data, ref int dataPos, int index, bool asByte, float[] floatArray)
        {
            float normalized;
            if (asByte)
            {
                byte compressed = data[dataPos];
                normalized = compressed / 255f;
                dataPos += 1;
            }
            else
            {
                // Little-endian ushort from two bytes
                int lo = data[dataPos];
                int hi = data[dataPos + 1];
                ushort compressed = (ushort)(lo | (hi << 8));
                normalized = compressed / 65535f;
                dataPos += 2;
            }

            float min = BasisMuscleRange.MinMuscle[index];
            float range = BasisMuscleRange.RangeMuscle[index];
            float max = BasisMuscleRange.MaxMuscle[index];

            float value = min + normalized * range;
            floatArray[index] = math.clamp(value, min, max);
        }
        public static float MuscleDecompress(ushort value, float minValue, float maxValue)
        {
            float normalized = value / FloatRangeDifference;
            return normalized * (maxValue - minValue) + minValue;
        }
        private static void EnqueueAndProcessAdditionalData(BasisNetworkReceiver baseReceiver, BasisAvatarBuffer avatarBuffer, LocalAvatarSyncMessage message, int dataLength)
        {
            // Add to profiler
            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.ServerSideSyncPlayer, dataLength);

            // Queue the avatar buffer
            baseReceiver.EnQueueAvatarBuffer(avatarBuffer);

            // Process additional avatar data
            if (message.AdditionalAvatarDataSize > 0 && message.AdditionalAvatarDatas != null)
            {
                bool isDifferentAvatar = message.LinkedAvatarIndex != baseReceiver.LastLinkedAvatarIndex;
                if (isDifferentAvatar)
                {
                    return;
                }

                for (int Index = 0; Index < message.AdditionalAvatarDataSize; Index++)
                {
                    AdditionalAvatarData data = message.AdditionalAvatarDatas[Index];

                    if (data.messageIndex < baseReceiver.NetworkBehaviourCount)
                    {
                        baseReceiver.NetworkBehaviours[data.messageIndex].OnNetworkMessageServerReductionSystem(data.array);
                    }
                }
            }
        }
    }
}
