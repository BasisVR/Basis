using Basis.Scripts.Avatar;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Device_Management.Devices.Desktop;
using Basis.Scripts.Device_Management.Devices.Simulation;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.Transmitters;
using Basis.Scripts.Profiler;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using System.Collections;
using System.Collections.Generic;
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
        if (BasisDataStore.LoadAvatar(BasisLocalPlayer.LoadFileNameAndExtension, BasisLocalPlayer.DefaultAvatar, BasisPlayer.LoadModeLocal, out BasisDataStore.BasisSavedAvatar LastSavedAvatar))
        {
            await BasisLocalPlayer.Instance.LoadInitialAvatar(LastSavedAvatar);
        }
    }
    [MenuItem("Basis/Trackers/Hide Trackers")]
    public static void HideTrackersEditor()
    {
        HideTrackers();
    }

    [MenuItem("Basis/Trackers/Show Trackers")]
    public static void ShowTrackersEditor()
    {
        ShowTrackers();
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
        foreach (var device in allDevicesToRemove)
        {
           FindSimulate().CreatePhysicalTrackedDevice(device.UniqueID, "{htc}vr_tracker_vive_3_0");
        }
    }
    public static BasisSimulateXR FindSimulate()
    {
        if (BasisDeviceManagement.Instance.TryFindBasisBaseTypeManagement("SimulateXR", out List<BasisBaseTypeManagement> Matched))
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
    public static  void CreatePuckTracker()
    {
        BasisLocalPlayer.Instance.LocalAvatarDriver.PutAvatarIntoTPose();

        FindSimulate().CreatePhysicalTrackedDevice("{htc}vr_tracker_vive_3_0" + UnityEngine.Random.Range(-9999999999999, 999999999999), "{htc}vr_tracker_vive_3_0");
        BasisDeviceManagement.ShowTrackers();
        BasisLocalPlayer.Instance.LocalAvatarDriver.ResetAvatarAnimator();
    }
    [MenuItem("Basis/Trackers/Create Vive Right Controller")]
    public static  void CreateViveRightTracker()
    {
        BasisLocalPlayer.Instance.LocalBoneDriver.FindBone(out BasisBoneControl RightHand, BasisBoneTrackedRole.RightHand);
        BasisInputXRSimulate RightTracker =  FindSimulate().CreatePhysicalTrackedDevice("{indexcontroller}valve_controller_knu_3_0_right" + UnityEngine.Random.Range(-9999999999999, 999999999999), "{indexcontroller}valve_controller_knu_3_0_right", BasisBoneTrackedRole.RightHand, true);
        RightTracker.FollowMovement.position = RightHand.OutgoingWorldData.position;
        RightTracker.FollowMovement.rotation = Quaternion.identity;
        BasisDeviceManagement.ShowTrackers();
    }
    [MenuItem("Basis/Trackers/Create Vive Left Controller")]
    public static  void CreateViveLeftTracker()
    {
        BasisLocalPlayer.Instance.LocalBoneDriver.FindBone(out BasisBoneControl LeftHand, BasisBoneTrackedRole.LeftHand);
        BasisInputXRSimulate LeftTracker =  FindSimulate().CreatePhysicalTrackedDevice("{indexcontroller}valve_controller_knu_3_0_left" + UnityEngine.Random.Range(-9999999999999, 999999999999), "{indexcontroller}valve_controller_knu_3_0_left", BasisBoneTrackedRole.LeftHand, true);
        LeftTracker.FollowMovement.position = LeftHand.OutgoingWorldData.position;
        LeftTracker.FollowMovement.rotation = Quaternion.identity;
        BasisDeviceManagement.ShowTrackers();
    }
    [MenuItem("Basis/Trackers/Create Unknown Tracker")]
    public static  void CreateUnknowonTracker()
    {
        BasisLocalPlayer.Instance.LocalBoneDriver.FindBone(out BasisBoneControl LeftHand, BasisBoneTrackedRole.LeftHand);
        BasisInputXRSimulate LeftTracker =  FindSimulate().CreatePhysicalTrackedDevice("Unknown" + UnityEngine.Random.Range(-9999999999999, 999999999999), "Unknown", BasisBoneTrackedRole.CenterEye, false);
        LeftTracker.FollowMovement.position = LeftHand.OutgoingWorldData.position;
        LeftTracker.FollowMovement.rotation = Quaternion.identity;
        BasisDeviceManagement.ShowTrackers();
    }
    [MenuItem("Basis/Trackers/Create Left And Right Hands")]
    public static void CreateLRTracker()
    {
        CreateViveLeftTracker();
        CreateViveRightTracker();
    }
    [MenuItem("Basis/Trackers/Create 3Point Tracking")]
    public static  void CreatePuck3Tracker()
    {
        BasisLocalPlayer.Instance.LocalAvatarDriver.PutAvatarIntoTPose();
        BasisSimulateXR XR = FindSimulate();
        BasisInputXRSimulate BasisHips =  XR.CreatePhysicalTrackedDevice("{htc}vr_tracker_vive_3_0 BasisHips | " + UnityEngine.Random.Range(-9999999999999, 999999999999), "{htc}vr_tracker_vive_3_0");
        BasisInputXRSimulate BasisLeftFoot =  XR.CreatePhysicalTrackedDevice("{htc}vr_tracker_vive_3_0 BasisLeftFoot | " + UnityEngine.Random.Range(-9999999999999, 999999999999), "{htc}vr_tracker_vive_3_0");
        BasisInputXRSimulate BasisRightFoot =  XR.CreatePhysicalTrackedDevice("{htc}vr_tracker_vive_3_0 BasisRightFoot | " + UnityEngine.Random.Range(-9999999999999, 999999999999), "{htc}vr_tracker_vive_3_0");

        var hips = BasisLocalPlayer.Instance.LocalAvatarDriver.References.Hips;
        var leftFoot = BasisLocalPlayer.Instance.LocalAvatarDriver.References.leftFoot;
        var rightFoot = BasisLocalPlayer.Instance.LocalAvatarDriver.References.rightFoot;

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
        BasisDeviceManagement.ShowTrackers();
    }
    [MenuItem("Basis/Trackers/Create MaxTracker Tracking")]
    public static void CreateFullMaxTracker()
    {
        //  BasisLocalPlayer.Instance.AvatarDriver.PutAvatarIntoTPose();
        // Create an array of the tracker names for simplicity
        string trackerName = "{htc}vr_tracker_vive_3_0";

        var avatarDriver = BasisLocalPlayer.Instance.LocalAvatarDriver.References;
        // avatarDriver.neck, avatarDriver.head,
        // Array of all relevant body parts
        Transform[] bodyParts = new Transform[]
        {
            avatarDriver.Hips, avatarDriver.chest,
            avatarDriver.leftShoulder, avatarDriver.RightShoulder,
            avatarDriver.leftLowerArm,avatarDriver.RightLowerArm,
            avatarDriver.LeftLowerLeg, avatarDriver.RightLowerLeg,
            avatarDriver.leftFoot, avatarDriver.leftToes,
            avatarDriver.rightFoot, avatarDriver.rightToes
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
        BasisDeviceManagement.ShowTrackers();
    }
    [MenuItem("Basis/Trackers/Create MaxTracker Tracking Normal Pos")]
    public static void CreateFullMaxTrackerUnModifedPos()
    {
        //  BasisLocalPlayer.Instance.AvatarDriver.PutAvatarIntoTPose();
        // Create an array of the tracker names for simplicity
        string trackerName = "{htc}vr_tracker_vive_3_0";

        var avatarDriver = BasisLocalPlayer.Instance.LocalAvatarDriver.References;
        // avatarDriver.neck, avatarDriver.head,
        // Array of all relevant body parts
        Transform[] bodyParts = new Transform[]
        {
            avatarDriver.Hips, avatarDriver.chest,
            avatarDriver.leftShoulder, avatarDriver.RightShoulder,
            avatarDriver.leftLowerArm,avatarDriver.RightLowerArm,
            avatarDriver.LeftLowerLeg, avatarDriver.RightLowerLeg,
            avatarDriver.leftFoot, avatarDriver.leftToes,
            avatarDriver.rightFoot, avatarDriver.rightToes
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
        BasisDeviceManagement.ShowTrackers();
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

        BasisAvatarEyeInput basisAvatarEyeInput = GameObject.FindFirstObjectByType<BasisAvatarEyeInput>();
        if (basisAvatarEyeInput != null)
        {
            basisAvatarEyeInput.InjectedX = UnityEngine.Random.Range(-3, 3);
            basisAvatarEyeInput.InjectedZ = UnityEngine.Random.Range(-3, 3);
            basisAvatarEyeInput.rotationX = UnityEngine.Random.Range(-360, 360);
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
                playerID = (ushort)(BasisNetworkManagement.Players.Count + 1)
            },
            localReadyMessage = new ReadyMessage()
        };
        serverSideSyncPlayerMessage.localReadyMessage.clientAvatarChangeMessage = new ClientAvatarChangeMessage();
        serverSideSyncPlayerMessage.localReadyMessage.localAvatarSyncMessage = new LocalAvatarSyncMessage();
        if(BasisNetworkManagement.Players.TryGetValue((ushort)BasisNetworkManagement.LocalPlayerPeer.Id, out BasisNetworkPlayer Player))
        {
          BasisNetworkTransmitter Transmitter = (BasisNetworkTransmitter)Player;
            if (Transmitter != null)
            {
                BasisDebug.Log("Apply SpawnFakeRemote");
                serverSideSyncPlayerMessage.localReadyMessage.localAvatarSyncMessage = Transmitter.LASM;
            }
            CreateTestRemotePlayer(serverSideSyncPlayerMessage);
        }
    }
    public async static void CreateTestRemotePlayer(ServerReadyMessage ServerReadyMessage)
    {
        await BasisRemotePlayerFactory.CreateRemotePlayer(ServerReadyMessage, BasisNetworkManagement.instantiationParameters);
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
}
