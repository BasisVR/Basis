using Basis.BasisUI;
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
    /// <summary>
    /// Central orchestrator for device discovery, start/stop, and mode switching across Desktop and XR.
    /// </summary>
    /// <remarks>
    /// This MonoBehaviour is intended to exist exactly once in a scene. Use <see cref="Instance"/> for access.
    /// It initializes players, loads settings/bindings, restores previously connected devices, and manages XR lifecycle.
    /// </remarks>
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
        /// If <c>true</c>, activates <see cref="BasisNetworking"/> once initialization completes.
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
        [SerializeField] public List<BasisBaseTypeManagement> BaseTypes = new();

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
        [SerializeField] public BasisLocalInputActions InputActions;

        /// <summary>
        /// Optional device name matcher used when probing for base types.
        /// </summary>
        public BasisDeviceNameMatcher BasisDeviceNameMatcher;

        /// <summary>
        /// Overrides the default mode selection when non-empty.
        /// </summary>
        public string ForcedDefault = string.Empty;

        /// <summary>
        /// Optional LipSync profile used by audio-driven facial animation systems.
        /// </summary>
        public Profile LipSyncProfile;

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

            StaticCurrentMode = BasisConstants.None;
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            BasisSettingsSystem.Initalize();
            BasisSettingsDefaults.LoadAll();
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
        private void OnDestroy()
        {
            BasisXRManagement.DeInitalize();
            BasisPlayerFactory.DeInitalize();
            StopAllDevices();
            UnsubscribeEvents();
            BasisUlipSyncDriver.DisposeShared();
        }
        public void Simulate()
        {
            int Count = BaseTypes.Count;
            for (int Index = 0; Index < Count; Index++)
            {
                BasisBaseTypeManagement Sim = BaseTypes[Index];
                if (Sim != null)
                {
                    Sim.Simulate();
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
            BasisPlayerFactory.Initalize();
            BasisXRManagement.Initalize();
            BasisCommandLineArgs.Initialize(BakedInCommandLineArgs, out ForcedDefault);
            BasisUlipSyncDriver.Initialize(BasisDeviceManagement.Instance.LipSyncProfile);
            await BasisPlayerFactory.CreateLocalPlayer(new InstantiationParameters(transform, true));
            StartAllStartIfPermanentlyExists();
            await SwitchSetModeToDefault();

            SubscribeEvents();

            await BasisActionDriver.LoadBindings();

            OnInitializationCompleted?.Invoke();
            OnInitializationComplete = true;
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
            SMDMicrophone.LoadInMicrophoneData(mode);
            StaticCurrentMode = mode;
            await BasisActionDriver.LoadBindings();
            BasisDebug.Log($"Loading mode: {mode}", BasisDebug.LogTag.Device);
        }

        /// <summary>
        /// Stops all active device SDKs, resets the current mode, and shuts down XR.
        /// </summary>
        public void StopAllDevices()
        {
            for (int i = 0; i < BaseTypes.Count; i++)
            {
                BaseTypes[i]?.AttemptStopSDK();
            }

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
            for (int i = 0; i < BaseTypes.Count; i++)
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
                    input.Control.InverseOffsetFromBone = prev.InverseOffsetFromBone;
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
                    InverseOffsetFromBone = device.Control.InverseOffsetFromBone
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
                HasEvents = false;
            }
        }

        /// <summary>
        /// Optional networking GameObject activated after initialization when <see cref="FireOffNetwork"/> is enabled.
        /// </summary>
        public GameObject BasisNetworking;

        /// <summary>
        /// Event handler invoked after initialization to toggle networking activation.
        /// </summary>
        private void RunAfterInitialized()
        {
            if (FireOffNetwork && BasisNetworking != null)
            {
                BasisNetworking.SetActive(true);
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
        public static bool IsCurrentModeVR() =>
            string.Equals(StaticCurrentMode, BasisConstants.OpenVRLoader, StringComparison.Ordinal) ||
            string.Equals(StaticCurrentMode, BasisConstants.OpenXRLoader, StringComparison.Ordinal);

        #endregion
    }
}
