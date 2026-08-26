using Basis.Scripts.Device_Management;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Basis.Scripts.Networking
{
    /// <summary>
    /// Restarts the application and reconnects to the current server, restoring the
    /// active device mode. In a standalone player this relaunches the process with
    /// <c>--force-&lt;mode&gt;</c>, the saved renderer's <c>-force-&lt;api&gt;</c> flag and
    /// <c>--connection=ip:port#password</c> args. In the
    /// editor it stores the intent and toggles play mode off then on, restoring the same
    /// mode and reconnecting on the next play session.
    /// </summary>
    public static class BasisAppRelaunch
    {
        public static bool IsSupported
        {
            get
            {
#if UNITY_EDITOR
                return true;
#elif UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX
                return true;
#else
                return false;
#endif
            }
        }

        public static bool RebootAndReconnect()
        {
#if UNITY_EDITOR
            return EditorRebootAndReconnect();
#elif UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX
            return StandaloneRebootAndReconnect();
#else
            BasisDebug.LogWarning("Reboot and Reconnect is not supported on this platform.");
            return false;
#endif
        }

        private static bool IsRestorableMode(string mode)
        {
            return mode == BasisConstants.Desktop
                || mode == BasisConstants.OpenVRLoader
                || mode == BasisConstants.OpenXRLoader;
        }

        private static string BuildConnectionValue()
        {
            string ip = BasisNetworkManagement.Ip;
            if (string.IsNullOrEmpty(ip)) return string.Empty;

            string value = ip + ":" + BasisNetworkManagement.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(BasisNetworkManagement.Password))
                value += "#" + BasisNetworkManagement.Password;

            return value;
        }

#if UNITY_EDITOR
        private const string EditorModeKey = "Basis.RebootReconnect.Mode";
        private const string EditorConnectionKey = "Basis.RebootReconnect.Connection";
        private const string EditorPendingPlayKey = "Basis.RebootReconnect.PendingPlay";

        private static bool EditorRebootAndReconnect()
        {
            string mode = BasisDeviceManagement.StaticCurrentMode;
            if (IsRestorableMode(mode)) SessionState.SetString(EditorModeKey, mode);
            else SessionState.EraseString(EditorModeKey);

            string connection = BasisNetworkConnection.LocalPlayerIsConnected ? BuildConnectionValue() : string.Empty;
            if (!string.IsNullOrEmpty(connection)) SessionState.SetString(EditorConnectionKey, connection);
            else SessionState.EraseString(EditorConnectionKey);

            SessionState.SetBool(EditorPendingPlayKey, true);
            EditorApplication.playModeStateChanged += ResumeOnExitedPlayMode;
            EditorApplication.isPlaying = false;
            return true;
        }

        private static void ResumeOnExitedPlayMode(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredEditMode) return;
            EditorApplication.playModeStateChanged -= ResumeOnExitedPlayMode;
            if (!SessionState.GetBool(EditorPendingPlayKey, false)) return;
            SessionState.SetBool(EditorPendingPlayKey, false);
            EditorApplication.isPlaying = true;
        }

        [InitializeOnLoadMethod]
        private static void ResumeAfterDomainReload()
        {
            if (!SessionState.GetBool(EditorPendingPlayKey, false)) return;
            SessionState.SetBool(EditorPendingPlayKey, false);
            EditorApplication.delayCall += () =>
            {
                if (!EditorApplication.isPlayingOrWillChangePlaymode)
                    EditorApplication.isPlaying = true;
            };
        }

        public static bool TryConsumeEditorForcedMode(out string mode)
        {
            mode = SessionState.GetString(EditorModeKey, string.Empty);
            if (string.IsNullOrEmpty(mode)) return false;
            SessionState.EraseString(EditorModeKey);
            return true;
        }

        public static bool TryConsumeEditorConnection(out string connectionValue)
        {
            connectionValue = SessionState.GetString(EditorConnectionKey, string.Empty);
            if (string.IsNullOrEmpty(connectionValue)) return false;
            SessionState.EraseString(EditorConnectionKey);
            return true;
        }
#endif

#if (UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX) && !UNITY_EDITOR
        private static bool StandaloneRebootAndReconnect()
        {
            string executable = ResolveExecutablePath();
            if (string.IsNullOrEmpty(executable))
            {
                BasisDebug.LogError("Reboot and Reconnect failed: could not resolve the executable path.");
                return false;
            }

            string arguments = BuildArguments();
            string workingDirectory = System.IO.Path.GetDirectoryName(executable) ?? string.Empty;

#if UNITY_STANDALONE_WIN
            if (TryStartWindowsNative(executable, arguments, workingDirectory))
            {
                Application.Quit();
                return true;
            }
#endif

            if (TryStartManaged(executable, arguments, workingDirectory))
            {
                Application.Quit();
                return true;
            }

            return false;
        }

#if UNITY_STANDALONE_WIN
        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern System.IntPtr ShellExecuteW(System.IntPtr hwnd, string lpOperation, string lpFile, string lpParameters, string lpDirectory, int nShowCmd);

        private const int SW_SHOWNORMAL = 1;

        private static bool TryStartWindowsNative(string executable, string arguments, string workingDirectory)
        {
            try
            {
                System.IntPtr result = ShellExecuteW(System.IntPtr.Zero, "open", executable, arguments, workingDirectory, SW_SHOWNORMAL);
                if (result.ToInt64() > 32) return true;

                int lastError = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                BasisDebug.LogWarning($"Reboot and Reconnect ShellExecuteW returned {result.ToInt64()} (Win32 error {lastError}); falling back to managed launch.");
                return false;
            }
            catch (System.Exception ex)
            {
                BasisDebug.LogWarning($"Reboot and Reconnect ShellExecuteW threw; falling back to managed launch: {ex}");
                return false;
            }
        }
#endif

        private static bool TryStartManaged(string executable, string arguments, string workingDirectory)
        {
            try
            {
                System.Diagnostics.ProcessStartInfo startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = arguments,
                    UseShellExecute = false,
                    WorkingDirectory = workingDirectory,
                };
                System.Diagnostics.Process.Start(startInfo);
                return true;
            }
            catch (System.Exception ex)
            {
                BasisDebug.LogError($"Reboot and Reconnect failed to launch a new instance: {ex}");
                return false;
            }
        }

        private static string ResolveExecutablePath()
        {
            try
            {
                string path = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(path)) return path;
            }
            catch { }

            try
            {
                string[] args = System.Environment.GetCommandLineArgs();
                if (args != null && args.Length > 0 && !string.IsNullOrEmpty(args[0])) return args[0];
            }
            catch { }

            return null;
        }

        private static string BuildArguments()
        {
            System.Text.StringBuilder builder = new System.Text.StringBuilder();

            string mode = BasisDeviceManagement.StaticCurrentMode;
            if (IsRestorableMode(mode)) Append(builder, "--force-" + mode);

            if (Basis.Scripts.Rendering.BasisGraphicsApiSelection.TryGetRelaunchArguments(out string forceApi, out string apiMarker))
            {
                Append(builder, forceApi);
                Append(builder, apiMarker);
            }

            if (BasisNetworkConnection.LocalPlayerIsConnected)
            {
                string connection = BuildConnectionValue();
                if (!string.IsNullOrEmpty(connection)) Append(builder, "--connection=" + connection);
            }

            return builder.ToString();
        }

        private static void Append(System.Text.StringBuilder builder, string argument)
        {
            if (builder.Length > 0) builder.Append(' ');
            if (argument.IndexOf(' ') >= 0) builder.Append('"').Append(argument).Append('"');
            else builder.Append(argument);
        }
#endif
    }
}
