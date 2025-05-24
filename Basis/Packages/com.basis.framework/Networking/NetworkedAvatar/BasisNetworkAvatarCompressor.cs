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
            int offset = 0;
            CompressAvatarData(ref offset, ref Transmit.FloatArray,ref Transmit.UshortArray, ref Transmit.LASM,Transmit.PoseHandler, Transmit.HumanPose, Anim);

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
            BasisNetworkManagement.LocalPlayerPeer.Send(Transmit.AvatarSendWriter, BasisNetworkCommons.MovementChannel, DeliveryMethod.Sequenced);
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
            int Offset = 0;
            LocalAvatarSyncMessage = new LocalAvatarSyncMessage();
            CompressAvatarData(ref Offset, ref FloatArray, ref UshortArray, ref LocalAvatarSyncMessage, PoseHandler, HumanPose, Anim);
        }
        [BurstCompile]
        public static void CompressAvatarData(ref int Offset, ref float[] FloatArray, ref ushort[] NetworkSend, ref LocalAvatarSyncMessage LocalAvatarSyncMessage, HumanPoseHandler Handler, HumanPose PoseHandler, Animator Anim)
        {
            if (Handler == null)
            {
                Handler = new HumanPoseHandler(Anim.avatar, Anim.transform);
            }
            Offset = 0;
            // Retrieve the human pose from the Animator
            Handler.GetHumanPose(ref PoseHandler);

            // Copy muscles [0..14]
            Array.Copy(PoseHandler.muscles, 0, FloatArray, 0, BasisAvatarMuscleRange.FirstBuffer);

            // Copy muscles [21..end]
            Array.Copy(PoseHandler.muscles, BasisAvatarMuscleRange.SecondBuffer, FloatArray, BasisAvatarMuscleRange.FirstBuffer, BasisAvatarMuscleRange.SizeAfterGap);
            //we write position first so we can use that on the server
            BasisUnityBitPackerExtensionsUnsafe.WriteVectorFloatToBytes(Anim.bodyPosition, ref LocalAvatarSyncMessage.array, ref Offset);
            BasisUnityBitPackerExtensionsUnsafe.WriteQuaternionToBytes(Anim.bodyRotation, ref LocalAvatarSyncMessage.array, ref Offset, BasisNetworkPlayer.RotationCompression);

            if(NetworkSend == null)
            {
                BasisDebug.LogError("Network send was null!");
                NetworkSend = new ushort[LocalAvatarSyncMessage.StoredBones];
            }
            if (FloatArray == null)
            {
                BasisDebug.LogError("FloatArray send was null!");
                FloatArray = new float[LocalAvatarSyncMessage.StoredBones];
            }

            var NetworkOutData = NetworkSend;
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

            networkSendNative.CopyTo(NetworkSend);

            floatArrayNative.Dispose();
            minMuscleNative.Dispose();
            maxMuscleNative.Dispose();
            rangeMuscleNative.Dispose();
            networkSendNative.Dispose();

            BasisUnityBitPackerExtensionsUnsafe.WriteUShortsToBytes(NetworkOutData, ref LocalAvatarSyncMessage.array, ref Offset);
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
