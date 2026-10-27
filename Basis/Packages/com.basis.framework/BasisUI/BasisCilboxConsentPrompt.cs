using System;
using System.Collections.Generic;
using Basis.Scripts.Device_Management;

namespace Basis.BasisUI
{
    public static class BasisCilboxConsentPrompt
    {
        public enum Answer
        {
            Deny,
            Allow,
            AllowAndRemember,
        }

        private const string SevereHex = "#FF5A4A";
        private const int MaxNameLength = 48;

        public static void Show(string contentName, string contentKind, string owner, bool yours, IReadOnlyList<string> items, IReadOnlyList<string> blocked, IReadOnlyList<BasisCilboxPermissionText.Use> uses, Action<Answer> onAnswer, Action<BasisNotification> onDeferred)
        {
            if (!BasisCilboxPermissions.UntrustedAllowed)
            {
                onAnswer?.Invoke(Answer.Deny);
                return;
            }
            string title = BasisLocalization.Get("settings.cilboxPermissions.prompt.title");
            string description = Body(contentName, contentKind, owner, yours);
            if (BasisNotificationCenter.RouteToNotifications)
            {
                Defer(title, description, contentName, contentKind, owner, yours, items, blocked, uses, onAnswer, onDeferred);
                return;
            }
            if (!BasisMainMenu.Instance) BasisMainMenu.Open();
            if (!BasisMainMenu.Instance)
            {
                Defer(title, description, contentName, contentKind, owner, yours, items, blocked, uses, onAnswer, onDeferred);
                return;
            }
            if (BasisMainMenu.Instance.Dialogue)
            {
                BasisMainMenu.Instance.Dialogue.OnInstanceReleased += () => BasisDeviceManagement.EnqueueOnMainThread(() => Show(contentName, contentKind, owner, yours, items, blocked, uses, onAnswer, onDeferred));
                return;
            }

            List<BasisCilboxPermissionText.Line> lines = BasisCilboxPermissionText.Describe(items, uses);
            bool severe = lines.Exists(line => line.Severe);
            bool answered = false;
            BasisMenuDialoguePanel panel = BasisMenuDialoguePanel.CreateNew(
                title,
                description,
                BasisLocalization.Get("settings.cilboxPermissions.prompt.allow"),
                BasisLocalization.Get("settings.cilboxPermissions.prompt.deny"),
                accepted =>
                {
                    answered = true;
                    onAnswer?.Invoke(accepted ? Answer.Allow : Answer.Deny);
                },
                false,
                severe ? BasisPanelSeverity.Hot : BasisPanelSeverity.Caution,
                BasisNotificationCategory.Content);
            if (panel == null)
            {
                Defer(title, description, contentName, contentKind, owner, yours, items, blocked, uses, onAnswer, onDeferred);
                return;
            }

            BasisMainMenu.Instance.Dialogue = panel;
            panel.CaptureOnClose = false;
            panel.EnableAlternate(BasisLocalization.Get("settings.cilboxPermissions.prompt.remember"), () =>
            {
                answered = true;
                onAnswer?.Invoke(Answer.AllowAndRemember);
            });
            panel.ShowDetails(Rows(lines, false));
            PanelButton moreInfo = null;
            moreInfo = panel.AddOption(BasisLocalization.Get("settings.cilboxPermissions.prompt.moreInfo"), () =>
            {
                panel.ShowDetails(Rows(BasisCilboxPermissionText.Review(items, blocked, uses), true));
                if (moreInfo != null) moreInfo.gameObject.SetActive(false);
            }, false);
            if (moreInfo != null) moreInfo.rectTransform.SetAsLastSibling();
            panel.OnInstanceReleased += () =>
            {
                if (!answered) Defer(title, description, contentName, contentKind, owner, yours, items, blocked, uses, onAnswer, onDeferred);
            };
        }

        public static string Body(string contentName, string contentKind, string owner, bool yours)
        {
            string name = CleanName(contentName), kind = KindLabel(contentKind);
            bool avatar = contentKind == "Avatar", world = contentKind == "World";
            if (world || (!yours && string.IsNullOrWhiteSpace(owner))) return BasisLocalization.Get("settings.cilboxPermissions.prompt.body", name, kind);
            if (yours) return BasisLocalization.Get(avatar ? "settings.cilboxPermissions.prompt.bodyYours" : "settings.cilboxPermissions.prompt.bodySpawnedYours", name, kind);
            return BasisLocalization.Get(avatar ? "settings.cilboxPermissions.prompt.bodyOwner" : "settings.cilboxPermissions.prompt.bodySpawned", name, kind, CleanName(owner));
        }

        private static List<BasisMenuDialoguePanel.DetailRow> Rows(List<BasisCilboxPermissionText.Line> lines, bool wrap)
        {
            List<BasisMenuDialoguePanel.DetailRow> rows = new List<BasisMenuDialoguePanel.DetailRow>(lines.Count);
            foreach (BasisCilboxPermissionText.Line line in lines)
            {
                rows.Add(new BasisMenuDialoguePanel.DetailRow(line.Severe ? $"<color={SevereHex}>{line.Text}</color>" : line.Text, line.Detail, true, wrap));
            }
            return rows;
        }

        public static string KindLabel(string contentKind)
        {
            switch (contentKind)
            {
                case "Avatar": return BasisLocalization.Get("settings.cilboxPermissions.kind.avatar");
                case "World": return BasisLocalization.Get("settings.cilboxPermissions.kind.world");
                default: return BasisLocalization.Get("settings.cilboxPermissions.kind.prop");
            }
        }

        public static string CleanName(string contentName)
        {
            if (string.IsNullOrWhiteSpace(contentName)) return BasisLocalization.Get("settings.cilboxPermissions.unnamed");
            string clean = contentName.Replace("<", string.Empty).Replace(">", string.Empty).Trim();
            return clean.Length > MaxNameLength ? clean.Substring(0, MaxNameLength) : clean;
        }

        private static void Defer(string title, string description, string contentName, string contentKind, string owner, bool yours, IReadOnlyList<string> items, IReadOnlyList<string> blocked, IReadOnlyList<BasisCilboxPermissionText.Use> uses, Action<Answer> onAnswer, Action<BasisNotification> onDeferred)
        {
            if (!BasisCilboxPermissions.UntrustedAllowed)
            {
                onAnswer?.Invoke(Answer.Deny);
                return;
            }
            BasisNotification notice = BasisNotificationCenter.AddPending(
                title,
                description,
                AddressableAssets.Sprites.Unlocked,
                reopen: () => Show(contentName, contentKind, owner, yours, items, blocked, uses, onAnswer, onDeferred),
                onDismiss: () => onAnswer?.Invoke(Answer.Deny),
                category: BasisNotificationCategory.Content);
            onDeferred?.Invoke(notice);
        }
    }
}
