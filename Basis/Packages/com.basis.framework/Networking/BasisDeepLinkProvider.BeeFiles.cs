using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Platform;
using System;
using System.IO;
using UnityEngine;

namespace Basis.Scripts.Networking
{
    public static partial class BasisDeepLinkProvider
    {
        public const string BeeFileProductName = "Basis Unity";
        public const string BeeFileProgId = "BasisLabs.BEE";
        public const string BeeFileMimeType = "application/x-basis-bee";
        public static bool OwnsBeeFiles => Application.productName == BeeFileProductName;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void InitializeBeeFiles()
        {
            if (!OwnsBeeFiles) return;
#if (UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX) && !UNITY_EDITOR
            RegisterPlatformBeeFiles();
#endif
            string path = GetCliBeePath();
            if (path != null) OpenBeeFile(path);
        }
        public static void OpenBeeFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !BasisBeeFileDrop.HasBeeExtension(path)) return;
            if (BasisLocalPlayer.PlayerReady)
            {
                BasisDesktopFileDrop.SubmitDroppedFiles(new[] { path });
                return;
            }
            void Ready()
            {
                BasisLocalPlayer.OnLocalPlayerInitialized -= Ready;
                BasisDesktopFileDrop.SubmitDroppedFiles(new[] { path });
            }
            BasisLocalPlayer.OnLocalPlayerInitialized += Ready;
        }
        private static string GetCliBeePath()
        {
            try
            {
                string[] args = Environment.GetCommandLineArgs();
                if (args == null) return null;
                for (int i = 1; i < args.Length; i++)
                    if (BasisBeeFileDrop.HasBeeExtension(args[i]) && File.Exists(args[i]))
                        return args[i];
            }
            catch { }
            return null;
        }
#if (UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX) && !UNITY_EDITOR
        private static void RegisterPlatformBeeFiles()
        {
            try
            {
#if UNITY_STANDALONE_WIN
                string dataPath = Application.dataPath.Replace('/', '\\');
                if (!dataPath.EndsWith("_Data", StringComparison.OrdinalIgnoreCase)) return;
                RegisterWindowsBeeFiles(dataPath.Substring(0, dataPath.Length - 5) + ".exe");
#elif UNITY_STANDALONE_LINUX
                string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exePath)) return;
                RegisterLinuxBeeFiles(exePath);
#endif
            }
            catch (Exception ex)
            {
                BasisDebug.LogWarning($"[BasisDeepLink] .BEE file registration failed: {ex.Message}");
            }
        }
#endif
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int RegGetValueW(IntPtr hKey, string subKey, string value, int flags, out int type, System.Text.StringBuilder data, ref int cbData);
        [System.Runtime.InteropServices.DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
        private static void RegisterWindowsBeeFiles(string exePath)
        {
            const string prefsKey = "__basis_beeFileCommand__";
            const int KEY_ALL_ACCESS = 0xF003F, REG_SZ = 1, RRF_RT_REG_SZ = 0x2, SHCNE_ASSOCCHANGED = 0x08000000;
            IntPtr hkcu = new IntPtr(unchecked((int)0x80000001));
            string cmd = $"\"{exePath}\" \"%1\"";
            if (PlayerPrefs.GetString(prefsKey, string.Empty) == cmd) return;
            string extensionKey = @"Software\Classes\" + BasisBeeConstants.BasisEncryptedExtension.ToLowerInvariant();
            System.Text.StringBuilder current = new System.Text.StringBuilder(256);
            int size = current.Capacity * 2;
            bool claimDefault = RegGetValueW(hkcu, extensionKey, null, RRF_RT_REG_SZ, out _, current, ref size) != 0 || current.Length == 0 || current.ToString() == BeeFileProgId;
            if (!SetRegistryString(hkcu, extensionKey, claimDefault ? string.Empty : null, BeeFileProgId)) return;
            SetRegistryString(hkcu, extensionKey + @"\OpenWithProgids", BeeFileProgId, string.Empty);
            string progKey = @"Software\Classes\" + BeeFileProgId;
            if (!SetRegistryString(hkcu, progKey, string.Empty, Application.productName + " Bundle")) return;
            SetRegistryString(hkcu, progKey + @"\DefaultIcon", string.Empty, $"\"{exePath}\",0");
            if (!SetRegistryString(hkcu, progKey + @"\shell\open\command", string.Empty, cmd)) return;
            SHChangeNotify(SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
            PlayerPrefs.SetString(prefsKey, cmd);
            PlayerPrefs.Save();
            BasisDebug.Log($"[BasisDeepLink] Registered {BasisBeeConstants.BasisEncryptedExtension} files -> {cmd}{(claimDefault ? string.Empty : " (Open With only, another app is the default)")}");
            bool SetRegistryString(IntPtr root, string subKey, string valueName, string data)
            {
                int ret = RegCreateKeyExW(root, subKey, 0, IntPtr.Zero, 0, KEY_ALL_ACCESS, IntPtr.Zero, out IntPtr key, out _);
                if (ret != 0) { BasisDebug.LogError($"[BasisDeepLink] RegCreateKeyExW({subKey}) failed: {ret}"); return false; }
                if (valueName != null) RegSetValueExW(key, valueName, 0, REG_SZ, data, (data.Length + 1) * 2);
                RegCloseKey(key);
                return true;
            }
        }
#endif
#if UNITY_STANDALONE_LINUX && !UNITY_EDITOR
        private static void RegisterLinuxBeeFiles(string exePath)
        {
            const string prefsKey = "__basis_beeFileCommand__";
            string safeExe = exePath.Replace("\n", "").Replace("\r", "");
            if (PlayerPrefs.GetString(prefsKey, string.Empty) == safeExe) return;
            string share = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            string mimeRoot = Path.Combine(share, "mime");
            string mimePackages = Path.Combine(mimeRoot, "packages");
            string desktopDir = Path.Combine(share, "applications");
            Directory.CreateDirectory(mimePackages);
            Directory.CreateDirectory(desktopDir);
            File.WriteAllText(Path.Combine(mimePackages, $"{DeepLinkScheme}-bee.xml"),
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                "<mime-info xmlns=\"http://www.freedesktop.org/standards/shared-mime-info\">\n" +
                $"  <mime-type type=\"{BeeFileMimeType}\">\n" +
                "    <comment>Basis encrypted bundle</comment>\n" +
                "    <glob pattern=\"*.bee\"/>\n" +
                "    <glob pattern=\"*.BEE\"/>\n" +
                "  </mime-type>\n" +
                "</mime-info>\n");
            string desktopName = $"{DeepLinkScheme}-bee.desktop";
            File.WriteAllText(Path.Combine(desktopDir, desktopName),
                "[Desktop Entry]\n" +
                $"Name={Application.productName.Replace("\n", "").Replace("\r", "")}\n" +
                $"Exec=\"{safeExe}\" %f\n" +
                "Type=Application\n" +
                "NoDisplay=true\n" +
                $"MimeType={BeeFileMimeType};\n");
            RunProcess("update-mime-database", mimeRoot);
            RunProcess("xdg-mime", $"default {desktopName} {BeeFileMimeType}");
            RunProcess("update-desktop-database", desktopDir);
            PlayerPrefs.SetString(prefsKey, safeExe);
            PlayerPrefs.Save();
        }
#endif
    }
}
