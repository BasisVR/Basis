using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Profiler;
using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using static SerializableBasis;

namespace Basis.Scripts.Networking.Receivers
{
    [DefaultExecutionOrder(15001)]
    [System.Serializable]
    public class BasisNetworkReceiver : BasisNetworkPlayer
    {
        public ushort[] CopyData = new ushort[LocalAvatarSyncMessage.StoredBones];
        [SerializeField]
        public BasisAudioReceiver AudioReceiverModule = new BasisAudioReceiver();
        [Header("Interpolation Settings")]
        public double delayTime = 0.1f; // How far behind real-time we want to stay, hopefully double is good.
        [SerializeField]
        public Queue<BasisAvatarBuffer> PayloadQueue = new Queue<BasisAvatarBuffer>();
        public BasisRemotePlayer RemotePlayer;
        public bool HasEvents = false;

        private NativeArray<float3> OutputVectors;      // Merged positions and scales
        private NativeArray<float3> TargetVectors; // Merged target positions and scales
        private NativeArray<float> musclesPreEuro;
        private NativeArray<float> targetMuscles;
        private NativeArray<float> EuroValuesOutput;
        private NativeArray<float2> positionFilters;
        private NativeArray<float2> derivativeFilters;

        public JobHandle musclesHandle;
        public JobHandle AvatarHandle;
        public UpdateAvatarMusclesJob musclesJob = new UpdateAvatarMusclesJob();
        public UpdateAvatarJob AvatarJob = new UpdateAvatarJob();
        public float[] MuscleFinalStageOutput = new float[LocalAvatarSyncMessage.StoredBones];
        public quaternion OutputRotation;
        public BasisAvatarBuffer First;
        public BasisAvatarBuffer Last;
        public static int BufferCapacityBeforeCleanup = 3;
        public float interpolationTime;
        public double TimeBeforeCompletion;
        public double TimeInThePast;
        public bool HasAvatarQueue;

        public BasisOneEuroFilterParallelJob oneEuroFilterJob;
        public  float MinCutoff = 0.001f;
        public  float Beta = 5f;
        public float DerivativeCutoff = 1.0f;

        public bool updateFilters;
        public bool enableEuroFilter = true;
        public JobHandle EuroFilterHandle;
        public Vector3 PositionOffset;
        public bool LogFirstError = false;
        public float[] Eyes = new float[4];
        /// <summary>
        /// Perform computations to interpolate and update avatar state.
        /// </summary>
        public void Compute(double TimeAsDouble)
        {
            if (HasAvatarQueue)
            {
                // Complete previously scheduled jobs to avoid scheduling over incomplete ones
                if (AvatarHandle.IsCompleted) AvatarHandle.Complete();
                if (musclesHandle.IsCompleted) musclesHandle.Complete();
                if (EuroFilterHandle.IsCompleted) EuroFilterHandle.Complete();

                // Calculate interpolation time
                interpolationTime = Mathf.Clamp01((float)((TimeAsDouble - TimeInThePast) / TimeBeforeCompletion));
                if (First == null)
                {
                    if (Last != null)
                    {
                        First = Last;
                        PayloadQueue.TryDequeue(out Last);
                        BasisDebug.LogError("Last != null filled in gap", BasisDebug.LogTag.Networking);
                    }
                    else
                    {
                        PayloadQueue.TryDequeue(out First);
                        BasisDebug.LogError("Last and first are null replacing First!", BasisDebug.LogTag.Networking);
                    }
                }
                if (Last == null)
                {
                    PayloadQueue.TryDequeue(out Last);
                    BasisDebug.LogError("Last == null tried to dequeue", BasisDebug.LogTag.Networking);

                }
                try
                {
                    TargetVectors[0] = Last.Position; // Target position at index 0
                    OutputVectors[0] = First.Position; // Position at index 0
                    Vector3 Scale = GetScale();
                    OutputVectors[1] = Scale;    // Scale at index 1
                    TargetVectors[1] = Scale;    // Target scale at index 1
                    musclesPreEuro.CopyFrom(First.Muscles);
                    targetMuscles.CopyFrom(Last.Muscles);
                }
                catch (Exception ex)
                {
                    // Log the full exception details, including stack trace
                    BasisDebug.LogError($"Error in Vector Set Or Muscle Set: {ex.Message}\nStack Trace:\n{ex.StackTrace}");
                }
                AvatarJob.Time = interpolationTime;


                //need to make sure AvatarJob and so on its complete and ready to be rescheduled

                AvatarHandle = AvatarJob.Schedule();

                // Muscle interpolation job
                musclesJob.Time = interpolationTime;
                musclesHandle = musclesJob.Schedule(LocalAvatarSyncMessage.StoredBones, 64, AvatarHandle);

                if (updateFilters)
                {
                    ForceUpdateFilters();
                }

                oneEuroFilterJob.DeltaTime = interpolationTime;
                EuroFilterHandle = oneEuroFilterJob.Schedule(LocalAvatarSyncMessage.StoredBones, 64, musclesHandle);
            }
        }
        public void Apply(double TimeAsDouble, float DeltaTime)
        {
            if (PoseHandler == null)
            {
                return;
            }
            try
            {
                if (HasAvatarQueue)
                {
                    OutputRotation = math.slerp(First.rotation, Last.rotation, interpolationTime);

                    // Complete the jobs and apply the results
                    EuroFilterHandle.Complete();


                    bool ReadyState = ApplyPoseData(Player.BasisAvatar.Animator, OutputVectors[1], OutputVectors[0], OutputRotation, enableEuroFilter ? EuroValuesOutput : musclesPreEuro);

                    if (ReadyState)
                    {
                        PoseHandler.SetHumanPose(ref HumanPose);
                    }
                    else
                    {
                        BasisDebug.LogError("Not Ready For Pose Set!");
                    }

                    RemotePlayer.RemoteBoneDriver.SimulateAndApply(RemotePlayer, DeltaTime);
                    RemotePlayer.RemoteBoneDriver.CalculateHeadBoneData();
                    BasisCalibratedCoords Coords = RemotePlayer.RemoteBoneDriver.Mouth.OutgoingWorldData;
                    AudioReceiverModule.AudioSourceTransform.SetPositionAndRotation(Coords.position, Coords.rotation);
                }
                if (interpolationTime >= 1 && PayloadQueue.TryDequeue(out BasisAvatarBuffer result))
                {
                    First = Last;
                    Last = result;

                    if (Last != null)
                    {
                        TimeBeforeCompletion = Last.SecondsInterval;
                    }
                    TimeInThePast = TimeAsDouble;
                }
            }
            catch (Exception ex)
            {
                if (LogFirstError == false)
                {
                    // Log the full exception details, including stack trace
                    BasisDebug.LogError($"Error in Apply: {ex.Message}\nStack Trace:\n{ex.StackTrace}");

                    // If the exception has an inner exception, log it as well
                    if (ex.InnerException != null)
                    {
                        BasisDebug.LogError($"Inner Exception: {ex.InnerException.Message}\nStack Trace:\n{ex.InnerException.StackTrace}");
                    }
                    LogFirstError = true;
                }
            }
        }
        public void EnQueueAvatarBuffer(ref BasisAvatarBuffer avatarBuffer)
        {
            if(avatarBuffer == null)
            {
                BasisDebug.LogError("Missing Avatar Buffer!");
                return;
            }
            if (HasAvatarQueue)
            {
                PayloadQueue.Enqueue(avatarBuffer);
                while (PayloadQueue.Count > BufferCapacityBeforeCleanup)
                {
                    PayloadQueue.TryDequeue(out BasisAvatarBuffer Buffer);
                }
            }
            else
            {
                First = avatarBuffer;
                Last = avatarBuffer;
                HasAvatarQueue = true;
            }
        }
        public bool ApplyPoseData(Animator animator, float3 Scale, float3 Position, Quaternion Rotation, NativeArray<float> Muscles)
        {
            // Directly adjust scaling by applying the inverse of the AvatarHumanScale
            Vector3 Scaling = Vector3.one / animator.humanScale;  // Initial scaling with human scale inverse

            // Now adjust scaling with the output scaling vector
            Scaling = Divide(Scaling, Scale);  // Apply custom scaling logic

            // Apply scaling to position
            Vector3 ScaledPosition = Vector3.Scale(Position, Scaling);  // Apply the scaling
            HumanPose.bodyPosition = ScaledPosition;
            HumanPose.bodyRotation = Rotation;

            // Copy from job to MuscleFinalStageOutput
            Muscles.CopyTo(MuscleFinalStageOutput);
            // First, copy the first 14 elements directly
            Array.Copy(MuscleFinalStageOutput, 0, HumanPose.muscles, 0, FirstBuffer);
            // Then, copy the remaining elements from index 15 onwards into the pose.muscles array, starting from index 21
            Array.Copy(MuscleFinalStageOutput, FirstBuffer, HumanPose.muscles, SecondBuffer, SizeAfterGap);
            Array.Copy(Eyes, 0, HumanPose.muscles, FirstBuffer, 4);
            // Adjust the local scale of the animator's transform
            animator.transform.localScale = Scale;  // Directly adjust scale with output scaling
            return true;
        }
        public Vector3 GetScale()
        {
            if (Player != null && Player.BasisAvatar != null)
            {
                Vector3 Scale = Player.BasisAvatarTransform.localScale;
                if (Scale != Vector3.zero)
                {
                    return Scale;
                }
                else
                {
                    return Vector3.one;
                }
            }
            else
            {
                return Vector3.one;
            }
        }
        public static Vector3 Divide(Vector3 a, Vector3 b)
        {
            // Define a small epsilon to avoid division by zero, using a flexible value based on magnitude
            const float epsilon = 0.00001f;

            return new Vector3(
                Mathf.Abs(b.x) > epsilon ? a.x / b.x : a.x,  // Avoid scaling if b is too small
                Mathf.Abs(b.y) > epsilon ? a.y / b.y : a.y,  // Same for y-axis
                Mathf.Abs(b.z) > epsilon ? a.z / b.z : a.z   // Same for z-axis
            );
        }
        public void ReceiveNetworkAudio(ServerAudioSegmentMessage audioSegment)
        {
            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.ServerAudioSegment, audioSegment.audioSegmentData.LengthUsed);
            AudioReceiverModule.OnDecode(audioSegment.audioSegmentData.buffer, audioSegment.audioSegmentData.LengthUsed);
            Player.AudioReceived?.Invoke(true);
        }
        public void ReceiveSilentNetworkAudio(ServerAudioSegmentMessage audioSilentSegment)
        {
            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.ServerAudioSegment, 1);
            AudioReceiverModule.OnDecodeSilence();
            Player.AudioReceived?.Invoke(false);
        }
        public async void ReceiveAvatarChangeRequest(ServerAvatarChangeMessage ServerAvatarChangeMessage)
        {
            RemotePlayer.CACM = ServerAvatarChangeMessage.clientAvatarChangeMessage;
            BasisLoadableBundle BasisLoadableBundle = BasisBundleConversionNetwork.ConvertNetworkBytesToBasisLoadableBundle(ServerAvatarChangeMessage.clientAvatarChangeMessage.byteArray);

           await RemotePlayer.CreateAvatar(ServerAvatarChangeMessage.clientAvatarChangeMessage.loadMode, BasisLoadableBundle);
        }
        public override void Initialize()
        {
            HumanPose.muscles = new float[95];
            OutputVectors = new NativeArray<float3>(2, Allocator.Persistent); // Index 0 = position, Index 1 = scale
            TargetVectors = new NativeArray<float3>(2, Allocator.Persistent); // Index 0 = target position, Index 1 = target scale
            musclesPreEuro = new NativeArray<float>(LocalAvatarSyncMessage.StoredBones, Allocator.Persistent);
            targetMuscles = new NativeArray<float>(LocalAvatarSyncMessage.StoredBones, Allocator.Persistent);
            EuroValuesOutput = new NativeArray<float>(LocalAvatarSyncMessage.StoredBones, Allocator.Persistent);

            positionFilters = new NativeArray<float2>(LocalAvatarSyncMessage.StoredBones, Allocator.Persistent);
            derivativeFilters = new NativeArray<float2>(LocalAvatarSyncMessage.StoredBones, Allocator.Persistent);

            musclesJob = new UpdateAvatarMusclesJob();
            AvatarJob = new UpdateAvatarJob();
            musclesJob.Outputmuscles = musclesPreEuro;
            musclesJob.targetMuscles = targetMuscles;
            AvatarJob.OutputVector = OutputVectors;
            AvatarJob.TargetVector = TargetVectors;

            ForceUpdateFilters();

            RemotePlayer = (BasisRemotePlayer)Player;
            AudioReceiverModule.OnEnable(this);
            if (HasEvents == false)
            {
                RemotePlayer.RemoteAvatarDriver.CalibrationComplete += OnCalibration;
                HasEvents = true;
            }
        }
        public void ForceUpdateFilters()
        {
            for (int Index = 0; Index < LocalAvatarSyncMessage.StoredBones; Index++)
            {
                positionFilters[Index] = new float2(0,0);
                derivativeFilters[Index] = new float2(0,0);
            }

            oneEuroFilterJob = new BasisOneEuroFilterParallelJob
            {
                InputValues = musclesPreEuro,
                OutputValues = EuroValuesOutput,
                DeltaTime = interpolationTime,
                MinCutoff = MinCutoff,
                Beta = Beta,
                DerivativeCutoff = DerivativeCutoff,
                PositionFilters = positionFilters,
                DerivativeFilters = derivativeFilters,
            };
        }
        public void OnCalibration()
        {
            AudioReceiverModule.OnCalibration(this);
        }
        public override void DeInitialize()
        {
            // Dispose vector data if initialized
            if (OutputVectors != null && OutputVectors.IsCreated) OutputVectors.Dispose();
            if (TargetVectors != null && TargetVectors.IsCreated) TargetVectors.Dispose();
            if (musclesPreEuro != null && musclesPreEuro.IsCreated) musclesPreEuro.Dispose();
            if (targetMuscles != null && targetMuscles.IsCreated) targetMuscles.Dispose();
            if (EuroValuesOutput != null && EuroValuesOutput.IsCreated) EuroValuesOutput.Dispose();
            if (positionFilters != null && positionFilters.IsCreated) positionFilters.Dispose();
            if (derivativeFilters != null && derivativeFilters.IsCreated) derivativeFilters.Dispose();

            // Unsubscribe from events if required
            if (RemotePlayer != null)
            {
                if (HasEvents && RemotePlayer.RemoteAvatarDriver != null)
                {
                    RemotePlayer.RemoteAvatarDriver.CalibrationComplete -= OnCalibration;
                    HasEvents = false;
                }
            }
            // Handle audio receiver module cleanup
            AudioReceiverModule?.OnDestroy();
        }
    }
}
