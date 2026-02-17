using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management.Devices.OpenVR.Structs;
using Basis.Scripts.Device_Management.Devices.Unity_Spatial_Tracking;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
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
        public bool IsInUse = false;
        public static string SteamVRBehaviour = "SteamVR_Behaviour";
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
            BasisDebug.Log($"Device index {deviceIndex} is connected: {deviceConnected}");

            var error = new ETrackedPropertyError();
            var id = new StringBuilder(64);
            Valve.VR.OpenVR.System.GetStringTrackedDeviceProperty(
                deviceIndex,
                ETrackedDeviceProperty.Prop_RenderModelName_String,
                id,
                64,
                ref error);

            ETrackedDeviceClass deviceClass = Valve.VR.OpenVR.System.GetTrackedDeviceClass(deviceIndex);
            string uniqueID = $"{deviceIndex}|{id}";
            string notUnique = id.ToString();

            if (deviceConnected)
            {
                CreateTrackerDevice(deviceIndex, deviceClass, uniqueID, notUnique);
            }
            else
            {
                DestroyPhysicalTrackedDevice(uniqueID);
            }
        }
        private void CreateTrackerDevice(uint deviceIndex, ETrackedDeviceClass deviceClass, string uniqueID, string notUniqueID)
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
                    CreateHMD(openVRDevice, uniqueID, notUniqueID);
                    break;
                case ETrackedDeviceClass.Controller:
                    CreateController(openVRDevice, uniqueID, notUniqueID);
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
                    CreateTracker(openVRDevice, uniqueID, notUniqueID, false, BasisBoneTrackedRole.CenterEye);
                    break;
                case ETrackedDeviceClass.DisplayRedirect:
                    BasisDebug.Log("Was DisplayRedirect Device");
                    break;
                case ETrackedDeviceClass.Max:
                    BasisDebug.Log("Was Max Device");
                    CreateTracker(openVRDevice, uniqueID, notUniqueID, false, BasisBoneTrackedRole.CenterEye);
                    break;
                default:
                    CreateTracker(openVRDevice, uniqueID, notUniqueID, false, BasisBoneTrackedRole.CenterEye);
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
        private void CreateHMD(OpenVRDevice device, string uniqueID, string notUniqueID)
        {
            if (!TypicalDevices.ContainsKey(uniqueID))
            {
                GameObject Output = GenerateGameobject(uniqueID);
                var spatial = Output.AddComponent<BasisOpenVRInputSpatial>();
                spatial.ClassName = nameof(BasisOpenVRInputSpatial);
                bool foundRole = TryAssignRole(device.deviceClass, device.deviceIndex, notUniqueID, out BasisBoneTrackedRole role, out SteamVR_Input_Sources source);
                spatial.Initialize(device, uniqueID, notUniqueID, nameof(BasisOpenVRManagement), foundRole, role);

                BasisDeviceManagement.Instance.TryAdd(spatial);
                TypicalDevices.TryAdd(uniqueID, device);
            }
            else
            {
                HandleExistingDevice(uniqueID, notUniqueID, nameof(BasisOpenVRInputSpatial), device);
            }
        }
        public void CreateController(OpenVRDevice device, string uniqueID, string notUniqueID)
        {
            if (!TypicalDevices.ContainsKey(uniqueID))
            {
                GameObject Output = GenerateGameobject(uniqueID);
                var controller = Output.AddComponent<BasisOpenVRInputController>();
                controller.ClassName = nameof(BasisOpenVRInputController);
                bool foundRole = TryAssignRole(device.deviceClass, device.deviceIndex, notUniqueID, out BasisBoneTrackedRole role, out SteamVR_Input_Sources source);
                controller.Initialize(device, uniqueID, notUniqueID, nameof(BasisOpenVRManagement), foundRole, role, source);
                BasisDeviceManagement.Instance.TryAdd(controller);
                TypicalDevices.TryAdd(uniqueID, device);
            }
            else
            {
                HandleExistingDevice(uniqueID, notUniqueID, nameof(BasisOpenVRInputController), device);
            }
        }
        public void CreateTracker(OpenVRDevice device, string uniqueID, string notUniqueID, bool autoAssignRole, BasisBoneTrackedRole role)
        {
            if (!TypicalDevices.ContainsKey(uniqueID))
            {
                GameObject Output = GenerateGameobject(uniqueID);
                var input = Output.AddComponent<BasisOpenVRInput>();
                input.ClassName = nameof(BasisOpenVRInput);
                 input.Initialize(device, uniqueID, notUniqueID, nameof(BasisOpenVRManagement), autoAssignRole, role);
                BasisDeviceManagement.Instance.TryAdd(input);
                TypicalDevices.TryAdd(uniqueID, device);
            }
            else
            {
                HandleExistingDevice(uniqueID, notUniqueID, nameof(BasisOpenVRInput), device);
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
                    BasisDebug.LogError($"Unable to discover Correctly using Name for role lookup {source} device in Index was {controllerRole}");
                    return true;
                }
                else
                {
                    if (NameInCaseFallback.ToLower().Contains("right"))
                    {
                        role = BasisBoneTrackedRole.RightHand;
                        source = SteamVR_Input_Sources.RightHand;
                        BasisDebug.LogError($"Unable to discover Correctly using Name for role lookup {source} device in Index was {controllerRole}");
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
        private void HandleExistingDevice(string uniqueID, string notUniqueID, string className, OpenVRDevice device)
        {
            string subsystem = nameof(BasisOpenVRManagement);

            foreach (BasisInput input in BasisDeviceManagement.Instance.AllInputDevices)
            {
                // NOTE: Fixed SubSystemIdentifier check (was comparing to uniqueID)
                if (input.UniqueDeviceIdentifier == uniqueID && input.SubSystemIdentifier == subsystem)
                {
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
        public override void StopSDK()
        {
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

            SteamVR_Render.steamvr_render = null;
            SteamVR_Render = null;
            IsInUse = false;
            SteamVR_Events.DeviceConnected.RemoveListener(OnDeviceConnected);
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

            // Register SteamVR events
            SteamVR_Events.DeviceConnected.Listen(OnDeviceConnected);
            SteamVR_Events.System(EVREventType.VREvent_TrackedDeviceRoleChanged).Listen(OnTrackedDeviceRoleChanged);

            SteamVR_Render.Initialize(SteamVR_Render);

            bool State = await WaitingUntilReady();

            if (State)
            {
                BasisDebug.Log("SteamVR SDK started successfully.");
                BasisCursorManagement.UnlockCursorBypassChecks("Forceful Unlock OPENVR");
            }
            else
            {
                BasisDebug.Log("SteamVR SDK failed falling back.");
              await  BasisDeviceManagement.Instance.SwitchSetModeToDefault();
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
        public override void Simulate()
        {
            if (SteamVR_Render != null)
            {
                SteamVR_Render.Simulate();
            }
        }
    }
}
