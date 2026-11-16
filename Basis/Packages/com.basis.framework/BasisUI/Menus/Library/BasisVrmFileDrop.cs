using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Platform;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Basis.BasisUI
{
    [AutoStaticsCleanup]
    public static partial class BasisVrmFileDrop
    {
        public const string Extension = ".vrm";
        private static bool handling;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Initialize()
        {
            BasisDesktopFileDrop.RegisterAcceptedExtensions(Extension);
            BasisDesktopFileDrop.OnFilesDropped -= OnFilesDropped;
            BasisDesktopFileDrop.OnFilesDropped += OnFilesDropped;
        }
        private static void OnFilesDropped(string[] paths)
        {
            if (paths == null) return;
            for (int Index = paths.Length - 1; Index >= 0; Index--)
            {
                string path = paths[Index];
                if (string.IsNullOrWhiteSpace(path) || !BasisVrmDocument.IsVrmLocation(path) || !File.Exists(path)) continue;
                Wear(path);
                return;
            }
        }
        private static async void Wear(string path)
        {
            if (handling)
            {
                BasisDebug.LogWarning($"VRM drop: still loading the previous avatar, ignoring {Path.GetFileName(path)}.", BasisDebug.LogTag.Avatar);
                return;
            }
            handling = true;
            try
            {
                if (!IsInStorage(path) && await AskToSave(Path.GetFileName(path)))
                {
                    string stored = await Task.Run(() => CopyIntoStorage(path));
                    if (stored != null) path = stored;
                }
                await WearVrm(path);
            }
            catch (Exception exception)
            {
                BasisDebug.LogError($"VRM drop: {path} failed ({exception}).", BasisDebug.LogTag.Avatar);
            }
            finally
            {
                handling = false;
            }
        }
        public static string StorageFolder => Path.Combine(string.IsNullOrEmpty(BasisIOManagement.PersistentDataPath) ? Application.persistentDataPath : BasisIOManagement.PersistentDataPath, "VRM Avatars");
        private static bool IsInStorage(string path)
        {
            try
            {
                string folder = Path.GetFullPath(StorageFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                return Path.GetFullPath(path).StartsWith(folder, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }
        private static Task<bool> AskToSave(string fileName)
        {
            TaskCompletionSource<bool> answer = new TaskCompletionSource<bool>();
            BasisMainMenu.Open();
            BasisMenuBase<BasisMainMenu> menu = BasisMainMenu.Instance;
            if (menu == null)
            {
                answer.SetResult(false);
                return answer.Task;
            }
            if (menu.Dialogue != null && !menu.Dialogue.IsReleased) menu.Dialogue.ReleaseInstance();
            menu.OpenDialogue(BasisLocalization.Get("library.vrmDrop.save.title"), BasisLocalization.Get("library.vrmDrop.save.body", fileName), BasisLocalization.Get("ui.yes"), BasisLocalization.Get("ui.no"), accepted => answer.TrySetResult(accepted));
            return answer.Task;
        }
        private static string CopyIntoStorage(string source)
        {
            try
            {
                string folder = StorageFolder;
                Directory.CreateDirectory(folder);
                string hash;
                using (FileStream stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
                using (SHA256 sha = SHA256.Create())
                {
                    hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
                }
                string name = Path.GetFileNameWithoutExtension(source);
                foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
                if (string.IsNullOrWhiteSpace(name)) name = "avatar";
                string target = Path.Combine(folder, name + Extension);
                if (File.Exists(target) && !SameContent(target, hash)) target = Path.Combine(folder, name + "_" + hash.Substring(0, 8) + Extension);
                if (File.Exists(target) && SameContent(target, hash)) return target;
                string temporary = target + ".partial";
                File.Copy(source, temporary, true);
                File.Move(temporary, target);
                BasisDebug.Log($"VRM drop: saved {Path.GetFileName(source)} to {target}", BasisDebug.LogTag.Avatar);
                return target;
            }
            catch (Exception exception)
            {
                BasisDebug.LogError($"VRM drop: could not save {source} into storage ({exception.Message}), wearing it from where it is.", BasisDebug.LogTag.Avatar);
                return null;
            }
        }
        private static bool SameContent(string path, string hash)
        {
            using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            using SHA256 sha = SHA256.Create();
            return string.Equals(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty), hash, StringComparison.Ordinal);
        }
        public static async Task<bool> WearVrm(string pathOrUrl)
        {
            BasisLocalPlayer player = BasisLocalPlayer.Instance;
            if (player == null || string.IsNullOrWhiteSpace(pathOrUrl)) return false;
            string location = pathOrUrl;
            if (!Uri.TryCreate(pathOrUrl, UriKind.Absolute, out Uri uri) || uri.IsFile) location = new Uri(Path.GetFullPath(uri != null && uri.IsFile ? uri.LocalPath : pathOrUrl)).AbsoluteUri;
            if (!BasisVrmDocument.IsVrmLocation(location)) return false;
            BasisLoadableBundle bundle = new BasisLoadableBundle
            {
                BasisRemoteBundleEncrypted = new BasisRemoteEncyptedBundle { RemoteBeeFileLocation = location },
                BasisBundleConnector = BasisVrmAvatarLoader.BuildConnector("VRM Avatar", null, string.Empty, 0),
                BasisLocalEncryptedBundle = new BasisStoredEncryptedBundle(),
                UnlockPassword = string.Empty,
            };
            BasisDebug.Log($"Wearing VRM avatar {location}", BasisDebug.LogTag.Avatar);
            await player.CreateAvatar((byte)BasisLoadMode.Download, bundle);
            return string.Equals(BasisLocalPlayer.CurrentAvatarUniqueID, location, StringComparison.Ordinal);
        }
    }
}
