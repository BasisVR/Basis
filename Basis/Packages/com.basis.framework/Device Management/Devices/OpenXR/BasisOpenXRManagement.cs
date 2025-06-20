using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Basis.Scripts.Device_Management.Devices.UnityInputSystem
{
    [Serializable]
    public partial class BasisOpenXRManagement : BasisBaseTypeManagement
    {
        [SerializeField]
        private List<BasisInput> controls = new List<BasisInput>();
        [SerializeField]
        public HashSet<InputDevice> OpenXRTrackers = new HashSet<InputDevice>();
        [SerializeField]
        public List<BasisOpenxrDeviceTrackedInfo> Trackers = new List<BasisOpenxrDeviceTrackedInfo>();
        [NonSerialized]
        private Dictionary<int, InputDevice> trackedDevices = new Dictionary<int, InputDevice>();
        public string[] HTCOpenXRViveTracker = new string[] { "HTC Vive Tracker (OpenXR)" };
        public string[] CommonUsagesWeAccept = new string[]
        {
            "Left Foot", "Right Foot", "Left Shoulder", "Right Shoulder",
            "Left Elbow", "Right Elbow", "Left Knee", "Right Knee", "Waist",
            "Chest", "Camera", "Keyboard"
        };
        private void CreatePhysicalHandTracker(string device, string uniqueID, BasisBoneTrackedRole role)
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

        public override void StopSDK()
        {
            BasisDebug.Log("Stopping SDK for BasisOpenXRManagement");

            foreach (var device in controls)
            {
                BasisDebug.Log($"Destroying control device: {device.UniqueDeviceIdentifier}");
                DestroyPhysicalTrackedDevice(device.UniqueDeviceIdentifier);
            }

            controls.Clear();
            OpenXRTrackers.Clear();

            BasisDebug.Log("SDK stopped and all resources cleaned up.");
            InputSystem.onDeviceChange -= onDeviceChange;
        }

        public override void BeginLoadSDK()
        {
            BasisDebug.Log("Begin loading SDK (no-op)");
        }

        public override void StartSDK()
        {
            BasisDebug.Log("Starting SDK for BasisOpenXRManagement");

            BasisDeviceManagement.Instance.SetCameraRenderState(true);

            CreatePhysicalHeadTracker("Head OPENXR", "Head OPENXR");
            CreatePhysicalHandTracker("Left Hand OPENXR", "Left Hand OPENXR", BasisBoneTrackedRole.LeftHand);
            CreatePhysicalHandTracker("Right Hand OPENXR", "Right Hand OPENXR", BasisBoneTrackedRole.RightHand);
            BasisDebug.Log("SDK started successfully.");
            InputSystem.onDeviceChange += onDeviceChange;
        }

        private void onDeviceChange(InputDevice device, InputDeviceChange change)
        {
            foreach (var internaldevice in InputSystem.devices)
            {
                TryAddTracker(internaldevice);
            }
        }

        public void Update()
        {
            CheckTrackersPulse();
        }
        public void CheckTrackersPulse()
        {
            int trackerscount = Trackers.Count;
            for (int Index = 0; Index < trackerscount; Index++)
            {
                BasisOpenxrDeviceTrackedInfo device = Trackers[Index];
                device.IsActive = device.State.action.ReadValue<int>();
                if (device.IsActive != 0)
                {
                    if (OpenXRTrackers.Contains(device.device) == false)
                    {
                        OpenXRTrackers.Add(device.device);
                        CreatePhysicalFullBodyTracker(device.device, device.usage, $"{device.device.name}", $"{device.device.name} {device.device.deviceId}");
                    }
                }
                else
                {
                    if (OpenXRTrackers.Contains(device.device))
                    {
                        string RemoveID = $"{device.device.name} {device.device.deviceId}";
                        DestroyPhysicalTrackedDevice(RemoveID);
                        OpenXRTrackers.Remove(device.device);
                    }
                }
            }
        }
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
        public override string Type()
        {
            return "OpenXRLoader";
        }
        private void TryAddTracker(InputDevice addedTracker)
        {
            BasisDebug.Log($"Trying to add tracker: {addedTracker.name}, ID: {addedTracker.deviceId}");

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
    }
}
