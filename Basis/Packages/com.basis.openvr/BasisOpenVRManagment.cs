using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices.OpenVR.Structs;
using Basis.Scripts.Device_Management.Devices.Unity_Spatial_Tracking;
using Basis.Scripts.Drivers;
using Basis.Scripts.Rendering;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.XR;
using Valve.VR;

namespace Basis.Scripts.Device_Management.Devices.OpenVR
{
    [Serializable]
    public class BasisOpenVRManagement : BasisBaseTypeManagement
    {
        public GameObject SteamVR_BehaviourGameobject;
        public SteamVR_Render SteamVR_Render;
        public SteamVR SteamVR;
        public Dictionary<string, OpenVRDevice> TypicalDevices = new Dictionary<string, OpenVRDevice>();
        /// <summary>
        /// Maps a live device index to the uniqueID it was created with, captured at connect.
        /// On disconnect the device index may be dead and its render-model property unreadable, so
        /// recomputing the uniqueID can produce a different string, miss the teardown, and strand the
        /// old input object on its hand role. Looking the id up here makes teardown target the exact
        /// object that was created.
        /// </summary>
        private readonly Dictionary<uint, string> ConnectedIndexToUniqueID = new Dictionary<uint, string>();
        public bool IsInUse = false;
        /// <summary>
        /// When true, device creation/destruction from SteamVR events is suppressed.
        /// Used during soft swap to keep the runtime alive without processing device changes.
        /// </summary>
        public bool IsSuspended = false;
        public static string SteamVRBehaviour = "SteamVR_Behaviour";
        /// <summary>
        /// Off pending verification: first VR run with the subsystem cut active broke rendering
        /// (movement fine — Basis polls poses from the compositor itself), suggesting the Valve XR
        /// display provider is not independent of the input subsystem as assumed. Saves only the
        /// idle XR mirror updates (~0.04ms) when proven safe.
        /// </summary>
        public static bool CutUnityXRInputSubsystems = false;
        /// <summary>
        /// Which signal presence is being read from. Resolved on the first frame a source answers
        /// and latched, so the choice is logged once rather than every frame.
        /// </summary>
        private enum HMDPresenceSource
        {
            /// <summary>Nothing has answered yet.</summary>
            Unresolved,
            /// <summary>Unity XR's userPresence feature on the head device.</summary>
            UserPresenceFeature,
            /// <summary>OpenVR user-interaction events on a headset that reports a proximity sensor.</summary>
            ProximitySensorEvents,
            /// <summary>No proximity-backed source exists on this headset.</summary>
            NoProximitySignal
        }
        private HMDPresenceSource PresenceSource = HMDPresenceSource.Unresolved;
        /// <summary>
        /// Worn state carried between OpenVR's user-interaction events, used when the userPresence
        /// feature is unavailable. Seeded worn — the SDK only starts with the user in the headset.
        /// </summary>
        private bool ProximityWorn = true;
        private bool ProximitySensorResolved = false;
        private bool ProximitySensorPresent = false;
        /// <summary>
        /// Reused by the per-frame userPresence query so the poll does not allocate.
        /// </summary>
        private static readonly List<UnityEngine.XR.InputDevice> HeadDevices = new List<UnityEngine.XR.InputDevice>();
        /// <summary>
        /// Cadence for re-reading the compositor's recommended render target. Covers changes that
        /// arrive without a settings event, such as SteamVR's own automatic resolution adjustment
        /// and external tools writing the per-application resolution directly.
        /// </summary>
       // public static float ResolutionPollInterval = 0.5f;
        //private float ResolutionPollTimer;
        private void OnDeviceConnected(uint deviceIndex, bool deviceConnected)
        {
            StartCoroutine(DelayedOnDeviceConnectedCoroutine(deviceIndex, deviceConnected));
        }

        private IEnumerator DelayedOnDeviceConnectedCoroutine(uint deviceIndex, bool deviceConnected)
        {
            // Wait for 3 frames
            for (int i = 0; i < 3; i++)
            {
                yield return null;
            }

            DelayedOnDeviceConnected(deviceIndex, deviceConnected);
        }

        private void DelayedOnDeviceConnected(uint deviceIndex, bool deviceConnected)
        {
            if (IsSuspended) return;

            BasisDebug.Log($"Device index {deviceIndex} is connected: {deviceConnected}");

            var error = new ETrackedPropertyError();
            var id = new StringBuilder(64);
            Valve.VR.OpenVR.System.GetStringTrackedDeviceProperty(
                deviceIndex,
                ETrackedDeviceProperty.Prop_RenderModelName_String,
                id,
                64,
                ref error);

            var serialBuilder = new StringBuilder(64);
            Valve.VR.OpenVR.System.GetStringTrackedDeviceProperty(
                deviceIndex,
                ETrackedDeviceProperty.Prop_SerialNumber_String,
                serialBuilder,
                64,
                ref error);
            string deviceSerial = serialBuilder.ToString();

            var controllerTypeBuilder = new StringBuilder(64);
            Valve.VR.OpenVR.System.GetStringTrackedDeviceProperty(
                deviceIndex,
                ETrackedDeviceProperty.Prop_ControllerType_String,
                controllerTypeBuilder,
                64,
                ref error);
            string controllerType = controllerTypeBuilder.ToString();

            ETrackedDeviceClass deviceClass = Valve.VR.OpenVR.System.GetTrackedDeviceClass(deviceIndex);
            string uniqueID = $"{deviceIndex}|{id}";
            string notUnique = id.ToString();

            if (deviceConnected)
            {
                // Remember the id this index was created with so teardown can find it even after the
                // device goes dark and its render-model property can no longer be read.
                ConnectedIndexToUniqueID[deviceIndex] = uniqueID;
                CreateTrackerDevice(deviceIndex, deviceClass, uniqueID, notUnique, deviceSerial, controllerType);
            }
            else
            {
                // Prefer the id captured at connect; recomputing from a now-dead index can yield a
                // different string and leave the original input object stranded (a duplicate holder).
                if (ConnectedIndexToUniqueID.TryGetValue(deviceIndex, out string knownUniqueID))
                {
                    ConnectedIndexToUniqueID.Remove(deviceIndex);
                    DestroyPhysicalTrackedDevice(knownUniqueID);
                }
                else
                {
                    // No id was cached for this index (a disconnect with no matching connect, or the
                    // cache was cleared underneath us). Fall back to the recomputed id, which can be
                    // stale on a now-dead index and may leave a stranded holder — flag it.
                    BasisDebug.LogError($"No cached uniqueID for disconnected device index {deviceIndex}; falling back to recomputed id {uniqueID}", BasisDebug.LogTag.Device);
                    DestroyPhysicalTrackedDevice(uniqueID);
                }
            }
        }
        private void CreateTrackerDevice(uint deviceIndex, ETrackedDeviceClass deviceClass, string uniqueID, string notUniqueID, string deviceSerial = "", string controllerType = "")
        {
            OpenVRDevice openVRDevice = new OpenVRDevice
            {
                deviceClass = deviceClass,
                deviceIndex = deviceIndex,
                deviceName = uniqueID,
            };

            switch (deviceClass)
            {
                case ETrackedDeviceClass.HMD:
                    CreateHMD(openVRDevice, uniqueID, notUniqueID, deviceSerial, controllerType);
                    break;
                case ETrackedDeviceClass.Controller:
                    CreateController(openVRDevice, uniqueID, notUniqueID, deviceSerial, controllerType);
                    break;
                case ETrackedDeviceClass.TrackingReference:
                    BasisDebug.Log("Was TrackingReference Device");
                    //  CreateTracker(openVRDevice, uniqueID, notUniqueID, false, BasisBoneTrackedRole.CenterEye);
                    break;
                case ETrackedDeviceClass.Invalid:
                    BasisDebug.Log("Was Invalid Device");
                    break;
                case ETrackedDeviceClass.GenericTracker:
                    BasisDebug.Log("Was GenericTracker Device");
                    CreateTracker(openVRDevice, uniqueID, notUniqueID, false, BasisBoneTrackedRole.CenterEye, deviceSerial, controllerType);
                    break;
                case ETrackedDeviceClass.DisplayRedirect:
                    BasisDebug.Log("Was DisplayRedirect Device");
                    break;
                case ETrackedDeviceClass.Max:
                    BasisDebug.Log("Was Max Device");
                    CreateTracker(openVRDevice, uniqueID, notUniqueID, false, BasisBoneTrackedRole.CenterEye, deviceSerial, controllerType);
                    break;
                default:
                    CreateTracker(openVRDevice, uniqueID, notUniqueID, false, BasisBoneTrackedRole.CenterEye, deviceSerial, controllerType);
                    break;
            }
        }
        public GameObject GenerateGameobject(string uniqueID)
        {
            var gameObject = new GameObject(uniqueID)
            {
                transform = { parent = BasisLocalPlayer.Instance.transform }
            };
            return gameObject;
        }
        private void CreateHMD(OpenVRDevice device, string uniqueID, string notUniqueID, string deviceSerial = "", string controllerType = "")
        {
            if (!TypicalDevices.ContainsKey(uniqueID))
            {
                GameObject Output = GenerateGameobject(uniqueID);
                var spatial = Output.AddComponent<BasisOpenVRInputSpatial>();
                spatial.ClassName = nameof(BasisOpenVRInputSpatial);
                spatial.DeviceSerial = deviceSerial;
                spatial.DeviceControllerType = controllerType;
                bool foundRole = TryAssignRole(device.deviceClass, device.deviceIndex, notUniqueID, out BasisBoneTrackedRole role, out SteamVR_Input_Sources source);
                spatial.Initialize(device, uniqueID, notUniqueID, nameof(BasisOpenVRManagement), foundRole, role);

                BasisDeviceManagement.Instance.TryAdd(spatial);
                TypicalDevices.TryAdd(uniqueID, device);
            }
            else
            {
                HandleExistingDevice(uniqueID, notUniqueID, nameof(BasisOpenVRInputSpatial), device, deviceSerial, controllerType);
            }
        }
        public void CreateController(OpenVRDevice device, string uniqueID, string notUniqueID, string deviceSerial = "", string controllerType = "")
        {
            if (!TypicalDevices.ContainsKey(uniqueID))
            {
                GameObject Output = GenerateGameobject(uniqueID);
                var controller = Output.AddComponent<BasisOpenVRInputController>();
                controller.ClassName = nameof(BasisOpenVRInputController);
                controller.DeviceSerial = deviceSerial;
                controller.DeviceControllerType = controllerType;
                bool foundRole = TryAssignRole(device.deviceClass, device.deviceIndex, notUniqueID, out BasisBoneTrackedRole role, out SteamVR_Input_Sources source);
                controller.Initialize(device, uniqueID, notUniqueID, nameof(BasisOpenVRManagement), foundRole, role, source);
                BasisDeviceManagement.Instance.TryAdd(controller);
                TypicalDevices.TryAdd(uniqueID, device);
            }
            else
            {
                HandleExistingDevice(uniqueID, notUniqueID, nameof(BasisOpenVRInputController), device, deviceSerial, controllerType);
            }
        }
        public void CreateTracker(OpenVRDevice device, string uniqueID, string notUniqueID, bool autoAssignRole, BasisBoneTrackedRole role, string deviceSerial = "", string controllerType = "")
        {
            if (!TypicalDevices.ContainsKey(uniqueID))
            {
                GameObject Output = GenerateGameobject(uniqueID);
                var input = Output.AddComponent<BasisOpenVRInput>();
                input.ClassName = nameof(BasisOpenVRInput);
                input.DeviceSerial = deviceSerial;
                input.DeviceControllerType = controllerType;
                input.Initialize(device, uniqueID, notUniqueID, nameof(BasisOpenVRManagement), autoAssignRole, role);
                BasisDeviceManagement.Instance.TryAdd(input);
                TypicalDevices.TryAdd(uniqueID, device);
            }
            else
            {
                HandleExistingDevice(uniqueID, notUniqueID, nameof(BasisOpenVRInput), device, deviceSerial, controllerType);
            }
        }
        public bool TryAssignRole(ETrackedDeviceClass deviceClass, uint deviceIndex, string NameInCaseFallback, out BasisBoneTrackedRole role, out SteamVR_Input_Sources source)
        {

            if(NameInCaseFallback.ToLower().Contains("/rendermodels/"))
            {
                BasisDebug.LogError("a controller had /rendermodels/ in its name why? this seems wrong!");
                role = BasisBoneTrackedRole.CenterEye;
                source = SteamVR_Input_Sources.Keyboard;
                return false;
            }
            if (Valve.VR.OpenVR.System.IsTrackedDeviceConnected(deviceIndex))
            {
                BasisDebug.Log($"{deviceIndex} was found to be connected");
            }
            else
            {
                BasisDebug.LogError($"{deviceIndex} was found to not be connected");
            }

            source = SteamVR_Input_Sources.Any;
            role = BasisBoneTrackedRole.CenterEye;

            if (deviceClass == ETrackedDeviceClass.HMD)
            {
                role = BasisBoneTrackedRole.CenterEye;
                source = SteamVR_Input_Sources.Head;
                return true;
            }

            if (deviceClass == ETrackedDeviceClass.Controller)
            {
                var controllerRole = Valve.VR.OpenVR.System.GetControllerRoleForTrackedDeviceIndex(deviceIndex);
                if (controllerRole == ETrackedControllerRole.LeftHand)
                {
                    role = BasisBoneTrackedRole.LeftHand;
                    source = SteamVR_Input_Sources.LeftHand;
                    BasisDebug.Log($"{deviceIndex} was found to be a LeftHand");
                    return true;
                }

                if (controllerRole == ETrackedControllerRole.RightHand)
                {
                    role = BasisBoneTrackedRole.RightHand;
                    source = SteamVR_Input_Sources.RightHand;
                    BasisDebug.Log($"{deviceIndex} was found to be a RightHand");
                    return true;
                }
                if (NameInCaseFallback.ToLower().Contains("left"))
                {
                    role = BasisBoneTrackedRole.LeftHand;
                    source = SteamVR_Input_Sources.LeftHand;
                    BasisDebug.LogWarning($"Resolved {source} via name fallback; SteamVR controller role was {controllerRole}");
                    return true;
                }
                else
                {
                    if (NameInCaseFallback.ToLower().Contains("right"))
                    {
                        role = BasisBoneTrackedRole.RightHand;
                        source = SteamVR_Input_Sources.RightHand;
                        BasisDebug.LogWarning($"Resolved {source} via name fallback; SteamVR controller role was {controllerRole}");
                        return true;
                    }
                }
                BasisDebug.LogError($"Device unknown {NameInCaseFallback} we poorly detected {controllerRole}");
            }

            return false;
        }
        public void DestroyPhysicalTrackedDevice(string id)
        {
            TypicalDevices.Remove(id);
            BasisDeviceManagement.Instance.RemoveDevicesFrom(nameof(BasisOpenVRManagement), id);
        }
        private void HandleExistingDevice(string uniqueID, string notUniqueID, string className, OpenVRDevice device, string deviceSerial = "", string controllerType = "")
        {
            string subsystem = nameof(BasisOpenVRManagement);

            foreach (BasisInput input in BasisDeviceManagement.Instance.AllInputDevices)
            {
                // NOTE: Fixed SubSystemIdentifier check (was comparing to uniqueID)
                if (input.UniqueDeviceIdentifier == uniqueID && input.SubSystemIdentifier == subsystem)
                {
                    if (!string.IsNullOrEmpty(deviceSerial))
                    {
                        input.DeviceSerial = deviceSerial;
                    }
                    if (!string.IsNullOrEmpty(controllerType))
                    {
                        input.DeviceControllerType = controllerType;
                    }
                    if (input.ClassName == className)
                    {
                        // Compute role once for all relevant branches
                        bool foundRole = TryAssignRole(
                            device.deviceClass,
                            device.deviceIndex,
                            notUniqueID,
                            out BasisBoneTrackedRole role,
                            out SteamVR_Input_Sources source
                        );

                        if (input is BasisOpenVRInputSpatial spatial)
                        {
                            spatial.Initialize(
                                device,
                                uniqueID,
                                notUniqueID,
                                subsystem,
                                foundRole,
                                role
                            );
                        }
                        else if (input is BasisOpenVRInputController controller)
                        {
                            controller.Initialize(
                                device,
                                uniqueID,
                                notUniqueID,
                                subsystem,
                                foundRole,
                                role,
                                source
                            );
                        }
                        else if (input is BasisOpenVRInput basisInput)
                        {
                            basisInput.Initialize(
                                device,
                                uniqueID,
                                notUniqueID,
                                subsystem,
                                false,
                                BasisBoneTrackedRole.CenterEye
                            );
                        }
                        else
                        {
                            BasisDebug.LogError("Some other Class Name " + input.ClassName + " look over this!");
                        }

                        return;
                    }
                    else
                    {
                        DestroyPhysicalTrackedDevice(uniqueID);
                        OnDeviceConnected(device.deviceIndex, true);
                        return;
                    }
                }
            }
        }
        /// <summary>
        /// Destroys all tracked device inputs but keeps SteamVR runtime, event listeners, and rendering alive.
        /// </summary>
        public override void SoftStopDevices()
        {
            IsSuspended = true;
            foreach (var device in TypicalDevices.Keys.ToList())
            {
                DestroyPhysicalTrackedDevice(device);
            }
            ConnectedIndexToUniqueID.Clear();
            BasisDebug.Log("OpenVR: Soft-stopped input devices (runtime kept alive)", BasisDebug.LogTag.Device);
        }

        /// <summary>
        /// Re-enumerates all connected SteamVR devices and recreates their input components.
        /// Assumes the SteamVR runtime is still active from a prior soft stop.
        /// </summary>
        public override void SoftStartDevices()
        {
            if (!IsInUse || SteamVR == null)
            {
                BasisDebug.LogError("OpenVR: Cannot soft-start, runtime is not active", BasisDebug.LogTag.Device);
                return;
            }

            IsSuspended = false;
            BasisLocalCameraDriver.AllowXRRenderering(true);
            // Soft start keeps the SteamVR runtime alive, so the eye-texture scale baked
            // in by ApplyRecommendedRenderResolution on initial StartSDK is still valid.

            // Re-enumerate all connected devices
            for (uint i = 0; i < 64; i++)
            {
                if (Valve.VR.OpenVR.System != null && Valve.VR.OpenVR.System.IsTrackedDeviceConnected(i))
                {
                    OnDeviceConnected(i, true);
                }
            }

            BasisCursorManagement.UnlockCursorBypassChecks("Forceful Unlock OPENVR SoftStart");
            BasisDebug.Log("OpenVR: Soft-started input devices", BasisDebug.LogTag.Device);
        }

        public override void StopSDK()
        {
            // The worker must be idle before the runtime is disposed under it.
            ShutdownInputThread();
            IsSuspended = false;
            // Reset the persistent eye-texture multiplier so a subsequent OpenXR/Desktop
            // session doesn't inherit OpenVR's headset-native scaling.
            XRSettings.eyeTextureResolutionScale = 1f;
          //  BasisDynamicResolution.OnAllocationScaleChanged -= OnAllocationScaleChanged;
           // BasisDynamicResolution.ExternalAllocationOwner = false;
           // BasisDynamicResolution.ResetAppliedState();
            SteamVR.SafeDispose();

            if (SteamVR_BehaviourGameobject != null)
            {
                GameObject.Destroy(SteamVR_BehaviourGameobject);
            }
            SteamVR_BehaviourGameobject = null;

            foreach (var device in TypicalDevices.Keys.ToList())
            {
                DestroyPhysicalTrackedDevice(device);
            }
            ConnectedIndexToUniqueID.Clear();

            SteamVR_Render.steamvr_render = null;
            SteamVR_Render = null;
            IsInUse = false;
            SteamVR_Events.DeviceConnected.RemoveListener(OnDeviceConnected);
            SteamVR_Events.System(EVREventType.VREvent_SteamVRSectionSettingChanged).RemoveListener(OnResolutionSettingChanged);
            SteamVR_Events.System(EVREventType.VREvent_DashboardDeactivated).RemoveListener(OnResolutionSettingChanged);
            SteamVR_Events.System(EVREventType.VREvent_TrackedDeviceUserInteractionStarted).RemoveListener(OnHMDUserInteractionStarted);
            SteamVR_Events.System(EVREventType.VREvent_TrackedDeviceUserInteractionEnded).RemoveListener(OnHMDUserInteractionEnded);
        }
        public override async void StartSDK()
        {
            if (IsInUse)
            {
                BasisDebug.LogError("Already using OpenVR!");
                return;
            }
            IsInUse = true;
            BasisLocalCameraDriver.AllowXRRenderering(true);

            BasisDebug.Log("Starting SteamVR Instance...");
            SteamVR = SteamVR.instance;

            if (SteamVR_BehaviourGameobject == null)
            {
                SteamVR_BehaviourGameobject = new GameObject(SteamVRBehaviour);
            }
            SteamVR_BehaviourGameobject.transform.parent = this.transform;
            // Initialize SteamVR components
            SteamVR_Render = BasisHelpers.GetOrAddComponent<SteamVR_Render>(SteamVR_BehaviourGameobject);

            // Presence is per-session: this object outlives a stop/start, so a stale source or a
            // stale worn latch would otherwise carry into the next run.
            PresenceSource = HMDPresenceSource.Unresolved;
            ProximityWorn = true;
            ProximitySensorResolved = false;
            ProximitySensorPresent = false;

            // Register SteamVR events
            SteamVR_Events.DeviceConnected.Listen(OnDeviceConnected);
            SteamVR_Events.System(EVREventType.VREvent_TrackedDeviceRoleChanged).Listen(OnTrackedDeviceRoleChanged);
            SteamVR_Events.System(EVREventType.VREvent_TrackedDeviceUserInteractionStarted).Listen(OnHMDUserInteractionStarted);
            SteamVR_Events.System(EVREventType.VREvent_TrackedDeviceUserInteractionEnded).Listen(OnHMDUserInteractionEnded);
            SteamVR_Events.System(EVREventType.VREvent_SteamVRSectionSettingChanged).Listen(OnResolutionSettingChanged);
            SteamVR_Events.System(EVREventType.VREvent_DashboardDeactivated).Listen(OnResolutionSettingChanged);

            SteamVR_Render.Initialize(SteamVR_Render);

            bool State = await WaitingUntilReady();

            if (State)
            {
                BasisDebug.Log("SteamVR SDK started successfully.");
                // The SDK only comes up with the user in the headset, so commit that without waiting
                // on the debounce. Left at its default of not-present, the first real report would be
                // a change into worn rather than out of it, and taking the headset off would then be
                // the second edge rather than the first.
                BasisHMDPresence.ForcePresence(true);
             //   BasisDynamicResolution.ExternalAllocationOwner = true;
             //   BasisDynamicResolution.OnAllocationScaleChanged += OnAllocationScaleChanged;
               // ApplyRecommendedRenderResolution();
                if (CutUnityXRInputSubsystems)
                {
                    StopUnityXRInputSubsystems();
                }
                BasisCursorManagement.UnlockCursorBypassChecks("Forceful Unlock OPENVR");
            }
            else
            {
                BasisDebug.Log("SteamVR SDK failed falling back.");
                await BasisDeviceManagement.Instance.SwitchSetModeToDefault();
                // Reported after the switch so the notice can name the mode actually landed in,
                // rather than guessing at what the default resolves to on this platform.
                BasisXRRuntimeNotice.ReportFallback(BasisConstants.OpenVRLoader,
                    BasisDeviceManagement.StaticCurrentMode,
                    Basis.BasisUI.BasisLocalization.Get("settings.platform.vrFailed.steamvr"));
            }
        }
        /*
        /// <summary>
        /// Pulls SteamVR's grown recommended render target (which factors in the lens
        /// distortion overlap between eyes) and bakes it into XRSettings.eyeTextureResolutionScale.
        /// The OpenVR XR plugin allocates eye textures at Unity's default size — without this,
        /// Steam super-sampling and the per-headset native resolution are silently ignored.
        /// The recommendation is re-read from the runtime rather than taken from
        /// SteamVR.sceneWidth/sceneHeight, which are frozen in the SteamVR constructor, so
        /// compositor-side changes (per-app resolution, supersample slider, external tools
        /// such as OVR Dynamic Resolution) are followed while the client is running.
        /// </summary>
        private void ApplyRecommendedRenderResolution()
        {
            if (SteamVR == null) return;
            CVRSystem system = Valve.VR.OpenVR.System;
            if (system == null) return;

            uint recommendedWidth = 0;
            uint recommendedHeight = 0;
            system.GetRecommendedRenderTargetSize(ref recommendedWidth, ref recommendedHeight);
            if (recommendedWidth == 0 || recommendedHeight == 0) return;

            float grownWidth = recommendedWidth;
            float grownHeight = recommendedHeight;

            VRTextureBounds_t[] bounds = SteamVR.textureBounds;
            if (bounds != null && bounds.Length == 2)
            {
                grownWidth = BasisOpenVRResolutionPolicy.GrowForLensOverlap(recommendedWidth,
                    BasisOpenVRResolutionPolicy.LargestSpan(bounds[0].uMin, bounds[0].uMax, bounds[1].uMin, bounds[1].uMax));
                grownHeight = BasisOpenVRResolutionPolicy.GrowForLensOverlap(recommendedHeight,
                    BasisOpenVRResolutionPolicy.LargestSpan(bounds[0].vMin, bounds[0].vMax, bounds[1].vMin, bounds[1].vMax));
            }

            float recommendedMax = Mathf.Max(grownWidth, grownHeight);
            float targetMax = recommendedMax * BasisDynamicResolution.AllocationScale;
            float currentMax = Mathf.Max(XRSettings.eyeTextureWidth, XRSettings.eyeTextureHeight);

            if (!BasisOpenVRResolutionPolicy.TryComputeEyeTextureScale(targetMax, currentMax, XRSettings.eyeTextureResolutionScale, BasisOpenVRResolutionPolicy.DefaultDeadband, out float scale))
            {
                return;
            }

            if (Mathf.Approximately(scale, XRSettings.eyeTextureResolutionScale))
            {
                return;
            }

            XRSettings.eyeTextureResolutionScale = scale;
            BasisDebug.Log($"OpenVR resolution: eye texture scaled {scale:F3}× to {targetMax:F0} (compositor {recommendedMax:F0}, allocation {BasisDynamicResolution.AllocationScale:F2}, was {currentMax:F0})", BasisDebug.LogTag.Device);
        }
        */
        private void PollRecommendedRenderResolution()
        {
          //  ResolutionPollTimer += Time.unscaledDeltaTime;
          //  if (ResolutionPollTimer < ResolutionPollInterval)
          ////  {
          //      return;
          //  }
           // ResolutionPollTimer = 0f;
         //   ApplyRecommendedRenderResolution();
        }
        /// <summary>
        /// OpenVR mode reads every pose and button straight from the OpenVR API (compositor poses,
        /// SteamVR actions), so the Unity XR input subsystem the loader started has no consumers.
        /// Left running it mirrors the HMD, controllers, trackers and base stations into the Unity
        /// Input System as TrackedDevices, which bills every InputSystem.Update and keeps the
        /// before-render input update alive. Stopping it removes those devices; the display
        /// subsystem is untouched.
        /// </summary>
        private static void StopUnityXRInputSubsystems()
        {
            List<XRInputSubsystem> inputSubsystems = new List<XRInputSubsystem>();
            SubsystemManager.GetSubsystems(inputSubsystems);
            int count = inputSubsystems.Count;
            for (int Index = 0; Index < count; Index++)
            {
                XRInputSubsystem inputSubsystem = inputSubsystems[Index];
                if (inputSubsystem != null && inputSubsystem.running)
                {
                    inputSubsystem.Stop();
                    BasisDebug.Log("OpenVR: Stopped Unity XR input subsystem, SteamVR input is polled via OpenVR directly", BasisDebug.LogTag.Device);
                }
            }
        }
        public async Task<bool> WaitingUntilReady()
        {
            // Wait for SteamVR to initialize
            while (SteamVR.initializedState == SteamVR.InitializedStates.None)
            {
                BasisDebug.LogWarning("Waiting for SteamVR to switch from None...");
                await Task.Yield();
            }
            while (SteamVR.initializedState == SteamVR.InitializedStates.Initializing)
            {
                BasisDebug.LogWarning("SteamVR switched to Initializing...");
                await Task.Yield();
            }
            // Handle initialization failure
            if (SteamVR.initializedState == SteamVR.InitializedStates.InitializeFailure)
            {
                BasisDebug.LogError("SteamVR failed to initialize", BasisDebug.LogTag.Device);
                return false;
            }
            else
            {
                if (SteamVR.initializedState == SteamVR.InitializedStates.InitializeSuccess)
                {
                    BasisDebug.Log("SteamVR Initialize Success", BasisDebug.LogTag.Device);
                    return true;
                }
            }
            return false;
        }
        private void OnDeviceConnected(int deviceIndex, bool deviceConnected)
        {
            OnDeviceConnected((uint)deviceIndex, deviceConnected);
        }

        private void OnTrackedDeviceRoleChanged(VREvent_t vrEvent)
        {
            OnDeviceConnected(vrEvent.trackedDeviceIndex, true);
        }
        public override bool IsDeviceBootable(string BootRequest)
        {
            if (BootRequest == "OpenVRLoader")
            {
                return true;
            }
            return false;
        }
        /// <summary>
        /// When true the SteamVR action-state update (UpdateActionState + button/axis/skeleton
        /// reads — the bulk of the per-frame input cost) runs on a worker thread, kicked from
        /// <see cref="SimulateKick"/> and joined in <see cref="Simulate"/> so it overlaps the
        /// comms/network-apply work between the two. OpenVR interfaces are thread-safe, the
        /// update path reads main-thread state only via SteamVR_Input.CaptureMainThreadState,
        /// and no Basis code touches action state between kick and join (readers are the eye
        /// driver before the kick, and device polls after the join).
        /// </summary>
        public static bool ThreadedInputUpdate = true;
        private System.Threading.Thread inputThread;
        private readonly System.Threading.SemaphoreSlim inputKick = new System.Threading.SemaphoreSlim(0);
        private readonly System.Threading.ManualResetEventSlim inputDone = new System.Threading.ManualResetEventSlim(true);
        private volatile bool inputThreadRun;
        private bool inputKicked;
        static readonly Unity.Profiling.ProfilerMarker sMarkerJoinInput = new Unity.Profiling.ProfilerMarker("BasisDriver.DeviceManagement.JoinInput");
        static readonly Unity.Profiling.ProfilerMarker sMarkerHMDPresence = new Unity.Profiling.ProfilerMarker("BasisDriver.DeviceManagement.HMDPresence");

        public override void SimulateKick()
        {
            if (!IsDeviceBooted || SteamVR_Render == null || !ThreadedInputUpdate)
            {
                return;
            }
            SteamVR_Input.CaptureMainThreadState();
            EnsureInputThread();
            inputDone.Wait();
            inputDone.Reset();
            inputKicked = true;
            inputKick.Release();
        }

        private void EnsureInputThread()
        {
            if (inputThread != null && inputThread.IsAlive)
            {
                return;
            }
            // Drain any kick left over from a previous thread's shutdown (its wake-up Release may
            // outlive it across a VR -> desktop -> VR round trip) so the new thread only runs when
            // kicked this session.
            while (inputKick.Wait(0))
            {
            }
            inputDone.Set();
            inputThreadRun = true;
            inputThread = new System.Threading.Thread(InputThreadLoop)
            {
                Name = "BasisSteamVRInput",
                IsBackground = true,
            };
            inputThread.Start();
        }

        private void InputThreadLoop()
        {
            UnityEngine.Profiling.Profiler.BeginThreadProfiling("Basis", "SteamVR Input");
            while (inputThreadRun)
            {
                inputKick.Wait();
                if (!inputThreadRun)
                {
                    break;
                }
                try
                {
                    Valve.VR.SteamVR_Render.SimulateInput();
                }
                catch (Exception e)
                {
                    BasisDebug.LogError($"SteamVR input thread: {e}", BasisDebug.LogTag.Device);
                }
                finally
                {
                    inputDone.Set();
                }
            }
            inputDone.Set();
            UnityEngine.Profiling.Profiler.EndThreadProfiling();
        }

        private void ShutdownInputThread()
        {
            if (inputThread == null)
            {
                return;
            }
            inputThreadRun = false;
            inputKick.Release();
            if (!inputThread.Join(1000))
            {
                BasisDebug.LogError("SteamVR input thread did not stop in time", BasisDebug.LogTag.Device);
            }
            inputThread = null;
            inputKicked = false;
            inputDone.Set();
        }

        private void OnDestroy()
        {
        //    BasisDynamicResolution.OnAllocationScaleChanged -= OnAllocationScaleChanged;
            ShutdownInputThread();
        }

        public override void Simulate()
        {
            if (!IsDeviceBooted) return;
            if (SteamVR_Render != null)
            {
                if (inputKicked)
                {
                    // The main-thread half is independent of action state, so it fills the
                    // remaining wait instead of running after the join.
                    SteamVR_Render.SimulatePosesAndEvents();
                    using (sMarkerJoinInput.Auto())
                    {
                        inputDone.Wait();
                    }
                    inputKicked = false;
                }
                else
                {
                    SteamVR_Input.CaptureMainThreadState();
                    Valve.VR.SteamVR_Render.SimulateInput();
                    SteamVR_Render.SimulatePosesAndEvents();
                }
            }
            using (sMarkerHMDPresence.Auto())
            {
                PollHMDPresence();
            }

            PollRecommendedRenderResolution();
        }

        /// <summary>
        /// The SteamVR dashboard writes its resolution slider into the steamvr settings section, so
        /// a section change is the earliest signal that the recommended render target moved. The
        /// apply itself is deferred to the next <see cref="Simulate"/> rather than run inside the
        /// event pump, keeping a single apply path and one reallocation per frame at most.
        /// </summary>
        private void OnResolutionSettingChanged(VREvent_t vrEvent)
        {
           // ResolutionPollTimer = ResolutionPollInterval;
        }

        private void OnAllocationScaleChanged()
        {
           // ResolutionPollTimer = ResolutionPollInterval;
        }

        /// <summary>
        /// Reports whether the headset is actually being worn into <see cref="BasisHMDPresence"/>,
        /// every frame. Sources are tried in order of trust and the first that answers wins:
        /// OpenVR's user-interaction events on a headset that reports a proximity sensor, then
        /// Unity XR's userPresence feature.
        /// <para>
        /// The proximity sensor is asked first because it is the only source here wired to the
        /// physical face sensor. Valve's XR plugin answers the userPresence query on every head
        /// device whether or not it tracks anything, so preferring it pinned presence worn for the
        /// whole session and no take-off edge ever reached the hub.
        /// </para>
        /// <para>
        /// The activity level is deliberately not consulted. It is motion-based — the HMD drops to
        /// <see cref="EDeviceActivityLevel.k_EDeviceActivityLevel_Idle"/> after roughly ten seconds
        /// without movement — so a worn but still headset reads identically to one sitting on the
        /// desk, and no debounce separates them because that idle state lasts as long as the user
        /// holds still. Where no proximity-backed source exists, presence stays worn instead of
        /// being guessed from movement: a missed swap is recoverable, a wrong one drops a user out
        /// of VR mid-session.
        /// </para>
        /// Runs even when <see cref="IsSuspended"/> — the runtime is alive during soft swap, which
        /// is what lets the headset going back on bring VR back.
        /// </summary>
        private void PollHMDPresence()
        {
            if (Valve.VR.OpenVR.System == null) return;

            if (HasProximitySensor())
            {
                SetPresenceSource(HMDPresenceSource.ProximitySensorEvents);
                BasisHMDPresence.ReportPresence(ProximityWorn);
                return;
            }

            if (TryReadUserPresenceFeature(out bool worn))
            {
                SetPresenceSource(HMDPresenceSource.UserPresenceFeature);
                BasisHMDPresence.ReportPresence(worn);
                return;
            }

            SetPresenceSource(HMDPresenceSource.NoProximitySignal);
            BasisHMDPresence.ReportPresence(true);
        }

        /// <summary>
        /// Reads the head device's userPresence feature — the same signal the OpenXR path uses.
        /// Only consulted for headsets that report no proximity sensor, because the return value
        /// says the feature was answered, not that anything drives it: Valve's XR plugin answers it
        /// on any head device and leaves it pinned worn. It arrives through the Unity XR input
        /// subsystem, so it also goes quiet if <see cref="CutUnityXRInputSubsystems"/> is ever
        /// turned on.
        /// </summary>
        private static bool TryReadUserPresenceFeature(out bool worn)
        {
            HeadDevices.Clear();
            UnityEngine.XR.InputDevices.GetDevicesAtXRNode(UnityEngine.XR.XRNode.Head, HeadDevices);
            int Count = HeadDevices.Count;
            for (int Index = 0; Index < Count; Index++)
            {
                if (HeadDevices[Index].TryGetFeatureValue(UnityEngine.XR.CommonUsages.userPresence, out worn))
                {
                    return true;
                }
            }
            worn = false;
            return false;
        }

        /// <summary>
        /// Whether the HMD reports a proximity sensor. Latched only once the property reads cleanly,
        /// so a failed read early in startup is retried rather than taken as "no sensor".
        /// </summary>
        private bool HasProximitySensor()
        {
            if (ProximitySensorResolved) return ProximitySensorPresent;

            ETrackedPropertyError PropertyError = ETrackedPropertyError.TrackedProp_Success;
            bool Present = Valve.VR.OpenVR.System.GetBoolTrackedDeviceProperty(
                Valve.VR.OpenVR.k_unTrackedDeviceIndex_Hmd,
                ETrackedDeviceProperty.Prop_ContainsProximitySensor_Bool,
                ref PropertyError);

            if (PropertyError != ETrackedPropertyError.TrackedProp_Success)
            {
                return false;
            }

            ProximitySensorPresent = Present;
            ProximitySensorResolved = true;
            return ProximitySensorPresent;
        }

        /// <summary>
        /// Latches the resolved source and logs it once, so a session that never swaps can be told
        /// apart from one that had no usable signal.
        /// </summary>
        private void SetPresenceSource(HMDPresenceSource Source)
        {
            if (PresenceSource == Source) return;
            PresenceSource = Source;
            switch (Source)
            {
                case HMDPresenceSource.UserPresenceFeature:
                    BasisDebug.Log("OpenVR: HMD presence read from the userPresence feature (headset reports no proximity sensor)", BasisDebug.LogTag.Device);
                    break;
                case HMDPresenceSource.ProximitySensorEvents:
                    BasisDebug.Log("OpenVR: HMD presence read from proximity sensor events", BasisDebug.LogTag.Device);
                    break;
                case HMDPresenceSource.NoProximitySignal:
                    BasisDebug.Log("OpenVR: headset reports no proximity sensor and userPresence is unavailable — presence pinned worn, auto swap will not trigger", BasisDebug.LogTag.Device);
                    break;
            }
        }

        /// <summary>
        /// OpenVR raises these for the HMD off its proximity sensor as the headset is put on and
        /// taken off. They are edge-triggered, so <see cref="ProximityWorn"/> holds the state
        /// between them.
        /// </summary>
        private void OnHMDUserInteractionStarted(VREvent_t vrEvent)
        {
            if (vrEvent.trackedDeviceIndex != Valve.VR.OpenVR.k_unTrackedDeviceIndex_Hmd) return;
            ProximityWorn = true;
        }

        /// <inheritdoc cref="OnHMDUserInteractionStarted"/>
        private void OnHMDUserInteractionEnded(VREvent_t vrEvent)
        {
            if (vrEvent.trackedDeviceIndex != Valve.VR.OpenVR.k_unTrackedDeviceIndex_Hmd) return;
            ProximityWorn = false;
        }
    }
}
