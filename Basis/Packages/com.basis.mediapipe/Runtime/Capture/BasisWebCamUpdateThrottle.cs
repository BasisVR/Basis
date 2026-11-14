using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;
using Unity.Scripting.LifecycleManagement;

namespace Basis.MediaPipe
{
    [AutoStaticsCleanup]
    public static partial class BasisWebCamUpdateThrottle
    {
        public const double MaxUpdatesPerSecond = 120.0;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void NativeUpdate();
        private static NativeUpdate nativeUpdate;
        private static double nextUpdate;
        private static bool installed;
        public static void Install()
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            if (installed) return;
            installed = true;
            PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
            if (!Replace(ref root))
            {
                BasisDebug.LogError("BasisMediaPipe: UpdateVideoTextures not found in the player loop; webcam updates are not throttled.");
                return;
            }
            PlayerLoop.SetPlayerLoop(root);
            Application.quitting += Uninstall;
#endif
        }
        private static void Uninstall()
        {
            Application.quitting -= Uninstall;
            installed = false;
        }
        private static bool Replace(ref PlayerLoopSystem system)
        {
            if (system.subSystemList == null) return false;
            for (int index = 0; index < system.subSystemList.Length; index++)
            {
                PlayerLoopSystem child = system.subSystemList[index];
                if (child.type == typeof(PostLateUpdate.UpdateVideoTextures))
                {
                    if (child.updateFunction == IntPtr.Zero) return child.updateDelegate == Tick;
                    IntPtr function = Marshal.ReadIntPtr(child.updateFunction);
                    if (function == IntPtr.Zero) return false;
                    nativeUpdate = Marshal.GetDelegateForFunctionPointer<NativeUpdate>(function);
                    child.updateFunction = IntPtr.Zero;
                    child.updateDelegate = Tick;
                    system.subSystemList[index] = child;
                    return true;
                }
                if (Replace(ref child))
                {
                    system.subSystemList[index] = child;
                    return true;
                }
            }
            return false;
        }
        private static void Tick()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (now < nextUpdate) return;
            nextUpdate = Math.Max(nextUpdate + 1.0 / MaxUpdatesPerSecond, now - 1.0 / MaxUpdatesPerSecond);
            nativeUpdate?.Invoke();
        }
    }
}
