using Basis.Network.Core;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Profiler;
using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
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
        public float timer = 0f;
        public float interval = 0.0333333333333333f;
        public float SmallestDistanceToAnotherPlayer;
        [SerializeField]
        public BasisAudioTransmission AudioTransmission = new BasisAudioTransmission();
        public NativeArray<float3> targetPositions;
        public NativeArray<float> distances;
        public NativeArray<bool> DistanceResults;

        public NativeArray<bool> HearingResults;
        public NativeArray<bool> AvatarResults;
        public NativeArray<float> smallestDistance;

        public float[] FloatArray = new float[LocalAvatarSyncMessage.StoredBones];
        public ushort[] UshortArray = new ushort[LocalAvatarSyncMessage.StoredBones];
        [SerializeField]
        public LocalAvatarSyncMessage LASM = new LocalAvatarSyncMessage();
        public float UnClampedInterval;

        public float DefaultInterval = 0.0333333333333333f;
        public float BaseMultiplier = 1f; // Starting multiplier.
        public float IncreaseRate = 0.005f; // Rate of increase per unit distance.
        public BasisDistanceJobs distanceJob = new BasisDistanceJobs();
        public JobHandle distanceJobHandle;
        public int IndexLength = -1;
        public float SlowestSendRate = 2.5f;
        public NetDataWriter AvatarSendWriter = new NetDataWriter(true, LocalAvatarSyncMessage.AvatarSyncSize + 1);
        public bool[] MicrophoneRangeIndex;
        public bool[] LastMicrophoneRangeIndex;

        public bool[] HearingIndex;
        public bool[] AvatarIndex;
        public ushort[] HearingIndexToId;

        public AdditionalAvatarData[] AdditionalAvatarData;
        public Dictionary<byte, AdditionalAvatarData> SendingOutAvatarData = new Dictionary<byte, AdditionalAvatarData>();
        public float[] CalculatedDistances;
        public static Action AfterAvatarChanges;
        public const float SmallestOutgoingInterval = 0.005f;
        /// <summary>
        /// schedules data going out. replaces existing byte index.
        /// </summary>
        /// <param name="AvatarData"></param>
        public void AddAdditional(AdditionalAvatarData AvatarData)
        {
            SendingOutAvatarData[AvatarData.messageIndex] = AvatarData;
        }
        public void ClearAdditional()
        {
            SendingOutAvatarData.Clear();
        }
        void SendOutLatest()
        {
            timer += Time.deltaTime;

            if (timer >= interval)
            {
                if (Player.BasisAvatar != null)
                {
                    ScheduleCheck();
                    BasisNetworkAvatarCompressor.Compress(this, Player.BasisAvatar.Animator);
                    distanceJobHandle.Complete();
                    HandleResults();
                    SmallestDistanceToAnotherPlayer = distanceJob.smallestDistance[0];

                    // Calculate next interval and clamp it
                    UnClampedInterval = DefaultInterval * (BaseMultiplier + (SmallestDistanceToAnotherPlayer * IncreaseRate));
                    interval = math.clamp(UnClampedInterval, SmallestOutgoingInterval, SlowestSendRate);

                    // Account for overshoot
                    timer -= interval;
                }
            }
        }
        public void HandleResults()
        {
            if (distanceJob.DistanceResults == null ||
                MicrophoneRangeIndex == null ||
                MicrophoneRangeIndex.Length != distanceJob.DistanceResults.Length)
            {
                return;
            }

            distanceJob.DistanceResults.CopyTo(MicrophoneRangeIndex);
            distanceJob.HearingResults.CopyTo(HearingIndex);
            distanceJob.AvatarResults.CopyTo(AvatarIndex);
            distanceJob.distances.CopyTo(CalculatedDistances);

            MicrophoneOutputCheck();
            IterationOverRemotePlayers();
        }
        /// <summary>
        /// how far we can hear locally
        /// </summary>
        public void IterationOverRemotePlayers()
        {
            for (int Index = 0; Index < IndexLength; Index++)
            {
                try
                {
                    Receivers.BasisNetworkReceiver Rec = BasisNetworkManagement.ReceiverArray[Index];
                    //first handle avatar itself
                    if (Rec.RemotePlayer.InAvatarRange != AvatarIndex[Index])
                    {
                        Rec.RemotePlayer.InAvatarRange = AvatarIndex[Index];
                        Rec.RemotePlayer.ReloadAvatar();
                    }
                    //then handle voice
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
                    //now we process the avatar based stuff in order of risk to break.
                    if (Rec.RemotePlayer.HasJiggles)
                    {
                        if (float.IsNaN(CalculatedDistances[Index]) || CalculatedDistances[Index] == 0)
                        {
                            CalculatedDistances[Index] = 0.1f;
                        }
                        Rec.RemotePlayer.BasisAvatarStrainJiggleDriver.Simulate(CalculatedDistances[Index]);
                    }
                    Rec.RemotePlayer.EyeFollow.Simulate();
                    Rec.RemotePlayer.FacialBlinkDriver.Simulate();
                }
                catch (Exception ex)
                {
                    BasisDebug.LogError($"{ex} {ex.StackTrace}");
                }
            }
        }
        /// <summary>
        ///lets the server know who can hear us.
        /// </summary>
        public void MicrophoneOutputCheck()
        {
            if (AreBoolArraysEqual(MicrophoneRangeIndex, LastMicrophoneRangeIndex) == false)
            {
                //BasisDebug.Log("Arrays where not equal!");
                Array.Copy(MicrophoneRangeIndex, LastMicrophoneRangeIndex, IndexLength);
                List<ushort> TalkingPoints = new List<ushort>(IndexLength);
                for (int Index = 0; Index < IndexLength; Index++)
                {
                    bool User = MicrophoneRangeIndex[Index];
                    if (User)
                    {
                        TalkingPoints.Add(HearingIndexToId[Index]);
                    }
                }
                HasReasonToSendAudio = TalkingPoints.Count != 0;
                //even if we are not listening to anyone we still need to tell the server that!
                VoiceReceiversMessage VRM = new VoiceReceiversMessage
                {
                    users = TalkingPoints.ToArray()
                };
                NetDataWriter writer = new NetDataWriter();
                VRM.Serialize(writer);
                BasisNetworkManagement.LocalPlayerPeer.Send(writer, BasisNetworkCommons.AudioRecipients, DeliveryMethod.ReliableOrdered);
                BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.AudioRecipients, writer.Length);
            }
        }
        public static bool AreBoolArraysEqual(bool[] array1, bool[] array2)
        {
            // Check if both arrays are null
            if (array1 == null && array2 == null)
            {
                return true;
            }

            // Check if one of them is null
            if (array1 == null || array2 == null)
            {
                return false;
            }

            int Arraylength = array1.Length;
            // Check if lengths differ
            if (Arraylength != array2.Length)
            {
                return false;
            }

            // Compare values
            for (int Index = 0; Index < Arraylength; Index++)
            {
                if (array1[Index] != array2[Index])
                {
                    return false;
                }
            }

            return true;
        }
        public override void Initialize()
        {
            IndexLength = -1;
            AudioTransmission.OnEnable(this);
            OnAvatarCalibrationLocal();
            if (HasEvents == false)
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
            distanceJob.AvatarDistance = SMModuleDistanceBasedReductions.AvatarRange;
            distanceJob.HearingDistance = SMModuleDistanceBasedReductions.HearingRange;
            distanceJob.VoiceDistance = SMModuleDistanceBasedReductions.MicrophoneRange;
            distanceJob.referencePosition = MouthBone.OutgoingWorldData.position;
            if (IndexLength != BasisNetworkManagement.ReceiverCount)
            {
                ResizeOrCreateArrayData(BasisNetworkManagement.ReceiverCount);
                LastMicrophoneRangeIndex = new bool[BasisNetworkManagement.ReceiverCount];
                MicrophoneRangeIndex = new bool[BasisNetworkManagement.ReceiverCount];
                HearingIndex = new bool[BasisNetworkManagement.ReceiverCount];
                AvatarIndex = new bool[BasisNetworkManagement.ReceiverCount];
                CalculatedDistances = new float[BasisNetworkManagement.ReceiverCount];

                IndexLength = BasisNetworkManagement.ReceiverCount;
                HearingIndexToId = BasisNetworkManagement.RemotePlayers.Keys.ToArray();
            }
            for (int Index = 0; Index < BasisNetworkManagement.ReceiverCount; Index++)
            {
                targetPositions[Index] = BasisNetworkManagement.ReceiverArray[Index].MouthBone.OutgoingWorldData.position;
            }
            smallestDistance[0] = float.MaxValue;
            distanceJobHandle = distanceJob.Schedule(targetPositions.Length, 64);
        }
        public void ResizeOrCreateArrayData(int TotalUserCount)
        {
            if (distanceJobHandle.IsCompleted == false)
            {
                distanceJobHandle.Complete();
            }
            if (targetPositions.IsCreated)
            {
                targetPositions.Dispose();
            }
            if (distances.IsCreated)
            {
                distances.Dispose();
            }
            if (smallestDistance.IsCreated)
            {
                smallestDistance.Dispose();
            }
            if (DistanceResults.IsCreated)
            {
                DistanceResults.Dispose();
            }
            if (HearingResults.IsCreated)
            {
                HearingResults.Dispose();
            }
            if (AvatarResults.IsCreated)
            {
                AvatarResults.Dispose();
            }
            smallestDistance = new NativeArray<float>(1, Allocator.Persistent);
            smallestDistance[0] = float.MaxValue;
            targetPositions = new NativeArray<float3>(TotalUserCount, Allocator.Persistent);
            distances = new NativeArray<float>(TotalUserCount, Allocator.Persistent);
            DistanceResults = new NativeArray<bool>(TotalUserCount, Allocator.Persistent);

            HearingResults = new NativeArray<bool>(TotalUserCount, Allocator.Persistent);
            AvatarResults = new NativeArray<bool>(TotalUserCount, Allocator.Persistent);
            // Step 2: Find closest index in the next frame
            distanceJob.distances = distances;
            distanceJob.DistanceResults = DistanceResults;
            distanceJob.HearingResults = HearingResults;
            distanceJob.AvatarResults = AvatarResults;


            distanceJob.targetPositions = targetPositions;

            distanceJob.smallestDistance = smallestDistance;
        }
        public override void DeInitialize()
        {
            if (AudioTransmission != null)
            {
                AudioTransmission.OnDisable();
            }
            if (HasEvents)
            {
                Player.OnAvatarSwitchedFallBack -= OnAvatarCalibrationLocal;
                Player.OnAvatarSwitched -= OnAvatarCalibrationLocal;
                Player.OnAvatarSwitched -= SendOutAvatarChange;
                AfterAvatarChanges -= SendOutLatest;
                if (targetPositions.IsCreated) targetPositions.Dispose();
                if (distances.IsCreated) distances.Dispose();
                if (smallestDistance.IsCreated)
                {
                    smallestDistance.Dispose();
                }
                if (DistanceResults.IsCreated)
                {
                    DistanceResults.Dispose();
                }
                if (HearingResults.IsCreated)
                {
                    HearingResults.Dispose();
                }
                if (AvatarResults.IsCreated)
                {
                    AvatarResults.Dispose();
                }
                HasEvents = false;
            }
        }
        public void SendOutAvatarChange()
        {
            NetDataWriter Writer = new NetDataWriter();
            ClientAvatarChangeMessage ClientAvatarChangeMessage = new ClientAvatarChangeMessage
            {
                byteArray = BasisBundleConversionNetwork.ConvertBasisLoadableBundleToBytes(Player.AvatarMetaData),
                loadMode = Player.AvatarLoadMode,
            };
            ClientAvatarChangeMessage.Serialize(Writer);
            BasisNetworkManagement.LocalPlayerPeer.Send(Writer, BasisNetworkCommons.AvatarChangeMessage, DeliveryMethod.ReliableOrdered);
            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.AvatarChange, Writer.Length);
        }
    }
}
