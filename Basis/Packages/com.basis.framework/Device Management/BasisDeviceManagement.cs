using Basis.BasisUI;
using Basis.Scripts.Avatar;
using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.Command_Line_Args;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Device_Management.Devices.Desktop;
using Basis.Scripts.Networking;
using Basis.Scripts.Player;
using Basis.Scripts.TransformBinders;
using Basis.Scripts.TransformBinders.BoneControl;
using Basis.Scripts.UI.NamePlate;
using Basis.Scripts.UI.UI_Panels;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.ResourceManagement.ResourceProviders;
using static Basis.Scripts.UI.UI_Panels.BasisDataStoreAvatarKeys;
using static Basis.Scripts.UI.UI_Panels.BasisDataStoreItemKeys;
using Basis.Scripts.Settings;
using Basis.Scripts.BasisSdk.Players;
namespace Basis.Scripts.Device_Management
{
    /// <summary>
    /// Central orchestrator for device discovery, start/stop, and mode switching across Desktop and XR.
    /// </summary>
    /// <remarks>
    /// This MonoBehaviour is intended to exist exactly once in a scene. Use <see cref="Instance"/> for access.
    /// It initializes players, loads settings/bindings, restores previously connected devices, and manages XR lifecycle.
    /// </remarks>
    [DefaultExecutionOrder(-1000)]
    public class BasisDeviceManagement : MonoBehaviour
    {
        /// <summary>
        /// Guard flag to prevent duplicate event subscriptions.
        /// </summary>
        public static bool HasEvents = false;

        /// <summary>
        /// The currently active boot mode. For a safe static accessor, use <see cref="StaticCurrentMode"/>.
        /// </summary>
        public string CurrentMode = BasisConstants.None;

        /// <summary>
        /// If <c>true</c>, brings up <see cref="Basis.Scripts.Networking.BasisNetworkManagement"/>
        /// and <see cref="Basis.Scripts.UI.NamePlate.BasisRemoteNamePlateDriver"/> once initialization completes.
        /// </summary>
        public bool FireOffNetwork = true;

        /// <summary>
        /// Static proxy for <see cref="CurrentMode"/> that is safe to use from anywhere.
        /// </summary>
        /// <value>Returns the instance's <see cref="CurrentMode"/>, or <see cref="BasisConstants.InvalidConst"/> if the instance is missing.</value>
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
                    OnBootModeChanged?.Invoke(value);
                }
                else
                {
                    BasisDebug.LogError("Unable to set CurrentMode: Instance is null.");
                }
            }
        }

        /// <summary>
        /// Fallback data for bone tracking; applied when device-provided bone data is unavailable.
        /// </summary>
        public BasisFallBackBoneData FBBD;

        /// <summary>
        /// Singleton-style reference to the active <see cref="BasisDeviceManagement"/>.
        /// </summary>
        public static BasisDeviceManagement Instance;

        /// <summary>
        /// Fired when the boot mode changes after a successful <see cref="SwitchSetMode(string)"/> or default mode selection.
        /// </summary>
        public static event Action<string> OnBootModeChanged;

        /// <summary>
        /// Delegate signature for <see cref="OnInitializationCompleted"/>.
        /// </summary>
        public delegate void InitializationCompletedHandler();

        /// <summary>
        /// Invoked once <see cref="Initialize"/> finishes successfully.
        /// </summary>
        public static event InitializationCompletedHandler OnInitializationCompleted;

        /// <summary>
        /// A threadsafe queue of actions scheduled to run on Unity's main thread.
        /// </summary>
        public static readonly ConcurrentQueue<Action> mainThreadActions = new ConcurrentQueue<Action>();

        /// <summary>
        /// Optional callback executed each update tick of the device-management loop (owner-controlled).
        /// </summary>
        public static Action OnDeviceManagementLoop;

        /// <summary>
        /// a disabled gameobject that we can spawn things under for security
        /// </summary>
        public GameObject CreationGameobject;
        /// <summary>
        /// Command-line arguments baked into the build, used when platform args are unavailable (e.g., mobile).
        /// </summary>
        [SerializeField] public string[] BakedInCommandLineArgs = Array.Empty<string>();

        /// <summary>
        /// UI hover audio.
        /// </summary>
        [SerializeField] public AudioClip HoverUI;

        /// <summary>
        /// UI press/click audio.
        /// </summary>
        [SerializeField] public AudioClip pressUI;

        /// <summary>
        /// Audio played when a chat text message is received from another player.
        /// If left unassigned, a default notification tone is generated at runtime.
        /// </summary>
        [SerializeField] public AudioClip ChatNotificationUI;

        /// <summary>
        /// Camera shutter sound played when a photo is captured.
        /// </summary>
        [SerializeField] public AudioClip CameraShutterSound;

        /// <summary>
        /// Countdown tick sound played each second during the camera timer.
        /// </summary>
        [SerializeField] public AudioClip CameraCountdownTickSound;

        /// <summary>
        /// Live collection of all input devices currently managed by this system.
        /// </summary>
        [SerializeField] public BasisObservableList<BasisInput> AllInputDevices = new();

        /// <summary>
        /// Wrapper for platform-specific XR start/stop/loading.
        /// </summary>
        [SerializeField] public BasisXRManagement BasisXRManagement = new();

        /// <summary>
        /// Registered device SDK managers capable of booting into given modes (Desktop/XR/etc.).
        /// </summary>
        [SerializeField] public BasisBaseTypeManagement[] BaseTypes;

        /// <summary>
        /// Helpers that constrain transforms to input devices.
        /// </summary>
        [SerializeField] public List<BasisLockToInput> BasisLockToInputs = new();

        /// <summary>
        /// Cache of previously connected devices to allow restoration of roles and offsets.
        /// </summary>
        [SerializeField] public List<BasisStoredPreviousDevice> PreviouslyConnectedDevices = new();

        /// <summary>
        /// Input action asset for local player control.
        /// </summary>
        [SerializeField] public InputActionAsset InputActions;

        /// <summary>
        /// Root GameObject hosting settings modules and input plumbing. Activated by BasisLocalInputActions.Initialize.
        /// </summary>
        [SerializeField] public GameObject InputActionsRoot;

        /// <summary>
        /// Optional device name matcher used when probing for base types.
        /// </summary>
        public BasisDeviceNameMatcher BasisDeviceNameMatcher;

        /// <summary>
        /// Overrides the default mode selection when non-empty.
        /// </summary>
        public string ForcedDefault = string.Empty;

        /// <summary>
        /// The VR mode that was active before a soft swap switched to Desktop.
        /// Used to restore the correct VR mode when the headset is detected again.
        /// </summary>
        public string AutoSwapPreviousVRMode = string.Empty;

        /// <summary>
        /// True when the system is in Desktop mode via a soft swap, with the XR runtime still alive.
        /// </summary>
        public bool IsSoftSwapped = false;

        /// <summary>
        /// Guard flag to prevent overlapping auto swap operations.
        /// </summary>
        private bool _autoSwapInProgress = false;

        /// <summary>
        /// Set when a presence change lands while a swap is already running. The presence hub only
        /// fires on change, so such an edge would otherwise be dropped for good and leave the mode
        /// disagreeing with the headset until the user takes it off and puts it back on.
        /// </summary>
        private bool _autoSwapPendingRecheck = false;

        #region Unity Lifecycle

        /// <summary>
        /// Unity start hook. Ensures singleton, sets culture to invariant, and kicks off <see cref="Initialize"/>.
        /// </summary>
        private async void Start()
        {
            if (BasisHelpers.CheckInstance(Instance))
            {
                Instance = this;
            }

            // Detect Wine/Proton once up front so any subsystem can branch on it.
            BasisProtonDetection.Initialize();

            StaticCurrentMode = BasisConstants.None;
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            BasisSettingsSystem.Initialize();
            // Localization must initialize before BasisSettingsDefaults so that
            // auto-detection can see an empty settings dict on first run — any
            // earlier binding constructor would write "en" as a default and
            // defeat the HasSaveData("language") check.
            Basis.BasisUI.BasisLocalization.Initialize();
            Basis.BasisUI.BasisTMPFontFallbacks.RefreshJapanesePriority();
            BasisSettingsDefaults.LoadAll();
            Basis.BasisUI.SettingsProvider.ApplyJiggleStartupSettings();
            // Applied here and nowhere else: the GPU Resident Drawer rebuild this triggers is only
            // cheap while the loading scene is the whole scene.
            Basis.Scripts.Rendering.BasisGpuOcclusionCulling.ApplyStartupSetting();
            try
            {
                await Initialize();
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"Initialize threw: {e}");
            }
        }

        /// <summary>
        /// Unity destroy hook. Tears down players/devices and unsubscribes events.
        /// </summary>
        private async void OnDestroy()
        {
            CleanupAutoSwap();
            BasisXRManagement.DeInitialize();
            BasisPlayerFactory.DeInitialize();
            BasisAvatarFactory.DeInitialize();
            StopAllDevices();
            UnsubscribeEvents();

            if (BasisNetworkManagement.IsInitialized)
            {
                BasisNetworkConnection.OnDestroy();
                await BasisNetworkLifeCycle.Destroy();
            }
            BasisRemoteNamePlateDriver.Dispose();
        }
        static readonly Unity.Profiling.ProfilerMarker sMarkerLoop = new Unity.Profiling.ProfilerMarker("BasisDriver.DeviceManagement.Loop");
        static readonly Unity.Profiling.ProfilerMarker sMarkerBaseTypes = new Unity.Profiling.ProfilerMarker("BasisDriver.DeviceManagement.BaseTypes");
        /// <summary>
        /// Starts asynchronous per-frame device work (e.g. the SteamVR input update on a worker
        /// thread). Called by the driver earlier in LateUpdate than <see cref="Simulate"/>, which
        /// joins that work before the local player consumes it.
        /// </summary>
        public void SimulateKick()
        {
            int Count = BaseTypes.Length;
            for (int Index = 0; Index < Count; Index++)
            {
                BaseTypes[Index]?.SimulateKick();
            }
        }
        public void Simulate()
        {
            using (sMarkerLoop.Auto())
            {
                OnDeviceManagementLoop?.Invoke();
            }
            using (sMarkerBaseTypes.Auto())
            {
                int Count = BaseTypes.Length;
                for (int Index = 0; Index < Count; Index++)
                {
                    BaseTypes[Index]?.Simulate();
                }
            }
        }

        #endregion

        #region Initialization
        public static bool OnInitializationComplete = false;
        /// <summary>
        /// Initializes the device system, creates a local player, starts persistent devices, and switches to the default mode.
        /// </summary>
        /// <returns>A task that completes when initialization and bindings load are finished.</returns>
        public async Task Initialize()
        {

            BasisAvatarFactory.Initialize();
            BasisPlayerFactory.Initialize();
            BasisXRManagement.Initialize();
            BasisCommandLineArgs.Initialize(BakedInCommandLineArgs, out ForcedDefault);

            //legacy!!! delete in a few months!
            if (File.Exists(BasisDataStoreAvatarKeys.FilePath))
            {
                //lets try finding old avatars and then nuke there old data.
                await BasisDataStoreAvatarKeys.LoadKeys();
                await BasisDataStoreItemKeys.LoadKeys();

                AvatarKey[] activeKeys = BasisDataStoreAvatarKeys.DisplayKeys();
                foreach (AvatarKey Key in activeKeys)
                {
                    ItemKey ItemKey = new ItemKey
                    {
                        Url = Key.Url,
                        Pass = Key.Pass,
                        Mode = BundledContentHolder.Mode.Avatar,
                        EmbeddedSettings = EmbeddedSettings.Default
                    };

                    await BasisDataStoreItemKeys.AddNewKey(ItemKey);
                }

                File.Delete(BasisDataStoreAvatarKeys.FilePath);
            }

            await BasisPlayerFactory.CreateLocalPlayer(new InstantiationParameters(transform, true));
            StartAllStartIfPermanentlyExists();
            await SwitchSetModeToDefault();

            SubscribeEvents();

            await BasisActionDriver.LoadBindings();

            SetupAutoSwap();
            OnInitializationCompleted?.Invoke();
            OnInitializationComplete = true;
            BasisSettingsSystem.NotifyFinishedChanges();
        }

        #endregion

        #region Mode Handling

        /// <summary>
        /// Switches to the default mode based on platform and overrides (e.g., Server → Headless, Mobile → OpenXR, Desktop → Desktop).
        /// </summary>
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

        /// <summary>
        /// Switches the system to a new mode, shutting down the previous one and starting devices or XR as needed.
        /// </summary>
        /// <param name="newMode">The mode to enter; see <see cref="BasisConstants"/> for known values.</param>
        public async Task SwitchSetMode(string newMode)
        {
            if (string.IsNullOrEmpty(newMode))
            {
                BasisDebug.LogError("SwitchSetMode called with null/empty mode.", BasisDebug.LogTag.Device);
                return;
            }

            if (string.Equals(StaticCurrentMode, newMode, StringComparison.Ordinal))
            {
                BasisDebug.LogError($"Mode '{newMode}' already active. Call {nameof(StopAllDevices)} first.", BasisDebug.LogTag.Device);
                return;
            }

            // Refuse before anything is torn down, so a blocked switch leaves the session exactly
            // as it was instead of shutting VR down and landing in Desktop with no explanation.
            if (!CanEnterMode(newMode, out string blockedReason))
            {
                BasisXRRuntimeNotice.ReportBlocked(newMode, blockedReason);
                return;
            }

            // Check whether we should use a soft swap (keep the XR runtime alive)
            string swapMode = BasisSettingsSystem.LoadString("swap_mode", BasisSettingsDefaults.SwapMode_Shutdown);
            bool useSoftSwap = !string.Equals(swapMode, BasisSettingsDefaults.SwapMode_Shutdown, StringComparison.OrdinalIgnoreCase);

            if (useSoftSwap)
            {
                bool currentIsVR = IsCurrentModeVR();
                bool newIsDesktop = string.Equals(newMode, BasisConstants.Desktop, StringComparison.Ordinal);
                bool newIsVR = string.Equals(newMode, BasisConstants.OpenVRLoader, StringComparison.Ordinal) ||
                               string.Equals(newMode, BasisConstants.OpenXRLoader, StringComparison.Ordinal);

                // VR → Desktop: soft switch, keeping XR runtime alive
                if (currentIsVR && newIsDesktop)
                {
                    await SoftSwitchToDesktop();
                    return;
                }

                // Desktop (soft-swapped) → original VR mode: soft switch back
                if (IsSoftSwapped && newIsVR && string.Equals(newMode, AutoSwapPreviousVRMode, StringComparison.Ordinal))
                {
                    await SoftSwitchToVR();
                    return;
                }
            }

            // Full swap (current default behavior)
            if (!string.Equals(StaticCurrentMode, BasisConstants.None, StringComparison.Ordinal))
            {
                BasisDebug.Log($"Shutting down mode: {StaticCurrentMode}", BasisDebug.LogTag.Device);
                StopAllDevices();
            }
            else
            {
                BasisDebug.Log($"No active mode to shutdown (was '{StaticCurrentMode}')", BasisDebug.LogTag.Device);
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

        /// <summary>
        /// Starts all SDKs that match the requested mode and loads settings, microphones, and input bindings.
        /// </summary>
        /// <param name="mode">The target mode used to select matching <see cref="BasisBaseTypeManagement"/> entries.</param>
        public async Task StartDevices(string mode)
        {
            // Set mode BEFORE starting devices so subsystems (e.g. cursor locking)
            // see the correct mode during initialization.
            StaticCurrentMode = mode;

            if (TryFindBasisBaseTypeManagement(mode, out var matched))
            {
                // Safely iterate and await each start
                for (int Index = 0; Index < matched.Count; Index++)
                {
                    var type = matched[Index];
                    if (type != null)
                    {
                        await type.AttemptStartSDK();
                    }
                }
            }

            BasisSettingsSystem.LoadAllSettings();
#if !BASIS_DISABLE_MICROPHONE
            SMDMicrophone.LoadInMicrophoneData(mode);
#endif
            await BasisActionDriver.LoadBindings();
            BasisDebug.Log($"Loading mode: {mode}", BasisDebug.LogTag.Device);
        }

        /// <summary>
        /// Stops all active device SDKs, resets the current mode, and shuts down XR.
        /// </summary>
        public void StopAllDevices()
        {
           var length = BaseTypes.Length;
            for (int i = 0; i < length; i++)
            {
                BaseTypes[i]?.AttemptStopSDK();
            }

            IsSoftSwapped = false;
            AutoSwapPreviousVRMode = string.Empty;
            StaticCurrentMode = BasisConstants.None;
            ShutDownXR();
        }

        /// <summary>
        /// Stops the XR loader and compacts the <see cref="AllInputDevices"/> list by removing null entries.
        /// </summary>
        public void ShutDownXR()
        {
            BasisXRManagement.StopXR();

            // Purge nulls to keep lists tidy
            AllInputDevices.RemoveAll(item => item == null);
        }

        /// <summary>
        /// Calls <see cref="BasisBaseTypeManagement.StartIfPermanentlyExists"/> on all base types to ensure persistent devices are started.
        /// </summary>
        public void StartAllStartIfPermanentlyExists()
        {
            var length = BaseTypes.Length;
            for (int i = 0; i < length; i++)
            {
                BaseTypes[i]?.StartIfPermanentlyExists();
            }
        }

        /// <summary>
        /// Unassigns all Full-Body (FB) trackers across managed devices.
        /// </summary>
        public static void UnassignFBTrackers()
        {
            var inst = Instance;
            if (inst == null) return;

            for (int i = 0; i < inst.AllInputDevices.Count; i++)
            {
                inst.AllInputDevices[i]?.UnAssignFBTracker();
            }

            // A full FB-tracker unassign invalidates the stored rotation-calibration reference, so a later
            // avatar build falls back to the uncalibrated capture instead of a stale calibration. The
            // position-offset head snapshot is invalidated for the same reason (FullBodyCalibration
            // re-captures both later in the same pass).
            BasisAvatarIKStageCalibration.HasCalibrationReference = false;
            BasisAvatarIKStageCalibration.HasCalibrationHeadSnapshot = false;
        }

        /// <summary>
        /// Finds all <see cref="BasisBaseTypeManagement"/> entries that can boot for the supplied name.
        /// </summary>
        /// <param name="name">The mode or identifier to match.</param>
        /// <param name="match">Output list of matched base types. Empty when none found.</param>
        /// <param name="OnlyFinding">If <c>true</c>, only test for bootability; do not consider other constraints.</param>
        /// <returns><c>true</c> if at least one match is found or the name equals <see cref="BasisConstants.Exiting"/>.</returns>
        public bool TryFindBasisBaseTypeManagement(string name, out List<BasisBaseTypeManagement> match, bool OnlyFinding = false)
        {
            match = new List<BasisBaseTypeManagement>();
            if (string.IsNullOrEmpty(name) || BaseTypes == null)
            {
                return false;
            }

            var length = BaseTypes.Length;
            for (int i = 0; i < length; i++)
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

        /// <summary>
        /// Adds an input device to <see cref="AllInputDevices"/> if not present and attempts restoration of previous role/offsets.
        /// </summary>
        /// <param name="input">The device to register.</param>
        /// <returns><c>true</c> if the device was added; <c>false</c> when null or already present.</returns>
        public bool TryAdd(BasisInput input)
        {
            if (input == null)
            {
                BasisDebug.LogError("Tried to add null input device.", BasisDebug.LogTag.Device);
                return false;
            }

            if (AllInputDevices.Contains(input))
            {
                BasisDebug.LogError("Attempted to add duplicate input device.", BasisDebug.LogTag.Device);
                return false;
            }

            AllInputDevices.Add(input);
            BasisSettingsSystem.ReapplySettings();

            if (RestoreDevice(input.SubSystemIdentifier, input.UniqueDeviceIdentifier, out var prev))
            {
                if (CheckBeforeOverride(prev))
                {
                    BasisDebug.Log("Override Check Passed", BasisDebug.LogTag.Device);
                    StartCoroutine(RestoreInversetOffsets(input, prev));
                }
                else
                {
                    BasisDebug.LogError("Existing Device Exist with this role!", BasisDebug.LogTag.Device);
                }
            }

            return true;
        }

        /// <summary>
        /// Coroutine that applies stored inverse-offset and role to a device on the next frame.
        /// </summary>
        /// <param name="input">The device to restore.</param>
        /// <param name="prev">The previously stored device metadata.</param>
        private IEnumerator RestoreInversetOffsets(BasisInput input, BasisStoredPreviousDevice prev)
        {
            BasisDebug.Log("Waiting until end of frame for input", BasisDebug.LogTag.Device);
            yield return new WaitForEndOfFrame();

            if (input != null)
            {
                BasisDebug.Log($"Device restored: {prev.trackedRole}", BasisDebug.LogTag.Device);
                if (prev.hasRoleAssigned)
                {
                    if (CheckBeforeOverride(prev))
                    {
                        input.ApplyTrackerCalibration(prev.trackedRole);
                    }
                    else
                    {
                        BasisDebug.Log($"Device unable to take role: {prev.trackedRole} already had existing role", BasisDebug.LogTag.Device);
                    }
                }
                if (prev.hasRoleAssigned)
                {
                    if (input.HasControl)
                    {
                        input.Control.SetInverseOffset(prev.InverseOffsetFromBone);
                        // Restore the scale-free calibration snapshot too: ApplyTrackerCalibration above
                        // re-captured against the player's LIVE (non-T-pose) body, poisoning the snapshot
                        // the same way it poisoned the offset we just overwrote. Then re-derive for the
                        // current avatar/DeviceScale in case either changed while the device was gone.
                        input.HasCalibratedOffsetSnapshot = prev.HasCalibratedOffsetSnapshot;
                        input.CalibratedUnscaledPosition = prev.CalibratedUnscaledPosition;
                        input.CalibratedUnscaledRotation = prev.CalibratedUnscaledRotation;
                        input.CalibratedUnscaledHeadPosition = prev.CalibratedUnscaledHeadPosition;
                        input.CalibratedUnscaledHeadRotation = prev.CalibratedUnscaledHeadRotation;
                        BasisAvatarIKStageCalibration.ReprojectTrackerOffsetsForCurrentAvatar();
                    }
                    else
                    {
                        BasisDebug.LogError($"Unable to restore inverse offset for role {prev.trackedRole}: device has no control.", BasisDebug.LogTag.Device);
                    }
                }
                if (input.HasControl)
                {
                    input.Control.OnHasRigChanged?.Invoke(true);
                }
            }
            else
            {
                BasisDebug.LogError("Device was removed!", BasisDebug.LogTag.Device);
            }
        }

        /// <summary>
        /// Attempts to locate previously connected device info and remove it from the cache for consumption.
        /// </summary>
        /// <param name="subsystem">Subsystem identifier.</param>
        /// <param name="id">Unique device identifier.</param>
        /// <param name="restored">Outputs the stored device record when found.</param>
        /// <returns><c>true</c> if a matching stored device was found; otherwise <c>false</c>.</returns>
        public bool RestoreDevice(string subsystem, string id, out BasisStoredPreviousDevice restored)
        {
            restored = null;
            if (PreviouslyConnectedDevices == null || PreviouslyConnectedDevices.Count == 0)
                return false;

            // Safe index-based remove when found
            for (int i = 0; i < PreviouslyConnectedDevices.Count; i++)
            {
                var dev = PreviouslyConnectedDevices[i];
                if (dev != null && dev.UniqueDeviceIdentifier == id && dev.SubSystemIdentifier == subsystem)
                {
                    restored = dev;
                    PreviouslyConnectedDevices.RemoveAt(i);
                    BasisDebug.Log("Device is restorable — restoring.", BasisDebug.LogTag.Device);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Caches device role and inverse-offset information to allow restoration after a disconnect.
        /// </summary>
        /// <param name="device">The device to snapshot.</param>
        public void CacheDevice(BasisInput device)
        {
            if (device == null) return;

            if (device.TryGetRole(out var role) && device.Control != null)
            {
                PreviouslyConnectedDevices.Add(new BasisStoredPreviousDevice
                {
                    trackedRole = role,
                    hasRoleAssigned = device.hasRoleAssigned,
                    SubSystemIdentifier = device.SubSystemIdentifier,
                    UniqueDeviceIdentifier = device.UniqueDeviceIdentifier,
                    InverseOffsetFromBone = device.Control.InverseOffsetFromBone,
                    HasCalibratedOffsetSnapshot = device.HasCalibratedOffsetSnapshot,
                    CalibratedUnscaledPosition = device.CalibratedUnscaledPosition,
                    CalibratedUnscaledRotation = device.CalibratedUnscaledRotation,
                    CalibratedUnscaledHeadPosition = device.CalibratedUnscaledHeadPosition,
                    CalibratedUnscaledHeadRotation = device.CalibratedUnscaledHeadRotation
                });
            }
        }

        /// <summary>
        /// Removes and destroys devices that match the given subsystem and id. Stores state for later restoration.
        /// </summary>
        /// <param name="subsystem">Subsystem identifier.</param>
        /// <param name="id">Unique device identifier.</param>
        public void RemoveDevicesFrom(string subsystem, string id)
        {
            for (int i = AllInputDevices.Count - 1; i >= 0; i--)
            {
                BasisInput device = AllInputDevices[i];
                if (device != null && device.SubSystemIdentifier == subsystem && device.UniqueDeviceIdentifier == id)
                {
                    CacheDevice(device);
                    AllInputDevices[i] = null;
                    Destroy(device.gameObject);
                }
            }

            AllInputDevices.RemoveAll(item => item == null);
        }

        /// <summary>
        /// Checks whether a stored device can safely override an existing role assignment.
        /// </summary>
        /// <param name="stored">Previously stored device record.</param>
        /// <returns><c>true</c> if no live device currently uses the stored role; otherwise <c>false</c>.</returns>
        public bool CheckBeforeOverride(BasisStoredPreviousDevice stored)
        {
            if (stored == null)
            {
                BasisDebug.Log("stored Was Null!", BasisDebug.LogTag.Device);
                return false;
            }

            for (int i = 0; i < AllInputDevices.Count; i++)
            {
                var device = AllInputDevices[i];
                if (device != null && device.TryGetRole(out var role) && role == stored.trackedRole)
                {
                    if (stored.UniqueDeviceIdentifier != device.UniqueDeviceIdentifier)
                    {
                        BasisDebug.Log($"Bail as device Existed Already in that role {stored.UniqueDeviceIdentifier} - {device.UniqueDeviceIdentifier}", BasisDebug.LogTag.Device);
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// Finds a live device by its tracked role.
        /// </summary>
        /// <param name="found">Outputs the matching device when found.</param>
        /// <param name="FindRole">The target role.</param>
        /// <returns><c>true</c> when a device with the role exists; otherwise <c>false</c>.</returns>
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

        /// <summary>
        /// Shows or hides visual debug objects for all tracked devices.
        /// </summary>
        /// <param name="show">If <c>true</c>, show visuals; otherwise hide.</param>
        public static void VisibleTrackers(bool show)
        {
            var inst = Instance;
            if (inst == null)
            {
                BasisDebug.LogError("Missing Device Manager", BasisDebug.LogTag.Device);
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

        /// <summary>
        /// Subscribes internal event handlers, guarded by <see cref="HasEvents"/>.
        /// </summary>
        private void SubscribeEvents()
        {
            if (!HasEvents)
            {
                OnInitializationCompleted += RunAfterInitialized;
                BasisSettingsDefaults.EnableFBT.OnChanged += OnEnableFBTChanged;
                BasisSettingsDefaults.TrackerVisuals.OnChanged += OnTrackerVisualsChanged;
                BasisLocalPlayer.AfterSimulateOnRender.AddAction(98, ApplyAllDeviceMovement);
                HasEvents = true;
            }
        }

        /// <summary>
        /// Unsubscribes previously attached internal event handlers.
        /// </summary>
        private void UnsubscribeEvents()
        {
            if (HasEvents)
            {
                OnInitializationCompleted -= RunAfterInitialized;
                BasisSettingsDefaults.EnableFBT.OnChanged -= OnEnableFBTChanged;
                BasisSettingsDefaults.TrackerVisuals.OnChanged -= OnTrackerVisualsChanged;
                BasisLocalPlayer.AfterSimulateOnRender.RemoveAction(98, ApplyAllDeviceMovement);
                HasEvents = false;
            }
        }

        /// <summary>
        /// Applies every active device's latched pose to its transform in a single
        /// pass after simulation, replacing the per-device AfterSimulateOnRender hook.
        /// Serial by design: the device set is small and all share BasisLocalPlayer as
        /// their root, so a jobified TransformAccessArray write would serialize on one
        /// worker anyway and only add scheduling/sync overhead.
        /// </summary>
        private void ApplyAllDeviceMovement()
        {
            BasisObservableList<BasisInput> devices = AllInputDevices;
            int count = devices.Count;
            for (int Index = 0; Index < count; Index++)
            {
                BasisInput device = devices[Index];
                if (device != null && device.HasEvents)
                {
                    device.ApplyFinalMovement();
                }
            }
        }

        /// <summary>
        /// Reacts to the master FBT toggle. Flipping it off immediately unassigns
        /// any already-calibrated full-body trackers so the avatar drops back to
        /// head + hands + foot IK. Flipping it on is a no-op — the user must run
        /// calibration again to reassign trackers to roles.
        /// </summary>
        private void OnEnableFBTChanged(bool value)
        {
            if (!value)
            {
                UnassignFBTrackers();
            }
        }

        /// <summary>
        /// Live re-render when the tracker-visual mode changes. Hides currently-shown visuals this
        /// frame and re-shows them next frame (after Unity's deferred Destroy completes) so the new
        /// mode's visual is picked cleanly. Only devices already showing a visual are refreshed, so
        /// nothing appears while trackers are meant to be hidden.
        /// </summary>
        private void OnTrackerVisualsChanged(string value)
        {
            StartCoroutine(RefreshTrackerVisualsNextFrame());
        }

        private IEnumerator RefreshTrackerVisualsNextFrame()
        {
            bool anyVisible = false;
            for (int i = 0; i < AllInputDevices.Count; i++)
            {
                BasisInput input = AllInputDevices[i];
                if (input == null) continue;
                if (input.BasisVisualTracker != null || BasisTrackerMarkerGizmos.IsShowing(input))
                {
                    input.HideTrackedVisual();
                    anyVisible = true;
                }
            }
            if (!anyVisible)
            {
                yield break;
            }
            yield return null;
            for (int i = 0; i < AllInputDevices.Count; i++)
            {
                BasisInput input = AllInputDevices[i];
                if (input == null) continue;
                input.ShowTrackedVisual();
            }
        }

        /// <summary>
        /// Event handler invoked after initialization to bring up the static
        /// network manager + nameplate driver, replacing what the prefab bootstrap MBs used to do.
        /// </summary>
        private void RunAfterInitialized()
        {
            if (FireOffNetwork)
            {
                BasisRemoteNamePlateDriver.Initialize();
                BasisNetworkLifeCycle.Initialize();
            }
        }

        #endregion

        #region Static Utility

        /// <summary>
        /// Enqueues an action to be executed on the Unity main thread.
        /// </summary>
        /// <param name="action">The action to execute.</param>
        public static void EnqueueOnMainThread(Action action)
        {
            if (action == null)
            {
                BasisDebug.LogError("EnqueueOnMainThread received null action.");
                return;
            }
            mainThreadActions.Enqueue(action);
        }

        /// <summary>
        /// Determines the default mode for the current platform and build configuration.
        /// </summary>
        /// <returns>
        /// <list type="bullet">
        /// <item><description><see cref="BasisConstants.Headless"/> on server builds.</description></item>
        /// <item><description><see cref="BasisConstants.OpenXRLoader"/> on mobile platforms.</description></item>
        /// <item><description><see cref="BasisConstants.Desktop"/> on desktop platforms.</description></item>
        /// </list>
        /// </returns>
        public string DefaultMode()
        {
#if UNITY_SERVER
            return BasisConstants.Headless;
#else
            if (Application.isMobilePlatform) // try to boot vr first on standalone devices.
            {
                // iOS devices (iPhones/iPads) should use Desktop mode for touch controls
                if (Application.platform == RuntimePlatform.IPhonePlayer)
                {
                    return BasisConstants.Desktop;
                }
                // On Android we assume OpenXR for VR headsets like Quest
                return BasisConstants.OpenXRLoader;
            }
            else
            {
                return BasisConstants.Desktop;
            }
#endif
        }

        /// <summary>
        /// Indicates whether the current runtime is a mobile platform (Android).
        /// </summary>
        public static bool IsMobileHardware() => Application.isMobilePlatform;

        /// <summary>
        /// Returns <c>true</c> when the current static mode equals <see cref="BasisConstants.Desktop"/>.
        /// </summary>
        public static bool IsUserInDesktop() => string.Equals(StaticCurrentMode, BasisConstants.Desktop, StringComparison.Ordinal);
        /// <summary>
        /// Returns <c>true</c> when the current static mode indicates a VR/XR loader.
        /// </summary>
        public static bool IsCurrentModeVR() => IsVRMode(StaticCurrentMode);

        /// <summary>
        /// Returns <c>true</c> when <paramref name="mode"/> names a VR/XR loader.
        /// </summary>
        public static bool IsVRMode(string mode) =>
            string.Equals(mode, BasisConstants.OpenVRLoader, StringComparison.Ordinal) ||
            string.Equals(mode, BasisConstants.OpenXRLoader, StringComparison.Ordinal);

        /// <summary>
        /// Whether <paramref name="mode"/> can be entered right now.
        /// <para>
        /// Once one XR plug-in has initialized it owns the graphics device for the rest of the
        /// process, so the other VR runtime cannot take over without a restart — attempting it
        /// only tears the working runtime down and lands in Desktop. The mode is refused here
        /// instead, and the platform panel greys its entry out with <paramref name="blockedReason"/>
        /// as the hover tooltip so the greyed control explains itself.
        /// </para>
        /// The live runtime is read from the XR loader rather than the current mode, so a soft
        /// swap to Desktop (which deliberately keeps the runtime alive) still blocks the switch.
        /// </summary>
        /// <param name="mode">The mode being offered or requested.</param>
        /// <param name="blockedReason">Localized explanation when the result is <c>false</c>; otherwise null.</param>
        public static bool CanEnterMode(string mode, out string blockedReason)
        {
            blockedReason = null;

            if (!IsVRMode(mode)) return true;

            BasisDeviceManagement inst = Instance;
            if (inst == null) return true;

            string activeLoader = inst.BasisXRManagement.ActiveLoaderName;
            if (string.IsNullOrEmpty(activeLoader) || string.Equals(activeLoader, mode, StringComparison.Ordinal))
            {
                return true;
            }

            blockedReason = BasisLocalization.Get("settings.platform.otherRuntimeLive",
                BasisXRRuntimeNotice.ModeDisplayName(activeLoader),
                BasisXRRuntimeNotice.ModeDisplayName(mode));
            return false;
        }

        #endregion

        #region Soft Swap (Auto Swap / Keep Runtime Alive)

        /// <summary>
        /// Switches from VR to Desktop without shutting down the XR runtime.
        /// The VR SDK keeps running in the background; only input devices are destroyed.
        /// Desktop input is created to drive the avatar with mouse/keyboard.
        /// </summary>
        public async Task SoftSwitchToDesktop()
        {
            if (IsUserInDesktop())
            {
                BasisDebug.LogError("Already in Desktop — cannot soft-switch.", BasisDebug.LogTag.Device);
                return;
            }

            AutoSwapPreviousVRMode = StaticCurrentMode;
            IsSoftSwapped = true;

            BasisDebug.Log($"Soft-switching from {AutoSwapPreviousVRMode} to Desktop (keeping runtime alive)", BasisDebug.LogTag.Device);

            var length = BaseTypes.Length;
            // Soft-stop VR input devices — runtime stays alive
            for (int i = 0; i < length; i++)
            {
                var bt = BaseTypes[i];
                if (bt != null && bt.IsDeviceBooted && bt.IsDeviceBootable(AutoSwapPreviousVRMode))
                {
                    bt.SoftStopDevices();
                }
            }

            // Do NOT call ShutDownXR() — the XR loader stays initialized

            // Clear stale VR cursor state before Desktop devices initialize
            BasisCursorManagement.OnReset();

            // Start desktop devices (sets StaticCurrentMode, reloads settings, etc.)
            await StartDevices(BasisConstants.Desktop);
        }

        /// <summary>
        /// Restores VR mode from a soft swap without re-initializing the XR runtime.
        /// Desktop input is destroyed and VR input devices are recreated.
        /// </summary>
        public async Task SoftSwitchToVR()
        {
            if (!IsSoftSwapped || string.IsNullOrEmpty(AutoSwapPreviousVRMode))
            {
                BasisDebug.LogError("Not in a soft-swapped state — cannot restore VR.", BasisDebug.LogTag.Device);
                return;
            }

            string vrMode = AutoSwapPreviousVRMode;
            IsSoftSwapped = false;
            AutoSwapPreviousVRMode = string.Empty;

            BasisDebug.Log($"Soft-switching from Desktop back to {vrMode}", BasisDebug.LogTag.Device);

            var length = BaseTypes.Length;
            // Stop desktop devices normally
            for (int i = 0; i < length; i++)
            {
                var bt = BaseTypes[i];
                if (bt != null && bt.IsDeviceBooted && bt.IsDeviceBootable(BasisConstants.Desktop))
                {
                    bt.AttemptStopSDK();
                }
            }

            // Set mode BEFORE starting devices and reset cursor state so VR subsystems
            // see the correct mode and desktop cursor locks don't linger.
            StaticCurrentMode = vrMode;
            BasisCursorManagement.OnReset();

            // Soft-start VR input devices — runtime is already alive
            for (int i = 0; i < length; i++)
            {
                var bt = BaseTypes[i];
                if (bt != null && bt.IsDeviceBooted && bt.IsDeviceBootable(vrMode))
                {
                    bt.SoftStartDevices();
                }
            }

            BasisSettingsSystem.LoadAllSettings();
#if !BASIS_DISABLE_MICROPHONE
            SMDMicrophone.LoadInMicrophoneData(vrMode);
#endif
            await BasisActionDriver.LoadBindings();
            BasisDebug.Log($"Soft-switch to {vrMode} complete", BasisDebug.LogTag.Device);
        }

        #endregion

        #region Auto Swap Management

        /// <summary>
        /// Subscribes to <see cref="BasisHMDPresence.OnPresenceChanged"/> so the system can
        /// auto-swap between VR and Desktop when the headset is put on or taken off.
        /// The VR SDKs (OpenVR / OpenXR) report presence into the static hub every frame;
        /// this code only reacts when the debounced value actually changes.
        /// </summary>
        private void SetupAutoSwap()
        {
            BasisHMDPresence.OnPresenceChanged += OnHMDPresenceChanged;
        }

        /// <summary>
        /// Unsubscribes from presence events during shutdown.
        /// </summary>
        private void CleanupAutoSwap()
        {
            BasisHMDPresence.OnPresenceChanged -= OnHMDPresenceChanged;
            BasisHMDPresence.Reset();
        }

        /// <summary>
        /// Reacts to headset presence changes. Only acts when the Auto Swap setting is enabled.
        /// Each VR SDK reports the headset's own worn signal into <see cref="BasisHMDPresence"/>
        /// (proximity on both the OpenVR and OpenXR paths); this handler triggers the soft swap.
        /// A swap takes long enough that presence can change while it runs, so the committed value
        /// is re-read afterwards and the swap repeated until the mode matches the headset.
        /// </summary>
        private async void OnHMDPresenceChanged(bool isPresent)
        {
            if (_autoSwapInProgress)
            {
                _autoSwapPendingRecheck = true;
                return;
            }

            string swapMode = BasisSettingsSystem.LoadString("swap_mode", BasisSettingsDefaults.SwapMode_Shutdown);
            if (!string.Equals(swapMode, BasisSettingsDefaults.SwapMode_AutoSwap, StringComparison.OrdinalIgnoreCase)) return;

            // Gated here rather than at the hub so the sensor keeps being read and reported while
            // this is off — the presence state stays diagnosable, it just stops changing modes.
            if (!BasisSettingsDefaults.UsePresenceSensor.RawValue) return;

            bool shouldSwitchToDesktop = !isPresent && IsCurrentModeVR();
            bool shouldSwitchToVR = isPresent && IsSoftSwapped;

            if (!shouldSwitchToDesktop && !shouldSwitchToVR) return;

            _autoSwapInProgress = true;
            try
            {
                while (true)
                {
                    _autoSwapPendingRecheck = false;

                    if (shouldSwitchToDesktop)
                    {
                        BasisDebug.Log("AutoSwap: Headset removed — switching to Desktop", BasisDebug.LogTag.Device);
                        await SoftSwitchToDesktop();
                    }
                    else
                    {
                        BasisDebug.Log("AutoSwap: Headset detected — switching to VR", BasisDebug.LogTag.Device);
                        await SoftSwitchToVR();
                    }

                    if (!_autoSwapPendingRecheck) break;

                    // Presence moved while the swap was running and the guard above swallowed the
                    // event. Take the committed value as the truth and settle against it.
                    isPresent = BasisHMDPresence.IsPresent;
                    shouldSwitchToDesktop = !isPresent && IsCurrentModeVR();
                    shouldSwitchToVR = isPresent && IsSoftSwapped;

                    if (!shouldSwitchToDesktop && !shouldSwitchToVR) break;

                    BasisDebug.Log("AutoSwap: Presence changed mid-swap — reconciling", BasisDebug.LogTag.Device);
                }
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"AutoSwap: Swap failed — {e}", BasisDebug.LogTag.Device);
            }
            finally
            {
                _autoSwapInProgress = false;
                _autoSwapPendingRecheck = false;
            }
        }

        #endregion
    }
}
