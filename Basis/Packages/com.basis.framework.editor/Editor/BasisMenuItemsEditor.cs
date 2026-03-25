using Basis.Network.Core;
using Basis.Scripts.Avatar;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Device_Management.Devices.Desktop;
using Basis.Scripts.Device_Management.Devices.Simulation;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.Transmitters;
using Basis.Scripts.Profiler;
using Basis.Scripts.TransformBinders.BoneControl;
using BasisNetworkServer.BasisNetworking;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Unity.Profiling;
using Unity.Profiling.Editor;
using UnityEditor;
using UnityEngine;
using static Basis.Scripts.Device_Management.BasisDeviceManagement;
using static SerializableBasis;


public static class BasisMenuItemsEditor
{
    [MenuItem("Basis/Avatar/ReloadAvatar")]
    public static async Task ReloadAvatar()
    {
        if (BasisDataStore.LoadAvatar(BasisLocalPlayer.LoadFileNameAndExtension, BasisBeeConstants.DefaultAvatar, BasisPlayer.LoadModeLocal, out BasisDataStore.BasisSavedAvatar LastSavedAvatar))
        {
            await BasisLocalPlayer.Instance.LoadInitialAvatar(LastSavedAvatar);
        }
    }
    [MenuItem("Basis/Trackers/Hide Trackers")]
    public static void HideTrackersEditor()
    {
        BasisDeviceManagement.VisibleTrackers(false);
    }

    [MenuItem("Basis/Trackers/Show Trackers")]
    public static void ShowTrackersEditor()
    {
        BasisDeviceManagement.VisibleTrackers(true);
    }
    [MenuItem("Basis/Trackers/Destroy All Tracker")]
    public static void DestroyXRInput()
    {
        List<BasisInput> allDevicesToRemove = new List<BasisInput>(BasisDeviceManagement.Instance.AllInputDevices);

        // Remove devices from AllInputDevices list after iteration
        foreach (var device in allDevicesToRemove)
        {
            BasisDeviceManagement.Instance.RemoveDevicesFrom("BasisSimulateXR", device.UniqueDeviceIdentifier);
        }
    }
    [MenuItem("Basis/Trackers/Destroy And Restore XR Input")]
    public static void DestroyAndRebuildXRInput()
    {
        DestroyXRInput();
        List<BasisStoredPreviousDevice> allDevicesToRemove = new List<BasisStoredPreviousDevice>(BasisDeviceManagement.Instance.PreviouslyConnectedDevices);
        var Value = FindSimulate();
        foreach (var device in allDevicesToRemove)
        {
            Value.CreatePhysicalTrackedDevice(device.UniqueDeviceIdentifier, "{htc}vr_tracker_vive_3_0");
        }
    }
    public static BasisSimulateXR FindSimulate()
    {
        if (BasisDeviceManagement.Instance.TryFindBasisBaseTypeManagement("SimulateXR", out List<BasisBaseTypeManagement> Matched, true))
        {
            foreach (var m in Matched)
            {
                BasisSimulateXR XR = (BasisSimulateXR)m;
                return XR;
            }
        }
        return null;
    }
    [MenuItem("Basis/Trackers/Create Puck Tracker")]
    public static void CreatePuckTracker()
    {
        BasisLocalPlayer.Instance.LocalAvatarDriver.PutAvatarIntoTPose();
        var Value = FindSimulate();
        Value.CreatePhysicalTrackedDevice("{htc}vr_tracker_vive_3_0" + UnityEngine.Random.Range(-9999999999999, 999999999999), "{htc}vr_tracker_vive_3_0");
        BasisDeviceManagement.VisibleTrackers(true);
        BasisLocalPlayer.Instance.LocalAvatarDriver.ResetAvatarAnimator();
    }
    [MenuItem("Basis/Trackers/Create Vive Right Controller")]
    public static void CreateViveRightTracker()
    {
        BasisLocalPlayer.Instance.LocalBoneDriver.FindBone(out BasisLocalBoneControl RightHand, BasisBoneTrackedRole.RightHand);
        var Value = FindSimulate();
        BasisInputXRSimulate RightTracker = Value.CreatePhysicalTrackedDevice("{indexcontroller}valve_controller_knu_3_0_right" + UnityEngine.Random.Range(-9999999999999, 999999999999), "{indexcontroller}valve_controller_knu_3_0_right", BasisBoneTrackedRole.RightHand, true);
        RightTracker.FollowMovement.position = RightHand.OutgoingWorldData.position;
        RightTracker.FollowMovement.rotation = Quaternion.identity;
        BasisDeviceManagement.VisibleTrackers(true);
    }
    [MenuItem("Basis/Trackers/Create Vive Left Controller")]
    public static void CreateViveLeftTracker()
    {
        BasisLocalPlayer.Instance.LocalBoneDriver.FindBone(out BasisLocalBoneControl LeftHand, BasisBoneTrackedRole.LeftHand);
        var Value = FindSimulate();
        BasisInputXRSimulate LeftTracker = Value.CreatePhysicalTrackedDevice("{indexcontroller}valve_controller_knu_3_0_left" + UnityEngine.Random.Range(-9999999999999, 999999999999), "{indexcontroller}valve_controller_knu_3_0_left", BasisBoneTrackedRole.LeftHand, true);
        LeftTracker.FollowMovement.position = LeftHand.OutgoingWorldData.position;
        LeftTracker.FollowMovement.rotation = Quaternion.identity;
        BasisDeviceManagement.VisibleTrackers(true);
    }
    [MenuItem("Basis/Trackers/Create Unknown Tracker")]
    public static void CreateUnknowonTracker()
    {
        BasisLocalPlayer.Instance.LocalBoneDriver.FindBone(out BasisLocalBoneControl LeftHand, BasisBoneTrackedRole.LeftHand);
        var Value = FindSimulate();
        BasisInputXRSimulate LeftTracker = Value.CreatePhysicalTrackedDevice("Unknown" + UnityEngine.Random.Range(-9999999999999, 999999999999), "Unknown", BasisBoneTrackedRole.CenterEye, false);
        LeftTracker.FollowMovement.position = LeftHand.OutgoingWorldData.position;
        LeftTracker.FollowMovement.rotation = Quaternion.identity;
        BasisDeviceManagement.VisibleTrackers(true);
    }
    [MenuItem("Basis/Trackers/Create Left And Right Hands")]
    public static void CreateLRTracker()
    {
        CreateViveLeftTracker();
        CreateViveRightTracker();
    }
    [MenuItem("Basis/Trackers/Create 3Point Tracking")]
    public static void CreatePuck3Tracker()
    {
        BasisLocalPlayer.Instance.LocalAvatarDriver.PutAvatarIntoTPose();
        BasisSimulateXR XR = FindSimulate();
        BasisInputXRSimulate BasisHips = XR.CreatePhysicalTrackedDevice("{htc}vr_tracker_vive_3_0 BasisHips | " + UnityEngine.Random.Range(-9999999999999, 999999999999), "{htc}vr_tracker_vive_3_0");
        BasisInputXRSimulate BasisLeftFoot = XR.CreatePhysicalTrackedDevice("{htc}vr_tracker_vive_3_0 BasisLeftFoot | " + UnityEngine.Random.Range(-9999999999999, 999999999999), "{htc}vr_tracker_vive_3_0");
        BasisInputXRSimulate BasisRightFoot = XR.CreatePhysicalTrackedDevice("{htc}vr_tracker_vive_3_0 BasisRightFoot | " + UnityEngine.Random.Range(-9999999999999, 999999999999), "{htc}vr_tracker_vive_3_0");

        var hips = BasisLocalAvatarDriver.Mapping.Hips;
        var leftFoot = BasisLocalAvatarDriver.Mapping.leftFoot;
        var rightFoot = BasisLocalAvatarDriver.Mapping.rightFoot;

        Vector3 HipsPosition = ModifyVector(hips.position);
        Vector3 leftFootPosition = ModifyVector(leftFoot.position);
        Vector3 rightFootPosition = ModifyVector(rightFoot.position);

        BasisHips.FollowMovement.position = HipsPosition;
        BasisLeftFoot.FollowMovement.position = leftFootPosition;
        BasisRightFoot.FollowMovement.position = rightFootPosition;

        BasisHips.FollowMovement.rotation = UnityEngine.Random.rotation;
        BasisLeftFoot.FollowMovement.rotation = UnityEngine.Random.rotation;
        BasisRightFoot.FollowMovement.rotation = UnityEngine.Random.rotation;
        BasisLocalPlayer.Instance.LocalAvatarDriver.ResetAvatarAnimator();
        // Show the trackers
        BasisDeviceManagement.VisibleTrackers(true);
    }
    [MenuItem("Basis/Trackers/Create MaxTracker Tracking")]
    public static void CreateFullMaxTracker()
    {
        //  BasisLocalPlayer.Instance.AvatarDriver.PutAvatarIntoTPose();
        // Create an array of the tracker names for simplicity
        string trackerName = "{htc}vr_tracker_vive_3_0";

        var avatarDriver = BasisLocalAvatarDriver.Mapping;
        // avatarDriver.neck, avatarDriver.head,
        // Array of all relevant body parts
        Transform[] bodyParts = new Transform[]
        {
            avatarDriver.Hips, avatarDriver.chest,
            avatarDriver.leftShoulder, avatarDriver.RightShoulder,
            avatarDriver.leftLowerArm,avatarDriver.RightLowerArm,
            avatarDriver.LeftLowerLeg, avatarDriver.RightLowerLeg,
            avatarDriver.leftFoot, avatarDriver.leftToe,
            avatarDriver.rightFoot, avatarDriver.rightToe
        };
        int bodyPartsCount = bodyParts.Length;
        // Create an array of the BasisInputXRSimulate instances
        List<BasisInputXRSimulate> trackers = new List<BasisInputXRSimulate>(bodyPartsCount);
        BasisSimulateXR XR = FindSimulate();
        for (int Index = 0; Index < bodyPartsCount; Index++)
        {
            if (bodyParts[Index] != null)
            {
                XR.CreatePhysicalTrackedDevice(trackerName + " part " + Index, trackerName);
                trackers.Add(XR.Inputs[Index]);
                Vector3 bodyPartPosition = ModifyVector(bodyParts[Index].position);
                XR.Inputs[Index].FollowMovement.SetPositionAndRotation(bodyPartPosition, UnityEngine.Random.rotation);
            }
        }
        BasisDeviceManagement.VisibleTrackers(true);
    }
    [MenuItem("Basis/Trackers/Create MaxTracker Tracking Normal Pos")]
    public static void CreateFullMaxTrackerUnModifedPos()
    {
        //  BasisLocalPlayer.Instance.AvatarDriver.PutAvatarIntoTPose();
        // Create an array of the tracker names for simplicity
        string trackerName = "{htc}vr_tracker_vive_3_0";

        var avatarDriver = BasisLocalAvatarDriver.Mapping;
        // avatarDriver.neck, avatarDriver.head,
        // Array of all relevant body parts
        Transform[] bodyParts = new Transform[]
        {
            avatarDriver.Hips, avatarDriver.chest,
            avatarDriver.leftShoulder, avatarDriver.RightShoulder,
            avatarDriver.leftLowerArm,avatarDriver.RightLowerArm,
            avatarDriver.LeftLowerLeg, avatarDriver.RightLowerLeg,
            avatarDriver.leftFoot, avatarDriver.leftToe,
            avatarDriver.rightFoot, avatarDriver.rightToe
        };
        int bodyPartsCount = bodyParts.Length;
        // Create an array of the BasisInputXRSimulate instances
        List<BasisInputXRSimulate> trackers = new List<BasisInputXRSimulate>(bodyPartsCount);
        BasisSimulateXR XR = FindSimulate();
        for (int Index = 0; Index < bodyPartsCount; Index++)
        {
            if (bodyParts[Index] != null)
            {
                XR.CreatePhysicalTrackedDevice(trackerName + " part " + Index, trackerName);
                trackers.Add(XR.Inputs[Index]);
                Vector3 bodyPartPosition = ModifyVector(bodyParts[Index].position);
                XR.Inputs[Index].FollowMovement.SetPositionAndRotation(bodyPartPosition, UnityEngine.Random.rotation);
            }
        }
        BasisDeviceManagement.VisibleTrackers(true);
    }
    [MenuItem("Basis/Avatar/TPose Animator")]
    public static void PutAvatarIntoTpose()
    {
        BasisLocalPlayer.Instance.LocalAvatarDriver.PutAvatarIntoTPose();
    }
    [MenuItem("Basis/Avatar/Normal Animator")]
    public static void ResetAvatarAnimator()
    {
        BasisLocalPlayer.Instance.LocalAvatarDriver.ResetAvatarAnimator();
    }
    public static float randomRange = 0.1f;
    static Vector3 ModifyVector(Vector3 original)
    {
        float randomX = UnityEngine.Random.Range(-randomRange, randomRange);
        float randomY = UnityEngine.Random.Range(-randomRange, randomRange);
        float randomZ = UnityEngine.Random.Range(-randomRange, randomRange);
        return new Vector3(original.x + randomX, original.y + randomY, original.z + randomZ);
    }
    [MenuItem("Basis/Calibration/CalibrateFB")]
    public static void CalibrateEditor()
    {
        BasisAvatarIKStageCalibration.FullBodyCalibration();
    }
    [MenuItem("Basis/Calibration/ProvideRandomData and create 3 point")]
    public static void ProvideRandomData()
    {
        Vector3 RotationVector = UnityEngine.Random.rotation.eulerAngles;
        Vector3 OnlyY = new Vector3(0, RotationVector.y, 0);
        BasisLocalPlayer.Instance.transform.eulerAngles = OnlyY;

        BasisDesktopEye basisAvatarEyeInput = GameObject.FindFirstObjectByType<BasisDesktopEye>();
        if (basisAvatarEyeInput != null)
        {
            basisAvatarEyeInput.rotationYaw = UnityEngine.Random.Range(-360, 360);
        }
        BasisLocalPlayer.Instance.StartCoroutine(WaitAndCreatePuck3Tracker());
    }
    private static IEnumerator WaitAndCreatePuck3Tracker()
    {
        // Wait for the end of the frame
        yield return null;
        yield return new WaitForEndOfFrame();
        yield return null;
        // Call the final API
        CreatePuck3Tracker();
    }
    [MenuItem("Basis/Player/Spawn Fake Remote")]
    public static void SpawnFakeRemote()
    {
        ServerReadyMessage serverSideSyncPlayerMessage = new ServerReadyMessage
        {
            playerIdMessage = new PlayerIdMessage
            {
                playerID = (ushort)(BasisNetworkPlayers.Players.Count + 1)
            },
            localReadyMessage = new ReadyMessage()
        };
        serverSideSyncPlayerMessage.localReadyMessage.clientAvatarChangeMessage = new ClientAvatarChangeMessage();
        serverSideSyncPlayerMessage.localReadyMessage.localAvatarSyncMessage = new LocalAvatarSyncMessage();
        if (BasisNetworkPlayers.Players.TryGetValue((ushort)BasisNetworkConnection.LocalPlayerPeer.Id, out BasisNetworkPlayer Player))
        {
            BasisNetworkTransmitter Transmitter = (BasisNetworkTransmitter)Player;
            if (Transmitter != null)
            {
                BasisDebug.Log("Apply SpawnFakeRemote");
                serverSideSyncPlayerMessage.localReadyMessage.localAvatarSyncMessage = Transmitter.storedAvatarData.LASM;
            }
            CreateTestRemotePlayer(serverSideSyncPlayerMessage);
        }
    }
    public static void CreateTestRemotePlayer(ServerReadyMessage ServerReadyMessage)
    {
        BasisRemotePlayerFactory.CreateRemotePlayer(ServerReadyMessage, BasisNetworkManagement.instantiationParameters);
    }
    // Group 1: Authentication and Player Metadata
    [Serializable]
    [ProfilerModuleMetadata("Authentication and Player Metadata Profiler")]
    public class AuthenticationAndPlayerMetadataProfilerModule : ProfilerModule
    {
        static readonly ProfilerCounterDescriptor[] k_Counters = new ProfilerCounterDescriptor[]
        {
        new ProfilerCounterDescriptor(BasisNetworkProfiler.AuthenticationMessageText, "Authentication Message"),
        new ProfilerCounterDescriptor(BasisNetworkProfiler.PlayerIdMessageText, "Player ID Message"),
        new ProfilerCounterDescriptor(BasisNetworkProfiler.PlayerMetaDataMessageText, "Player Metadata Message")
        };

        static readonly string[] k_AutoEnabledCategoryNames = new string[]
        {
        ProfilerCategory.Scripts.Name,
        ProfilerCategory.Network.Name
        };

        public AuthenticationAndPlayerMetadataProfilerModule() : base(k_Counters, autoEnabledCategoryNames: k_AutoEnabledCategoryNames) { }
    }

    // Group 2: Avatar Profiler
    [Serializable]
    [ProfilerModuleMetadata("Avatar Profiler")]
    public class AvatarProfilerModule : ProfilerModule
    {
        static readonly ProfilerCounterDescriptor[] k_Counters = new ProfilerCounterDescriptor[]
        {
        new ProfilerCounterDescriptor(BasisNetworkProfiler.AvatarDataMessageText, "Avatar Data Message"),
        new ProfilerCounterDescriptor(BasisNetworkProfiler.LocalAvatarSyncMessageText, "Local Avatar Sync Message"),
        new ProfilerCounterDescriptor(BasisNetworkProfiler.AvatarChangeMessageText, "Avatar Change Message"),
        new ProfilerCounterDescriptor(BasisNetworkProfiler.ServerAvatarDataMessageText, "Server Avatar Message")
        };

        static readonly string[] k_AutoEnabledCategoryNames = new string[]
        {
        ProfilerCategory.Scripts.Name,
        ProfilerCategory.Network.Name
        };

        public AvatarProfilerModule() : base(k_Counters, autoEnabledCategoryNames: k_AutoEnabledCategoryNames) { }
    }

    // Group 3: Ownership Profiler
    [Serializable]
    [ProfilerModuleMetadata("Ownership Profiler")]
    public class OwnershipProfilerModule : ProfilerModule
    {
        static readonly ProfilerCounterDescriptor[] k_Counters = new ProfilerCounterDescriptor[]
        {
        new ProfilerCounterDescriptor(BasisNetworkProfiler.OwnershipTransferMessageText, "Ownership Transfer Message"),
        new ProfilerCounterDescriptor(BasisNetworkProfiler.RequestOwnershipTransferMessageText, "Request Ownership Transfer Message")
        };

        static readonly string[] k_AutoEnabledCategoryNames = new string[]
        {
        ProfilerCategory.Scripts.Name,
        ProfilerCategory.Network.Name
        };

        public OwnershipProfilerModule() : base(k_Counters, autoEnabledCategoryNames: k_AutoEnabledCategoryNames) { }
    }

    // Group 4: Audio and Communication
    [Serializable]
    [ProfilerModuleMetadata("Audio and Communication Profiler")]
    public class AudioAndCommunicationProfilerModule : ProfilerModule
    {
        static readonly ProfilerCounterDescriptor[] k_Counters = new ProfilerCounterDescriptor[]
        {
        new ProfilerCounterDescriptor(BasisNetworkProfiler.AudioSegmentDataMessageText, "Audio Segment Data Message"),
        new ProfilerCounterDescriptor(BasisNetworkProfiler.ServerAudioSegmentMessageText, "Server Audio Segment Message"),
        new ProfilerCounterDescriptor(BasisNetworkProfiler.AudioRecipientsMessageText, "Audio Recipients Message")
        };

        static readonly string[] k_AutoEnabledCategoryNames = new string[]
        {
        ProfilerCategory.Scripts.Name,
        ProfilerCategory.Network.Name
        };

        public AudioAndCommunicationProfilerModule() : base(k_Counters, autoEnabledCategoryNames: k_AutoEnabledCategoryNames) { }
    }

    // Group 5: Scene and Synchronization
    [Serializable]
    [ProfilerModuleMetadata("Scene and Synchronization Profiler")]
    public class SceneAndSynchronizationProfilerModule : ProfilerModule
    {
        static readonly ProfilerCounterDescriptor[] k_Counters = new ProfilerCounterDescriptor[]
        {
        new ProfilerCounterDescriptor(BasisNetworkProfiler.SceneDataMessageText, "Scene Data Message"),
        new ProfilerCounterDescriptor(BasisNetworkProfiler.ServerSideSyncPlayerMessageText, "Server Side Sync Player Message"),
        new ProfilerCounterDescriptor(BasisNetworkProfiler.ReadyMessageText, "Ready Message"),
        new ProfilerCounterDescriptor(BasisNetworkProfiler.CreateAllRemoteMessageText, "Create All Remote Message"),
        new ProfilerCounterDescriptor(BasisNetworkProfiler.CreateSingleRemoteMessageText, "Create Single Remote Message")
        };

        static readonly string[] k_AutoEnabledCategoryNames = new string[]
        {
        ProfilerCategory.Scripts.Name,
        ProfilerCategory.Network.Name
        };

        public SceneAndSynchronizationProfilerModule() : base(k_Counters, autoEnabledCategoryNames: k_AutoEnabledCategoryNames) { }
    }


    [MenuItem("Basis/ServerProfiler/RequestStatFrames")]
    public static void RequestStatsTick()
    {
        BasisNetworkEvents.Snapshotdata += DataPass;
        BasisNetworkEvents.RequestStatFrames();
        BasisNetworkManagement.HasRequested = true;
    }
    [MenuItem("Basis/ServerProfiler/StopStatFrames")]
    public static void TryStopStats()
    {
        BasisNetworkEvents.StopStatFrames();
        BasisNetworkManagement.HasRequested = false;
    }
    public static void DataPass(BasisNetworkStatistics.Snapshot Snapshot)
    {
        BasisDebug.Log("Adding Data from Snapshot Network Stats",BasisDebug.LogTag.Networking);
        // OPTIONAL: if key is a channelId (byte), map to const field names of BasisNetworkCommons
        BasisNetworkProfiler.ResolveName = (index) =>
        {
            // If your keys are bytes, clamp
            byte channelId = (byte)index;
            var field = typeof(BasisNetworkCommons)
                .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                .FirstOrDefault(f => f.IsLiteral && !f.IsInitOnly &&
                                     f.FieldType == typeof(byte) &&
                                     (byte)f.GetRawConstantValue() == channelId);

            return field?.Name ?? $"UnknownChannel({channelId})";
        };

        // INBOUND per index
        foreach (var item in Snapshot.PerIndex)
        {
            int index = item.Key;
            ulong bytes = item.Value.Bytes;
            ulong count = item.Value.Count;

            BasisNetworkProfiler.SampleInbound(index, bytes, count);
        }

        // OUTBOUND per index
        foreach (var item in Snapshot.OutPerIndex)
        {
            int index = item.Key;
            ulong bytes = item.Value.Bytes;
            ulong count = item.Value.Count;

            BasisNetworkProfiler.SampleOutbound(index, bytes, count);
        }
    }
}
