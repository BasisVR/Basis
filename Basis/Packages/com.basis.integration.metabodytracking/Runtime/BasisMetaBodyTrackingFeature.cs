using Unity.Scripting.LifecycleManagement;
#if BASIS_FRAMEWORK_EXISTS
using System;
using System.Runtime.InteropServices;
using AOT;
using UnityEngine;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.XR.OpenXR.Features;
#endif

namespace Basis.Integration.MetaBodyTracking
{
    /// <summary>
    /// Body tracking through Meta's OpenXR extensions. <c>XR_FB_body_tracking</c> gives the upper
    /// body (hips through both hands); <c>XR_META_body_tracking_full_body</c> extends the same joint
    /// indices with legs and feet; <c>XR_META_body_tracking_fidelity</c> asks for the camera driven
    /// high fidelity solve rather than the cheap kinematic one; and
    /// <c>XR_META_body_tracking_calibration</c> lets us hand the runtime the player's real height
    /// instead of letting it guess. Every one of them is optional — whatever the runtime does not
    /// offer is skipped, and with none of them the whole feature stays inert.
    ///
    /// This class owns the runtime side only: it creates the body tracker, locates the joints once
    /// per frame and hands them out in Unity space. Turning joints into Basis trackers is
    /// <see cref="BasisMetaBodyTrackerSource"/>'s job.
    /// </summary>
#if UNITY_EDITOR
    [AutoStaticsCleanup]
    [OpenXRFeature(UiName = "Basis Meta Body Tracking",
        BuildTargetGroups = new[] { BuildTargetGroup.Standalone, BuildTargetGroup.Android },
        Company = "Basis",
        Desc = "Meta body tracking (hips, chest, elbows, knees, feet) as Basis trackers.",
        OpenxrExtensionStrings = ExtensionStrings,
        Version = "1.0.0",
        FeatureId = FeatureIdString)]
#endif
    public partial class BasisMetaBodyTrackingFeature : OpenXRFeature
    {
        public const string FeatureIdString = "com.basis.openxr.feature.metabodytracking";

        public const string ExtensionBodyTracking = "XR_FB_body_tracking";
        public const string ExtensionFullBody = "XR_META_body_tracking_full_body";
        public const string ExtensionFidelity = "XR_META_body_tracking_fidelity";
        public const string ExtensionCalibration = "XR_META_body_tracking_calibration";

        /// <summary>Space separated list handed to the loader; missing ones are logged, not fatal.</summary>
        public const string ExtensionStrings =
            ExtensionBodyTracking + " " + ExtensionFullBody + " " + ExtensionFidelity + " " + ExtensionCalibration;

        private const string Tag = "[BasisMetaBodyTracking]";

        private const uint XR_TYPE_BODY_TRACKER_CREATE_INFO_FB = 1000076001;
        private const uint XR_TYPE_BODY_JOINTS_LOCATE_INFO_FB = 1000076002;
        private const uint XR_TYPE_BODY_JOINT_LOCATIONS_FB = 1000076005;
        private const uint XR_TYPE_BODY_TRACKING_CALIBRATION_INFO_META = 1000283002;

        private const uint XR_BODY_JOINT_SET_DEFAULT_FB = 0;
        private const uint XR_BODY_JOINT_SET_FULL_BODY_META = 1000274000;

        private const uint XR_BODY_TRACKING_FIDELITY_LOW_META = 1;
        private const uint XR_BODY_TRACKING_FIDELITY_HIGH_META = 2;

        private const ulong XR_SPACE_LOCATION_ORIENTATION_VALID_BIT = 0x00000001;
        private const ulong XR_SPACE_LOCATION_POSITION_VALID_BIT = 0x00000002;
        private const int XR_ERROR_RUNTIME_FAILURE = -2;

        /// <summary>Offset of XrFrameState.predictedDisplayTime on a 64 bit ABI (type, pad, next, time).</summary>
        private const int FrameStatePredictedDisplayTimeOffset = 16;

        /// <summary>Expected sizeof(XrBodyJointLocationFB): ulong flags plus XrPosef, padded to 8.</summary>
        private const int ExpectedJointStride = 40;

        /// <summary>Height change worth re-suggesting to the runtime, in metres.</summary>
        private const float HeightChangeEpsilon = 0.005f;

        // ---- Runtime state ----

        /// <summary>True once the runtime reported XR_FB_body_tracking enabled for this session.</summary>
        public static bool IsSupported { get; private set; }
        public static bool SupportsFullBody { get; private set; }
        public static bool SupportsFidelity { get; private set; }
        public static bool SupportsCalibration { get; private set; }

        /// <summary>Joint set the live tracker was created with; None when no tracker exists.</summary>
        public static BasisMetaBodyJointSet ActiveJointSet { get; private set; }

        /// <summary>True while the runtime says it is actually producing a body pose this frame.</summary>
        public static bool IsBodyActive { get; private set; }

        /// <summary>The runtime's own 0..1 confidence in the current body pose.</summary>
        public static float BodyConfidence { get; private set; }

        /// <summary>Last result code from xrLocateBodyJointsFB (0 == success), for status display.</summary>
        public static int LastLocateResult { get; private set; }

        private static ulong s_Session;
        private static ulong s_AppSpace;
        private static ulong s_BodyTracker;
        private static long s_PredictedDisplayTime;

        private static float s_SuggestedHeight;

        [NoAutoStaticsCleanup] private static IntPtr s_JointBuffer;
        private static int s_JointCapacity;
        private static int s_JointStride;
        private static uint s_LocatedJointCount;
        private static int s_LocatedFrame = -1;

        // ---- Native types ----

        [StructLayout(LayoutKind.Sequential)]
        private struct XrVector3f
        {
            public float x;
            public float y;
            public float z;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XrQuaternionf
        {
            public float x;
            public float y;
            public float z;
            public float w;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XrPosef
        {
            public XrQuaternionf orientation;
            public XrVector3f position;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XrBodyJointLocationFB
        {
            public ulong locationFlags;
            public XrPosef pose;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XrBodyTrackerCreateInfoFB
        {
            public uint type;
            public IntPtr next;
            public uint bodyJointSet;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XrBodyJointsLocateInfoFB
        {
            public uint type;
            public IntPtr next;
            public ulong baseSpace;
            public long time;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XrBodyJointLocationsFB
        {
            public uint type;
            public IntPtr next;
            public uint isActive;
            public float confidence;
            public uint jointCount;
            public IntPtr jointLocations;
            public uint skeletonChangedCount;
            public long time;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XrBodyTrackingCalibrationInfoMETA
        {
            public uint type;
            public IntPtr next;
            public float bodyHeight;
        }

        private delegate int Type_xrGetInstanceProcAddr(ulong instance, string name, out IntPtr function);
        private delegate int Type_xrWaitFrame(ulong session, IntPtr frameWaitInfo, IntPtr frameState);
        private delegate int Type_xrCreateBodyTrackerFB(ulong session, ref XrBodyTrackerCreateInfoFB createInfo, out ulong bodyTracker);
        private delegate int Type_xrDestroyBodyTrackerFB(ulong bodyTracker);
        private delegate int Type_xrLocateBodyJointsFB(ulong bodyTracker, ref XrBodyJointsLocateInfoFB locateInfo, ref XrBodyJointLocationsFB locations);
        private delegate int Type_xrRequestBodyTrackingFidelityMETA(ulong bodyTracker, uint fidelity);
        private delegate int Type_xrSuggestBodyTrackingCalibrationOverrideMETA(ulong bodyTracker, ref XrBodyTrackingCalibrationInfoMETA calibrationInfo);
        private delegate int Type_xrResetBodyTrackingCalibrationMETA(ulong bodyTracker);

        private static Type_xrGetInstanceProcAddr d_getProc;
        [NoAutoStaticsCleanup] private static Type_xrGetInstanceProcAddr d_originalGetProc;
        [NoAutoStaticsCleanup] private static Type_xrWaitFrame d_originalWaitFrame;
        private static Type_xrCreateBodyTrackerFB d_createBodyTracker;
        private static Type_xrDestroyBodyTrackerFB d_destroyBodyTracker;
        private static Type_xrLocateBodyJointsFB d_locateBodyJoints;
        private static Type_xrRequestBodyTrackingFidelityMETA d_requestFidelity;
        private static Type_xrSuggestBodyTrackingCalibrationOverrideMETA d_suggestCalibration;
        private static Type_xrResetBodyTrackingCalibrationMETA d_resetCalibration;

        [NoAutoStaticsCleanup] private static readonly Type_xrGetInstanceProcAddr s_getProcHook = HookGetProc;
        [NoAutoStaticsCleanup] private static readonly Type_xrWaitFrame s_waitFrameHook = HookWaitFrame;

        // ---- Instance and session lifecycle ----

        protected override IntPtr HookGetInstanceProcAddr(IntPtr func)
        {
            d_originalGetProc = Marshal.GetDelegateForFunctionPointer<Type_xrGetInstanceProcAddr>(func);
            return Marshal.GetFunctionPointerForDelegate(s_getProcHook);
        }

        /// <summary>
        /// Wraps xrWaitFrame purely to keep the predicted display time: the joints have to be located
        /// for the frame that is about to be shown, and Unity exposes no other way to read it.
        /// </summary>
        [MonoPInvokeCallback(typeof(Type_xrGetInstanceProcAddr))]
        private static int HookGetProc(ulong instance, string name, out IntPtr function)
        {
            function = IntPtr.Zero;
            int result = XR_ERROR_RUNTIME_FAILURE;
            try
            {
                result = d_originalGetProc.Invoke(instance, name, out function);
                if (name != "xrWaitFrame")
                {
                    return result;
                }
                if (result != 0 || function == IntPtr.Zero)
                {
                    function = IntPtr.Zero;
                    return result;
                }
                d_originalWaitFrame = Marshal.GetDelegateForFunctionPointer<Type_xrWaitFrame>(function);
                function = Marshal.GetFunctionPointerForDelegate(s_waitFrameHook);
                return 0;
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"{Tag} xrGetInstanceProcAddr hook failed for {name}: {e}", BasisDebug.LogTag.Device);
                return result;
            }
        }

        [MonoPInvokeCallback(typeof(Type_xrWaitFrame))]
        private static int HookWaitFrame(ulong session, IntPtr frameWaitInfo, IntPtr frameState)
        {
            int result = XR_ERROR_RUNTIME_FAILURE;
            try
            {
                result = d_originalWaitFrame != null ? d_originalWaitFrame.Invoke(session, frameWaitInfo, frameState) : 0;
                if (result == 0 && frameState != IntPtr.Zero)
                {
                    // xrWaitFrame runs off the render thread; a single aligned 64 bit store is all this is.
                    System.Threading.Interlocked.Exchange(ref s_PredictedDisplayTime,
                        Marshal.ReadInt64(frameState, FrameStatePredictedDisplayTimeOffset));
                }
                return result;
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"{Tag} xrWaitFrame hook failed: {e}", BasisDebug.LogTag.Device);
                return result;
            }
        }

        protected override bool OnInstanceCreate(ulong instance)
        {
            IsSupported = OpenXRRuntime.IsExtensionEnabled(ExtensionBodyTracking);
            SupportsFullBody = IsSupported && OpenXRRuntime.IsExtensionEnabled(ExtensionFullBody);
            SupportsFidelity = IsSupported && OpenXRRuntime.IsExtensionEnabled(ExtensionFidelity);
            SupportsCalibration = IsSupported && OpenXRRuntime.IsExtensionEnabled(ExtensionCalibration);

            if (!IsSupported)
            {
                BasisDebug.Log($"{Tag} {ExtensionBodyTracking} not enabled by this runtime; body tracking unavailable.", BasisDebug.LogTag.Device);
                return base.OnInstanceCreate(instance);
            }

            BasisDebug.Log($"{Tag} extensions: body={IsSupported} fullBody={SupportsFullBody} fidelity={SupportsFidelity} calibration={SupportsCalibration}", BasisDebug.LogTag.Device);

            d_getProc = Marshal.GetDelegateForFunctionPointer<Type_xrGetInstanceProcAddr>(xrGetInstanceProcAddr);
            d_createBodyTracker = Load<Type_xrCreateBodyTrackerFB>(instance, "xrCreateBodyTrackerFB");
            d_destroyBodyTracker = Load<Type_xrDestroyBodyTrackerFB>(instance, "xrDestroyBodyTrackerFB");
            d_locateBodyJoints = Load<Type_xrLocateBodyJointsFB>(instance, "xrLocateBodyJointsFB");
            if (SupportsFidelity)
            {
                d_requestFidelity = Load<Type_xrRequestBodyTrackingFidelityMETA>(instance, "xrRequestBodyTrackingFidelityMETA");
            }
            if (SupportsCalibration)
            {
                d_suggestCalibration = Load<Type_xrSuggestBodyTrackingCalibrationOverrideMETA>(instance, "xrSuggestBodyTrackingCalibrationOverrideMETA");
                d_resetCalibration = Load<Type_xrResetBodyTrackingCalibrationMETA>(instance, "xrResetBodyTrackingCalibrationMETA");
            }
            return base.OnInstanceCreate(instance);
        }

        private static T Load<T>(ulong instance, string name) where T : Delegate
        {
            if (d_getProc.Invoke(instance, name, out IntPtr p) == 0 && p != IntPtr.Zero)
            {
                return Marshal.GetDelegateForFunctionPointer<T>(p);
            }
            BasisDebug.LogError($"{Tag} failed to resolve {name}", BasisDebug.LogTag.Device);
            return null;
        }

        protected override void OnSessionCreate(ulong session)
        {
            s_Session = session;
        }

        protected override void OnAppSpaceChange(ulong space)
        {
            s_AppSpace = space;
        }

        protected override void OnSessionBegin(ulong session)
        {
            s_Session = session;
            ulong space = GetCurrentAppSpace();
            if (space != 0)
            {
                s_AppSpace = space;
            }
            TryCreateTracker();
        }

        protected override void OnSessionEnd(ulong session)
        {
            DestroyTracker();
        }

        protected override void OnSessionDestroy(ulong session)
        {
            DestroyTracker();
            s_Session = 0;
            s_AppSpace = 0;
        }

        protected override void OnInstanceDestroy(ulong instance)
        {
            IsSupported = false;
            SupportsFullBody = false;
            SupportsFidelity = false;
            SupportsCalibration = false;
        }

        // ---- Tracker lifetime ----

        private static void TryCreateTracker()
        {
            if (!IsSupported || s_BodyTracker != 0 || d_createBodyTracker == null || s_Session == 0)
            {
                return;
            }

            // Full body first: it uses the same joint indices, so falling back to the default set
            // only loses the legs. A runtime that advertises the extension can still refuse the
            // joint set, which is what the retry is for.
            if (SupportsFullBody && Create(XR_BODY_JOINT_SET_FULL_BODY_META, BasisMetaBodyJointSet.FullBody))
            {
                return;
            }
            Create(XR_BODY_JOINT_SET_DEFAULT_FB, BasisMetaBodyJointSet.UpperBody);
        }

        private static bool Create(uint jointSet, BasisMetaBodyJointSet described)
        {
            XrBodyTrackerCreateInfoFB createInfo = new XrBodyTrackerCreateInfoFB
            {
                type = XR_TYPE_BODY_TRACKER_CREATE_INFO_FB,
                next = IntPtr.Zero,
                bodyJointSet = jointSet,
            };
            int result = d_createBodyTracker.Invoke(s_Session, ref createInfo, out ulong tracker);
            if (result != 0 || tracker == 0)
            {
                BasisDebug.Log($"{Tag} xrCreateBodyTrackerFB({described}) returned {result}", BasisDebug.LogTag.Device);
                return false;
            }

            s_BodyTracker = tracker;
            ActiveJointSet = described;
            EnsureJointBuffer(described == BasisMetaBodyJointSet.FullBody
                ? BasisMetaBodyJointCount.FullBody
                : BasisMetaBodyJointCount.Default);

            BasisDebug.Log($"{Tag} body tracker created ({described}, {s_JointCapacity} joints, {s_JointStride}B stride).", BasisDebug.LogTag.Device);
            ApplyFidelity();
            ApplyHeightOverride();
            return true;
        }

        private static void DestroyTracker()
        {
            if (s_BodyTracker != 0)
            {
                d_destroyBodyTracker?.Invoke(s_BodyTracker);
                s_BodyTracker = 0;
            }
            ActiveJointSet = BasisMetaBodyJointSet.None;
            IsBodyActive = false;
            BodyConfidence = 0f;
            LastLocateResult = 0;
            s_LocatedJointCount = 0;
            s_LocatedFrame = -1;
            s_SuggestedHeight = 0f;
            System.Threading.Interlocked.Exchange(ref s_PredictedDisplayTime, 0);
            FreeJointBuffer();
        }

        private static void EnsureJointBuffer(int jointCount)
        {
            s_JointStride = Marshal.SizeOf<XrBodyJointLocationFB>();
            if (s_JointStride != ExpectedJointStride)
            {
                // Nothing to be done about it here, but the surprise would silently shear every joint.
                BasisDebug.LogError($"{Tag} unexpected XrBodyJointLocationFB stride {s_JointStride} (expected {ExpectedJointStride}).", BasisDebug.LogTag.Device);
            }
            if (s_JointBuffer != IntPtr.Zero && jointCount <= s_JointCapacity)
            {
                return;
            }
            FreeJointBuffer();
            s_JointCapacity = jointCount;
            s_JointBuffer = Marshal.AllocHGlobal(s_JointStride * jointCount);
        }

        private static void FreeJointBuffer()
        {
            if (s_JointBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(s_JointBuffer);
                s_JointBuffer = IntPtr.Zero;
            }
            s_JointCapacity = 0;
        }

        // ---- Per frame ----

        /// <summary>
        /// Locates every joint once per frame. Cheap to call repeatedly — the second and later calls
        /// in a frame return immediately, so each device can just ask before reading its own joint.
        /// </summary>
        public static void EnsureLocated()
        {
            if (s_BodyTracker == 0 || d_locateBodyJoints == null || s_JointBuffer == IntPtr.Zero)
            {
                return;
            }
            if (s_LocatedFrame == Time.frameCount)
            {
                return;
            }
            s_LocatedFrame = Time.frameCount;

            long displayTime = System.Threading.Interlocked.Read(ref s_PredictedDisplayTime);
            if (s_AppSpace == 0 || displayTime == 0)
            {
                IsBodyActive = false;
                return;
            }

            XrBodyJointsLocateInfoFB locateInfo = new XrBodyJointsLocateInfoFB
            {
                type = XR_TYPE_BODY_JOINTS_LOCATE_INFO_FB,
                next = IntPtr.Zero,
                baseSpace = s_AppSpace,
                time = displayTime,
            };
            XrBodyJointLocationsFB locations = new XrBodyJointLocationsFB
            {
                type = XR_TYPE_BODY_JOINT_LOCATIONS_FB,
                next = IntPtr.Zero,
                jointCount = (uint)s_JointCapacity,
                jointLocations = s_JointBuffer,
            };

            LastLocateResult = d_locateBodyJoints.Invoke(s_BodyTracker, ref locateInfo, ref locations);
            if (LastLocateResult != 0)
            {
                IsBodyActive = false;
                s_LocatedJointCount = 0;
                return;
            }

            IsBodyActive = locations.isActive != 0;
            BodyConfidence = locations.confidence;
            s_LocatedJointCount = IsBodyActive ? Math.Min(locations.jointCount, (uint)s_JointCapacity) : 0;
        }

        /// <summary>
        /// Reads one located joint, converted from OpenXR's right handed frame into Unity's. Returns
        /// false when the body is not being tracked or that joint's pose is not valid this frame.
        /// </summary>
        public static bool TryGetJoint(BasisMetaBodyJoint joint, out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = Quaternion.identity;

            int index = (int)joint;
            if (!IsBodyActive || index < 0 || index >= s_LocatedJointCount)
            {
                return false;
            }

            XrBodyJointLocationFB location = Marshal.PtrToStructure<XrBodyJointLocationFB>(
                IntPtr.Add(s_JointBuffer, index * s_JointStride));
            const ulong required = XR_SPACE_LOCATION_POSITION_VALID_BIT | XR_SPACE_LOCATION_ORIENTATION_VALID_BIT;
            if ((location.locationFlags & required) != required)
            {
                return false;
            }

            position = new Vector3(location.pose.position.x, location.pose.position.y, -location.pose.position.z);
            rotation = new Quaternion(-location.pose.orientation.x, -location.pose.orientation.y, location.pose.orientation.z, location.pose.orientation.w);
            return true;
        }

        // ---- Runtime knobs ----

        /// <summary>
        /// Asks the runtime for the high fidelity (camera driven) solve when the setting wants it,
        /// low otherwise. Safe to call at any time; a runtime without the extension ignores it.
        /// </summary>
        public static void ApplyFidelity()
        {
            if (s_BodyTracker == 0 || d_requestFidelity == null)
            {
                return;
            }
            bool high = BasisMetaBodyTrackingSettings.HighFidelity.RawValue;
            int result = d_requestFidelity.Invoke(s_BodyTracker, high ? XR_BODY_TRACKING_FIDELITY_HIGH_META : XR_BODY_TRACKING_FIDELITY_LOW_META);
            BasisDebug.Log($"{Tag} requested {(high ? "high" : "low")} fidelity, returned {result}", BasisDebug.LogTag.Device);
        }

        /// <summary>
        /// Hands the runtime the player's real standing height so its body solve is scaled to them
        /// rather than to its own guess. Basis measures eye height, so it is converted with the same
        /// eye to crown ratio the rest of the framework uses. Does nothing until Basis has a genuine
        /// measurement; clears any previous suggestion when the setting is off. Cheap to call every
        /// scan — only an actual change in the measured height reaches the runtime.
        /// </summary>
        public static void ApplyHeightOverride()
        {
            if (s_BodyTracker == 0 || d_suggestCalibration == null)
            {
                return;
            }
            if (!BasisMetaBodyTrackingSettings.ApplyPlayerHeight.RawValue)
            {
                ResetHeightOverride();
                return;
            }
            if (!BasisHeightDriver.HasGenuinePlayerEyeHeight || BasisHeightDriver.PlayerEyeHeight <= 0f)
            {
                return;
            }

            float bodyHeight = BasisHeightDriver.PlayerEyeHeight / BasisMetaBodyTrackingSettings.EyeHeightToFullHeightRatio;
            if (Mathf.Abs(bodyHeight - s_SuggestedHeight) < HeightChangeEpsilon)
            {
                return;
            }

            XrBodyTrackingCalibrationInfoMETA info = new XrBodyTrackingCalibrationInfoMETA
            {
                type = XR_TYPE_BODY_TRACKING_CALIBRATION_INFO_META,
                next = IntPtr.Zero,
                bodyHeight = bodyHeight,
            };
            int result = d_suggestCalibration.Invoke(s_BodyTracker, ref info);
            s_SuggestedHeight = result == 0 ? bodyHeight : 0f;
            BasisDebug.Log($"{Tag} suggested body height {bodyHeight:F3}m, returned {result}", BasisDebug.LogTag.Device);
        }

        /// <summary>Drops our height suggestion and lets the runtime estimate the player again.</summary>
        public static void ResetHeightOverride()
        {
            if (s_BodyTracker == 0 || d_resetCalibration == null || s_SuggestedHeight == 0f)
            {
                return;
            }
            int result = d_resetCalibration.Invoke(s_BodyTracker);
            s_SuggestedHeight = 0f;
            BasisDebug.Log($"{Tag} reset body tracking calibration, returned {result}", BasisDebug.LogTag.Device);
        }
    }
}
#endif
