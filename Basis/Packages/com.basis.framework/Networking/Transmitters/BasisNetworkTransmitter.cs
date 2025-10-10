using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Players;
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
        public BasisDistanceJob distanceJob;
        public JobHandle distanceJobHandle;

        public int IndexLength = -1;
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
        public List<ushort> TalkingPoints = new List<ushort>(128);
        public NetDataWriter MicrophoneWriter = new NetDataWriter();
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
                    distanceJobHandle.Complete();

                    HandleResults();

                    SmallestDistanceToAnotherPlayer = smallestDistance[0]; // still squared
                    ServerMetaDataMessage Message = BasisNetworkManagement.ServerMetaDataMessage;

                    DefaultInterval = Message.SyncInterval / 1000f;

                    float CalculatedIntervalBase = Message.BaseMultiplier + (SmallestDistanceToAnotherPlayer * Message.IncreaseRate);
                    UnClampedInterval = DefaultInterval * CalculatedIntervalBase;
                    intervalSeconds = Mathf.Clamp(UnClampedInterval, DefaultInterval, Message.SlowestSendRate);
                }
                // account for overshoot
                timer -= intervalSeconds;
            }
        }
        public void HandleResults()
        {
            if (!DistanceResults.IsCreated || MicrophoneRangeIndex == null || MicrophoneRangeIndex.Length != DistanceResults.Length)
            {
                return;
            }

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
            float ActiveTime = Time.time;
            for (int Index = 0; Index < IndexLength; Index++)
            {
                try
                {
                    var Rec = BasisNetworkPlayers.ReceiversSnapshot[Index];
                    if (Rec == null)
                    {
                        continue;
                    }

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
                    Rec.RemotePlayer.FacialBlinkDriver.Simulate(ActiveTime);
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
                TalkingPoints.Clear();
                Array.Copy(MicrophoneRangeIndex, LastMicrophoneRangeIndex, IndexLength);
                for (int Index = 0; Index < IndexLength; Index++)
                {
                    if (MicrophoneRangeIndex[Index])
                    {
                        TalkingPoints.Add(HearingIndexToId[Index]);
                    }
                }
                HasReasonToSendAudio = TalkingPoints.Count != 0;

                VoiceReceiversMessage VRM = new VoiceReceiversMessage
                {
                    users = TalkingPoints.ToArray()
                };
                MicrophoneWriter.Reset();
                BasisDebug.Log("Sending out Microphone Check Data", BasisDebug.LogTag.Voice);
                VRM.Serialize(MicrophoneWriter);
                BasisNetworkConnection.LocalPlayerPeer.Send(MicrophoneWriter, BasisNetworkCommons.AudioRecipientsChannel, DeliveryMethod.ReliableOrdered);
                BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.AudioRecipients, MicrophoneWriter.Length);
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
                BasisNetworkPlayer.OnRemotePlayerJoined += Rebuild;
                BasisNetworkPlayer.OnRemotePlayerLeft += Rebuild;
                HasEvents = true;
            }
        }
        public bool requiresRebuild = false;
        public void Rebuild(BasisNetworkPlayer player, BasisRemotePlayer RemotePlayer)
        {
            requiresRebuild = true;
        }
        public void ScheduleCheck()
        {
            distanceJob.SquaredAvatarDistance = SMModuleDistanceBasedReductions.AvatarRange;
            distanceJob.SquaredHearingDistance = SMModuleDistanceBasedReductions.HearingRange;
            distanceJob.SquaredVoiceDistance = SMModuleDistanceBasedReductions.MicrophoneRange;
            distanceJob.HysteresisMargin = 0.05f;
            distanceJob.referencePosition = MouthBone.OutgoingWorldData.position;

            int ReceiverCount = BasisNetworkPlayers.ReceiverCount;
            if (IndexLength != ReceiverCount || requiresRebuild)
            {
                ResizeOrCreateArrayData(ReceiverCount);

                LastMicrophoneRangeIndex = new bool[ReceiverCount];
                MicrophoneRangeIndex = new bool[ReceiverCount];
                HearingIndex = new bool[ReceiverCount];
                AvatarIndex = new bool[ReceiverCount];
                CalculatedDistances = new float[ReceiverCount];
                IndexLength = ReceiverCount;
                requiresRebuild = false;
                HearingIndexToId = BasisNetworkPlayers.RemotePlayers.Keys.ToArray();
            }

            // fill target positions
            var Snapshot = BasisNetworkPlayers.ReceiversSnapshot;
            for (int Index = 0; Index < ReceiverCount; Index++)
            {
                var Remote = Snapshot[Index];
                if (RemoteBoneJobSystem.GetOutGoingMouth(Remote.playerId, out float3 outgoing))
                {
                    targetPositions[Index] = outgoing;
                }
                else
                {
                    BasisDebug.LogError($"Missing Mouth for {Remote.playerId}");
                }
            }

            // wire arrays (distanceJob is a field holding NativeArrays you manage elsewhere)
            distanceJob.outMin = smallestDistance;

            // single job, no batches, no reducer
            distanceJobHandle = distanceJob.Schedule();
        }

        public void ResizeOrCreateArrayData(int TotalUserCount)
        {
            // wait for in-flight jobs
            if (!distanceJobHandle.IsCompleted) distanceJobHandle.Complete();
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
            distanceJob.distanceSq = distances;
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

                BasisNetworkPlayer.OnRemotePlayerJoined -= Rebuild;
                BasisNetworkPlayer.OnRemotePlayerLeft -= Rebuild;

                AfterAvatarChanges -= SendOutLatest;

                if (!distanceJobHandle.IsCompleted) distanceJobHandle.Complete();
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
        public struct BasisDistanceJob : IJob
        {
            public float SquaredVoiceDistance;
            public float SquaredHearingDistance;
            public float SquaredAvatarDistance;
            public float HysteresisMargin; // 0..~0.5

            [ReadOnly] public float3 referencePosition;
            [ReadOnly] public NativeArray<float3> targetPositions;

            [ReadOnly] public NativeArray<bool> PrevDistanceInside;
            [ReadOnly] public NativeArray<bool> PrevHearingInside;
            [ReadOnly] public NativeArray<bool> PrevAvatarInside;

            [WriteOnly] public NativeArray<float> distanceSq;  // d^2 per target
            [WriteOnly] public NativeArray<bool> DistanceInside;
            [WriteOnly] public NativeArray<bool> HearingInside;
            [WriteOnly] public NativeArray<bool> AvatarInside;

            // length = 1
            public NativeArray<float> outMin;

            [BurstCompile]
            private static bool Hysteresis(bool wasInside, float d2, float thr2, float margin)
            {
                margin = math.clamp(margin, 0f, 0.49f);
                float insideFactor = 1f + margin;
                float outsideFactor = 1f - margin;
                float factor = math.select(outsideFactor, insideFactor, wasInside);
                return d2 < thr2 * factor;
            }

            public void Execute()
            {
                float3 refPos = referencePosition;
                float minD2 = float.PositiveInfinity;

                for (int i = 0; i < targetPositions.Length; i++)
                {
                    float3 diff = targetPositions[i] - refPos;
                    float d2 = math.lengthsq(diff);
                    distanceSq[i] = d2;

                    DistanceInside[i] = Hysteresis(PrevDistanceInside[i], d2, SquaredVoiceDistance, HysteresisMargin);
                    HearingInside[i] = Hysteresis(PrevHearingInside[i], d2, SquaredHearingDistance, HysteresisMargin);
                    AvatarInside[i] = Hysteresis(PrevAvatarInside[i], d2, SquaredAvatarDistance, HysteresisMargin);

                    minD2 = math.min(minD2, d2);
                }

                if (outMin.IsCreated) outMin[0] = minD2;
            }
        }
    }
}
