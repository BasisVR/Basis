#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Basis.Scripts.Platform
{
    public static partial class BasisDesktopFileDrop
    {
        [DllImport("__Internal")] private static extern void BasisWebFileDrop_Install();
        [DllImport("__Internal")] private static extern void BasisWebFileDrop_Uninstall();
        [DllImport("__Internal")] private static extern string BasisWebFileDrop_Poll();

        private static bool _webInstalled;

        private static void InstallPlatform()
        {
            if (_webInstalled) return;
            _webInstalled = true;
            BasisWebFileDrop_Install();
            Application.quitting += Uninstall;
        }

        private static void UninstallPlatform()
        {
            Application.quitting -= Uninstall;
            if (!_webInstalled) return;
            _webInstalled = false;
            BasisWebFileDrop_Uninstall();
        }

        private static void PollPlatform()
        {
            if (!_webInstalled) return;
            string batch;
            while ((batch = BasisWebFileDrop_Poll()) != null)
            {
                string[] paths = batch.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (paths.Length > 0) _pendingBatches.Enqueue(paths);
            }
        }
    }
}
#endif
