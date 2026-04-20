using Basis.Network.Core.Compression;
using Basis.Scripts.Networking.Compression;
using Basis.Scripts.Networking.Receivers;
using System;
using Unity.Mathematics;
using static SerializableBasis;
namespace Basis.Scripts.Networking.NetworkedAvatar
{
    public static class BasisNetworkAvatarDecompressor
    {
        public static void DecompressAndProcessAvatar(BasisNetworkReceiver baseReceiver, ServerSideSyncPlayerMessage syncMessage)
        {
            if (syncMessage.avatarSerialization.array == null)
            {
                throw new ArgumentException("Cannot serialize avatar data.");
            }

            byte[] data = syncMessage.avatarSerialization.array;
            int length = data.Length;

            BasisAvatarBitPacking.BitQuality q = (BasisAvatarBitPacking.BitQuality)syncMessage.avatarSerialization.DataQualityLevel;
            if (!BasisAvatarBitPacking.IsValidQuality(q))
            {
                BasisDebug.LogError($"Invalid avatar quality level {syncMessage.avatarSerialization.DataQualityLevel}", BasisDebug.LogTag.Networking);
                return;
            }
            int expected = BasisAvatarBitPacking.ConvertToSize(q);

            if (length >= expected)
            {
                int offset = 0;
                double interval = (double)BasisNetworkManagement.ServerMetaDataMessage.SyncInterval;
                if (TryCreateAvatarBuffer(data, ref offset, (interval + (double)syncMessage.interval) / 1000.0, q, out BasisAvatarBuffer avatarBuffer))
                {
                    avatarBuffer.Sequence = syncMessage.sequence;
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

            BasisAvatarBitPacking.BitQuality q = (BasisAvatarBitPacking.BitQuality)avatarSerialization.DataQualityLevel;
            if (!BasisAvatarBitPacking.IsValidQuality(q))
            {
                BasisDebug.LogError($"Invalid avatar quality level {avatarSerialization.DataQualityLevel}", BasisDebug.LogTag.Networking);
                return;
            }
            int expected = BasisAvatarBitPacking.ConvertToSize(q);

            if (length >= expected)
            {
                int offset = 0;
                if (TryCreateAvatarBuffer(data, ref offset, 0.01f, q, out BasisAvatarBuffer avatarBuffer))
                {
                    EnqueueAndProcessAdditionalData(baseReceiver, avatarBuffer, avatarSerialization);
                }
            }
            else
            {
                BasisDebug.LogError("Data did not have enough for AvatarsyncMessage", BasisDebug.LogTag.Networking);
            }
        }

        private static bool TryCreateAvatarBuffer(byte[] data, ref int offset, double secondsInterval, BasisAvatarBitPacking.BitQuality quality, out BasisAvatarBuffer basisAvatarBuffer)
        {
            basisAvatarBuffer = null;
            int startOffset = offset;

            if (!math.isfinite(secondsInterval))
            {
                goto Fail;
            }

            secondsInterval = math.clamp(secondsInterval, 1e-3, 1.0);

            basisAvatarBuffer = BasisAvatarBufferPool.Get();

            // Position
            if (!BasisUnityBitPackerExtensionsUnsafe.TryReadPosition(ref data, ref offset, out basisAvatarBuffer.Position))
            {
                goto Fail;
            }

            // Bone rotations (replaces muscle decompression)
            BasisBoneRotationUtils.DecompressBoneRotations(data, quality, ref basisAvatarBuffer.BoneRotations, ref offset);

            // Scale
            if (!BasisUnityBitPackerExtensionsUnsafe.TryReadUShort(ref data, ref offset, out ushort uScale))
            {
                goto Fail;
            }

            // Body rotation
            if (!BasisUnityBitPackerExtensionsUnsafe.TryReadCompressedQuaternionFromBytes(ref data, ref offset, out basisAvatarBuffer.Rotation))
            {
                goto Fail;
            }

            basisAvatarBuffer.Scale = BasisUnityBitPackerExtensionsUnsafe.DecompressScale(uScale);
            basisAvatarBuffer.SecondsInterval = secondsInterval;
            return true;

        Fail:
            offset = startOffset;
            if (basisAvatarBuffer != null)
            {
                BasisAvatarBufferPool.Release(basisAvatarBuffer);
                basisAvatarBuffer = null;
            }
            BasisDebug.LogError($"non finite data found in Decompression Stage, bailing.", BasisDebug.LogTag.Remote);
            return false;
        }

        private static void EnqueueAndProcessAdditionalData(BasisNetworkReceiver baseReceiver, BasisAvatarBuffer avatarBuffer, LocalAvatarSyncMessage message)
        {
            baseReceiver.EnQueueAvatarBuffer(avatarBuffer);

            if (message.AdditionalAvatarDataSize > 0 && message.AdditionalAvatarDatas != null)
            {
                bool isDifferentAvatar = message.LinkedAvatarIndex != baseReceiver.LastLinkedAvatarIndex;
                if (isDifferentAvatar) return;

                var behaviours = baseReceiver.NetworkBehaviours;
                int count = baseReceiver.NetworkBehaviourCount;
                if (behaviours == null) return;

                for (int Index = 0; Index < message.AdditionalAvatarDataSize; Index++)
                {
                    AdditionalAvatarData data = message.AdditionalAvatarDatas[Index];
                    if (data.messageIndex < count && data.messageIndex < behaviours.Length)
                        behaviours[data.messageIndex].OnNetworkMessageServerReductionSystem(data.array);
                }
            }
        }

    }
}
