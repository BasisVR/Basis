using System.Collections.Generic;
using Basis.BasisUI;
using Unity.Scripting.LifecycleManagement;

namespace Basis.Scripts.Avatar
{
    [AutoStaticsCleanup]
    public static partial class BasisVrmLicenseNotice
    {
        private static readonly HashSet<string> shown = new HashSet<string>();
        public static void NotifyIfRestricted(BasisLoadableBundle bundle)
        {
            BasisBundleConnector connector = bundle?.BasisBundleConnector;
            if (!BasisVrmAvatarLoader.TryGetDocument(connector, out BasisVrmDocument document)) return;
            if (document.Use != BasisVrmDocument.AvatarUse.OnlyAuthor && document.Use != BasisVrmDocument.AvatarUse.LicensedOnly) return;
            string key = string.IsNullOrEmpty(connector.UniqueVersion) ? bundle.BasisRemoteBundleEncrypted?.RemoteBeeFileLocation ?? string.Empty : connector.UniqueVersion;
            if (!shown.Add(key)) return;
            string author = string.IsNullOrWhiteSpace(document.Author) ? BasisLocalization.Get("avatar.vrmLicense.unknownAuthor") : document.Author.Trim();
            string title = BasisLocalization.Get("avatar.vrmLicense.title");
            string body = BasisLocalization.Get(document.Use == BasisVrmDocument.AvatarUse.OnlyAuthor ? "avatar.vrmLicense.onlyAuthor" : "avatar.vrmLicense.licensedOnly", author);
            if (!BasisNotificationCenter.RouteToNotifications && !BasisMainMenu.Instance)
            {
                BasisMainMenu.Open();
            }
            BasisMenuDialoguePanel.CreateNew(title, body, BasisLocalization.Get("ui.ok"), (System.Action<bool>)null, true, BasisPanelSeverity.Caution);
        }
    }
}
