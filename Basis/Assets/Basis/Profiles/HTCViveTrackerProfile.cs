#if !UNITY_STANDALONE_LINUX

using System;
using System.Collections.Generic;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.Layouts;
using UnityEngine.InputSystem.XR;
using UnityEngine.Scripting;
#if UNITY_EDITOR
using UnityEditor;
#endif
using PoseControl = UnityEngine.InputSystem.XR.PoseControl;
namespace UnityEngine.XR.OpenXR.Features.Interactions
{
#if UNITY_EDITOR
    [UnityEditor.XR.OpenXR.Features.OpenXRFeature(
        UiName = "HTC Vive Tracker Profile",
        BuildTargetGroups = new[] { BuildTargetGroup.Standalone, BuildTargetGroup.WSA },
        Company = "Basis Framework",
        Desc = "Allows for mapping input to the HTC Vive Tracker interaction profile.",
        DocumentationLink = Constants.k_DocumentationManualURL,
        OpenxrExtensionStrings = "XR_HTCX_vive_tracker_interaction",
        Version = "0.0.2",
        Category = UnityEditor.XR.OpenXR.Features.FeatureCategory.Interaction,
        FeatureId = featureId)]
#endif
    public class HTCViveTrackerProfile : OpenXRInteractionFeature
    {
        public const string featureId = "com.basis.openxr.feature.input.htcvivetracker";
        public const string profile = "/interaction_profiles/htc/vive_tracker_htcx";
        private const string kDeviceLocalizedName = "HTC Vive Tracker OpenXR";

        public static class TrackerUserPaths
        {
            private const string BasePath = "/user/vive_tracker_htcx/role/";

            public const string LeftFoot = BasePath + "left_foot";
            public const string RightFoot = BasePath + "right_foot";
            public const string LeftShoulder = BasePath + "left_shoulder";
            public const string RightShoulder = BasePath + "right_shoulder";
            public const string LeftElbow = BasePath + "left_elbow";
            public const string RightElbow = BasePath + "right_elbow";
            public const string LeftKnee = BasePath + "left_knee";
            public const string RightKnee = BasePath + "right_knee";
            public const string Waist = BasePath + "waist";
            public const string Chest = BasePath + "chest";
        }

        public static class TrackerComponentPaths
        {
            public const string Grip = "/input/grip/pose";
        }

        [InputControlLayout(isGenericTypeOfDevice = true, displayName = "XR Tracker")]
        public class XRTracker : TrackedDevice { }

        [Preserve]
        [InputControlLayout(displayName = "HTC Vive Tracker (OpenXR)",
            commonUsages = new[] {
                "Left Foot", "Right Foot", "Left Shoulder", "Right Shoulder",
                "Left Elbow", "Right Elbow", "Left Knee", "Right Knee",
                "Waist", "Chest", "Camera", "Keyboard", "Generic"
            })]
        public class XRViveTracker : XRTracker
        {
            [Preserve, InputControl(offset = 0, aliases = new[] { "device", "gripPose" }, usage = "Device", noisy = true)]
            public PoseControl devicePose { get; private set; }

            [Preserve, InputControl(offset = 8, alias = "gripPosition", noisy = true)]
            public new Vector3Control devicePosition { get; private set; }

            [Preserve, InputControl(offset = 20, alias = "gripOrientation", noisy = true)]
            public new QuaternionControl deviceRotation { get; private set; }

            [Preserve, InputControl(offset = 60)]
            public new ButtonControl isTracked { get; private set; }

            [Preserve, InputControl(offset = 64)]
            public new IntegerControl trackingState { get; private set; }

            protected override void FinishSetup()
            {
                base.FinishSetup();

                devicePose = GetChildControl<PoseControl>("devicePose");
                devicePosition = GetChildControl<Vector3Control>("devicePosition");
                deviceRotation = GetChildControl<QuaternionControl>("deviceRotation");
                isTracked = GetChildControl<ButtonControl>("isTracked");
                trackingState = GetChildControl<IntegerControl>("trackingState");

                var deviceDescriptor = XRDeviceDescriptor.FromJson(description.capabilities);
                var characteristics = deviceDescriptor.characteristics;

                var trackerUsages = new Dictionary<InputDeviceCharacteristics, string>
                {
                    { (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerLeftFoot, "Left Foot" },
                    { (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerRightFoot, "Right Foot" },
                    { (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerLeftShoulder, "Left Shoulder" },
                    { (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerRightShoulder, "Right Shoulder" },
                    { (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerLeftElbow, "Left Elbow" },
                    { (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerRightElbow, "Right Elbow" },
                    { (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerLeftKnee, "Left Knee" },
                    { (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerRightKnee, "Right Knee" },
                    { (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerWaist, "Waist" },
                    { (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerChest, "Chest" }
                };

                foreach (var kvp in trackerUsages)
                {
                    if ((characteristics & kvp.Key) != 0)
                    {
                        InputSystem.InputSystem.SetDeviceUsage(this, kvp.Value);
                        break;
                    }
                }
            }
        }

        [Flags]
        public enum InputDeviceTrackerCharacteristics : uint
        {
            TrackerHandHeld = 0x1000u,
            TrackerLeftFoot = 0x2000u,
            TrackerRightFoot = 0x4000u,
            TrackerLeftShoulder = 0x8000u,
            TrackerRightShoulder = 0x10000u,
            TrackerLeftElbow = 0x20000u,
            TrackerRightElbow = 0x40000u,
            TrackerLeftKnee = 0x80000u,
            TrackerRightKnee = 0x100000u,
            TrackerWaist = 0x200000u,
            TrackerChest = 0x400000u,
            TrackerCamera = 0x800000u,
            TrackerKeyboard = 0x800000u
        }

        protected override void RegisterDeviceLayout()
        {
            InputSystem.InputSystem.RegisterLayout<XRTracker>();
            InputSystem.InputSystem.RegisterLayout(typeof(XRViveTracker),
                matches: new InputDeviceMatcher()
                    .WithInterface(XRUtilities.InterfaceMatchAnyVersion)
                    .WithProduct(kDeviceLocalizedName));
        }

        protected override void UnregisterDeviceLayout()
        {
            InputSystem.InputSystem.RemoveLayout(nameof(XRViveTracker));
            InputSystem.InputSystem.RemoveLayout(nameof(XRTracker));
        }

        protected override void RegisterActionMapsWithRuntime()
        {
            var trackerConfigs = new (InputDeviceTrackerCharacteristics characteristic, string userPath)[]
            {
                (InputDeviceTrackerCharacteristics.TrackerLeftFoot, TrackerUserPaths.LeftFoot),
                (InputDeviceTrackerCharacteristics.TrackerRightFoot, TrackerUserPaths.RightFoot),
                (InputDeviceTrackerCharacteristics.TrackerLeftShoulder, TrackerUserPaths.LeftShoulder),
                (InputDeviceTrackerCharacteristics.TrackerRightShoulder, TrackerUserPaths.RightShoulder),
                (InputDeviceTrackerCharacteristics.TrackerLeftElbow, TrackerUserPaths.LeftElbow),
                (InputDeviceTrackerCharacteristics.TrackerRightElbow, TrackerUserPaths.RightElbow),
                (InputDeviceTrackerCharacteristics.TrackerLeftKnee, TrackerUserPaths.LeftKnee),
                (InputDeviceTrackerCharacteristics.TrackerRightKnee, TrackerUserPaths.RightKnee),
                (InputDeviceTrackerCharacteristics.TrackerWaist, TrackerUserPaths.Waist),
                (InputDeviceTrackerCharacteristics.TrackerChest, TrackerUserPaths.Chest),
            };

            var deviceConfigs = new List<DeviceConfig>();

            foreach (var (characteristic, path) in trackerConfigs)
            {
                deviceConfigs.Add(new DeviceConfig
                {
                    characteristics = InputDeviceCharacteristics.TrackedDevice | (InputDeviceCharacteristics)characteristic,
                    userPath = path
                });
            }

            var actionMap = new ActionMapConfig
            {
                name = "htcvivetracker",
                localizedName = kDeviceLocalizedName,
                desiredInteractionProfile = profile,
                manufacturer = "HTC",
                serialNumber = "",
                deviceInfos = deviceConfigs,
                actions = new List<ActionConfig>
                {
                    new ActionConfig
                    {
                        name = "devicePose",
                        localizedName = "Device Pose",
                        type = ActionType.Pose,
                        usages = new List<string> { "Device" },
                        bindings = new List<ActionBinding>
                        {
                            new ActionBinding
                            {
                                interactionPath = TrackerComponentPaths.Grip,
                                interactionProfileName = profile
                            }
                        }
                    }
                }
            };

            AddActionMap(actionMap);
        }

        protected override bool OnInstanceCreate(ulong xrInstance)
        {
            var result = base.OnInstanceCreate(xrInstance);

            if (OpenXRRuntime.IsExtensionEnabled("XR_HTCX_vive_tracker_interaction"))
                Debug.Log("Basis HTC Vive Tracker Extension Enabled");
            else
                Debug.Log("Basis HTC Vive Tracker Extension Not Enabled");

            return result;
        }
    }
}

#endif
