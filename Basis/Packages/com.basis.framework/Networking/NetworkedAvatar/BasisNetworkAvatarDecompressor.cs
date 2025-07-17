using Basis.Scripts.Networking.Compression;
using Basis.Scripts.Networking.Receivers;
using Basis.Scripts.Profiler;
using System;
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

        public static void DecompressAndProcessAvatar(BasisNetworkReceiver baseReceiver, ServerSideSyncPlayerMessage syncMessage, ushort playerId)
        {
            if (syncMessage.avatarSerialization.array == null)
            {
                throw new ArgumentException("Cannot serialize avatar data.");
            }

            byte[] data = syncMessage.avatarSerialization.array;
            int offset = 0;
            int length = data.Length;

            BasisAvatarBuffer avatarBuffer = CreateAvatarBuffer(data, ref offset, baseReceiver);
            avatarBuffer.Scale = Decompress(ReadUShort(data, ref offset), MinimumValueSupported, MaximumValueSupported);
            avatarBuffer.SecondsInterval = syncMessage.interval / 1000f;

            EnqueueAndProcessAdditionalData(baseReceiver, ref avatarBuffer, syncMessage.avatarSerialization,length);
        }

        public static void DecompressAndProcessAvatar(BasisNetworkReceiver baseReceiver, LocalAvatarSyncMessage syncMessage, ushort playerId)
        {
            if (syncMessage.array == null)
            {
                throw new ArgumentException("Cannot serialize " +
                    "avatar data.");
            }

            byte[] data = syncMessage.array;
            int offset = 0;
            int length = data.Length;

            BasisAvatarBuffer avatarBuffer = CreateAvatarBuffer(data, ref offset, baseReceiver);
            avatarBuffer.Scale = Decompress(ReadUShort(data, ref offset), MinimumValueSupported, MaximumValueSupported);
            avatarBuffer.SecondsInterval = 0.01f;

            EnqueueAndProcessAdditionalData(baseReceiver, ref avatarBuffer, syncMessage, length);
        }

        private static BasisAvatarBuffer CreateAvatarBuffer(byte[] data, ref int offset, BasisNetworkReceiver baseReceiver)
        {
            var avatarBuffer = new BasisAvatarBuffer
            {
                Position = BasisUnityBitPackerExtensionsUnsafe.ReadVectorFloatFromBytes(ref data, ref offset),
                rotation = BasisUnityBitPackerExtensionsUnsafe.ReadQuaternionFromBytes(ref data, BasisNetworkPlayer.RotationCompression, ref offset),
                Muscles = new float[LocalAvatarSyncMessage.StoredBones]
            };

            BasisUnityBitPackerExtensionsUnsafe.ReadMusclesFromBytes(ref data, ref baseReceiver.CopyData, ref offset);

            for (int MuscleIndex = 0; MuscleIndex < LocalAvatarSyncMessage.StoredBones; MuscleIndex++)
            {
                avatarBuffer.Muscles[MuscleIndex] = Decompress(baseReceiver.CopyData[MuscleIndex], BasisAvatarMuscleRange.MinMuscle[MuscleIndex], BasisAvatarMuscleRange.MaxMuscle[MuscleIndex]);
            }

            return avatarBuffer;
        }

        private static void EnqueueAndProcessAdditionalData(BasisNetworkReceiver baseReceiver, ref BasisAvatarBuffer avatarBuffer, LocalAvatarSyncMessage message, int dataLength)
        {
            // Add to profiler
            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.ServerSideSyncPlayer, dataLength);

            // Queue the avatar buffer
            baseReceiver.EnQueueAvatarBuffer(ref avatarBuffer);

            // Process additional avatar data
            if (message.AdditionalAvatarDataSize > 0 && message.AdditionalAvatarDatas != null)
            {
                bool isDifferentAvatar = message.LinkedAvatarIndex != baseReceiver.LastLinkedAvatarIndex;

                for (int Index = 0; Index < message.AdditionalAvatarDataSize; Index++)
                {
                    AdditionalAvatarData data = message.AdditionalAvatarDatas[Index];

                    if (data.messageIndex < baseReceiver.NetworkBehaviourCount)
                    {
                        baseReceiver.NetworkBehaviours[data.messageIndex].OnNetworkMessageServerReductionSystem(data.array, isDifferentAvatar);
                    }
                }
            }
        }
        private static ushort ReadUShort(byte[] data, ref int offset)
        {
            return BasisUnityBitPackerExtensions.ReadUShortFromBytes(ref data, ref offset);
        }

        public static float Decompress(ushort value, float minValue, float maxValue)
        {
            float normalized = value / FloatRangeDifference;
            return normalized * (maxValue - minValue) + minValue;
        }
    }
}
