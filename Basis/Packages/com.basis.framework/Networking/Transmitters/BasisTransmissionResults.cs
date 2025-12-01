using Basis.Network.Core;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.Receivers;
using Basis.Scripts.Networking.Transmitters;
using Basis.Scripts.Profiler;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using static SerializableBasis;
[System.Serializable]
public class BasisTransmissionResults
{
    public BasisDistanceJob distanceJob;
    public JobHandle distanceJobHandle;

    public int LastIndexLength = -1;
    public List<ushort> TalkingPoints = new List<ushort>(128);
    public float intervalSeconds = 0.05f;
    public float timer = 0f;
    public float SmallestDistanceToAnotherPlayer; // squared distance
    public float UnClampedInterval;
    public float DefaultInterval;

    public bool AnyMicrophoneRangeChanged;
    public bool AnyHearingRangeChanged;
    public bool AnyAvatarRangeChanged;
    public bool AnyIdOrderOrLengthChanged;
    public float outMin;

    [SerializeReference]
    public BasisLocalBoneControl MouthBone;
    [SerializeReference]
    public BasisNetworkTransmitter BasisNetworkTransmitter;
    public NetDataWriter AudioRecipientswriter = new NetDataWriter(true, 0);
    private NativeArray<float> distanceSq;
    private NativeArray<bool> hearingRange;
    private NativeArray<float3> targetPositions;
    public NativeArray<bool> MicrophoneRange;
    public NativeArray<bool> AvatarRange;
    public NativeArray<bool> PrevInMicrophoneRange;
    public NativeArray<bool> PrevInHearingRange;
    public NativeArray<bool> PrevInAvatarRange;
    public NativeArray<ushort> IndexToPlayerId;
    public NativeArray<ushort> LastIndexToPlayerId;

    public bool CanDoSimulate(float previousInterval, out BasisAvatar BasisAvatar)
    {
        var player = BasisNetworkTransmitter.Player;
        if (player != null)
        {
            BasisAvatar = player.BasisAvatar;
        }
        else
        {
            BasisAvatar = null;
        }
        if (BasisAvatar == null)
        {
            BasisDebug.LogError("Missing Basis Avatar. Cannot send network update.", BasisDebug.LogTag.System);
            timer -= previousInterval;
            return false;
        }
        return true;
    }
    /// <summary>
    /// Called each frame; drives scheduling of distance job and network sync.
    /// </summary>
    public void Simulate()
    {
        int receiverCount = BasisNetworkPlayers.ReceiverCount;
        var snapshot = BasisNetworkPlayers.ReceiversSnapshot;
        double activeTime = Time.timeAsDouble;
        for (int index = 0; index < receiverCount; index++)
        {
            try
            {
                var receiver = snapshot[index];
                var remote = receiver.RemotePlayer;
                // Small anim drivers
                remote.RemoteEyeDriver.Simulate(activeTime);
                remote.FacialBlinkDriver.Simulate(activeTime);
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"{ex} {ex.StackTrace}");
            }
        }


        timer += Time.deltaTime;

        if (timer <= intervalSeconds)
        {
            return;
        }

        // Use the actual accumulated interval (handles overshoot)
        float previousInterval = intervalSeconds;
        if (CanDoSimulate(previousInterval, out BasisAvatar avatar) == false)
        {
            return;
        }
        // Schedule job to compute all distance info
        distanceJob.SquaredAvatarDistance = SMModuleDistanceBasedReductions.AvatarRange;
        distanceJob.SquaredHearingDistance = SMModuleDistanceBasedReductions.HearingRange;
        distanceJob.SquaredVoiceDistance = SMModuleDistanceBasedReductions.MicrophoneRange;
        distanceJob.referencePosition = MouthBone.OutgoingWorldData.position;

        bool DifferentLengths = LastIndexLength != receiverCount;
        if (DifferentLengths)
        {
            ResizeOrCreateArrayData(receiverCount);//resets arrays and resizes
        }
        // Fill target positions and ID map aligned to snapshot order
        for (int index = 0; index < receiverCount; index++)
        {
            BasisNetworkReceiver remote = snapshot[index];
            if (remote == null)
            {
                BasisDebug.LogError("this shouldnt occur remote was out of bounds!", BasisDebug.LogTag.Networking);
                //target just becomes infinite
                IndexToPlayerId[index] = 0;
                targetPositions[index] = math.INFINITY;
                continue;
            }
            RemoteBoneJobSystem.GetOutGoingMouth(remote.playerId, out float3 outgoing);
            targetPositions[index] = outgoing;
            IndexToPlayerId[index] = remote.playerId;
        }
        distanceJobHandle = distanceJob.Schedule();
        // Compress avatar state (doesn't touch mouth bone used as input)
        BasisNetworkAvatarCompressor.Compress(BasisNetworkTransmitter, avatar.Animator);
        // Complete job, consume results, update send interval, send recipients
        distanceJobHandle.Complete();

        // Cache current as previous for next hysteresis step
        MicrophoneRange.CopyTo(PrevInMicrophoneRange);
        hearingRange.CopyTo(PrevInHearingRange);
        AvatarRange.CopyTo(PrevInAvatarRange);
        IndexToPlayerId.CopyTo(LastIndexToPlayerId);

        /// AnyMicrophoneRangeChanged AnyHearingRangeChanged AnyAvatarRangeChanged AnyIdOrderOrLengthChanged;
        AnyMicrophoneRangeChanged = distanceJob.AnyChangedArray[0];
        AnyHearingRangeChanged = distanceJob.AnyChangedArray[1];
        AnyAvatarRangeChanged = distanceJob.AnyChangedArray[2];
        AnyIdOrderOrLengthChanged = distanceJob.AnyChangedArray[3];

        SmallestDistanceToAnotherPlayer = distanceJob.SMD[0];
        //update the server with who we are talking to
        if (AnyIdOrderOrLengthChanged || AnyMicrophoneRangeChanged)
        {
            if (TalkingPoints.Capacity < receiverCount)
            {
                TalkingPoints.Capacity = receiverCount;
            }
            TalkingPoints.Clear();
            for (int index = 0; index < receiverCount; index++)
            {
                if (MicrophoneRange[index])
                {
                    TalkingPoints.Add(IndexToPlayerId[index]);
                }
            }
            BasisNetworkTransmitter.HasReasonToSendAudio = TalkingPoints.Count != 0;

            // Send to server
            VoiceReceiversMessage VoiceReceiversMessage = new VoiceReceiversMessage
            {
                Users = TalkingPoints.ToArray()
            };

            AudioRecipientswriter.Reset();
            VoiceReceiversMessage.Serialize(AudioRecipientswriter);
            //BasisDebug.Log($"Sending Microphone Check Data ({AudioRecipientswriter.Length})", BasisDebug.LogTag.Voice);
            BasisNetworkConnection.LocalPlayerPeer.Send(AudioRecipientswriter, BasisNetworkCommons.AudioRecipientsChannel, DeliveryMethod.ReliableOrdered);

            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.AudioRecipients, AudioRecipientswriter.Length);
            ApplyLocalChanges(receiverCount, snapshot);
        }

        if (!float.IsFinite(SmallestDistanceToAnotherPlayer))
        {
            // No receivers or all failed positions => treat as zero for rate calc
            SmallestDistanceToAnotherPlayer = 0f;
        }

        ServerMetaDataMessage ServerMetaDataMessage = BasisNetworkManagement.ServerMetaDataMessage;
        DefaultInterval = ServerMetaDataMessage.SyncInterval / 1000f;

        // Keep squared inside; apply sqrt at boundary where human-tuned params likely expect meters.
        float minLinear = math.sqrt(math.max(0f, SmallestDistanceToAnotherPlayer));

        float calculatedIntervalBase = ServerMetaDataMessage.BaseMultiplier + (minLinear * ServerMetaDataMessage.IncreaseRate);
        UnClampedInterval = DefaultInterval * calculatedIntervalBase;

        intervalSeconds = Mathf.Clamp(UnClampedInterval, DefaultInterval, ServerMetaDataMessage.SlowestSendRate);

        if (BasisAvatarRecorder.IsRecording)
        {
            var Anim = avatar.Animator;
            BasisAvatarRecorder.StoreData(intervalSeconds, Anim.bodyRotation, Anim.bodyPosition, BasisNetworkTransmitter.HumanPose.muscles, Anim.transform.localScale.y);
        }
        // account for overshoot using the interval that actually accumulated
        timer -= previousInterval;
        LastIndexLength = receiverCount;
    }
    public void ApplyLocalChanges(int receiverCount, BasisNetworkReceiver[] snapshot)
    {
        for (int index = 0; index < receiverCount; index++)
        {
            try
            {
                var receiver = snapshot[index];
                var remote = receiver.RemotePlayer;

                // Avatar in-range toggling
                if (remote.InAvatarRange != AvatarRange[index])
                {
                    remote.InAvatarRange = AvatarRange[index];
                    remote.ReloadAvatar();
                }

                // Distance-based mesh LOD
                remote.ChangeMeshLOD(distanceSq[index], SMModuleDistanceBasedReductions.MeshLod);

                // Voice start/stop (note: HearingIndex describes "we can hear them")
                bool canHear = hearingRange[index];
                if (receiver.AudioReceiverModule.HasAudioSource != canHear)
                {
                    if (canHear)
                    {
                        receiver.AudioReceiverModule.StartAudio();
                        remote.OutOfRangeFromLocal = false;
                    }
                    else
                    {
                        receiver.AudioReceiverModule.StopAudio();
                        remote.OutOfRangeFromLocal = true;
                    }
                }
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"{ex} {ex.StackTrace}");
            }
        }
    }
    /// <summary>
    /// Allocate / reallocate all NativeArrays.
    /// </summary>
    public void ResizeOrCreateArrayData(int receiverCount)
    {
        ReleaseResults();

        // wire job views
        distanceSq = new NativeArray<float>(receiverCount, Allocator.Persistent);
        MicrophoneRange = new NativeArray<bool>(receiverCount, Allocator.Persistent);
        hearingRange = new NativeArray<bool>(receiverCount, Allocator.Persistent);
        AvatarRange = new NativeArray<bool>(receiverCount, Allocator.Persistent);
        PrevInMicrophoneRange = new NativeArray<bool>(receiverCount, Allocator.Persistent);
        PrevInHearingRange = new NativeArray<bool>(receiverCount, Allocator.Persistent);
        PrevInAvatarRange = new NativeArray<bool>(receiverCount, Allocator.Persistent);
        targetPositions = new NativeArray<float3>(receiverCount, Allocator.Persistent);
        IndexToPlayerId = new NativeArray<ushort>(receiverCount, Allocator.Persistent);
        LastIndexToPlayerId = new NativeArray<ushort>(receiverCount, Allocator.Persistent);

        distanceJob.AnyChangedArray = new NativeArray<bool>(4, Allocator.Persistent);
        distanceJob.SMD = new NativeArray<float>(1, Allocator.Persistent);

        distanceJob.distanceSq = distanceSq;
        distanceJob.MicrophoneRange = MicrophoneRange;
        distanceJob.hearingRange = hearingRange;
        distanceJob.AvatarRange = AvatarRange;
        distanceJob.PrevInMicrophoneRange = PrevInMicrophoneRange;
        distanceJob.PrevInHearingRange = PrevInHearingRange;
        distanceJob.PrevInAvatarRange = PrevInAvatarRange;
        distanceJob.targetPositions = targetPositions;
        distanceJob.IndexToPlayerId = IndexToPlayerId;
        distanceJob.LastIndexToPlayerId = LastIndexToPlayerId;
    }

    /// <summary>
    /// Dispose NativeArrays and complete outstanding jobs.
    /// </summary>
    public void ReleaseResults()
    {
        // wait for in-flight jobs
        if (!distanceJobHandle.IsCompleted) { distanceJobHandle.Complete(); }

        // dispose old
        if (targetPositions.IsCreated) targetPositions.Dispose();
        if (distanceSq.IsCreated) distanceSq.Dispose();
        if (MicrophoneRange.IsCreated) MicrophoneRange.Dispose();
        if (hearingRange.IsCreated) hearingRange.Dispose();
        if (AvatarRange.IsCreated) AvatarRange.Dispose();
        if (PrevInMicrophoneRange.IsCreated) PrevInMicrophoneRange.Dispose();
        if (PrevInHearingRange.IsCreated) PrevInHearingRange.Dispose();
        if (PrevInAvatarRange.IsCreated) PrevInAvatarRange.Dispose();
        if (LastIndexToPlayerId.IsCreated) LastIndexToPlayerId.Dispose();
        if (IndexToPlayerId.IsCreated) IndexToPlayerId.Dispose();

        if(distanceJob.AnyChangedArray.IsCreated) distanceJob.AnyChangedArray.Dispose();
        if (distanceJob.SMD.IsCreated) distanceJob.SMD.Dispose();
    }
    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Standard)]
    public struct BasisDistanceJob : IJob
    {
        public float SquaredVoiceDistance;
        public float SquaredHearingDistance;
        public float SquaredAvatarDistance;

        public const float HysteresisMargin = 0.05f;

        [ReadOnly] public NativeArray<ushort> LastIndexToPlayerId;
        [ReadOnly] public NativeArray<ushort> IndexToPlayerId;

        [ReadOnly] public float3 referencePosition;
        [ReadOnly] public NativeArray<float3> targetPositions;

        [ReadOnly] public NativeArray<bool> PrevInMicrophoneRange;
        [ReadOnly] public NativeArray<bool> PrevInHearingRange;
        [ReadOnly] public NativeArray<bool> PrevInAvatarRange;

        [WriteOnly] public NativeArray<float> distanceSq;
        [WriteOnly] public NativeArray<bool> MicrophoneRange;
        [WriteOnly] public NativeArray<bool> hearingRange;
        [WriteOnly] public NativeArray<bool> AvatarRange;

        /// <summary>
        /// AnyMicrophoneRangeChanged AnyHearingRangeChanged AnyAvatarRangeChanged AnyIdOrderOrLengthChanged;
        /// </summary>
        [WriteOnly] public NativeArray<bool> AnyChangedArray;
        [WriteOnly] public NativeArray<float> SMD;
        public void Execute()
        {
            float3 refPos = referencePosition;
            float SmallestDistance = float.PositiveInfinity;
            int length = targetPositions.Length;

            bool AnyMicrophoneRangeChanged = false;
            bool AnyHearingRangeChanged = false;
            bool AnyAvatarRangeChanged = false;
            bool AnyIdOrderOrLengthChanged = false;

            for (int Index = 0; Index < length; Index++)
            {
                float3 diff = targetPositions[Index] - refPos;
                float d2 = math.lengthsq(diff);
                distanceSq[Index] = d2;

                bool prevDist = PrevInMicrophoneRange[Index];
                bool prevHear = PrevInHearingRange[Index];
                bool prevAvatar = PrevInAvatarRange[Index];

                bool InMicrophoneRange = Hysteresis(prevDist, d2, SquaredVoiceDistance, HysteresisMargin);
                bool InHearingRange = Hysteresis(prevHear, d2, SquaredHearingDistance, HysteresisMargin);
                bool InAvatarRange = Hysteresis(prevAvatar, d2, SquaredAvatarDistance, HysteresisMargin);

                MicrophoneRange[Index] = InMicrophoneRange;
                hearingRange[Index] = InHearingRange;
                AvatarRange[Index] = InAvatarRange;

                if (InMicrophoneRange != prevDist)
                {
                    AnyMicrophoneRangeChanged = true;
                }
                if (InHearingRange != prevHear)
                {
                    AnyHearingRangeChanged = true;
                }
                if (InAvatarRange != prevAvatar)
                {
                    AnyAvatarRangeChanged = true;
                }
                SmallestDistance = math.min(SmallestDistance, d2);
            }
            SMD[0] = SmallestDistance;
            int lenNow = IndexToPlayerId.Length;
            int lenPrev = LastIndexToPlayerId.Length;
            if (lenNow != lenPrev)
            {
                AnyIdOrderOrLengthChanged = true;
            }
            if (AnyIdOrderOrLengthChanged == false)
            {
                // Same length: check values one by one.
                for (int Index = 0; Index < lenNow; Index++)
                {
                    if (IndexToPlayerId[Index] != LastIndexToPlayerId[Index])
                    {
                        AnyIdOrderOrLengthChanged = true;
                        break;
                    }
                }
            }
            AnyChangedArray[0] = AnyMicrophoneRangeChanged;
            AnyChangedArray[1] = AnyHearingRangeChanged;
            AnyChangedArray[2] = AnyAvatarRangeChanged;
            AnyChangedArray[3] = AnyIdOrderOrLengthChanged;
        }
        [BurstCompile]
        private static bool Hysteresis(bool wasInside, float d2, float thr2, float margin)
        {
            margin = math.clamp(margin, 0f, 0.49f);
            float insideFactor = 1f + margin;
            float outsideFactor = 1f - margin;
            float factor = math.select(outsideFactor, insideFactor, wasInside);
            return d2 < thr2 * factor;
        }
    }
}
