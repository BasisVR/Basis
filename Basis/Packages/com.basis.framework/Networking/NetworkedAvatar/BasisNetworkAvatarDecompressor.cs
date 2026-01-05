using Basis.Network.Core.Compression;
using Basis.Scripts.Networking.Compression;
using Basis.Scripts.Networking.Receivers;
using System;
using Unity.Mathematics;
using UnityEngine.UIElements;
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

            if (length >= BasisBitPackingConstants.AvatarSyncSize)
            {
                int offset = 0;
                double interval = (double)BasisNetworkManagement.ServerMetaDataMessage.SyncInterval;
                if (CreateAvatarBuffer(data, ref offset, (interval + (double)syncMessage.interval) / 1000.0, out BasisAvatarBuffer avatarBuffer))
                {
                    EnqueueAndProcessAdditionalData(baseReceiver, avatarBuffer, syncMessage.avatarSerialization);
                }
            }
            else
            {
                BasisDebug.LogError("Data did not have enough for AvatarsyncMessage", BasisDebug.LogTag.Networking);
            }
        }

        public static void DecompressAndProcessAvatar(BasisNetworkReceiver baseReceiver, LocalAvatarSyncMessage avatarSerialization)
        {
            if (avatarSerialization.array == null)
            {
                throw new ArgumentException("Cannot serialize initial avatar data.");
            }

            byte[] data = avatarSerialization.array;
            int length = data.Length;

            if (length >= BasisBitPackingConstants.AvatarSyncSize)
            {
                int offset = 0;
                if (CreateAvatarBuffer(data, ref offset, 0.01f, out BasisAvatarBuffer avatarBuffer))
                {
                    EnqueueAndProcessAdditionalData(baseReceiver, avatarBuffer, avatarSerialization);
                }
            }
            else
            {
                BasisDebug.LogError("Data did not have enough for AvatarsyncMessage", BasisDebug.LogTag.Networking);
            }
        }

        private static bool CreateAvatarBuffer(byte[] data, ref int offset, double secondsInterval, out BasisAvatarBuffer BasisAvatarBuffer)
        {
            BasisAvatarBuffer = BasisAvatarBufferPool.Get();
            BasisAvatarBuffer.Position = BasisUnityBitPackerExtensionsUnsafe.ReadPosition(ref data, ref offset);
            BasisAvatarBuffer.Rotation = SanitizeRotation(BasisUnityBitPackerExtensionsUnsafe.ReadQuaternionFromBytes(ref data, ref offset));
            BasisOrderedDataSet.DecompressAvatarMuscles_BitPacked(data, ref BasisAvatarBuffer.Muscles, ref offset);
            BasisAvatarBuffer.Scale = MuscleDecompress(BasisUnityBitPackerExtensionsUnsafe.ReadUShort(ref data, ref offset), MinimumValueSupported, MaximumValueSupported);
            // Reject NaN, Infinity, zero, negative, or insane values
            if (!math.isfinite(secondsInterval) || secondsInterval <= 0.0 || secondsInterval > 1.0)
            {
                BasisDebug.LogError($"SecondsInterval was {secondsInterval}, rejecting", BasisDebug.LogTag.Remote);
                BasisAvatarBufferPool.Release(BasisAvatarBuffer);
                return false;
            }
            BasisAvatarBuffer.SecondsInterval = secondsInterval;

            if (!math.all(math.isfinite(BasisAvatarBuffer.Position)))
            {
                BasisDebug.LogError("Non-finite Position detected, rejecting", BasisDebug.LogTag.Remote);
                BasisAvatarBufferPool.Release(BasisAvatarBuffer);
                return false;
            }

            if (!math.all(math.isfinite(BasisAvatarBuffer.Scale)))
            {
                BasisDebug.LogError("Non-finite Scale detected, rejecting", BasisDebug.LogTag.Remote);
                BasisAvatarBufferPool.Release(BasisAvatarBuffer);
                return false;
            }
            return true;
        }
        private static quaternion SanitizeRotation(quaternion q)
        {
            if (!math.isfinite(q.value.x) || !math.isfinite(q.value.y) || !math.isfinite(q.value.z) || !math.isfinite(q.value.w))
            {
                return quaternion.identity;
            }

            float magSq = q.value.x * q.value.x + q.value.y * q.value.y + q.value.z * q.value.z + q.value.w * q.value.w;
            if (magSq < 1e-8f)
            {
                return quaternion.identity;
            }

            return math.normalize(q);
        }
        public static float MuscleDecompress(ushort value, float minValue, float maxValue)
        {
            float normalized = value / FloatRangeDifference;
            return normalized * (maxValue - minValue) + minValue;
        }

        private static void EnqueueAndProcessAdditionalData(BasisNetworkReceiver baseReceiver, BasisAvatarBuffer avatarBuffer, LocalAvatarSyncMessage message)
        {
            baseReceiver.EnQueueAvatarBuffer(avatarBuffer);

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
