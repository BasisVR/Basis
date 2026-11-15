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
