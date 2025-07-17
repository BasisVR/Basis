using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Profiler;
using Basis.Scripts.TransformBinders.BoneControl;
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
        public BasisRemoteBoneControl MouthBone;
        public ushort[] CopyData = new ushort[LocalAvatarSyncMessage.StoredBones];
        [SerializeField]
        public BasisAudioReceiver AudioReceiverModule = new BasisAudioReceiver();
        [SerializeField]
        public Queue<BasisAvatarBuffer> PayloadQueue = new Queue<BasisAvatarBuffer>();
        public BasisRemotePlayer RemotePlayer;
        public bool HasEvents = false;

        private NativeArray<float3> OutputVectors;      // Merged positions and scales
        private NativeArray<float3> TargetVectors; // Merged target positions and scales
                                                   //  private NativeArray<float> musclesPreEuro;
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
        public const int BufferCapacityBeforeCleanup = 3;
        public float interpolationTime;
        public double TimeBeforeCompletion;
        public double TimeInThePast;
        public bool HasAvatarQueue;

        public BasisOneEuroFilterParallelJob oneEuroFilterJob;
        public const float MinCutoff = 0.001f;
        public const float Beta = 5f;
        public const float DerivativeCutoff = 1.0f;
        //   public bool enableEuroFilter = true;
        public JobHandle EuroFilterHandle;
        public bool LogFirstError = false;
        public float[] Eyes = new float[4];
        /// <summary>
        /// Perform computations to interpolate and update avatar state.
        /// </summary>
        public void Compute(double TimeAsDouble)
        {
            if (HasAvatarQueue)
            {
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
                    //not a error  BasisDebug.LogError("Last == null tried to dequeue", BasisDebug.LogTag.Networking);

                }
                if (First != null)
                {
                    OutputVectors[0] = First.Position;
                    OutputVectors[1] = First.Scale;
                    EuroValuesOutput.CopyFrom(First.Muscles);
                }
                if (Last != null)
                {
                    TargetVectors[0] = Last.Position;
                    TargetVectors[1] = Last.Scale;
                    targetMuscles.CopyFrom(Last.Muscles);
                }
                AvatarJob.Time = interpolationTime;


                //need to make sure AvatarJob and so on its complete and ready to be rescheduled

                AvatarHandle = AvatarJob.Schedule();

                // Muscle interpolation job
                musclesJob.Time = interpolationTime;
                musclesHandle = musclesJob.Schedule(LocalAvatarSyncMessage.StoredBones, 64, AvatarHandle);

                oneEuroFilterJob.DeltaTime = interpolationTime;
                EuroFilterHandle = oneEuroFilterJob.Schedule(LocalAvatarSyncMessage.StoredBones, 64, musclesHandle);
            }
        }
        public void Apply(double TimeAsDouble)
        {
            if (PoseHandler == null)
            {
                return;
            }
            if (First == null)
            {
                return;
            }
            if (Last == null)
            {
                return;
            }
            if (HasAvatarQueue)
            {
                OutputRotation = math.slerp(First.rotation, Last.rotation, interpolationTime);
                try
                {
                    // Complete the jobs and apply the results
                    EuroFilterHandle.Complete();
                    ApplyComputedData();
                }
                catch (Exception ex)
                {
                    HandleException(ex);
                }
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
        public void ApplyComputedData()
        {
            //  bool ReadyState = ApplyPoseData(Player.BasisAvatarTransform, Player.BasisAvatar.Animator, OutputVectors[1], OutputVectors[0], OutputRotation, enableEuroFilter ? EuroValuesOutput : musclesPreEuro);
            Vector3 Scale = OutputVectors[1];
            bool ReadyState = ApplyPoseData(Player.BasisAvatarTransform, Player.BasisAvatar.Animator, Scale, OutputVectors[0], OutputRotation, EuroValuesOutput);
            if (ReadyState)
            {
                PoseHandler.SetHumanPose(ref HumanPose);
            }
            else
            {
                BasisDebug.LogError("Not Ready For Pose Set!");
            }
            RemotePlayer.RemoteBoneDriver.SimulateAndApplyRemote(Scale);
            AudioReceiverModule.MoveAudio(RemotePlayer.RemoteBoneDriver.Mouth.OutGoingData);
            if (RemotePlayer.HasRemoteNamePlate)
            {
                RemotePlayer.RemoteNamePlate.Simulate();
            }
        }
        public void HandleException(Exception ex)
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
        public void EnQueueAvatarBuffer(ref BasisAvatarBuffer avatarBuffer)
        {
            if (avatarBuffer == null)
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
        public bool ApplyPoseData(Transform AnimatorsTransform, Animator animator, float3 Scale, float3 Position, Quaternion Rotation, NativeArray<float> Muscles)
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
            Array.Copy(MuscleFinalStageOutput, 0, HumanPose.muscles, 0, BasisAvatarMuscleRange.FirstBuffer);
            // Then, copy the remaining elements from index 15 onwards into the pose.muscles array, starting from index 21
            Array.Copy(MuscleFinalStageOutput, BasisAvatarMuscleRange.FirstBuffer, HumanPose.muscles, BasisAvatarMuscleRange.SecondBuffer, BasisAvatarMuscleRange.SizeAfterGap);
            Array.Copy(Eyes, 0, HumanPose.muscles, BasisAvatarMuscleRange.FirstBuffer, 4);
            // Adjust the local scale of the animator's transform
            AnimatorsTransform.localScale = Scale;  // Directly adjust scale with output scaling
            return true;
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
                                                                              //  musclesPreEuro = new NativeArray<float>(LocalAvatarSyncMessage.StoredBones, Allocator.Persistent);
            targetMuscles = new NativeArray<float>(LocalAvatarSyncMessage.StoredBones, Allocator.Persistent);
            EuroValuesOutput = new NativeArray<float>(LocalAvatarSyncMessage.StoredBones, Allocator.Persistent);

            positionFilters = new NativeArray<float2>(LocalAvatarSyncMessage.StoredBones, Allocator.Persistent);
            derivativeFilters = new NativeArray<float2>(LocalAvatarSyncMessage.StoredBones, Allocator.Persistent);

            musclesJob = new UpdateAvatarMusclesJob();
            AvatarJob = new UpdateAvatarJob();
            musclesJob.Outputmuscles = EuroValuesOutput;
            musclesJob.targetMuscles = targetMuscles;
            AvatarJob.OutputVector = OutputVectors;
            AvatarJob.TargetVector = TargetVectors;

            ForceUpdateFilters();

            RemotePlayer = (BasisRemotePlayer)Player;
            AudioReceiverModule.Initalize(this);
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
                positionFilters[Index] = new float2(0, 0);
                derivativeFilters[Index] = new float2(0, 0);
            }

            oneEuroFilterJob = new BasisOneEuroFilterParallelJob
            {
                //  InputValues = musclesPreEuro,
                Values = EuroValuesOutput,
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
            AudioReceiverModule.AvatarChanged(this);
        }
        public override void DeInitialize()
        {
            EuroFilterHandle.Complete();
            AvatarHandle.Complete();
            musclesHandle.Complete();
            // Dispose vector data if initialized
            if (OutputVectors != null && OutputVectors.IsCreated) OutputVectors.Dispose();
            if (TargetVectors != null && TargetVectors.IsCreated) TargetVectors.Dispose();
            //   if (musclesPreEuro != null && musclesPreEuro.IsCreated) musclesPreEuro.Dispose();
            if (targetMuscles != null && targetMuscles.IsCreated) targetMuscles.Dispose();
            if (EuroValuesOutput != null && EuroValuesOutput.IsCreated) EuroValuesOutput.Dispose();
            if (positionFilters != null && positionFilters.IsCreated) positionFilters.Dispose();
            if (derivativeFilters != null && derivativeFilters.IsCreated) derivativeFilters.Dispose();

            if (RemotePlayer != null && HasEvents && RemotePlayer.RemoteAvatarDriver != null)
            {
                RemotePlayer.RemoteAvatarDriver.CalibrationComplete -= OnCalibration;
                HasEvents = false;
            }

            AudioReceiverModule?.OnDestroy();
        }
        public BasisNetworkReceiver(ushort PlayerID)
        {
            PlayerIDMessage.playerID = PlayerID;
            hasID = true;
        }
    }
}
