using System.Collections.Generic;
using UnityEngine.Scripting;
using UnityEngine.XR.OpenXR.Input;
using UnityEngine.InputSystem.Layouts;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.XR;
using UnityEngine.InputSystem;
using System.Runtime.InteropServices;
using System;

#if UNITY_EDITOR
using UnityEditor;
#endif

using PoseControl = UnityEngine.InputSystem.XR.PoseControl;

namespace UnityEngine.XR.OpenXR.Features.Interactions
{
    /// <summary>
    /// This <see cref="OpenXRInteractionFeature"/> enables the use of HTC Vive Trackers interaction profiles in OpenXR.
    /// </summary>
#if UNITY_EDITOR
    [UnityEditor.XR.OpenXR.Features.OpenXRFeature(
        UiName = "HTC Vive Tracker Profile",
        BuildTargetGroups = new[] { BuildTargetGroup.Standalone, BuildTargetGroup.WSA },
        Company = "MASSIVE",
        Desc = "Allows for mapping input to the HTC Vive Tracker interaction profile.",
        DocumentationLink = Constants.k_DocumentationManualURL,
        OpenxrExtensionStrings = "XR_HTCX_vive_tracker_interaction",
        Version = "0.0.1",
        Category = UnityEditor.XR.OpenXR.Features.FeatureCategory.Interaction,
        FeatureId = featureId)]
#endif
    public class HTCViveTrackerProfile : OpenXRInteractionFeature
    {
        /// <summary>
        /// The feature id string. This is used to give the feature a well known id for reference.
        /// </summary>
        public const string featureId = "com.massive.openxr.feature.input.htcvivetracker";

        /// <summary>
        /// The interaction profile string used to reference the <a href="https://www.khronos.org/registry/OpenXR/specs/1.0/html/xrspec.html#:~:text=in%20this%20case.-,VIVE%20Tracker%20interaction%20profile,-Interaction%20profile%20path">HTC Vive Tracker</a>.
        /// </summary>
        public const string profile = "/interaction_profiles/htc/vive_tracker_htcx";


        private const string kDeviceLocalizedName = "HTC Vive Tracker OpenXR";

        /// <summary>
        /// OpenXR user path definitions for the tracker.
        /// </summary>
        public static class TrackerUserPaths
        {
            /// <summary>
            /// Path for user left foot
            /// </summary>
            public const string leftFoot = "/user/vive_tracker_htcx/role/left_foot";

            /// <summary>
            /// Path for user roght foot
            /// </summary>
            public const string rightFoot = "/user/vive_tracker_htcx/role/right_foot";

            /// <summary>
            /// Path for user left shoulder
            /// </summary>
            public const string leftShoulder = "/user/vive_tracker_htcx/role/left_shoulder";

            /// <summary>
            /// Path for user right shoulder
            /// </summary>
            public const string rightShoulder = "/user/vive_tracker_htcx/role/right_shoulder";

            /// <summary>
            /// Path for user left elbow
            /// </summary>
            public const string leftElbow = "/user/vive_tracker_htcx/role/left_elbow";

            /// <summary>
            /// Path for user right elbow
            /// </summary>
            public const string rightElbow = "/user/vive_tracker_htcx/role/right_elbow";

            /// <summary>
            /// Path for user left knee
            /// </summary>
            public const string leftKnee = "/user/vive_tracker_htcx/role/left_knee";

            /// <summary>
            /// Path for user right knee
            /// </summary>
            public const string rightKnee = "/user/vive_tracker_htcx/role/right_knee";

            /// <summary>
            /// Path for user waist
            /// </summary>
            public const string waist = "/user/vive_tracker_htcx/role/waist";

            /// <summary>
            /// Path for user chest
            /// </summary>
            public const string chest = "/user/vive_tracker_htcx/role/chest";

            /// <summary>
            /// Path for user custom camera
            /// </summary>
            public const string camera = "/user/vive_tracker_htcx/role/camera";

            /// <summary>
            /// Path for user keyboard
            /// </summary>
            public const string keyboard = "/user/vive_tracker_htcx/role/keyboard";
        }

        /// <summary>
        /// OpenXR component path definitions for the tracker.
        /// </summary>
        public static class TrackerComponentPaths
        {
            /// <summary>
            /// Constant for a pose interaction binding '.../input/grip/pose' OpenXR Input Binding. Used by input subsystem to bind actions to physical inputs.
            /// </summary>
            public const string grip = "/input/grip/pose";
        }

        /// <summary>
        /// A base Input System device for XR Trackers, based off the TrackedDevice
        /// </summary>
        [InputControlLayout(isGenericTypeOfDevice = true, displayName = "XR Tracker")]
        public class XRTracker : TrackedDevice
        {
        }

        /// <summary>
        /// An Input System device based off the <a href="https://www.khronos.org/registry/OpenXR/specs/1.0/html/xrspec.html#_htc_vive_controller_profile">HTC Vive Tracker</a>.
        /// </summary>
        [Preserve, InputControlLayout(displayName = "HTC Vive Tracker (OpenXR)", commonUsages = new[] { "Left Foot", "Right Foot", "Left Shoulder", "Right Shoulder", "Left Elbow", "Right Elbow", "Left Knee", "Right Knee", "Waist", "Chest", "Camera", "Keyboard", "Generic" })]
        public class XRViveTracker : XRTracker
        {
            /// <summary>
            /// A <see cref="PoseControl"/> that represents information from the <see cref="HTCViveTrackerProfile.grip"/> OpenXR binding.
            /// </summary>
            [Preserve, InputControl(offset = 0, aliases = new[] { "device", "gripPose" }, usage = "Device", noisy = true)]
            public PoseControl devicePose { get; private set; }

            /// <summary>
            /// A [Vector3Control](xref:UnityEngine.InputSystem.Controls.Vector3Control) required for back compatibility with the XRSDK layouts. This is the device position. For the Oculus Touch device, this is both the grip and the pointer position. This value is equivalent to mapping devicePose/position.
            /// </summary>
            [Preserve, InputControl(offset = 8, alias = "gripPosition", noisy = true)]
            new public Vector3Control devicePosition { get; private set; }

            /// <summary>
            /// A [QuaternionControl](xref:UnityEngine.InputSystem.Controls.QuaternionControl) required for backwards compatibility with the XRSDK layouts. This is the device orientation. For the Oculus Touch device, this is both the grip and the pointer rotation. This value is equivalent to mapping devicePose/rotation.
            /// </summary>
            [Preserve, InputControl(offset = 20, alias = "gripOrientation", noisy = true)]
            new public QuaternionControl deviceRotation { get; private set; }

            [Preserve, InputControl(offset = 60)]
            new public ButtonControl isTracked { get; private set; }

            [Preserve, InputControl(offset = 64)]
            new public IntegerControl trackingState { get; private set; }

            /// <inheritdoc cref="OpenXRDevice"/>
            protected override void FinishSetup()
            {
                base.FinishSetup();
                devicePose = GetChildControl<PoseControl>("devicePose");
                devicePosition = GetChildControl<Vector3Control>("devicePosition");
                deviceRotation = GetChildControl<QuaternionControl>("deviceRotation");
                isTracked = GetChildControl<ButtonControl>("isTracked");
                trackingState = GetChildControl<IntegerControl>("trackingState");

                var capabilities = description.capabilities;
                XRDeviceDescriptor deviceDescriptor = XRDeviceDescriptor.FromJson(capabilities);

                if ((deviceDescriptor.characteristics & (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerLeftFoot) != 0)
                    InputSystem.InputSystem.SetDeviceUsage(this, "Left Foot");
                else if ((deviceDescriptor.characteristics & (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerRightFoot) != 0)
                    InputSystem.InputSystem.SetDeviceUsage(this, "Right Foot");
                else if ((deviceDescriptor.characteristics & (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerLeftShoulder) != 0)
                    InputSystem.InputSystem.SetDeviceUsage(this, "Left Shoulder");
                else if ((deviceDescriptor.characteristics & (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerRightShoulder) != 0)
                    InputSystem.InputSystem.SetDeviceUsage(this, "Right Shoulder");
                else if ((deviceDescriptor.characteristics & (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerLeftElbow) != 0)
                    InputSystem.InputSystem.SetDeviceUsage(this, "Left Elbow");
                else if ((deviceDescriptor.characteristics & (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerRightElbow) != 0)
                    InputSystem.InputSystem.SetDeviceUsage(this, "Right Elbow");
                else if ((deviceDescriptor.characteristics & (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerLeftKnee) != 0)
                    InputSystem.InputSystem.SetDeviceUsage(this, "Left Knee");
                else if ((deviceDescriptor.characteristics & (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerRightKnee) != 0)
                    InputSystem.InputSystem.SetDeviceUsage(this, "Right Knee");
                else if ((deviceDescriptor.characteristics & (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerWaist) != 0)
                    InputSystem.InputSystem.SetDeviceUsage(this, "Waist");
                else if ((deviceDescriptor.characteristics & (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerChest) != 0)
                    InputSystem.InputSystem.SetDeviceUsage(this, "Chest");
                else if ((deviceDescriptor.characteristics & (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerCamera) != 0)
                    InputSystem.InputSystem.SetDeviceUsage(this, "Camera");
                else if ((deviceDescriptor.characteristics & (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerKeyboard) != 0)
                    InputSystem.InputSystem.SetDeviceUsage(this, "Keyboard");

                Debug.Log("Device added");
            }
        }

        /// <summary>
        /// Registers the <see cref="ViveTracker"/> layout with the Input System.
        /// </summary>
        protected override void RegisterDeviceLayout()
        {
            InputSystem.InputSystem.RegisterLayout<XRTracker>();

            InputSystem.InputSystem.RegisterLayout(typeof(XRViveTracker),
                        matches: new InputDeviceMatcher()
                        .WithInterface(XRUtilities.InterfaceMatchAnyVersion)
                        .WithProduct(kDeviceLocalizedName));
        }

        /// <summary>
        /// Removes the <see cref="ViveTracker"/> layout from the Input System.
        /// </summary>
        protected override void UnregisterDeviceLayout()
        {
            InputSystem.InputSystem.RemoveLayout(nameof(XRViveTracker));
            InputSystem.InputSystem.RemoveLayout(nameof(XRTracker));
        }

        //
        // Summary:
        //     A set of bit flags describing XR.InputDevice characteristics.
        //"HandHeld" "Left Foot", "Right Foot", "Left Shoulder", "Right Shoulder", "Left Elbow", "Right Elbow", "Left Knee", "Right Knee", "Waist", "Chest", "Camera", "Keyboard"
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
            TrackerKeyboard = 0x800000u,
        }

        /// <inheritdoc/>
        protected override void RegisterActionMapsWithRuntime()
        {
            ActionMapConfig actionMap = new ActionMapConfig()
            {
                name = "htcvivetracker",
                localizedName = kDeviceLocalizedName,
                desiredInteractionProfile = profile,
                manufacturer = "HTC",
                serialNumber = "",
                deviceInfos = new List<DeviceConfig>()
                {
                    new DeviceConfig()
                    {
                        characteristics = (InputDeviceCharacteristics.TrackedDevice) | (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerLeftFoot,
                        userPath = TrackerUserPaths.leftFoot
                    },
                    new DeviceConfig()
                    {
                        characteristics = (InputDeviceCharacteristics.TrackedDevice) | (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerRightFoot,
                        userPath = TrackerUserPaths.rightFoot
                    },
                    new DeviceConfig()
                    {
                        characteristics = (InputDeviceCharacteristics.TrackedDevice) | (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerLeftShoulder,
                        userPath = TrackerUserPaths.leftShoulder
                    },
                    new DeviceConfig()
                    {
                        characteristics = (InputDeviceCharacteristics.TrackedDevice) | (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerRightShoulder,
                        userPath = TrackerUserPaths.rightShoulder
                    },
                    new DeviceConfig()
                    {
                        characteristics = (InputDeviceCharacteristics.TrackedDevice) | (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerLeftElbow,
                        userPath = TrackerUserPaths.leftElbow
                    },
                    new DeviceConfig()
                    {
                        characteristics = (InputDeviceCharacteristics.TrackedDevice) | (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerRightElbow,
                        userPath = TrackerUserPaths.rightElbow
                    },
                    new DeviceConfig()
                    {
                        characteristics = (InputDeviceCharacteristics.TrackedDevice) | (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerLeftKnee,
                        userPath = TrackerUserPaths.leftKnee
                    },
                    new DeviceConfig()
                    {
                        characteristics = (InputDeviceCharacteristics.TrackedDevice) | (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerRightKnee,
                        userPath = TrackerUserPaths.rightKnee
                    },
                    new DeviceConfig()
                    {
                        characteristics = (InputDeviceCharacteristics.TrackedDevice) | (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerWaist,
                        userPath = TrackerUserPaths.waist
                    },
                    new DeviceConfig()
                    {
                        characteristics = (InputDeviceCharacteristics.TrackedDevice) | (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerChest,
                        userPath = TrackerUserPaths.chest
                    },
                    new DeviceConfig()
                    {
                        characteristics = (InputDeviceCharacteristics.TrackedDevice) | (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerCamera,
                        userPath = TrackerUserPaths.camera
                    },
                    new DeviceConfig()
                    {
                        characteristics = (InputDeviceCharacteristics.TrackedDevice) | (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerKeyboard,
                        userPath = TrackerUserPaths.keyboard
                    },
                },
                actions = new List<ActionConfig>()
                {
                     new ActionConfig()
                    {
                        name = "devicePose",
                        localizedName = "Device Pose",
                        type = ActionType.Pose,
                        usages = new List<string>()
                        {
                            "Device",
                        },
                        bindings = new List<ActionBinding>()
                        {
                            new ActionBinding()
                            {
                                interactionPath = TrackerComponentPaths.grip,
                                interactionProfileName = profile,
                            }
                        }
                    },
                }
            };

            AddActionMap(actionMap);
        }
        protected override bool OnInstanceCreate(ulong xrInstance)
        {
            bool res = base.OnInstanceCreate(xrInstance);

            if (OpenXRRuntime.IsExtensionEnabled("XR_HTCX_vive_tracker_interaction"))
            {
                Debug.Log("HTC Vive Tracker Extension Enabled");
            }
            else
            {
                Debug.Log("HTC Vive Tracker Extension Not Enabled");
            }
            return res;
        }
    }
}
