using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Profiler;
using Basis.Scripts.TransformBinders.BoneControl;
using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections.Generic;
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

        [SerializeField]
        public BasisAudioTransmission AudioTransmission = new BasisAudioTransmission();

        // Core native buffers
        public NativeArray<float3> targetPositions;
        public NativeArray<float> distances; // squared distances

        // Prev-state (read-only in job) + New-state (write-only in job)
        public NativeArray<bool> PrevDistanceResults;
        public NativeArray<bool> PrevHearingResults;
        public NativeArray<bool> PrevAvatarResults;

        public NativeArray<bool> DistanceResults;
        public NativeArray<bool> HearingResults;
        public NativeArray<bool> AvatarResults;

        // Reduction buffer
        public NativeArray<float> smallestDistance; // length 1, stores MIN **squared** distance

        [SerializeField]
        public BasisStoredAvatarData storedAvatarData = new BasisStoredAvatarData();

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
        public ushort[] HearingIndexToId; // aligned to ReceiversSnapshot order
        public AdditionalAvatarData[] AdditionalAvatarData;
        public Dictionary<byte, AdditionalAvatarData> SendingOutAvatarData = new Dictionary<byte, AdditionalAvatarData>();
        public float[] CalculatedDistances; // squared distances mirror

        // temp recipients buffer to avoid per-tick allocations
        private ushort[] recipientsBuffer;

        public static Action AfterAvatarChanges;

        public float intervalSeconds = 0.5f;
        public float timer = 0f;
        public float SmallestDistanceToAnotherPlayer; // squared distance
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
                float previousInterval = intervalSeconds; // use the interval that actually accumulated

                if (Player != null ? Player.BasisAvatar : null != null)
                {
                    // Guard against missing mouth bone
                    if (MouthBone == null)
                    {
                        BasisDebug.LogError("MouthBone is null; cannot schedule distance job.", BasisDebug.LogTag.System);
                        // keep ticking at default interval to attempt recovery
                    }
                    else
                    {
                        ScheduleCheck();

                        // If compression reads avatar state, do it consistently before/after job completion.
                        // Here we do compression AFTER scheduling and BEFORE completing the job is okay
                        // only if it doesn't mutate bones read by ScheduleCheck. If it does, move this call
                        // either before ScheduleCheck() or after distanceJobHandle.Complete().
                        BasisNetworkAvatarCompressor.Compress(this, Player.BasisAvatar.Animator);

                        // complete distance job
                        distanceJobHandle.Complete();

                        HandleResults();

                        SmallestDistanceToAnotherPlayer = smallestDistance[0]; // still squared
                        if (!float.IsFinite(SmallestDistanceToAnotherPlayer))
                        {
                            // No receivers or all failed positions => treat as zero distance for rate calc,
                            // or choose to pin to default; here we use default base multiplier path.
                            SmallestDistanceToAnotherPlayer = 0f;
                        }

                        ServerMetaDataMessage Message = BasisNetworkManagement.ServerMetaDataMessage;
                        DefaultInterval = Message.SyncInterval / 1000f;

                        // Keep squared inside; apply sqrt at boundary where human-tuned params likely expect meters.
                        float minLinear = math.sqrt(math.max(0f, SmallestDistanceToAnotherPlayer));

                        float CalculatedIntervalBase = Message.BaseMultiplier + (minLinear * Message.IncreaseRate);
                        UnClampedInterval = DefaultInterval * CalculatedIntervalBase;
                        intervalSeconds = Mathf.Clamp(UnClampedInterval, DefaultInterval, Message.SlowestSendRate);
                    }
                }
                else
                {
                    BasisDebug.LogError("Missing Basis Avatar. Cannot send network update.", BasisDebug.LogTag.System);
                }

                // account for overshoot using the interval that actually accumulated
                timer -= previousInterval;
            }
        }

        public void HandleResults()
        {
            // Ensure all mirrors exist and match lengths before copying
            if (!DistanceResults.IsCreated || !HearingResults.IsCreated || !AvatarResults.IsCreated || !distances.IsCreated)
                return;

            if (MicrophoneRangeIndex == null || HearingIndex == null || AvatarIndex == null || CalculatedDistances == null)
                return;

            int n = DistanceResults.Length;
            if (MicrophoneRangeIndex.Length != n || HearingIndex.Length != n || AvatarIndex.Length != n || CalculatedDistances.Length != n)
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

                    // Avatar LOD/range toggling
                    if (Rec.RemotePlayer.InAvatarRange != AvatarIndex[Index])
                    {
                        Rec.RemotePlayer.InAvatarRange = AvatarIndex[Index];
                        Rec.RemotePlayer.ReloadAvatar();
                    }

                    Rec.RemotePlayer.ChangeMeshLOD(CalculatedDistances[Index], SMModuleDistanceBasedReductions.MeshLod);

                    // voice start/stop
                    if (Rec.AudioReceiverModule.HasAudioSource != HearingIndex[Index])
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
            // Basic validation
            if (MicrophoneRangeIndex == null || LastMicrophoneRangeIndex == null || HearingIndexToId == null)
            {
                return;
            }

            if (MicrophoneRangeIndex.Length != IndexLength || LastMicrophoneRangeIndex.Length != IndexLength || HearingIndexToId.Length != IndexLength)
            {
                BasisDebug.LogError("MicrophoneOutputCheck: length mismatch.", BasisDebug.LogTag.Voice);
                return;
            }

            // Fast exit if nothing changed
            if (AreBoolArraysEqual(MicrophoneRangeIndex, LastMicrophoneRangeIndex))
            {
                return;
            }

            // Rebuild talking points (ensure capacity to avoid repeated allocations)
            if (TalkingPoints.Capacity < IndexLength)
            {
                TalkingPoints.Capacity = IndexLength;
            }

            TalkingPoints.Clear();

            for (int Index = 0; Index < IndexLength; Index++)
            {
                if (MicrophoneRangeIndex[Index])
                {
                    TalkingPoints.Add(HearingIndexToId[Index]); // IDs aligned to snapshot order
                }
            }

            HasReasonToSendAudio = TalkingPoints.Count != 0;

            // Copy current -> last for next tick
            Array.Copy(MicrophoneRangeIndex, LastMicrophoneRangeIndex, IndexLength);

            // Serialize & send using a reusable buffer (no pool + ToArray dance)
            int count = TalkingPoints.Count;
            if (recipientsBuffer == null || recipientsBuffer.Length < count)
            {
                // grow (double to reduce churn)
                int newLen = recipientsBuffer == null ? math.max(8, count) : math.max(recipientsBuffer.Length * 2, count);
                recipientsBuffer = new ushort[newLen];
            }

            for (int i = 0; i < count; i++)
            {
                recipientsBuffer[i] = TalkingPoints[i];
            }

            var vrm = new VoiceReceiversMessage
            {
                // NOTE: if VoiceReceiversMessage can be upgraded, expose a Span-based setter to avoid this copy.
                users = CreateExactSizedArray(recipientsBuffer, count)
            };

            MicrophoneWriter.Reset();
#if BASIS_DEBUG || UNITY_EDITOR
            BasisDebug.Log($"Sending Microphone Check Data (count={count})", BasisDebug.LogTag.Voice);
#endif
            vrm.Serialize(MicrophoneWriter);

            BasisNetworkConnection.LocalPlayerPeer.Send(MicrophoneWriter,BasisNetworkCommons.AudioRecipientsChannel,DeliveryMethod.ReliableOrdered);

            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.AudioRecipients, MicrophoneWriter.Length);
        }

        private static ushort[] CreateExactSizedArray(ushort[] src, int count)
        {
            if (count == 0) return Array.Empty<ushort>();
            var arr = new ushort[count];
            Array.Copy(src, arr, count);
            return arr;
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
            if (MouthBone == null) return;

            distanceJob.SquaredAvatarDistance = SMModuleDistanceBasedReductions.AvatarRange;
            distanceJob.SquaredHearingDistance = SMModuleDistanceBasedReductions.HearingRange;
            distanceJob.SquaredVoiceDistance = SMModuleDistanceBasedReductions.MicrophoneRange;
            distanceJob.HysteresisMargin = 0.05f; // clamped inside
            distanceJob.referencePosition = MouthBone.OutgoingWorldData.position;

            int ReceiverCount = BasisNetworkPlayers.ReceiverCount;
            if (IndexLength != ReceiverCount || requiresRebuild)
            {
                ResizeOrCreateArrayData(ReceiverCount);

                // managed mirrors
                LastMicrophoneRangeIndex = new bool[ReceiverCount];
                MicrophoneRangeIndex = new bool[ReceiverCount];
                HearingIndex = new bool[ReceiverCount];
                AvatarIndex = new bool[ReceiverCount];
                CalculatedDistances = new float[ReceiverCount];
                HearingIndexToId = new ushort[ReceiverCount];

                IndexLength = ReceiverCount;
                requiresRebuild = false;
            }

            // fill target positions and ID map **aligned to snapshot order**
            var Snapshot = BasisNetworkPlayers.ReceiversSnapshot;
            for (int Index = 0; Index < ReceiverCount; Index++)
            {
                var Remote = Snapshot[Index];

                ushort rid = 0;
                float3 outgoing = float3.zero;
                bool hasMouth = Remote != null && RemoteBoneJobSystem.GetOutGoingMouth(Remote.playerId, out outgoing);

                if (hasMouth)
                {
                    targetPositions[Index] = outgoing;
                    rid = Remote.playerId;
                }
                else
                {
                    // Use reference position as fallback to avoid uninitialized memory in job
                    targetPositions[Index] = distanceJob.referencePosition;
                    if (Remote != null)
                        BasisDebug.LogError($"Missing Mouth for {Remote.playerId}");
                }

                HearingIndexToId[Index] = rid;
            }

            // reduction output
            distanceJob.outMin = smallestDistance;

            distanceJobHandle = distanceJob.Schedule();
        }

        public void ResizeOrCreateArrayData(int TotalUserCount)
        {
            // wait for in-flight jobs
            if (!distanceJobHandle.IsCompleted) distanceJobHandle.Complete();

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

            // wire job views
            distanceJob.distanceSq = distances;
            distanceJob.DistanceInside = DistanceResults;
            distanceJob.HearingInside = HearingResults;
            distanceJob.AvatarInside = AvatarResults;
            distanceJob.PrevDistanceInside = PrevDistanceResults;
            distanceJob.PrevHearingInside = PrevHearingResults;
            distanceJob.PrevAvatarInside = PrevAvatarResults;
            distanceJob.targetPositions = targetPositions;
        }

        public override void DeInitialize()
        {
            AudioTransmission?.DeInitialize();

            if (HasEvents)
            {
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
            public float HysteresisMargin; // 0..0.49 (clamped)

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
