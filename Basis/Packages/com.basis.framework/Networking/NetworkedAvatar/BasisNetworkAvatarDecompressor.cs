using Basis.Scripts.Networking.Compression;
using Basis.Scripts.Networking.Recievers;
using Basis.Scripts.Profiler;
using System;
using static SerializableBasis;
using Vector3 = UnityEngine.Vector3;
namespace Basis.Scripts.Networking.NetworkedAvatar
{
    public static class BasisNetworkAvatarDecompressor
    {
        /// <summary>
        /// Single API to handle all avatar decompression tasks.
        /// </summary>
        public static void DecompressAndProcessAvatar(BasisNetworkReceiver baseReceiver, ServerSideSyncPlayerMessage syncMessage, ushort PlayerId)
        {
            if (syncMessage.avatarSerialization.array == null)
            {
                throw new ArgumentException("Cant Serialize Avatar Data");
            }
            int Length = syncMessage.avatarSerialization.array.Length;
            baseReceiver.Offset = 0;
            AvatarBuffer avatarBuffer = new AvatarBuffer
            {
                Position = BasisUnityBitPackerExtensions.ReadVectorFloatFromBytes(ref syncMessage.avatarSerialization.array, ref baseReceiver.Offset),
                rotation = BasisUnityBitPackerExtensions.ReadQuaternionFromBytes(ref syncMessage.avatarSerialization.array, BasisNetworkPlayer.RotationCompression, ref baseReceiver.Offset)
            };
            BasisUnityBitPackerExtensions.ReadMusclesFromBytes(ref syncMessage.avatarSerialization.array, ref baseReceiver.CopyData, ref baseReceiver.Offset);
            for (int Index = 0; Index < LocalAvatarSyncMessage.StoredBones; Index++)
            {
                avatarBuffer.Muscles[Index] = Decompress(baseReceiver.CopyData[Index], BasisNetworkPlayer.MinMuscle[Index], BasisNetworkPlayer.MaxMuscle[Index]);
            }
            avatarBuffer.Scale = Vector3.one;
            BasisNetworkProfiler.ServerSideSyncPlayerMessageCounter.Sample(Length);
            avatarBuffer.SecondsInterval = syncMessage.interval / 1000.0f;
            baseReceiver.EnQueueAvatarBuffer(ref avatarBuffer);
            /*
            if (syncMessage.avatarSerialization.hasAdditionalAvatarData)
            {
                int Count = syncMessage.avatarSerialization.AdditionalAvatarDatas.Length;
              //  BasisDebug.Log("Rec out AvatarData " + Count);
                for (int Index = 0; Index < Count; Index++)
                {
                    AdditionalAvatarData Data = syncMessage.avatarSerialization.AdditionalAvatarDatas[Index];
                    baseReceiver.Player.BasisAvatar.OnNetworkMessageReceived?.Invoke(PlayerId, Data.messageIndex, Data.array, LiteNetLib.DeliveryMethod.Sequenced);
                }
            }
            */
        }
        /// <summary>
        /// Inital Payload
        /// </summary>
        /// <param name="baseReceiver"></param>
        /// <param name="syncMessage"></param>
        /// <exception cref="ArgumentException"></exception>
        public static void DecompressAndProcessAvatar(BasisNetworkReceiver baseReceiver, LocalAvatarSyncMessage syncMessage, ushort PlayerId)
        {
            if (syncMessage.array == null)
            {
                throw new ArgumentException("Cant Serialize Avatar Data");
            }
            int Length = syncMessage.array.Length;
            baseReceiver.Offset = 0;
            AvatarBuffer avatarBuffer = new AvatarBuffer
            {
                Position = BasisUnityBitPackerExtensions.ReadVectorFloatFromBytes(ref syncMessage.array, ref baseReceiver.Offset),
                rotation = BasisUnityBitPackerExtensions.ReadQuaternionFromBytes(ref syncMessage.array, BasisNetworkPlayer.RotationCompression, ref baseReceiver.Offset)
            };
            BasisUnityBitPackerExtensions.ReadMusclesFromBytesAsUShort(ref syncMessage.array, ref baseReceiver.CopyData, ref baseReceiver.Offset);
            //    UnityEngine.BasisDebug.Log("avatar Pos " + avatarBuffer.Position);
            if (avatarBuffer.Muscles == null)
            {
                avatarBuffer.Muscles = new float[LocalAvatarSyncMessage.StoredBones];
            }
            for (int Index = 0; Index < LocalAvatarSyncMessage.StoredBones; Index++)
            {
                avatarBuffer.Muscles[Index] = Decompress(baseReceiver.CopyData[Index], BasisNetworkPlayer.MinMuscle[Index], BasisNetworkPlayer.MaxMuscle[Index]);
            }
            avatarBuffer.Scale = Vector3.one;
            BasisNetworkProfiler.ServerSideSyncPlayerMessageCounter.Sample(Length);
            avatarBuffer.SecondsInterval = 0.01f;
            baseReceiver.EnQueueAvatarBuffer(ref avatarBuffer);
            /*
            if (syncMessage.hasAdditionalAvatarData)
            {
                int Count = syncMessage.AdditionalAvatarDatas.Length;
               //BasisDebug.Log("Rec out AvatarData " + Count);
                for (int Index = 0; Index < Count; Index++)
                {
                    AdditionalAvatarData Data = syncMessage.AdditionalAvatarDatas[Index];
                    //wont ever work    baseReceiver.Player.BasisAvatar.OnNetworkMessageReceived?.Invoke(PlayerId, Data.messageIndex, Data.array, LiteNetLib.DeliveryMethod.Sequenced);
                }
            }
            */
        }
        public static float Decompress(ushort value, float MinValue, float MaxValue)
        {
            // Map the ushort value back to the float range
            float normalized = (float)value / FloatRangeDifference; // 0..1  - UShortMin
            return normalized * (float)(MaxValue - MinValue) + MinValue;
        }

        private const ushort UShortMin = ushort.MinValue; // 0
        private const ushort UShortMax = ushort.MaxValue; // 65535
        private const float FloatRangeDifference = UShortMax - UShortMin;
    }
}
