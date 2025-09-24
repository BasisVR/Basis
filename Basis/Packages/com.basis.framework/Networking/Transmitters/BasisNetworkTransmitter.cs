using Basis.Network.Core;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Profiler;
using Basis.Scripts.TransformBinders.BoneControl;
using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using static SerializableBasis;

namespace Basis.Scripts.Networking.Transmitters
{
    [DefaultExecutionOrder(15001)]
    [System.Serializable]
    public class BasisNetworkTransmitter : BasisNetworkPlayer
    {
        public bool HasEvents = false;
        public BasisLocalBoneControl MouthBone;

        [SerializeField] public BasisAudioTransmission AudioTransmission = new BasisAudioTransmission();

        // Core native buffers
        public NativeArray<float3> targetPositions;
        public NativeArray<float> distances;

        // Prev-state (read-only in job) + New-state (write-only in job)
        public NativeArray<bool> PrevDistanceResults;
        public NativeArray<bool> PrevHearingResults;
        public NativeArray<bool> PrevAvatarResults;

        public NativeArray<bool> DistanceResults;
        public NativeArray<bool> HearingResults;
        public NativeArray<bool> AvatarResults;

        public NativeArray<bool> MeshLodResults; // unchanged usage

        // Reduction buffers
        public NativeArray<float> smallestDistance; // length 1, stores MIN **squared** distance
        public NativeArray<float> batchMins;        // per-batch minima

        [SerializeField] public StoredAvatarData storedAvatarData = new StoredAvatarData();
        [System.Serializable]
        public class StoredAvatarData
        {
            [SerializeField]
            public LocalAvatarSyncMessage LASM = new LocalAvatarSyncMessage(new byte[LocalAvatarSyncMessage.AvatarSyncSize]);
        }

        // Jobs & handles
        public BasisDistanceJobBatch distanceJob;
        public MinReduceJob reduceJob;
        public JobHandle distanceJobHandle;
        public JobHandle reduceJobHandle;

        public int IndexLength = -1;
        public int BatchSize = 64;

        public NetDataWriter AvatarSendWriter = new NetDataWriter(true, LocalAvatarSyncMessage.AvatarSyncSize + 2);

        // managed mirrors
        public bool[] MicrophoneRangeIndex;
        public bool[] LastMicrophoneRangeIndex;
        public bool[] HearingIndex;
        public bool[] AvatarIndex;
        public ushort[] HearingIndexToId;
        public AdditionalAvatarData[] AdditionalAvatarData;
        public Dictionary<byte, AdditionalAvatarData> SendingOutAvatarData = new Dictionary<byte, AdditionalAvatarData>();
        public float[] CalculatedDistances;

        public static Action AfterAvatarChanges;

        public float intervalSeconds = 0.5f;
        public float timer = 0f;
        public float SmallestDistanceToAnotherPlayer;    // still **squared** distance
        public float UnClampedInterval;
        public float DefaultInterval;

        public BasisNetworkTransmitter(ushort PlayerID)
        {
            playerId = PlayerID;
            hasID = true;
        }

        public void AddAdditional(AdditionalAvatarData AvatarData) => SendingOutAvatarData[AvatarData.messageIndex] = AvatarData;
        public void ClearAdditional() => SendingOutAvatarData.Clear();

        void SendOutLatest()
        {
            timer += Time.deltaTime;

            if (timer > intervalSeconds)
            {
                if (Player.BasisAvatar != null)
                {
                    ScheduleCheck();

                    BasisNetworkAvatarCompressor.Compress(this, Player.BasisAvatar.Animator);

                    // complete both phases (distance, then reduction)
                    reduceJobHandle.Complete();

                    HandleResults();

                    SmallestDistanceToAnotherPlayer = smallestDistance[0]; // still squared
                    ServerMetaDataMessage Message = BasisNetworkManagement.ServerMetaDataMessage;

                    DefaultInterval = Message.SyncInterval / 1000f;

                    float CalculatedIntervalBase = Message.BaseMultiplier + (SmallestDistanceToAnotherPlayer * Message.IncreaseRate);
                    UnClampedInterval = DefaultInterval * CalculatedIntervalBase;
                    intervalSeconds = Mathf.Clamp(UnClampedInterval, DefaultInterval, Message.SlowestSendRate);

                    // account for overshoot
                    timer -= intervalSeconds;
                }
            }
        }

        public void HandleResults()
        {
            if (!DistanceResults.IsCreated || MicrophoneRangeIndex == null || MicrophoneRangeIndex.Length != DistanceResults.Length)
                return;

            DistanceResults.CopyTo(MicrophoneRangeIndex);
            HearingResults.CopyTo(HearingIndex);
            AvatarResults.CopyTo(AvatarIndex);
            distances.CopyTo(CalculatedDistances);

            // copy new states into prev states for next frame's hysteresis
            DistanceResults.CopyTo(PrevDistanceResults);
            HearingResults.CopyTo(PrevHearingResults);
            AvatarResults.CopyTo(PrevAvatarResults);

            MicrophoneOutputCheck();
            IterationOverRemotePlayers();
        }

        /// <summary>How far we can hear locally.</summary>
        public void IterationOverRemotePlayers()
        {
            for (int Index = 0; Index < IndexLength; Index++)
            {
                try
                {
                    var Rec = BasisNetworkPlayers.ReceiversSnapshot[Index];
                    if (Rec == null) continue;

                    // avatar LOD/range
                    if (Rec.RemotePlayer.InAvatarRange != AvatarIndex[Index])
                    {
                        Rec.RemotePlayer.InAvatarRange = AvatarIndex[Index];
                        Rec.RemotePlayer.ReloadAvatar();
                    }

                    Rec.RemotePlayer.ChangeMeshLOD(CalculatedDistances[Index], SMModuleDistanceBasedReductions.MeshLod);

                    // voice start/stop
                    if (Rec.AudioReceiverModule.IsPlaying != HearingIndex[Index])
                    {
                        if (HearingIndex[Index])
                        {
                            Rec.AudioReceiverModule.StartAudio();
                            Rec.RemotePlayer.OutOfRangeFromLocal = false;
                        }
                        else
                        {
                            Rec.AudioReceiverModule.StopAudio();
                            Rec.RemotePlayer.OutOfRangeFromLocal = true;
                        }
                    }

                    Rec.RemotePlayer.RemoteEyeDriver.Simulate();
                    Rec.RemotePlayer.FacialBlinkDriver.Simulate();
                }
                catch (Exception ex)
                {
                    BasisDebug.LogError($"{ex} {ex.StackTrace}");
                }
            }
        }

        /// <summary>Lets the server know who can hear us.</summary>
        public void MicrophoneOutputCheck()
        {
            if (AreBoolArraysEqual(MicrophoneRangeIndex, LastMicrophoneRangeIndex) == false)
            {
                Array.Copy(MicrophoneRangeIndex, LastMicrophoneRangeIndex, IndexLength);

                List<ushort> TalkingPoints = new List<ushort>(IndexLength);
                for (int Index = 0; Index < IndexLength; Index++)
                {
                    if (MicrophoneRangeIndex[Index])
                    {
                        TalkingPoints.Add(HearingIndexToId[Index]);
                    }
                }
                HasReasonToSendAudio = TalkingPoints.Count != 0;

                VoiceReceiversMessage VRM = new VoiceReceiversMessage { users = TalkingPoints.ToArray() };
                NetDataWriter writer = new NetDataWriter();
                VRM.Serialize(writer);
                BasisNetworkConnection.LocalPlayerPeer.Send(writer, BasisNetworkCommons.AudioRecipientsChannel, DeliveryMethod.ReliableOrdered);
                BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.AudioRecipients, writer.Length);
            }
        }

        public static bool AreBoolArraysEqual(bool[] array1, bool[] array2)
        {
            if (array1 == null && array2 == null) return true;
            if (array1 == null || array2 == null) return false;
            if (array1.Length != array2.Length) return false;

            for (int i = 0; i < array1.Length; i++)
                if (array1[i] != array2[i]) return false;
            return true;
        }

        public override void Initialize()
        {
            IndexLength = -1;
            AudioTransmission.Initialize(this);
            OnAvatarCalibrationLocal();

            if (!HasEvents)
            {
                Player.OnAvatarSwitchedFallBack += OnAvatarCalibrationLocal;
                Player.OnAvatarSwitched += OnAvatarCalibrationLocal;
                Player.OnAvatarSwitched += SendOutAvatarChange;
                AfterAvatarChanges += SendOutLatest;
                HasEvents = true;
            }
        }

        public void ScheduleCheck()
        {
            // parameters & reference
            distanceJob.AvatarDistance = SMModuleDistanceBasedReductions.AvatarRange;
            distanceJob.HearingDistance = SMModuleDistanceBasedReductions.HearingRange;
            distanceJob.VoiceDistance = SMModuleDistanceBasedReductions.MicrophoneRange;
            distanceJob.HysteresisMargin = 0.05f; // same as before, tweak as needed
            distanceJob.referencePosition = MouthBone.OutgoingWorldData.position;

            int ReceiverCount = BasisNetworkPlayers.ReceiverCount;
            if (IndexLength != ReceiverCount)
            {
                ResizeOrCreateArrayData(ReceiverCount);

                LastMicrophoneRangeIndex = new bool[ReceiverCount];
                MicrophoneRangeIndex = new bool[ReceiverCount];
                HearingIndex = new bool[ReceiverCount];
                AvatarIndex = new bool[ReceiverCount];
                CalculatedDistances = new float[ReceiverCount];

                IndexLength = ReceiverCount;
                HearingIndexToId = BasisNetworkPlayers.RemotePlayers.Keys.ToArray();
            }

            // fill target positions
            var Snapshot = BasisNetworkPlayers.ReceiversSnapshot;
            for (int i = 0; i < ReceiverCount; i++)
            {
                var Remote = Snapshot[i];
                if (RemoteBoneJobSystem.GetOutGoingMouth(Remote.playerId, out float3 outgoing))
                    targetPositions[i] = outgoing;
            }

            // set up job: batch mins length = ceil(N / batchSize)
            int numBatches = math.max(1, (ReceiverCount + BatchSize - 1) / BatchSize);
            EnsureBatchMinsSize(numBatches);

            distanceJob.batchSize = BatchSize;
            distanceJob.batchMins = batchMins;

            // schedule: phase 1 (batched distance + hysteresis)
            distanceJobHandle = distanceJob.ScheduleBatch(targetPositions.Length, BatchSize);

            // schedule: phase 2 (reduce batch mins => smallestDistance[0])
            reduceJob.batchMins = batchMins;
            reduceJob.outMin = smallestDistance;
            reduceJobHandle = reduceJob.Schedule(distanceJobHandle);
        }

        void EnsureBatchMinsSize(int needed)
        {
            if (!batchMins.IsCreated || batchMins.Length != needed)
            {
                if (batchMins.IsCreated) batchMins.Dispose();
                batchMins = new NativeArray<float>(needed, Allocator.Persistent);
                // not required to init; producers will write each slot
            }
        }

        public void ResizeOrCreateArrayData(int TotalUserCount)
        {
            // wait for in-flight jobs
            if (!distanceJobHandle.IsCompleted) distanceJobHandle.Complete();
            if (!reduceJobHandle.IsCompleted) reduceJobHandle.Complete();

            // dispose old
            if (targetPositions.IsCreated) targetPositions.Dispose();
            if (distances.IsCreated) distances.Dispose();
            if (smallestDistance.IsCreated) smallestDistance.Dispose();
            if (DistanceResults.IsCreated) DistanceResults.Dispose();
            if (HearingResults.IsCreated) HearingResults.Dispose();
            if (AvatarResults.IsCreated) AvatarResults.Dispose();
            if (PrevDistanceResults.IsCreated) PrevDistanceResults.Dispose();
            if (PrevHearingResults.IsCreated) PrevHearingResults.Dispose();
            if (PrevAvatarResults.IsCreated) PrevAvatarResults.Dispose();
            if (MeshLodResults.IsCreated) MeshLodResults.Dispose();
            if (batchMins.IsCreated) batchMins.Dispose();

            // (re)create
            smallestDistance = new NativeArray<float>(1, Allocator.Persistent);
            smallestDistance[0] = float.PositiveInfinity;

            targetPositions = new NativeArray<float3>(TotalUserCount, Allocator.Persistent);
            distances = new NativeArray<float>(TotalUserCount, Allocator.Persistent);

            // outputs
            DistanceResults = new NativeArray<bool>(TotalUserCount, Allocator.Persistent);
            HearingResults = new NativeArray<bool>(TotalUserCount, Allocator.Persistent);
            AvatarResults = new NativeArray<bool>(TotalUserCount, Allocator.Persistent);

            // prevs (start false)
            PrevDistanceResults = new NativeArray<bool>(TotalUserCount, Allocator.Persistent);
            PrevHearingResults = new NativeArray<bool>(TotalUserCount, Allocator.Persistent);
            PrevAvatarResults = new NativeArray<bool>(TotalUserCount, Allocator.Persistent);

            MeshLodResults = new NativeArray<bool>(TotalUserCount, Allocator.Persistent);

            // wire job views
            distanceJob.distances = distances;
            distanceJob.DistanceInside = DistanceResults;
            distanceJob.HearingInside = HearingResults;
            distanceJob.AvatarInside = AvatarResults;
            distanceJob.PrevDistanceInside = PrevDistanceResults;
            distanceJob.PrevHearingInside = PrevHearingResults;
            distanceJob.PrevAvatarInside = PrevAvatarResults;
            distanceJob.targetPositions = targetPositions;

            // batch mins sized in ScheduleCheck (depends on TotalUserCount & BatchSize)
        }

        public override void DeInitialize()
        {
            AudioTransmission?.DeInitialize();

            if (HasEvents)
            {
                Player.OnAvatarSwitchedFallBack -= OnAvatarCalibrationLocal;
                Player.OnAvatarSwitched -= OnAvatarCalibrationLocal;
                Player.OnAvatarSwitched -= SendOutAvatarChange;
                AfterAvatarChanges -= SendOutLatest;

                if (!distanceJobHandle.IsCompleted) distanceJobHandle.Complete();
                if (!reduceJobHandle.IsCompleted) reduceJobHandle.Complete();

                if (targetPositions.IsCreated) targetPositions.Dispose();
                if (distances.IsCreated) distances.Dispose();
                if (smallestDistance.IsCreated) smallestDistance.Dispose();
                if (DistanceResults.IsCreated) DistanceResults.Dispose();
                if (HearingResults.IsCreated) HearingResults.Dispose();
                if (AvatarResults.IsCreated) AvatarResults.Dispose();
                if (PrevDistanceResults.IsCreated) PrevDistanceResults.Dispose();
                if (PrevHearingResults.IsCreated) PrevHearingResults.Dispose();
                if (PrevAvatarResults.IsCreated) PrevAvatarResults.Dispose();
                if (MeshLodResults.IsCreated) MeshLodResults.Dispose();
                if (batchMins.IsCreated) batchMins.Dispose();

                HasEvents = false;
            }
        }

        public static NetDataWriter AvatarChangeWriter = new NetDataWriter();
        public void SendOutAvatarChange()
        {
            LastLinkedAvatarIndex = (byte)((LastLinkedAvatarIndex + 1) % (byte.MaxValue + 1));

            ClientAvatarChangeMessage ClientAvatarChangeMessage = new ClientAvatarChangeMessage
            {
                byteArray = BasisBundleConversionNetwork.ConvertBasisLoadableBundleToBytes(Player.AvatarMetaData),
                loadMode = Player.AvatarLoadMode,
                LocalAvatarIndex = LastLinkedAvatarIndex,
            };
            AvatarChangeWriter.Reset();
            ClientAvatarChangeMessage.Serialize(AvatarChangeWriter);
            BasisNetworkConnection.LocalPlayerPeer.Send(AvatarChangeWriter, BasisNetworkCommons.AvatarChangeMessageChannel, DeliveryMethod.ReliableOrdered);
            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.AvatarChange, AvatarChangeWriter.Length);
        }

        [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Standard)]
        public struct BasisDistanceJobBatch : IJobParallelForBatch
        {
            public float VoiceDistance;
            public float HearingDistance;
            public float AvatarDistance;
            public float HysteresisMargin;
            public int batchSize;

            [ReadOnly] public float3 referencePosition;
            [ReadOnly] public NativeArray<float3> targetPositions;

            [ReadOnly] public NativeArray<bool> PrevDistanceInside;
            [ReadOnly] public NativeArray<bool> PrevHearingInside;
            [ReadOnly] public NativeArray<bool> PrevAvatarInside;

            [WriteOnly] public NativeArray<float> distances;
            [WriteOnly] public NativeArray<bool> DistanceInside;
            [WriteOnly] public NativeArray<bool> HearingInside;
            [WriteOnly] public NativeArray<bool> AvatarInside;

            // We write one unique element per batch — explicitly allow this access pattern.
            [NativeDisableParallelForRestriction]
            public NativeArray<float> batchMins;

            [BurstCompile]
            private static bool Hysteresis(bool wasInside, float d2, float threshold2, float margin)
            {
                float insideFactor = 1f + margin;
                float outsideFactor = 1f - margin;
                float factor = math.select(outsideFactor, insideFactor, wasInside);
                return d2 < threshold2 * factor;
            }

            public void Execute(int startIndex, int count)
            {
                float v2 = VoiceDistance * VoiceDistance;
                float h2 = HearingDistance * HearingDistance;
                float a2 = AvatarDistance * AvatarDistance;

                float minD2 = float.PositiveInfinity;
                int end = startIndex + count;

                for (int i = startIndex; i < end; i++)
                {
                    float3 diff = targetPositions[i] - referencePosition;
                    float d2 = math.lengthsq(diff);
                    distances[i] = d2;

                    bool dIn = Hysteresis(PrevDistanceInside[i], d2, v2, HysteresisMargin);
                    bool hIn = Hysteresis(PrevHearingInside[i], d2, h2, HysteresisMargin);
                    bool aIn = Hysteresis(PrevAvatarInside[i], d2, a2, HysteresisMargin);

                    DistanceInside[i] = dIn;
                    HearingInside[i] = hIn;
                    AvatarInside[i] = aIn;

                    minD2 = math.min(minD2, d2);
                }

                // one writer per batch slot
                int batchIndex = startIndex / math.max(1, batchSize);
                batchMins[batchIndex] = minD2;
            }
        }

        [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Standard)]
        public struct MinReduceJob : IJob
        {
            [ReadOnly] public NativeArray<float> batchMins;
            public NativeArray<float> outMin;   // length = 1

            public void Execute()
            {
                float m = float.PositiveInfinity;
                for (int i = 0; i < batchMins.Length; i++)
                    m = math.min(m, batchMins[i]);
                outMin[0] = m;
            }
        }
    }
}
