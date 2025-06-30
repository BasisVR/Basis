using Basis.Scripts.Addressable_Driver.Resource;
using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.Command_Line_Args;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Device_Management.Devices.Desktop;
using Basis.Scripts.Drivers;
using Basis.Scripts.Player;
using Basis.Scripts.TransformBinders;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace Basis.Scripts.Device_Management
{
    public partial class BasisDeviceManagement : MonoBehaviour
    {
        public bool FireOffNetwork = true;
        public static bool HasEvents = false;
        public const string InvalidConst = "Invalid";
        public string[] BakedInCommandLineArgs = new string[] { };
        public static string NetworkManagement = "NetworkManagement";
        public static string CurrentMode = "None";
        [SerializeField]
        public const string Desktop = "Desktop";
        public static string BoneData = "Assets/ScriptableObjects/BoneData.asset";
        public static BasisFallBackBoneData FBBD;
        public const string ProfilePath = "Packages/com.hecomi.ulipsync/Assets/Profiles/uLipSync-Profile-Sample.asset";
        public static bool IsCurrentModeVR()
        {
            switch (CurrentMode)
            {
                case "OpenVRLoader":
                    return true;
                case "OpenXRLoader":
                    return true;
                default:
                    return false;
            }
        }
        public AudioClip HoverUI;
        public AudioClip pressUI;
        public string DefaultMode()
        {
            if (IsMobile())
            {
                return "OpenXRLoader";
            }
            else
            {
                return Desktop;
            }
        }
        public static bool IsMobile()
        {
            return Application.platform == RuntimePlatform.Android;
        }
        /// <summary>
        /// checks to see if we are in desktop
        /// this being false does not mean its vr.
        ///
        /// </summary>
        /// <returns></returns>
        public static bool IsUserInDesktop()
        {
            if (Desktop == BasisDeviceManagement.CurrentMode)
            {
                return true;
            }
            return false;
        }
        public static BasisDeviceManagement Instance;
        public static event Action<string> OnBootModeChanged;
        public static event Action<string> OnBootModeStopped;
        public delegate Task InitializationCompletedHandler();
        public static event InitializationCompletedHandler OnInitializationCompleted;
        public BasisDeviceNameMatcher BasisDeviceNameMatcher;
        [SerializeField]
        public BasisObservableList<BasisInput> AllInputDevices = new BasisObservableList<BasisInput>();
        [SerializeField]
        public BasisXRManagement BasisXRManagement = new BasisXRManagement();
        [SerializeField]
        public List<BasisBaseTypeManagement> BaseTypes = new List<BasisBaseTypeManagement>();
        [SerializeField]
        public List<BasisLockToInput> BasisLockToInputs = new List<BasisLockToInput>();
        [SerializeField]
        public List<BasisStoredPreviousDevice> PreviouslyConnectedDevices = new List<BasisStoredPreviousDevice>();
        [SerializeField]
        public List<DeviceSupportInformation> UseAbleDeviceConfigs = new List<DeviceSupportInformation>();
        [SerializeField]
        public BasisLocalInputActions InputActions;
        public static AsyncOperationHandle<BasisFallBackBoneData> BasisFallBackBoneDataAsync;
        public static AsyncOperationHandle<uLipSync.Profile> LipSyncProfile;
        public static readonly ConcurrentQueue<Action> mainThreadActions = new ConcurrentQueue<Action>();
        public static volatile bool hasPendingActions = false;
        public static Action OnDeviceManagementLoop;
        async void Start()
        {
            if (BasisHelpers.CheckInstance<BasisDeviceManagement>(Instance))
            {
                Instance = this;
            }
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            await Initialize();
        }
        void OnDestroy()
        {
            if (BasisFallBackBoneDataAsync.IsValid())
            {
                Addressables.Release(BasisFallBackBoneDataAsync);
            }
            if(LipSyncProfile.IsValid())
            {
                Addressables.Release(LipSyncProfile);
            }

            ShutDownXR(true);
            if (TryFindBasisBaseTypeManagement(Desktop, out List<BasisBaseTypeManagement> Matched))
            {
                foreach (var m in Matched)
                {
                    m.StopSDK();
                }
            }
            if (TryFindBasisBaseTypeManagement("SimulateXR", out Matched))
            {
                foreach (var m in Matched)
                {
                    m.StopSDK();
                }
            }
            if (HasEvents)
            {
                BasisXRManagement.CheckForPass -= CheckForPass;

                OnInitializationCompleted -= RunAfterInitialized;
                HasEvents = false;
            }
        }
        public static void UnassignFBTrackers()
        {
            foreach (BasisInput Input in BasisDeviceManagement.Instance.AllInputDevices)
            {
                Input.UnAssignFBTracker();
            }
        }
        public bool TryFindBasisBaseTypeManagement(string name, out List<BasisBaseTypeManagement> match)
        {
            match = new List<BasisBaseTypeManagement>();

            if (string.IsNullOrEmpty(name))
            {
                BasisDebug.LogError("Name parameter is null or empty.", BasisDebug.LogTag.Device);
                return false;
            }

            if (BaseTypes == null)
            {
                BasisDebug.LogError("BaseTypes list is null.", BasisDebug.LogTag.Device);
                return false;
            }

            foreach (BasisBaseTypeManagement type in BaseTypes)
            {
                if (type == null)
                {
                    BasisDebug.LogWarning("Null entry found in BaseTypes list.", BasisDebug.LogTag.Device);
                    continue;
                }

                if (type.Type() == name)
                {
                    match.Add(type);
                }
            }

            if (match.Count == 0)
            {
                BasisDebug.LogWarning($"No matches found for name '{name}'.", BasisDebug.LogTag.Device);
                return false;
            }

            return true;
        }
        public async Task Initialize()
        {
            BasisCommandLineArgs.Initialize(BakedInCommandLineArgs, out string ForcedDevicemanager);
            LoadFallbackData();
            InstantiationParameters parameters = new InstantiationParameters(this.transform,true);
            await BasisPlayerFactory.CreateLocalPlayer(parameters);

            if (string.IsNullOrEmpty(ForcedDevicemanager))
            {
                SwitchMode(DefaultMode());
            }
            else
            {
                SwitchMode(ForcedDevicemanager);
            }
            if (HasEvents == false)
            {
                BasisXRManagement.CheckForPass += CheckForPass;

                OnInitializationCompleted += RunAfterInitialized;
                HasEvents = true;
            }
            await OnInitializationCompleted?.Invoke();
        }
        public void LoadFallbackData()
        {
            BasisFallBackBoneDataAsync = Addressables.LoadAssetAsync<BasisFallBackBoneData>(BoneData);
            LipSyncProfile = Addressables.LoadAssetAsync<uLipSync.Profile>(ProfilePath);
            FBBD = BasisFallBackBoneDataAsync.WaitForCompletion();
            LipSyncProfile.WaitForCompletion();
        }
        public async Task RunAfterInitialized()
        {
            if (FireOffNetwork)
            {
                await LoadGameobject(NetworkManagement, new InstantiationParameters());
            }
        }
        public void SwitchMode(string newMode)
        {
            if (CurrentMode != "None")
            {
                BasisDebug.Log("killing off " + CurrentMode, BasisDebug.LogTag.Device);
                if (newMode == "Desktop")
                {
                    ShutDownXR();
                }
                else
                {
                    foreach (BasisBaseTypeManagement Type in BaseTypes)
                    {
                        if (Type.Type() == Desktop)
                        {
                            Type.StopSDK();
                        }
                    }
                }
            }

            CurrentMode = newMode;
            if (newMode != "Desktop" && newMode != "Exiting")
            {
                BasisCursorManagement.UnlockCursorBypassChecks("Forceful Unlock From Device Management");
            }
            OnBootModeChanged?.Invoke(CurrentMode);
            SMDMicrophone.LoadInMicrophoneData(CurrentMode);
            BasisDebug.Log("Loading " + CurrentMode, BasisDebug.LogTag.Device);

            switch (CurrentMode)
            {
                case "OpenVRLoader":
                case "OpenXRLoader":
                    BasisXRManagement.BeginLoad();
                    break;
                case "Desktop":
                    if (TryFindBasisBaseTypeManagement(Desktop, out List<BasisBaseTypeManagement> Matched))
                    {
                        foreach (var m in Matched)
                        {
                            m.BeginLoadSDK();
                        }
                    }
                    break;
                case "Exiting":
                    break;
                default:
                    BasisDebug.LogError("This should not occur (default)");
                    if (TryFindBasisBaseTypeManagement("Desktop", out Matched))
                    {
                        foreach (var m in Matched)
                        {
                            m.BeginLoadSDK();
                        }
                    }
                    break;
            }
        }
        public void ShutDownXR(bool isExiting = false)
        {
            if (TryFindBasisBaseTypeManagement("OpenVRLoader", out List<BasisBaseTypeManagement> Matched))
            {
                foreach (var m in Matched)
                {
                    m.StopSDK();
                }
            }
            if (TryFindBasisBaseTypeManagement("OpenXRLoader", out Matched))
            {
                foreach (var m in Matched)
                {
                    m.StopSDK();
                }
            }
            if (TryFindBasisBaseTypeManagement("SimulateXR", out Matched))
            {
                foreach (var m in Matched)
                {
                    m.StopSDK();
                }
            }
            BasisXRManagement.StopXR(isExiting);
            AllInputDevices.RemoveAll(item => item == null);

            OnBootModeStopped?.Invoke(CurrentMode);
        }
        public static async Task LoadGameobject(string playerAddressableID, InstantiationParameters instantiationParameters)
        {
            ChecksRequired Required = new ChecksRequired(false, false, false);
            (List<GameObject>, Addressable_Driver.AddressableGenericResource) data = await AddressableResourceProcess.LoadAsGameObjectsAsync(playerAddressableID, instantiationParameters, Required, BundledContentHolder.Selector.System);
            List<GameObject> gameObjects = data.Item1;

            if (gameObjects.Count == 0)
            {
                BasisDebug.LogError("Missing ");
            }
        }
        public static void ForceLoadXR()
        {
            SwitchSetMode("OpenVRLoader");
        }
        public static void ForceSetDesktop()
        {
            SwitchSetMode("Desktop");
        }
        public static void SwitchSetMode(string Mode)
        {
            if (Instance != null && Mode != CurrentMode)
            {
                Instance.SwitchMode(Mode);
            }
        }
        public static void ShowTrackers()
        {
            ShowTrackersAsync();
        }
        public void SetCameraRenderState(bool state)
        {
            BasisLocalCameraDriver.Instance.CameraData.allowXRRendering = state;
            // if (state)
            //{
            //  BasisLocalCameraDriver.Instance.Camera.stereoTargetEye = StereoTargetEyeMask.Both;
            // }
            //else
            //{
            //  BasisLocalCameraDriver.Instance.Camera.stereoTargetEye = StereoTargetEyeMask.None;
            //}
            // BasisDebug.Log("Stereo Set To " + BasisLocalCameraDriver.Instance.Camera.stereoTargetEye);
        }
        public static void ShowTrackersAsync()
        {
            var inputDevices = Instance.AllInputDevices;
            for (int Index = 0; Index < inputDevices.Count; Index++)
            {
                inputDevices[Index].ShowTrackedVisual();
            }
        }
        public static void HideTrackers()
        {
            for (int Index = 0; Index < Instance.AllInputDevices.Count; Index++)
            {
                Instance.AllInputDevices[Index].HideTrackedVisual();
            }
        }
        public void RemoveDevicesFrom(string SubSystem, string id)
        {
            for (int Index = 0; Index < AllInputDevices.Count; Index++)
            {
                BasisInput device = AllInputDevices[Index];
                if (device != null)
                {
                    if (device.SubSystemIdentifier == SubSystem && device.UniqueDeviceIdentifier == id)
                    {
                        CacheDevice(device);
                        AllInputDevices[Index] = null;
                        GameObject.Destroy(device.gameObject);
                    }
                }
            }

            AllInputDevices.RemoveAll(item => item == null);
        }
        private void CheckForPass(string type)
        {
            if (string.IsNullOrEmpty(type))
            {
                BasisDebug.LogError("Type parameter is null or empty.", BasisDebug.LogTag.Device);
                return;
            }

            BasisDebug.Log("Loading " + type, BasisDebug.LogTag.Device);

            if (!TryFindBasisBaseTypeManagement("SimulateXR", out List<BasisBaseTypeManagement> matchedSimulateXR))
            {
                BasisDebug.LogWarning("No BasisBaseTypeManagement found for 'SimulateXR'.", BasisDebug.LogTag.Device);
            }
            else if (matchedSimulateXR == null || matchedSimulateXR.Count == 0)
            {
                BasisDebug.LogWarning("'SimulateXR' list is null or empty.", BasisDebug.LogTag.Device);
            }
            else
            {
                foreach (var m in matchedSimulateXR)
                {
                    if (m == null)
                    {
                        BasisDebug.LogWarning("Null entry found in 'SimulateXR' list.", BasisDebug.LogTag.Device);
                        continue;
                    }
                    m.StartSDK();
                }
            }

            if (!TryFindBasisBaseTypeManagement(type, out List<BasisBaseTypeManagement> matchedType))
            {
                BasisDebug.LogWarning($"No BasisBaseTypeManagement found for type '{type}'.", BasisDebug.LogTag.Device);
            }
            else if (matchedType == null || matchedType.Count == 0)
            {
                BasisDebug.LogWarning($"List for type '{type}' is null or empty.", BasisDebug.LogTag.Device);
            }
            else
            {
                foreach (var m in matchedType)
                {
                    if (m == null)
                    {
                        BasisDebug.LogWarning($"Null entry found in list for type '{type}'.", BasisDebug.LogTag.Device);
                        continue;
                    }
                    m.StartSDK();
                }
            }
        }
        public bool TryAdd(BasisInput basisXRInput)
        {
            if (AllInputDevices.Contains(basisXRInput) == false)
            {
                AllInputDevices.Add(basisXRInput);
                if (RestoreDevice(basisXRInput.SubSystemIdentifier, basisXRInput.UniqueDeviceIdentifier, out BasisStoredPreviousDevice PreviousDevice))
                {
                    if (CheckBeforeOverride(PreviousDevice))
                    {
                        StartCoroutine(RestoreInversetOffsets(basisXRInput, PreviousDevice));
                    }
                    else
                    {
                        BasisDebug.Log("bailing out of restore already has a replacement", BasisDebug.LogTag.Device);
                    }
                }
                return true;
            }
            else
            {
                BasisDebug.LogError("already added a Input Device thats identical!", BasisDebug.LogTag.Device);
            }
            return false;
        }
        IEnumerator RestoreInversetOffsets(BasisInput basisXRInput, BasisStoredPreviousDevice PreviousDevice)
        {
            yield return new WaitForEndOfFrame();
            if (basisXRInput != null && basisXRInput.Control != null)
            {
                if (CheckBeforeOverride(PreviousDevice))
                {
                    BasisDebug.Log("device is restored " + PreviousDevice.trackedRole, BasisDebug.LogTag.Device);
                    basisXRInput.ApplyTrackerCalibration(PreviousDevice.trackedRole);
                    basisXRInput.Control.InverseOffsetFromBone = PreviousDevice.InverseOffsetFromBone;
                }
            }

        }
        public bool CheckBeforeOverride(BasisStoredPreviousDevice Stored)
        {
            foreach (var device in AllInputDevices)
            {
                if (device.TryGetRole(out BasisBoneTrackedRole Role))
                {
                    if (Role == Stored.trackedRole)
                    {
                        return false;
                    }
                }
            }
            return true;
        }
        public bool FindDevice(out BasisInput FindDevice, BasisBoneTrackedRole FindRole)
        {
            foreach (var device in AllInputDevices)
            {
                if (device != null && device.Control != null)
                {
                    if (device.Control.HasBone)
                    {
                        if (device.TryGetRole(out BasisBoneTrackedRole Role))
                        {
                            if (Role == FindRole)
                            {
                                FindDevice = device;
                                return true;

                            }
                        }
                    }
                }
            }
            FindDevice = null;
            return false;
        }
        public void CacheDevice(BasisInput DevicesThatsGettingPurged)
        {
            if (DevicesThatsGettingPurged.TryGetRole(out BasisBoneTrackedRole Role) && DevicesThatsGettingPurged.Control != null)
            {
                BasisStoredPreviousDevice StoredPreviousDevice = new BasisStoredPreviousDevice
                { InverseOffsetFromBone = DevicesThatsGettingPurged.Control.InverseOffsetFromBone }; ;

                StoredPreviousDevice.trackedRole = Role;
                StoredPreviousDevice.hasRoleAssigned = DevicesThatsGettingPurged.hasRoleAssigned;
                StoredPreviousDevice.SubSystem = DevicesThatsGettingPurged.SubSystemIdentifier;
                StoredPreviousDevice.UniqueID = DevicesThatsGettingPurged.UniqueDeviceIdentifier;
                PreviouslyConnectedDevices.Add(StoredPreviousDevice);
            }
        }
        public bool RestoreDevice(string SubSystem, string id, out BasisStoredPreviousDevice StoredPreviousDevice)
        {
            foreach (BasisStoredPreviousDevice Device in PreviouslyConnectedDevices)
            {
                if (Device.UniqueID == id && Device.SubSystem == SubSystem)
                {
                    BasisDebug.Log("this device is restoreable restoring..", BasisDebug.LogTag.Device);
                    PreviouslyConnectedDevices.Remove(Device);
                    StoredPreviousDevice = Device;
                    return true;
                }
            }
            StoredPreviousDevice = null;
            return false;
        }
        public static void EnqueueOnMainThread(Action action)
        {
            if (action == null) return;

            mainThreadActions.Enqueue(action);
            hasPendingActions = true;
        }
    }
}
