using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.Command_Line_Args;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Device_Management.Devices.Desktop;
using Basis.Scripts.Player;
using Basis.Scripts.TransformBinders;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using uLipSync;
using UnityEngine;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace Basis.Scripts.Device_Management
{
    public partial class BasisDeviceManagement : MonoBehaviour
    {
        public static bool HasEvents = false;
        public string CurrentMode = BasisConstants.None;
        public bool FireOffNetwork = true;
        public static string StaticCurrentMode
        {
            get
            {
                if (BasisDeviceManagement.Instance != null)
                {
                    return BasisDeviceManagement.Instance.CurrentMode;
                }
                else
                {
                    return BasisConstants.InvalidConst;
                }
            }
            set
            {
                if (BasisDeviceManagement.Instance != null)
                {
                    BasisDeviceManagement.Instance.CurrentMode = value;
                }
                else
                {
                    BasisDebug.LogError("Cant Set Missing CurrentMode");
                }
            }
        }

        public BasisFallBackBoneData FBBD;
        public static BasisDeviceManagement Instance;

        public static event Action<string> OnBootModeChanged;
        public delegate void InitializationCompletedHandler();
        public static event InitializationCompletedHandler OnInitializationCompleted;
        public static readonly ConcurrentQueue<Action> mainThreadActions = new ConcurrentQueue<Action>();
        public static volatile bool hasPendingActions = false;
        public static Action OnDeviceManagementLoop;

        [SerializeField] public string[] BakedInCommandLineArgs = new string[] { };
        [SerializeField] public AudioClip HoverUI;
        [SerializeField] public AudioClip pressUI;
        [SerializeField] public BasisObservableList<BasisInput> AllInputDevices = new();
        [SerializeField] public BasisXRManagement BasisXRManagement = new();
        [SerializeField] public List<BasisBaseTypeManagement> BaseTypes = new();
        [SerializeField] public List<BasisLockToInput> BasisLockToInputs = new();
        [SerializeField] public List<BasisStoredPreviousDevice> PreviouslyConnectedDevices = new();
        [SerializeField] public BasisLocalInputActions InputActions;

        public BasisDeviceNameMatcher BasisDeviceNameMatcher;
        public string ForcedDefault = string.Empty;

        public Profile LipSyncProfile;

        #region Unity Lifecycle

        async void Start()
        {
            if (BasisHelpers.CheckInstance(Instance)) Instance = this;

            StaticCurrentMode = BasisConstants.None;
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            await Initialize();
        }

        void OnDestroy()
        {
            BasisPlayerFactory.DeInitalize();
            StopAllDevices();
            UnsubscribeEvents();
        }

        #endregion

        #region Initialization

        public async Task Initialize()
        {
            BasisPlayerFactory.Initalize();
            BasisCommandLineArgs.Initialize(BakedInCommandLineArgs, out ForcedDefault);
            await BasisPlayerFactory.CreateLocalPlayer(new InstantiationParameters(transform, true));
            StartAllStartIfPermanentlyExists();
            SwitchSetModeToDefault();
            SubscribeEvents();

            if (OnInitializationCompleted != null)
            {
                OnInitializationCompleted.Invoke();
            }
        }
        #endregion

        #region Mode Handling

        public void SwitchSetModeToDefault()
        {
            string mode = string.IsNullOrEmpty(ForcedDefault) ? DefaultMode() : ForcedDefault;
            SwitchSetMode(mode);
        }

        public void SwitchSetMode(string newMode)
        {
            if (string.IsNullOrEmpty(newMode))
            {
                BasisDebug.LogError("SwitchMode called with null or empty mode.", BasisDebug.LogTag.Device);
                return;
            }
            if (StaticCurrentMode == newMode)
            {
                BasisDebug.LogError($"trying to boot Existing bailing, call {nameof(StopAllDevices)} first {newMode}", BasisDebug.LogTag.Device);
                return;
            }
            if (StaticCurrentMode != BasisConstants.None)
            {
                BasisDebug.Log($"Shutting down mode: {StaticCurrentMode}", BasisDebug.LogTag.Device);
                StopAllDevices();
            }
            else
            {
                BasisDebug.Log($"Skipping Device Shutdown: {StaticCurrentMode}", BasisDebug.LogTag.Device);
            }

            StaticCurrentMode = newMode;
            OnBootModeChanged?.Invoke(StaticCurrentMode);
            BasisDebug.Log($"Loading mode: {StaticCurrentMode}", BasisDebug.LogTag.Device);

            if (BasisXRManagement.TryBeginLoad(newMode))
            {

            }
            else
            {

                StartDevices(StaticCurrentMode);
            }
            SMDMicrophone.LoadInMicrophoneData(StaticCurrentMode);
        }
        #endregion

        #region Device Management

        public void StartDevices(string mode)
        {
            if (TryFindBasisBaseTypeManagement(mode, out var matched))
            {
                foreach (var type in matched)
                    type?.AttemptStartSDK();
            }
        }

        public void StopAllDevices()
        {
                foreach (var type in BaseTypes)
                type?.AttemptStopSDK();

            StaticCurrentMode = BasisConstants.None;
            ShutDownXR();
        }

        public void ShutDownXR()
        {
            BasisXRManagement.StopXR();
            AllInputDevices.RemoveAll(item => item == null);
        }

        public void StartAllStartIfPermanentlyExists()
        {
            foreach (var type in BaseTypes)
                type?.StartIfPermanentlyExists();
        }

        public static void UnassignFBTrackers()
        {
            foreach (var input in Instance.AllInputDevices)
                input.UnAssignFBTracker();
        }

        public bool TryFindBasisBaseTypeManagement(string name, out List<BasisBaseTypeManagement> match)
        {
            match = new List<BasisBaseTypeManagement>();
            if (string.IsNullOrEmpty(name) || BaseTypes == null) return false;

            foreach (var type in BaseTypes)
            {
                if (type != null && type.AttemptIsDeviceBootable(name))
                    match.Add(type);
            }

            return match.Count > 0 || name == BasisConstants.Exiting;
        }

        #endregion

        #region Device Restore & Tracking

        public bool TryAdd(BasisInput input)
        {
            if (AllInputDevices.Contains(input))
            {
                BasisDebug.LogError("Already added an identical input device!", BasisDebug.LogTag.Device);
                return false;
            }

            AllInputDevices.Add(input);

            if (RestoreDevice(input.SubSystemIdentifier, input.UniqueDeviceIdentifier, out var prev) && CheckBeforeOverride(prev))
            {
                StartCoroutine(RestoreInversetOffsets(input, prev));
            }

            return true;
        }

        IEnumerator RestoreInversetOffsets(BasisInput input, BasisStoredPreviousDevice prev)
        {
            yield return new WaitForEndOfFrame();

            if (input?.Control != null && CheckBeforeOverride(prev))
            {
                BasisDebug.Log("Device restored: " + prev.trackedRole, BasisDebug.LogTag.Device);
                input.ApplyTrackerCalibration(prev.trackedRole);
                input.Control.InverseOffsetFromBone = prev.InverseOffsetFromBone;
            }
        }

        public bool RestoreDevice(string subsystem, string id, out BasisStoredPreviousDevice restored)
        {
            foreach (var device in PreviouslyConnectedDevices)
            {
                if (device.UniqueID == id && device.SubSystem == subsystem)
                {
                    PreviouslyConnectedDevices.Remove(device);
                    restored = device;
                    BasisDebug.Log("Device is restorable, restoring...", BasisDebug.LogTag.Device);
                    return true;
                }
            }

            restored = null;
            return false;
        }

        public void CacheDevice(BasisInput device)
        {
            if (device.TryGetRole(out var role) && device.Control != null)
            {
                PreviouslyConnectedDevices.Add(new BasisStoredPreviousDevice
                {
                    trackedRole = role,
                    hasRoleAssigned = device.hasRoleAssigned,
                    SubSystem = device.SubSystemIdentifier,
                    UniqueID = device.UniqueDeviceIdentifier,
                    InverseOffsetFromBone = device.Control.InverseOffsetFromBone
                });
            }
        }

        public void RemoveDevicesFrom(string subsystem, string id)
        {
            for (int i = 0; i < AllInputDevices.Count; i++)
            {
                var device = AllInputDevices[i];
                if (device != null && device.SubSystemIdentifier == subsystem && device.UniqueDeviceIdentifier == id)
                {
                    CacheDevice(device);
                    AllInputDevices[i] = null;
                    Destroy(device.gameObject);
                }
            }

            AllInputDevices.RemoveAll(item => item == null);
        }

        public bool CheckBeforeOverride(BasisStoredPreviousDevice stored)
        {
            foreach (var device in AllInputDevices)
            {
                if (device?.TryGetRole(out var role) == true && role == stored.trackedRole)
                    return false;
            }
            return true;
        }

        public bool FindDevice(out BasisInput found, BasisBoneTrackedRole FindRole)
        {
            foreach (var device in AllInputDevices)
            {
                if (device?.Control != null && device.TryGetRole(out var role) && role == FindRole)
                {
                    found = device;
                    return true;
                }
            }

            found = null;
            return false;
        }

        public static void VisibleTrackers(bool show)
        {
            if (Instance == null)
            {
                BasisDebug.LogError("Missing Device Manager", BasisDebug.LogTag.Device);
                return;
            }

            foreach (var input in Instance.AllInputDevices)
            {
                if (input == null) continue;
                if (show) input.ShowTrackedVisual();
                else input.HideTrackedVisual();
            }
        }

        #endregion

        #region Event Helpers

        private void SubscribeEvents()
        {
            if (!HasEvents)
            {
                OnInitializationCompleted += RunAfterInitialized;
                HasEvents = true;
            }
        }

        private void UnsubscribeEvents()
        {
            if (HasEvents)
            {
                OnInitializationCompleted -= RunAfterInitialized;
                HasEvents = false;
            }
        }
        public GameObject BasisNetworking;
        private void RunAfterInitialized()
        {
            if(FireOffNetwork)
            {
                BasisNetworking.SetActive(true);
            }
        }
        #endregion

        #region Static Utility

        public static void EnqueueOnMainThread(Action action)
        {
            if (action == null) return;
            mainThreadActions.Enqueue(action);
            hasPendingActions = true;
        }

        public string DefaultMode()
        {
#if UNITY_SERVER
            return BasisConstants.Headless;
#else
#endif
            if (IsMobile())
            {
                return BasisConstants.OpenXRLoader;
            }
            else
            {
                return BasisConstants.Desktop;
            }
        }

        public static bool IsMobile() => Application.platform == RuntimePlatform.Android;
        public static bool IsUserInDesktop() => StaticCurrentMode == BasisConstants.Desktop;
        public static bool IsCurrentModeVR() => StaticCurrentMode == BasisConstants.OpenVRLoader || StaticCurrentMode == BasisConstants.OpenXRLoader;

#endregion
    }
}
