using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;

namespace Basis.Scripts.Device_Management.Devices.UnityInputSystem
{
    [Serializable]
    public class BasisOpenXRManagement : BasisBaseTypeManagement
    {
        [SerializeField]
        private List<BasisInput> controls = new List<BasisInput>();
        [NonSerialized]
        public HashSet<InputDevice> OpenXRTrackers = new HashSet<InputDevice>();
        [SerializeField]
        public List<BasisOpenxrDeviceTrackedInfo> Trackers = new List<BasisOpenxrDeviceTrackedInfo>();
        [NonSerialized]
        private Dictionary<int, InputDevice> trackedDevices = new Dictionary<int, InputDevice>();
        /// <summary>
        /// When true, device creation/destruction from InputSystem events is suppressed.
        /// Used during soft swap to keep the XR runtime alive without processing device changes.
        /// </summary>
        public bool IsSuspended = false;
        public string[] HTCOpenXRViveTracker = new string[] { "HTC Vive Tracker (OpenXR)" };
        public string[] CommonUsagesWeAccept = new string[]
        {
            "Left Foot", "Right Foot", "Left Shoulder", "Right Shoulder",
            "Left Elbow", "Right Elbow", "Left Knee", "Right Knee", "Waist",
            "Chest", "Camera", "Keyboard"
        };
        [NonSerialized]
        public XRHandSubsystem m_Subsystem;
        public BasisOpenXRHandInput LeftHand;
        public BasisOpenXRHandInput RightHand;
        public override bool IsDeviceBootable(string BootRequest)
        {
            if(BootRequest == "OpenXRLoader")
            {
                return true;
            }
            return false;
        }
        /// <summary>
        /// Destroys all tracked device inputs but keeps the OpenXR runtime and subsystems alive.
        /// Unsubscribes hand tracking to avoid null-reference while hands are destroyed.
        /// </summary>
        public override void SoftStopDevices()
        {
            IsSuspended = true;

            // Unsubscribe hand tracking to prevent null-ref on destroyed hand inputs
            if (m_Subsystem != null)
            {
                m_Subsystem.updatedHands -= OnHandUpdate;
            }

            foreach (var device in controls.ToList())
            {
                if (device != null)
                {
                    DestroyPhysicalTrackedDevice(device.UniqueDeviceIdentifier);
                }
            }
            controls.Clear();
            OpenXRTrackers.Clear();

            BasisDebug.Log("OpenXR: Soft-stopped input devices (runtime kept alive)", BasisDebug.LogTag.Device);
        }

        /// <summary>
        /// Recreates head, hand, and body tracker inputs. Assumes the OpenXR runtime is still active.
        /// </summary>
        public override void SoftStartDevices()
        {
            IsSuspended = false;
            BasisLocalCameraDriver.AllowXRRenderering(true);

            CreatePhysicalHeadTracker("Head OPENXR", "Head OPENXR");
            LeftHand = CreatePhysicalHandTracker("Left Hand OPENXR", "Left Hand OPENXR", BasisBoneTrackedRole.LeftHand);
            RightHand = CreatePhysicalHandTracker("Right Hand OPENXR", "Right Hand OPENXR", BasisBoneTrackedRole.RightHand);

            // Resubscribe to hand tracking
            if (m_Subsystem != null)
            {
                m_Subsystem.updatedHands += OnHandUpdate;
            }

            // Re-scan for full body trackers
            int count = InputSystem.devices.Count;
            for (int i = 0; i < count; i++)
            {
                TryAddTracker(InputSystem.devices[i]);
            }
            trackerscount = Trackers.Count;

            BasisCursorManagement.UnlockCursorBypassChecks("Forceful Unlock OPENXR SoftStart");
            BasisDebug.Log("OpenXR: Soft-started input devices", BasisDebug.LogTag.Device);
        }

        public override void StopSDK()
        {
            IsSuspended = false;
            BasisDebug.Log("Stopping SDK for BasisOpenXRManagement");
            BasisOpenXRRefreshRate.Unhook();

            foreach (var device in controls)
            {
                BasisDebug.Log($"Destroying control device: {device.UniqueDeviceIdentifier}");
                DestroyPhysicalTrackedDevice(device.UniqueDeviceIdentifier);
            }

            controls.Clear();
            OpenXRTrackers.Clear();

            BasisDebug.Log("SDK stopped and all resources cleaned up.");
            InputSystem.onDeviceChange -= onDeviceChange;
            BasisDeviceManagement.OnDeviceManagementLoop -= CheckTrackersPulse;
            if (m_Subsystem != null)
            {
                m_Subsystem.updatedHands -= OnHandUpdate;
            }
        }
        public override void StartSDK()
        {
            BasisDebug.Log("Starting SDK for BasisOpenXRManagement");
            PresenceSource = HMDPresenceSource.Unresolved;
            BasisLocalCameraDriver.AllowXRRenderering(true);

            CreatePhysicalHeadTracker("Head OPENXR", "Head OPENXR");
            LeftHand = CreatePhysicalHandTracker("Left Hand OPENXR", "Left Hand OPENXR", BasisBoneTrackedRole.LeftHand);
            RightHand = CreatePhysicalHandTracker("Right Hand OPENXR", "Right Hand OPENXR", BasisBoneTrackedRole.RightHand);
            BasisOpenXRRefreshRate.Hook();
            SMModuleMotionVectorsURP.SpaceWarpActive = OpenXRRuntime.IsExtensionEnabled("XR_FB_space_warp");
            SMModuleMotionVectorsURP.ApplyMotionVectors();
            BasisDebug.Log($"SpaceWarp extension {(SMModuleMotionVectorsURP.SpaceWarpActive ? "present" : "absent")}; motion vectors {(SMModuleMotionVectorsURP.SpaceWarpActive ? "on" : "off")}", BasisDebug.LogTag.Device);
            BasisDebug.Log("SDK started successfully.");
            InputSystem.onDeviceChange += onDeviceChange;
            BasisDeviceManagement.OnDeviceManagementLoop += CheckTrackersPulse;
            m_Subsystem = XRGeneralSettings.Instance?.Manager?.activeLoader?.GetLoadedSubsystem<XRHandSubsystem>();
            if (m_Subsystem != null)
            {
                m_Subsystem.updatedHands += OnHandUpdate;
            }
            BasisCursorManagement.UnlockCursorBypassChecks("Forceful Unlock OPENXR");
        }

        private BasisOpenXRHandInput CreatePhysicalHandTracker(string device, string uniqueID, BasisBoneTrackedRole role)
        {
            BasisDebug.Log($"Creating physical hand tracker: {uniqueID}, Role: {role}");

            var gameObject = new GameObject(uniqueID)
            {
                transform = { parent = BasisLocalPlayer.Instance.transform }
            };

            var basisXRInput = gameObject.AddComponent<BasisOpenXRHandInput>();
            basisXRInput.ClassName = nameof(BasisOpenXRHandInput);
            basisXRInput.Initialize(uniqueID, device, nameof(BasisOpenXRManagement), true, role);

            BasisDeviceManagement.Instance.TryAdd(basisXRInput);
            controls.Add(basisXRInput);

            BasisDebug.Log($"Hand tracker created and added: {uniqueID}");
            return basisXRInput;
        }

        private void CreatePhysicalHeadTracker(string device, string uniqueID)
        {
            BasisDebug.Log($"Creating physical head tracker: {uniqueID}");

            var gameObject = new GameObject(uniqueID)
            {
                transform = { parent = BasisLocalPlayer.Instance.transform }
            };

            var basisXRInput = gameObject.AddComponent<BasisOpenXRHeadInput>();
            basisXRInput.ClassName = nameof(BasisOpenXRHeadInput);
            basisXRInput.Initialize(uniqueID, device, nameof(BasisOpenXRManagement), true);

            BasisDeviceManagement.Instance.TryAdd(basisXRInput);
            controls.Add(basisXRInput);

            BasisDebug.Log($"Head tracker created and added: {uniqueID}");
        }

        public void CreatePhysicalFullBodyTracker(InputDevice Device, string usage, string generalisedDeviceName, string uniqueDeviceIdentifier)
        {
            BasisDebug.Log($"Creating full body tracker: {uniqueDeviceIdentifier}");

            var gameObject = new GameObject(uniqueDeviceIdentifier)
            {
                transform = { parent = BasisLocalPlayer.Instance.transform }
            };

            var basisXRInput = gameObject.AddComponent<BasisOpenXRTracker>();
            basisXRInput.ClassName = nameof(BasisOpenXRTracker);
            basisXRInput.DeviceSerial = Device.description.serial ?? string.Empty;
            basisXRInput.Initialize(Device, usage, uniqueDeviceIdentifier, generalisedDeviceName, nameof(BasisOpenXRManagement));

            BasisDeviceManagement.Instance.TryAdd(basisXRInput);
            controls.Add(basisXRInput);
            OpenXRTrackers.Add(Device);

            BasisDebug.Log($"Full body tracker created and added: {uniqueDeviceIdentifier}");
        }

        public void DestroyPhysicalTrackedDevice(string id)
        {
            BasisDebug.Log($"Destroying tracked device with ID: {id}");
            BasisDeviceManagement.Instance.RemoveDevicesFrom(nameof(BasisOpenXRManagement), id);
        }
        private void OnHandUpdate(XRHandSubsystem subsystem, XRHandSubsystem.UpdateSuccessFlags flags, XRHandSubsystem.UpdateType type)
        {
            if (type != XRHandSubsystem.UpdateType.BeforeRender)
            {
                return;
            }
            LeftHand.OnHandUpdate(subsystem, flags, type);
            RightHand.OnHandUpdate(subsystem, flags, type);
        }

        private void onDeviceChange(InputDevice device, InputDeviceChange change)
        {
            if (IsSuspended) return;

            int count = InputSystem.devices.Count;
            for (int Index = 0; Index < count; Index++)
            {
                InputDevice internaldevice = InputSystem.devices[Index];
                TryAddTracker(internaldevice);
            }
            trackerscount = Trackers.Count;
            RefreshHandSerials();
        }

        /// <summary>
        /// The hand inputs are created action-based before any controller connects, so their
        /// serials can't be captured at creation — pick them up from the InputSystem device
        /// carrying the matching hand usage whenever the device set changes.
        /// </summary>
        private void RefreshHandSerials()
        {
            int count = InputSystem.devices.Count;
            for (int Index = 0; Index < count; Index++)
            {
                InputDevice inputDevice = InputSystem.devices[Index];
                string serial = inputDevice.description.serial;
                if (string.IsNullOrEmpty(serial))
                {
                    continue;
                }
                foreach (UnityEngine.InputSystem.Utilities.InternedString usage in inputDevice.usages)
                {
                    string name = usage.ToString();
                    if (name == "LeftHand" && LeftHand != null)
                    {
                        LeftHand.DeviceSerial = serial;
                    }
                    else if (name == "RightHand" && RightHand != null)
                    {
                        RightHand.DeviceSerial = serial;
                    }
                }
            }
        }
        public int trackerscount;
        public void CheckTrackersPulse()
        {
            if (IsSuspended) return;

            for (int Index = 0; Index < trackerscount; Index++)
            {
                BasisOpenxrDeviceTrackedInfo device = Trackers[Index];
                if (device.State.action == null) continue;
                device.IsActive = device.State.action.ReadValue<int>();
                if (device.IsActive != 0)
                {
                    if (OpenXRTrackers.Contains(device.device) == false)
                    {
                        OpenXRTrackers.Add(device.device);
                        CreatePhysicalFullBodyTracker(device.device, device.usage, $"{device.device.name}", $"{device.device.name} {device.device.deviceId} {device.usage}");
                    }
                }
                else
                {
                    if (OpenXRTrackers.Contains(device.device))
                    {
                        string RemoveID = $"{device.device.name} {device.device.deviceId} {device.usage}";
                        DestroyPhysicalTrackedDevice(RemoveID);
                        OpenXRTrackers.Remove(device.device);
                    }
                }
            }
        }
        /// <summary>
        /// updates the data set but needs the count manual reset  trackerscount = Trackers.Count;
        /// this is just another thing to stop redudent work
        /// </summary>
        /// <param name="device"></param>
        /// <param name="usage"></param>
        private void TrackerAdded(InputDevice device, string usage)
        {
            BasisOpenxrDeviceTrackedInfo DeviceTrackedInfo = new BasisOpenxrDeviceTrackedInfo
            {
                layoutName = device.GetType().Name
            };
            DeviceTrackedInfo.device = device;
            DeviceTrackedInfo.State = new InputActionProperty(new InputAction($"trackingState_{usage}", InputActionType.Value, $"<{DeviceTrackedInfo.layoutName}>{{{usage}}}/trackingState", expectedControlType: "Integer"));
            DeviceTrackedInfo.State.action.Enable();
            DeviceTrackedInfo.usage = usage;
            Trackers.Add(DeviceTrackedInfo);

        }
        private void TryAddTracker(InputDevice addedTracker)
        {
           // BasisDebug.Log($"Trying to add tracker: {addedTracker.name}, ID: {addedTracker.deviceId}");

            if (HTCOpenXRViveTracker.Contains(addedTracker.displayName) && !trackedDevices.ContainsKey(addedTracker.deviceId))
            {
                string matchedUsage = null;
                foreach (UnityEngine.InputSystem.Utilities.InternedString usage in addedTracker.usages)
                {
                    string name = usage.ToString();
                    if (CommonUsagesWeAccept.Contains(name))
                    {
                        matchedUsage = name;
                        break;
                    }
                }

                if (!string.IsNullOrEmpty(matchedUsage))
                {
                    trackedDevices.Add(addedTracker.deviceId, addedTracker);
                    BasisDebug.Log($"Tracker matched and added: {addedTracker.name}, Usage: {matchedUsage}");
                    TrackerAdded(addedTracker, matchedUsage);
                }
                else
                {
                    BasisDebug.LogError($"No matching usage found for tracker: {addedTracker.name}");
                }
            }
        }

        /// <summary>
        /// Per-frame simulation. Polls headset presence even during soft swap
        /// (XR runtime stays alive so the query still works).
        /// </summary>
        public override void Simulate()
        {
            if (!IsDeviceBooted) return;
            PollHMDPresence();
        }

        /// <summary>
        /// Cached list to avoid allocation every frame.
        /// </summary>
        private static readonly List<UnityEngine.XR.InputDevice> _headDevices = new List<UnityEngine.XR.InputDevice>();

        /// <summary>
        /// Queries the OpenXR subsystem for <see cref="UnityEngine.XR.CommonUsages.userPresence"/>
        /// on the head-mounted device. Reports into <see cref="BasisHMDPresence"/>.
        /// </summary>
        private void PollHMDPresence()
        {
            _headDevices.Clear();
            UnityEngine.XR.InputDevices.GetDevicesAtXRNode(UnityEngine.XR.XRNode.Head, _headDevices);
            int Count = _headDevices.Count;
            for (int i = 0; i < Count; i++)
            {
                if (_headDevices[i].TryGetFeatureValue(UnityEngine.XR.CommonUsages.userPresence, out bool present))
                {
                    ReportPresenceSource(HMDPresenceSource.UserPresenceFeature);
                    BasisHMDPresence.ReportPresence(present);
                    return;
                }
            }

            if (Count == 0)
            {
                ReportPresenceSource(HMDPresenceSource.NoHeadDevice);
                BasisHMDPresence.ReportPresence(false);
                return;
            }

            ReportPresenceSource(HMDPresenceSource.NoPresenceSignal);
        }

        private enum HMDPresenceSource
        {
            Unresolved,
            UserPresenceFeature,
            NoHeadDevice,
            NoPresenceSignal
        }

        private HMDPresenceSource PresenceSource = HMDPresenceSource.Unresolved;

        private void ReportPresenceSource(HMDPresenceSource Source)
        {
            if (PresenceSource == Source) return;
            PresenceSource = Source;
            switch (Source)
            {
                case HMDPresenceSource.UserPresenceFeature:
                    BasisDebug.Log("OpenXR: HMD presence read from the userPresence feature", BasisDebug.LogTag.Device);
                    break;
                case HMDPresenceSource.NoHeadDevice:
                    BasisDebug.Log("OpenXR: no head device is connected — reporting the headset as not worn", BasisDebug.LogTag.Device);
                    break;
                case HMDPresenceSource.NoPresenceSignal:
                    BasisDebug.Log("OpenXR: head device present but userPresence is unavailable — presence left unchanged, auto swap will not trigger", BasisDebug.LogTag.Device);
                    break;
            }
        }
    }
}
