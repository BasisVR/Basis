using System;
using System.Runtime.InteropServices;
using Unity.Scripting.LifecycleManagement;
using Valve.VR;

namespace Basis.Scripts.Device_Management.Devices.OpenVR
{
    [AutoStaticsCleanup]
    public static partial class BasisOpenVRInput011
    {
        public const string InterfaceVersion = "IVRInput_011";
        private const int GetEyeTrackingDataForNextFrameSlot = 13;
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate EVRInputError GetEyeTrackingDataForNextFrameFn(ulong action, ETrackingUniverseOrigin origin, ref VREyeTrackingData_t data, uint size);
        private static GetEyeTrackingDataForNextFrameFn getEyeTrackingDataForNextFrame;
        public static bool Available => getEyeTrackingDataForNextFrame != null;
        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void RegisterSettingGate()
        {
            Unity.XR.OpenVR.OpenVRActionManifest.AllowEyeTracking = BasisSteamVRSdkSetting.ReadForBoot;
        }
        public static void OfferNewestSdkIfEyeGaze(uint deviceIndex, string controllerType, string deviceSerial)
        {
            CVRSystem system = Valve.VR.OpenVR.System;
            if (system == null)
            {
                return;
            }
            ETrackedPropertyError error = ETrackedPropertyError.TrackedProp_Success;
            bool gaze = system.GetBoolTrackedDeviceProperty(deviceIndex, ETrackedDeviceProperty.Prop_SupportsXrEyeGazeInteraction_Bool, ref error);
            if (controllerType != Unity.XR.OpenVR.OpenVRActionManifest.FrameHmdControllerType && !(error == ETrackedPropertyError.TrackedProp_Success && gaze))
            {
                return;
            }
            string runtimeVersion = system.GetRuntimeVersion();
            if (!Unity.XR.OpenVR.OpenVRActionManifest.IsAtLeast(runtimeVersion, Unity.XR.OpenVR.OpenVRActionManifest.MinimumRuntimeMajor, Unity.XR.OpenVR.OpenVRActionManifest.MinimumRuntimeMinor))
            {
                BasisDebug.Log($"[OpenVR] Eye gaze headset on SteamVR {runtimeVersion}, newest SDK needs {Unity.XR.OpenVR.OpenVRActionManifest.MinimumRuntimeMajor}.{Unity.XR.OpenVR.OpenVRActionManifest.MinimumRuntimeMinor}; not offering it", BasisDebug.LogTag.Device);
                return;
            }
            EVRInitError inputError = EVRInitError.None;
            Valve.VR.OpenVR.GetGenericInterface(InterfaceVersion, ref inputError);
            if (inputError != EVRInitError.None)
            {
                BasisDebug.Log($"[OpenVR] Eye gaze headset but runtime lacks {InterfaceVersion} ({inputError}); not offering newest SDK", BasisDebug.LogTag.Device);
                return;
            }
            System.Text.StringBuilder model = new System.Text.StringBuilder(64);
            system.GetStringTrackedDeviceProperty(deviceIndex, ETrackedDeviceProperty.Prop_ModelNumber_String, model, 64, ref error);
            string name = error == ETrackedPropertyError.TrackedProp_Success ? model.ToString() : string.Empty;
            BasisSteamVRSdkSetting.OfferForEyeGazeHeadset(string.IsNullOrEmpty(deviceSerial) ? name : deviceSerial, name);
        }
        public static bool Acquire()
        {
            getEyeTrackingDataForNextFrame = null;
            EVRInitError error = EVRInitError.None;
            IntPtr table = Valve.VR.OpenVR.GetGenericInterface("FnTable:" + InterfaceVersion, ref error);
            if (error != EVRInitError.None || table == IntPtr.Zero)
            {
                return false;
            }
            IntPtr function = Marshal.ReadIntPtr(table, GetEyeTrackingDataForNextFrameSlot * IntPtr.Size);
            if (function == IntPtr.Zero)
            {
                return false;
            }
            getEyeTrackingDataForNextFrame = Marshal.GetDelegateForFunctionPointer<GetEyeTrackingDataForNextFrameFn>(function);
            return true;
        }
        public static void Release()
        {
            getEyeTrackingDataForNextFrame = null;
        }
        public static EVRInputError GetEyeTrackingDataForNextFrame(ulong action, ETrackingUniverseOrigin origin, ref VREyeTrackingData_t data, uint size)
        {
            GetEyeTrackingDataForNextFrameFn function = getEyeTrackingDataForNextFrame;
            return function == null ? EVRInputError.NoData : function(action, origin, ref data, size);
        }
    }
}
