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
                var inst = Instance;
                return inst != null ? inst.CurrentMode : BasisConstants.InvalidConst;
            }
            set
            {
                var inst = Instance;
                if (inst != null)
                {
                    inst.CurrentMode = value;
                }
                else
                {
                    BasisDebug.LogError("[DeviceManagement] Unable to set CurrentMode: Instance is null.");
                }
            }
        }

        public BasisFallBackBoneData FBBD;
        public static BasisDeviceManagement Instance;

        public static event Action<string> OnBootModeChanged;
        public delegate void InitializationCompletedHandler();
        public static event InitializationCompletedHandler OnInitializationCompleted;

        public static readonly ConcurrentQueue<Action> mainThreadActions = new ConcurrentQueue<Action>();
        public static Action OnDeviceManagementLoop;

        [SerializeField] public string[] BakedInCommandLineArgs = Array.Empty<string>();
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

        private async void Start()
        {
            if (BasisHelpers.CheckInstance(Instance)) Instance = this;

            StaticCurrentMode = BasisConstants.None;
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

            try
            {
                await Initialize();
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"[DeviceManagement] Initialize threw: {e}");
            }
        }

        private void OnDestroy()
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
            await SwitchSetModeToDefault();

            SubscribeEvents();

            await BasisActionDriver.LoadBindings();

            OnInitializationCompleted?.Invoke();
        }

        #endregion

        #region Mode Handling

        public async Task SwitchSetModeToDefault()
        {
            string mode;
#if UNITY_SERVER
            mode = BasisConstants.Headless;
#else
            mode = string.IsNullOrEmpty(ForcedDefault) ? DefaultMode() : ForcedDefault;
#endif
            await SwitchSetMode(mode);
        }

        public async Task SwitchSetMode(string newMode)
        {
            if (string.IsNullOrEmpty(newMode))
            {
                BasisDebug.LogError("[DeviceManagement] SwitchSetMode called with null/empty mode.", BasisDebug.LogTag.Device);
                return;
            }

            if (string.Equals(StaticCurrentMode, newMode, StringComparison.Ordinal))
            {
                BasisDebug.LogError($"[DeviceManagement] Mode '{newMode}' already active. Call {nameof(StopAllDevices)} first.", BasisDebug.LogTag.Device);
                return;
            }

            if (!string.Equals(StaticCurrentMode, BasisConstants.None, StringComparison.Ordinal))
            {
                BasisDebug.Log($"[DeviceManagement] Shutting down mode: {StaticCurrentMode}", BasisDebug.LogTag.Device);
                StopAllDevices();
            }
            else
            {
                BasisDebug.Log($"[DeviceManagement] No active mode to shutdown (was '{StaticCurrentMode}')", BasisDebug.LogTag.Device);
            }

            StaticCurrentMode = newMode;

            // If XR loader does not take over, start devices directly.
            if (!BasisXRManagement.TryBeginLoad(StaticCurrentMode))
            {
                await StartDevices(StaticCurrentMode);
            }
        }

        #endregion

        #region Device Management

        public async Task StartDevices(string mode)
        {
            if (TryFindBasisBaseTypeManagement(mode, out var matched))
            {
                // Safely iterate and await each start
                for (int i = 0; i < matched.Count; i++)
                {
                    var type = matched[i];
                    if (type != null)
                    {
                        await type.AttemptStartSDK();
                    }
                }
            }

            await BasisSettingsSystem.LoadAllSettingsAsync();
            SMDMicrophone.LoadInMicrophoneData(mode);
            await BasisActionDriver.LoadBindings();

            OnBootModeChanged?.Invoke(mode);
            BasisDebug.Log($"[DeviceManagement] Loading mode: {mode}", BasisDebug.LogTag.Device);
        }

        public void StopAllDevices()
        {
            for (int i = 0; i < BaseTypes.Count; i++)
            {
                BaseTypes[i]?.AttemptStopSDK();
            }

            StaticCurrentMode = BasisConstants.None;
            ShutDownXR();
        }

        public void ShutDownXR()
        {
            BasisXRManagement.StopXR();

            // Purge nulls to keep lists tidy
            AllInputDevices.RemoveAll(item => item == null);
        }

        public void StartAllStartIfPermanentlyExists()
        {
            for (int i = 0; i < BaseTypes.Count; i++)
            {
                BaseTypes[i]?.StartIfPermanentlyExists();
            }
        }

        public static void UnassignFBTrackers()
        {
            var inst = Instance;
            if (inst == null) return;

            for (int i = 0; i < inst.AllInputDevices.Count; i++)
            {
                inst.AllInputDevices[i]?.UnAssignFBTracker();
            }
        }

        public bool TryFindBasisBaseTypeManagement(string name, out List<BasisBaseTypeManagement> match, bool OnlyFinding = false)
        {
            match = new List<BasisBaseTypeManagement>();
            if (string.IsNullOrEmpty(name) || BaseTypes == null) return false;

            for (int i = 0; i < BaseTypes.Count; i++)
            {
                var type = BaseTypes[i];
                if (type != null && type.AttemptIsDeviceBootable(name, OnlyFinding))
                {
                    match.Add(type);
                }
            }

            return match.Count > 0 || string.Equals(name, BasisConstants.Exiting, StringComparison.Ordinal);
        }

        #endregion

        #region Device Restore & Tracking

        public bool TryAdd(BasisInput input)
        {
            if (input == null)
            {
                BasisDebug.LogError("[DeviceManagement] Tried to add null input device.", BasisDebug.LogTag.Device);
                return false;
            }

            if (AllInputDevices.Contains(input))
            {
                BasisDebug.LogError("[DeviceManagement] Attempted to add duplicate input device.", BasisDebug.LogTag.Device);
                return false;
            }

            AllInputDevices.Add(input);

            if (RestoreDevice(input.SubSystemIdentifier, input.UniqueDeviceIdentifier, out var prev) && CheckBeforeOverride(prev))
            {
                StartCoroutine(RestoreInversetOffsets(input, prev));
            }

            return true;
        }

        private IEnumerator RestoreInversetOffsets(BasisInput input, BasisStoredPreviousDevice prev)
        {
            yield return new WaitForEndOfFrame();

            if (input != null && input.Control != null && CheckBeforeOverride(prev))
            {
                BasisDebug.Log($"[DeviceManagement] Device restored: {prev.trackedRole}", BasisDebug.LogTag.Device);
                input.ApplyTrackerCalibration(prev.trackedRole);
                input.Control.InverseOffsetFromBone = prev.InverseOffsetFromBone;
            }
        }

        public bool RestoreDevice(string subsystem, string id, out BasisStoredPreviousDevice restored)
        {
            restored = null;
            if (PreviouslyConnectedDevices == null || PreviouslyConnectedDevices.Count == 0)
                return false;

            // Safe index-based remove when found
            for (int i = 0; i < PreviouslyConnectedDevices.Count; i++)
            {
                var dev = PreviouslyConnectedDevices[i];
                if (dev != null && dev.UniqueID == id && dev.SubSystem == subsystem)
                {
                    restored = dev;
                    PreviouslyConnectedDevices.RemoveAt(i);
                    BasisDebug.Log("[DeviceManagement] Device is restorable — restoring.", BasisDebug.LogTag.Device);
                    return true;
                }
            }
            return false;
        }

        public void CacheDevice(BasisInput device)
        {
            if (device == null) return;

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
            for (int i = AllInputDevices.Count - 1; i >= 0; i--)
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
            if (stored == null) return false;

            for (int i = 0; i < AllInputDevices.Count; i++)
            {
                var device = AllInputDevices[i];
                if (device != null && device.TryGetRole(out var role) && role == stored.trackedRole)
                    return false;
            }
            return true;
        }

        public bool FindDevice(out BasisInput found, BasisBoneTrackedRole FindRole)
        {
            for (int i = 0; i < AllInputDevices.Count; i++)
            {
                var device = AllInputDevices[i];
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
            var inst = Instance;
            if (inst == null)
            {
                BasisDebug.LogError("[DeviceManagement] Missing Device Manager", BasisDebug.LogTag.Device);
                return;
            }

            for (int i = 0; i < inst.AllInputDevices.Count; i++)
            {
                var input = inst.AllInputDevices[i];
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
            if (FireOffNetwork && BasisNetworking != null)
            {
                BasisNetworking.SetActive(true);
            }
        }

        #endregion

        #region Static Utility

        public static void EnqueueOnMainThread(Action action)
        {
            if (action == null)
            {
                BasisDebug.LogError("[DeviceManagement] EnqueueOnMainThread received null action.");
                return;
            }
            mainThreadActions.Enqueue(action);
        }

        public string DefaultMode()
        {
#if UNITY_SERVER
            return BasisConstants.Headless;
#else
            if (IsMobile())
            {
                // On mobile we assume OpenXR (tunable per project).
                return BasisConstants.OpenXRLoader;
            }
            else
            {
                return BasisConstants.Desktop;
            }
#endif
        }

        public static bool IsMobile() => Application.platform == RuntimePlatform.Android;
        public static bool IsUserInDesktop() => string.Equals(StaticCurrentMode, BasisConstants.Desktop, StringComparison.Ordinal);
        public static bool IsCurrentModeVR() =>
            string.Equals(StaticCurrentMode, BasisConstants.OpenVRLoader, StringComparison.Ordinal) ||
            string.Equals(StaticCurrentMode, BasisConstants.OpenXRLoader, StringComparison.Ordinal);

        #endregion
    }
}
