#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace Basis.Scripts.Device_Management.Devices.WebXR
{
    public static class BasisWebXRNative
    {
        public const int StateIdle = 0, StateRequesting = 1, StateRunning = 2, StateFailed = 3, StateEnded = 4;
        public const int ErrorUnsupported = 1, ErrorDenied = 3, ErrorDevice = 4;
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern void BasisWebXR_Register();
        [DllImport("__Internal")] private static extern int BasisWebXR_Supported();
        [DllImport("__Internal")] private static extern void BasisWebXR_Request();
        [DllImport("__Internal")] private static extern void BasisWebXR_End();
        [DllImport("__Internal")] private static extern int BasisWebXR_State();
        [DllImport("__Internal")] private static extern int BasisWebXR_Error();
        public static bool IsSupported => BasisWebXR_Supported() == 1;
        public static int State => BasisWebXR_State();
        public static int Error => BasisWebXR_Error();
        public static void Register() => BasisWebXR_Register();
        public static void Request() => BasisWebXR_Request();
        public static void End() => BasisWebXR_End();
#else
        public static bool IsSupported => false;
        public static int State => StateIdle;
        public static int Error => ErrorUnsupported;
        public static void Register() { }
        public static void Request() { }
        public static void End() { }
#endif
    }
}
