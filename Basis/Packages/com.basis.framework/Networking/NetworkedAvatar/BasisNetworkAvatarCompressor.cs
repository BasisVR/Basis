using Basis.Network.Core;
using Basis.Scripts.Networking.Compression;
using Basis.Scripts.Networking.Transmitters;
using Basis.Scripts.Profiler;
using LiteNetLib;
using System;
using System.Linq;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using static SerializableBasis;

namespace Basis.Scripts.Networking.NetworkedAvatar
{
    public static class BasisNetworkAvatarCompressor
    {
        public static void Compress(BasisNetworkTransmitter Transmit, Animator Anim)
        {
            if (Transmit.UshortArray == null)
            {
                BasisDebug.LogError("Network send was null!");
                Transmit.UshortArray = new ushort[LocalAvatarSyncMessage.StoredBones];
            }
            if (Transmit.FloatArray == null)
            {
                BasisDebug.LogError("FloatArray send was null!");
                Transmit.FloatArray = new float[LocalAvatarSyncMessage.StoredBones];
            }
            if (Transmit.PoseHandler == null)
            {
                Transmit.PoseHandler = new HumanPoseHandler(Anim.avatar, Anim.transform);
            }
            if (Transmit.LASM.array == null || Transmit.LASM.array.Length == 0)
            {
                Transmit.LASM.array = new byte[LocalAvatarSyncMessage.AvatarSyncSize];
            }
            // Retrieve the human pose from the Animator
            Transmit.PoseHandler.GetHumanPose(ref Transmit.HumanPose);

            CompressAvatarData(ref Transmit.FloatArray, ref Transmit.UshortArray, ref Transmit.LASM, Transmit.PoseHandler, Transmit.HumanPose, Anim);

            if (Transmit.SendingOutAvatarData.Count == 0)
            {
                Transmit.LASM.AdditionalAvatarDatas = null;
            }
            else
            {
                Transmit.LASM.AdditionalAvatarDatas = Transmit.SendingOutAvatarData.Values.ToArray();
                // BasisDebug.Log("Sending out AvatarData " + Transmit.SendingOutAvatarData.Count);
            }
            Transmit.LASM.Serialize(Transmit.AvatarSendWriter);
            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.LocalAvatarSync, Transmit.AvatarSendWriter.Length);
            BasisNetworkManagement.LocalPlayerPeer.Send(Transmit.AvatarSendWriter, BasisNetworkCommons.PlayerAvatarChannel, DeliveryMethod.Sequenced);
            Transmit.AvatarSendWriter.Reset();
            Transmit.ClearAdditional();
        }
        public static void InitalAvatarData(Animator Anim, out LocalAvatarSyncMessage LocalAvatarSyncMessage)
        {
            HumanPoseHandler PoseHandler = new HumanPoseHandler(Anim.avatar, Anim.transform);
            HumanPose HumanPose = new HumanPose();
            PoseHandler.GetHumanPose(ref HumanPose);
            float[] FloatArray = new float[LocalAvatarSyncMessage.StoredBones];
            ushort[] UshortArray = new ushort[LocalAvatarSyncMessage.StoredBones];
            LocalAvatarSyncMessage = new LocalAvatarSyncMessage();
            if (LocalAvatarSyncMessage.array == null || LocalAvatarSyncMessage.array.Length == 0)
            {
                LocalAvatarSyncMessage.array = new byte[LocalAvatarSyncMessage.AvatarSyncSize];
            }
            CompressAvatarData(ref FloatArray, ref UshortArray, ref LocalAvatarSyncMessage, PoseHandler, HumanPose, Anim);
        }
        [BurstCompile]
        public static void CompressAvatarData( ref float[] FloatArray, ref ushort[] NetworkSend, ref LocalAvatarSyncMessage LocalAvatarSyncMessage, HumanPoseHandler Handler, HumanPose PoseHandler, Animator Anim)
        {
            int Offset = 0;
            // Copy muscles [0..14]
            Array.Copy(PoseHandler.muscles, 0, FloatArray, 0, BasisAvatarMuscleRange.FirstBuffer);

            // Copy muscles [21..end]
            Array.Copy(PoseHandler.muscles, BasisAvatarMuscleRange.SecondBuffer, FloatArray, BasisAvatarMuscleRange.FirstBuffer, BasisAvatarMuscleRange.SizeAfterGap);

            //we write position first so we can use that on the server
            BasisUnityBitPackerExtensionsUnsafe.WriteVectorFloatToBytes(Anim.bodyPosition, ref LocalAvatarSyncMessage.array, ref Offset);
            BasisUnityBitPackerExtensionsUnsafe.WriteQuaternionToBytes(Anim.bodyRotation, ref LocalAvatarSyncMessage.array, ref Offset, BasisNetworkPlayer.RotationCompression);

            CompressAvatarMuscles(ref NetworkSend, ref FloatArray, ref LocalAvatarSyncMessage, ref Offset);
            CompressScale(Anim.transform.localScale.y, ref LocalAvatarSyncMessage, ref Offset);
        }
        public static void CompressAvatarMuscles(ref ushort[] NetworkOutData, ref float[] FloatArray, ref LocalAvatarSyncMessage LocalAvatarSyncMessage, ref int Offset)
        {
            NativeArray<float> floatArrayNative = new NativeArray<float>(FloatArray, Allocator.TempJob);
            NativeArray<float> minMuscleNative = new NativeArray<float>(BasisAvatarMuscleRange.MinMuscle, Allocator.TempJob);
            NativeArray<float> maxMuscleNative = new NativeArray<float>(BasisAvatarMuscleRange.MaxMuscle, Allocator.TempJob);
            NativeArray<float> rangeMuscleNative = new NativeArray<float>(BasisAvatarMuscleRange.RangeMuscle, Allocator.TempJob);
            NativeArray<ushort> networkSendNative = new NativeArray<ushort>(LocalAvatarSyncMessage.StoredBones, Allocator.TempJob);

            CompressMusclesJob MuscleJob = new CompressMusclesJob
            {
                ValueArray = floatArrayNative,
                MinMuscle = minMuscleNative,
                MaxMuscle = maxMuscleNative,
                valueDiffence = rangeMuscleNative,
                NetworkSend = networkSendNative
            };

            JobHandle handle = MuscleJob.Schedule(LocalAvatarSyncMessage.StoredBones, 64);
            handle.Complete();

            networkSendNative.CopyTo(NetworkOutData);

            floatArrayNative.Dispose();
            minMuscleNative.Dispose();
            maxMuscleNative.Dispose();
            rangeMuscleNative.Dispose();
            networkSendNative.Dispose();

            BasisUnityBitPackerExtensionsUnsafe.WriteUShortsToBytes(NetworkOutData, ref LocalAvatarSyncMessage.array, ref Offset);
        }
        public static void CompressScale(float Scale, ref LocalAvatarSyncMessage LocalAvatarSyncMessage, ref int Offset)
        {
            //we can squeeze out more 
            const float MinimumValueSupported = 0.005f;
            const float MaximumValueSupported = 150;
            const float valueDiffence = MaximumValueSupported - MinimumValueSupported;

            //basis does not support ununiform scaling, if your avatar is not uniform you need to get help. - dooly
            float value = math.clamp(Scale, MinimumValueSupported, MaximumValueSupported);
            float normalized = (value - MinimumValueSupported) / valueDiffence; // 0..1
            ushort ScaleUshort = (ushort)(normalized * ushortRangeDifference);

            BasisUnityBitPackerExtensions.WriteUShortToBytes(ScaleUshort, ref LocalAvatarSyncMessage.array, ref Offset);
        }
        [BurstCompile]
        public struct CompressMusclesJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float> ValueArray;
            [ReadOnly] public NativeArray<float> MinMuscle;
            [ReadOnly] public NativeArray<float> MaxMuscle;
            [ReadOnly] public NativeArray<float> valueDiffence;
            [WriteOnly] public NativeArray<ushort> NetworkSend;
            public void Execute(int index)
            {
                float value = math.clamp(ValueArray[index], MinMuscle[index], MaxMuscle[index]);
                float normalized = (value - MinMuscle[index]) / valueDiffence[index]; // 0..1
                NetworkSend[index] = (ushort)(normalized * ushortRangeDifference); // Assuming ushortRangeDifference is ushort.MaxValue
            }
        }
        private const ushort UShortMin = ushort.MinValue; // 0
        private const ushort UShortMax = ushort.MaxValue; // 65535
        private const ushort ushortRangeDifference = UShortMax - UShortMin;
    }
}
