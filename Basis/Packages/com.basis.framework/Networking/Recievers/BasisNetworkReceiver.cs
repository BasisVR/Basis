using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Profiler;
using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using static SerializableBasis;

namespace Basis.Scripts.Networking.Recievers
{
    [DefaultExecutionOrder(15001)]
    [System.Serializable]
    public partial class BasisNetworkReceiver : BasisNetworkPlayer
    {
        public float[] silentData;
        public ushort[] CopyData = new ushort[LocalAvatarSyncMessage.StoredBones];
        [SerializeField]
        public BasisAudioReceiver AudioReceiverModule = new BasisAudioReceiver();
        [Header("Interpolation Settings")]
        public double delayTime = 0.1f; // How far behind real-time we want to stay, hopefully double is good.
        [SerializeField]
        public Queue<AvatarBuffer> PayloadQueue = new Queue<AvatarBuffer>();
        public BasisRemotePlayer RemotePlayer;
        public bool HasEvents = false;
        private NativeArray<float3> OuputVectors;      // Merged positions and scales
        private NativeArray<float3> TargetVectors; // Merged target positions and scales
        private NativeArray<float> musclesPreEuro;
        private NativeArray<float> targetMuscles;
        private NativeArray<float> EuroValuesOutput;
        public JobHandle musclesHandle;
        public JobHandle AvatarHandle;
        public UpdateAvatarMusclesJob musclesJob = new UpdateAvatarMusclesJob();
        public UpdateAvatarJob AvatarJob = new UpdateAvatarJob();
        public float[] MuscleFinalStageOutput = new float[LocalAvatarSyncMessage.StoredBones];
        public quaternion OutputRotation;
        public AvatarBuffer First;
        public AvatarBuffer Last;
        public static int BufferCapacityBeforeCleanup = 3;
        public float interpolationTime;
        public double TimeBeforeCompletion;
        public double TimeInThePast;
        public bool HasAvatarInitalized;

        public BasisOneEuroFilterParallelJob oneEuroFilterJob;
        public  float MinCutoff = 0.001f;
        public  float Beta = 5f;
        public float DerivativeCutoff = 1.0f;

        public bool updateFilters;
        public bool enableEuroFilter = true;

        /// <summary>
        /// Perform computations to interpolate and update avatar state.
        /// </summary>
        public void Compute(double TimeAsDouble)
        {
            if (Ready)
            {
                if (HasAvatarInitalized)
                {
                    // Calculate interpolation time
                    interpolationTime = Mathf.Clamp01((float)((TimeAsDouble - TimeInThePast) / TimeBeforeCompletion));
                    if(First == null)
                    {
                        if(Last != null)
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
                    if(Last == null)
                    {
                        PayloadQueue.TryDequeue(out Last);
                        BasisDebug.LogError("Last == null tried to dequeue", BasisDebug.LogTag.Networking);

                    }
                    try
                    {
                        TargetVectors[0] = Last.Position; // Target position at index 0
                        OuputVectors[0] = First.Position; // Position at index 0

                        OuputVectors[1] = First.Scale;    // Scale at index 1
                        TargetVectors[1] = Last.Scale;    // Target scale at index 1
                    }
                    catch (Exception ex)
                    {
                        // Log the full exception details, including stack trace
                        BasisDebug.LogError($"Error in Vector Set: {ex.Message}\nStack Trace:\n{ex.StackTrace}");
                    }
                    try
                    {
                        musclesPreEuro.CopyFrom(First.Muscles);
                        targetMuscles.CopyFrom(Last.Muscles);
                    }
                    catch (Exception ex)
                    {
                        // Log the full exception details, including stack trace
                        BasisDebug.LogError($"Error in Muscle Copy: {ex.Message}\nStack Trace:\n{ex.StackTrace}");
                    }
                    AvatarJob.Time = interpolationTime;

                    AvatarHandle = AvatarJob.Schedule();

                    // Muscle interpolation job
                    musclesJob.Time = interpolationTime;
                    musclesHandle = musclesJob.Schedule(LocalAvatarSyncMessage.StoredBones, 64, AvatarHandle);

                    if(updateFilters)
                    {
                        ForceUpdateFilters();
                    }

                    oneEuroFilterJob.DeltaTime = interpolationTime;
                    EuroFilterHandle = oneEuroFilterJob.Schedule(LocalAvatarSyncMessage.StoredBones,64,musclesHandle);
                }
            }
        }
        public JobHandle EuroFilterHandle;
        public Vector3 PositionOffset;
        public void Apply(double TimeAsDouble, float DeltaTime)
        {
            if (PoseHandler != null)
            {
                try
                {
                    if (HasAvatarInitalized)
                    {
                        OutputRotation = math.slerp(First.rotation, Last.rotation, interpolationTime);

                        // Complete the jobs and apply the results
                        EuroFilterHandle.Complete();


                        ApplyPoseData(Player.BasisAvatar.Animator, OuputVectors[1], OuputVectors[0], OutputRotation, enableEuroFilter ? EuroValuesOutput : musclesPreEuro);
                        PoseHandler.SetHumanPose(ref HumanPose);

                        RemotePlayer.RemoteBoneDriver.SimulateAndApply(DeltaTime);
                     //   RemotePlayer.BasisAvatar.FaceVisemeMesh.transform.position = RemotePlayer.RemoteBoneDriver.Hips.OutgoingWorldData.position;
                    }
                    if (interpolationTime >= 1 && PayloadQueue.TryDequeue(out AvatarBuffer result))
                    {
                        First = Last;
                        Last = result;

                        TimeBeforeCompletion = Last.SecondsInterval;
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
        }
        public bool LogFirstError = false;
        public void EnQueueAvatarBuffer(ref AvatarBuffer avatarBuffer)
        {
            if(avatarBuffer == null)
            {
                BasisDebug.LogError("Missing Avatar Buffer!");
                return;
            }
            if (Ready)
            {
                if (HasAvatarInitalized)
                {
                    PayloadQueue.Enqueue(avatarBuffer);
                    while (PayloadQueue.Count > BufferCapacityBeforeCleanup)
                    {
                        PayloadQueue.TryDequeue(out AvatarBuffer Buffer);
                    }
                }
                else
                {
                    First = avatarBuffer;
                    Last = avatarBuffer;
                    HasAvatarInitalized = true;
                }
            }
            else
            {
                BasisDebug.LogError("trying to apply Avatar Buffer before ready!");
            }
        }
        public void ApplyPoseData(Animator animator, float3 Scale, float3 Position, quaternion Rotation, NativeArray<float> Muscles)
        {
            if(animator == null)
            {
                BasisDebug.LogError("Missing Animator!");
                return;
            }
            // Directly adjust scaling by applying the inverse of the AvatarHumanScale
            Vector3 Scaling = Vector3.one / animator.humanScale;  // Initial scaling with human scale inverse

            // Now adjust scaling with the output scaling vector
            Scaling = Divide(Scaling, Scale);  // Apply custom scaling logic

            // Apply scaling to position
            Vector3 ScaledPosition = Vector3.Scale(Position, Scaling);  // Apply the scaling

            // BasisDebug.Log("ScaledPosition " + ScaledPosition);
            // Apply pose data
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
        }
        public float[] Eyes = new float[4];
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
            if (AudioReceiverModule.decoder != null)
            {
                BasisNetworkProfiler.ServerAudioSegmentMessageCounter.Sample(audioSegment.audioSegmentData.LengthUsed);
                AudioReceiverModule.decoder.OnDecode(audioSegment.audioSegmentData.buffer, audioSegment.audioSegmentData.LengthUsed);
                Player.AudioReceived?.Invoke(true);
            }
        }
        public void ReceiveSilentNetworkAudio(ServerAudioSegmentMessage audioSilentSegment)
        {
            if (AudioReceiverModule.decoder != null)
            {
                if (silentData == null || silentData.Length != AudioReceiverModule.decoder.FakePcmLength)
                {
                    silentData = new float[AudioReceiverModule.decoder.FakePcmLength];
                    Array.Fill(silentData, 0f);
                }
                BasisNetworkProfiler.ServerAudioSegmentMessageCounter.Sample(1);
                AudioReceiverModule.OnDecoded(silentData, AudioReceiverModule.decoder.FakePcmLength);
                Player.AudioReceived?.Invoke(false);
            }
        }
        public void ReceiveAvatarChangeRequest(ServerAvatarChangeMessage ServerAvatarChangeMessage)
        {
            RemotePlayer.CACM = ServerAvatarChangeMessage.clientAvatarChangeMessage;
            BasisLoadableBundle BasisLoadableBundle = BasisBundleConversionNetwork.ConvertNetworkBytesToBasisLoadableBundle(ServerAvatarChangeMessage.clientAvatarChangeMessage.byteArray);

            RemotePlayer.CreateAvatar(ServerAvatarChangeMessage.clientAvatarChangeMessage.loadMode, BasisLoadableBundle);
        }
        private NativeArray<float2> positionFilters;
        private NativeArray<float2> derivativeFilters;
        public override void Initialize()
        {
            if (!Ready)
            {
                HumanPose.muscles = new float[95];
                OuputVectors = new NativeArray<float3>(2, Allocator.Persistent); // Index 0 = position, Index 1 = scale
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
                AvatarJob.OutputVector = OuputVectors;
                AvatarJob.TargetVector = TargetVectors;

                ForceUpdateFilters();

                RemotePlayer = (BasisRemotePlayer)Player;
                AudioReceiverModule.OnEnable(this);
               //this wont work here OnAvatarCalibrationRemote();
                if (HasEvents == false)
                {
                    RemotePlayer.RemoteAvatarDriver.CalibrationComplete += OnCalibration;
                    HasEvents = true;
                }
                Ready = true;
            }
        }
        public void ForceUpdateFilters()
        {
            for (int i = 0; i < LocalAvatarSyncMessage.StoredBones; i++)
            {
                positionFilters[i] = new float2(0,0);
                derivativeFilters[i] = new float2(0,0);
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
        private float Alpha(float cutoff)
        {
            float te = 1.0f / (1.0f / interpolationTime);
            float tau = 1.0f / (2.0f * Mathf.PI * cutoff);
            return 1.0f / (1.0f + tau / te);
        }
        public void OnCalibration()
        {
            AudioReceiverModule.OnCalibration(this);
        }
        public override void DeInitialize()
        {
            if (OuputVectors.IsCreated) OuputVectors.Dispose();
            if (TargetVectors.IsCreated) TargetVectors.Dispose();
            if (musclesPreEuro.IsCreated) musclesPreEuro.Dispose();
            if (targetMuscles.IsCreated) targetMuscles.Dispose();
            if(EuroValuesOutput.IsCreated) EuroValuesOutput.Dispose();
            if (positionFilters.IsCreated) positionFilters.Dispose();
            if (derivativeFilters.IsCreated) derivativeFilters.Dispose();

            if (HasEvents && RemotePlayer != null && RemotePlayer.RemoteAvatarDriver != null)
            {
                RemotePlayer.RemoteAvatarDriver.CalibrationComplete -= OnCalibration;
                HasEvents = false;
            }
            if (AudioReceiverModule != null)
            {
                AudioReceiverModule.OnDestroy();
            }
        }
    }
}
