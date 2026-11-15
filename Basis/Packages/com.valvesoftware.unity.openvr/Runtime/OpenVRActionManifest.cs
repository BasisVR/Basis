using System;
using System.IO;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using Valve.VR;

namespace Unity.XR.OpenVR
{
    [AutoStaticsCleanup]
    public static partial class OpenVRActionManifest
    {
        public const string DefaultFileName = "actions.json";
        public const string EyeTrackingFileName = "actions_eyetracking.json";
        public const string FrameHmdControllerType = "frame_hmd";
        public const int MinimumRuntimeMajor = 2, MinimumRuntimeMinor = 18;
        public const string EyeTrackingInputVersion = "IVRInput_011";
        public static Func<bool> AllowEyeTracking;
        public static bool EyeTracking { get; private set; }
        public static string ChosenFileName { get; private set; } = DefaultFileName;
        public static string Choose()
        {
            EyeTracking = false;
            ChosenFileName = DefaultFileName;
            Func<bool> allow = AllowEyeTracking;
            if (allow == null || !allow())
            {
                Debug.Log($"<b>[OpenVR]</b> Action manifest {ChosenFileName} (newest SteamVR SDK off)");
                return ChosenFileName;
            }
            string eyePath = Path.Combine(Application.streamingAssetsPath, OpenVRSettings.StreamingAssetsFolderName, EyeTrackingFileName);
            if (!File.Exists(eyePath))
            {
                return ChosenFileName;
            }
            if (ProbeEyeTracking(out string reason))
            {
                EyeTracking = true;
                ChosenFileName = EyeTrackingFileName;
            }
            Debug.Log($"<b>[OpenVR]</b> Action manifest {ChosenFileName} ({reason})");
            return ChosenFileName;
        }
        public static string ReplaceFileName(string manifestPath, string fileName)
        {
            if (string.IsNullOrEmpty(manifestPath))
            {
                return manifestPath;
            }
            string directory = Path.GetDirectoryName(manifestPath);
            return string.IsNullOrEmpty(directory) ? fileName : Path.Combine(directory, fileName);
        }
        static bool ProbeEyeTracking(out string reason)
        {
            try
            {
                if (!Valve.VR.OpenVR.IsRuntimeInstalled() || !Valve.VR.OpenVR.IsHmdPresent())
                {
                    reason = "no runtime or headset";
                    return false;
                }
                EVRInitError initError = EVRInitError.None;
                CVRSystem system = Valve.VR.OpenVR.Init(ref initError, EVRApplicationType.VRApplication_Utility);
                if (initError != EVRInitError.None || system == null)
                {
                    reason = "probe init failed: " + initError;
                    return false;
                }
                try
                {
                    EVRInitError inputError = EVRInitError.None;
                    Valve.VR.OpenVR.GetGenericInterface(EyeTrackingInputVersion, ref inputError);
                    if (inputError != EVRInitError.None)
                    {
                        reason = "runtime lacks " + EyeTrackingInputVersion;
                        return false;
                    }
                    string runtimeVersion = system.GetRuntimeVersion();
                    if (!IsAtLeast(runtimeVersion, MinimumRuntimeMajor, MinimumRuntimeMinor))
                    {
                        reason = $"runtime {runtimeVersion} is older than {MinimumRuntimeMajor}.{MinimumRuntimeMinor}";
                        return false;
                    }
                    ETrackedPropertyError propertyError = ETrackedPropertyError.TrackedProp_Success;
                    bool gaze = system.GetBoolTrackedDeviceProperty(Valve.VR.OpenVR.k_unTrackedDeviceIndex_Hmd, ETrackedDeviceProperty.Prop_SupportsXrEyeGazeInteraction_Bool, ref propertyError);
                    if (propertyError == ETrackedPropertyError.TrackedProp_Success && gaze)
                    {
                        reason = "headset reports eye gaze";
                        return true;
                    }
                    if (ReadControllerType(system) == FrameHmdControllerType)
                    {
                        reason = "headset is " + FrameHmdControllerType;
                        return true;
                    }
                    reason = "headset has no eye tracking";
                    return false;
                }
                finally
                {
                    Valve.VR.OpenVR.Shutdown();
                }
            }
            catch (Exception e)
            {
                reason = "probe threw " + e.GetType().Name;
                return false;
            }
        }
        public static bool IsAtLeast(string version, int major, int minor)
        {
            if (string.IsNullOrEmpty(version))
            {
                return false;
            }
            string[] parts = version.Split('.');
            if (parts.Length < 2 || !int.TryParse(parts[0], out int foundMajor) || !int.TryParse(parts[1], out int foundMinor))
            {
                return false;
            }
            return foundMajor > major || (foundMajor == major && foundMinor >= minor);
        }
        static string ReadControllerType(CVRSystem system)
        {
            ETrackedPropertyError error = ETrackedPropertyError.TrackedProp_Success;
            uint capacity = system.GetStringTrackedDeviceProperty(Valve.VR.OpenVR.k_unTrackedDeviceIndex_Hmd, ETrackedDeviceProperty.Prop_ControllerType_String, null, 0, ref error);
            if (capacity <= 1)
            {
                return string.Empty;
            }
            var result = new System.Text.StringBuilder((int)capacity);
            system.GetStringTrackedDeviceProperty(Valve.VR.OpenVR.k_unTrackedDeviceIndex_Hmd, ETrackedDeviceProperty.Prop_ControllerType_String, result, capacity, ref error);
            return error == ETrackedPropertyError.TrackedProp_Success ? result.ToString() : string.Empty;
        }
    }
}
