using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.UI.UI_Panels;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Basis.Scripts.Avatar
{
    [AutoStaticsCleanup]
    public static partial class BasisVrmLicenseNotice
    {
        private static readonly HashSet<string> accepted = new HashSet<string>();
        public static async Task<bool> ConfirmIfRestricted(BasisLoadableBundle bundle, GameObject held, CancellationToken token)
        {
            BasisBundleConnector connector = bundle?.BasisBundleConnector;
            if (!BasisVrmAvatarLoader.TryGetDocument(connector, out BasisVrmDocument document)) return true;
            if (document.Use != BasisVrmDocument.AvatarUse.OnlyAuthor && document.Use != BasisVrmDocument.AvatarUse.LicensedOnly) return true;
            string location = bundle.BasisRemoteBundleEncrypted?.RemoteBeeFileLocation ?? string.Empty;
            string key = string.IsNullOrEmpty(connector.UniqueVersion) ? location : connector.UniqueVersion;
            if (accepted.Contains(key)) return true;
            string author = string.IsNullOrWhiteSpace(document.Author) ? BasisLocalization.Get("avatar.vrmLicense.unknownAuthor") : document.Author.Trim();
            string title = BasisLocalization.Get("avatar.vrmLicense.title");
            string body = BasisLocalization.Get(document.Use == BasisVrmDocument.AvatarUse.OnlyAuthor ? "avatar.vrmLicense.onlyAuthor" : "avatar.vrmLicense.licensedOnly", author);
            if (!BasisMainMenu.Instance) BasisMainMenu.Open();
            TaskCompletionSource<bool> choice = new TaskCompletionSource<bool>();
            BasisMenuDialoguePanel panel = BasisMenuDialoguePanel.CreateNew(title, body, BasisLocalization.Get("avatar.vrmLicense.wear"), BasisLocalization.Get("avatar.vrmLicense.remove"), wear => choice.TrySetResult(wear), false, BasisPanelSeverity.Caution);
            if (panel == null)
            {
                accepted.Add(key);
                return true;
            }
            held.SetActive(false);
            bool wearIt;
            using (token.Register(() => choice.TrySetCanceled()))
            {
                wearIt = await choice.Task;
            }
            if (wearIt)
            {
                accepted.Add(key);
                if (held) held.SetActive(true);
                return true;
            }
            await RemoveFromAvatars(location);
            return false;
        }
        private static async Task RemoveFromAvatars(string location)
        {
            if (string.IsNullOrEmpty(location)) return;
            await BasisDataStoreItemKeys.EnsureLoaded();
            List<BasisDataStoreItemKeys.ItemKey> matches = new List<BasisDataStoreItemKeys.ItemKey>();
            foreach (BasisDataStoreItemKeys.ItemKey item in BasisDataStoreItemKeys.DisplayKeys())
            {
                if (item != null && item.Mode == BundledContentHolder.Mode.Avatar && !item.EmbeddedSettings.IsEmbedded && string.Equals(item.Url, location, StringComparison.Ordinal)) matches.Add(item);
            }
            foreach (BasisDataStoreItemKeys.ItemKey item in matches) await BasisDataStoreItemKeys.RemoveKey(item);
            if (matches.Count > 0) await LibraryProvider.RefreshCurrentTab();
            if (BasisDataStore.LoadAvatar(BasisLocalPlayer.LoadFileNameAndExtension, BasisBeeConstants.DefaultAvatar, BasisPlayer.LoadModeLocal, out BasisDataStore.BasisSavedAvatar saved) && string.Equals(saved.UniqueID, location, StringComparison.Ordinal))
            {
                BasisDataStore.SaveAvatar(BasisBeeConstants.DefaultAvatar, BasisPlayer.LoadModeLocal, BasisLocalPlayer.LoadFileNameAndExtension);
            }
        }
    }
}
