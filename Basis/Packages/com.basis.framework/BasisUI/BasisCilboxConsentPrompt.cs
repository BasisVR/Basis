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
            BasisPromptArming.Arm(panel.AcceptButton);
            BasisPromptArming.Arm(panel.AlternateButton);
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

        public static void ShowRecord(BasisCilboxPermissions.Approval approval)
        {
            if (approval == null || BasisMainMenu.Instance == null) return;
            BasisMenuDialoguePanel before = BasisMainMenu.Instance.Dialogue;
            BasisMainMenu.Instance.OpenDialogue(
                $"{CleanName(approval.ContentName)} ({KindLabel(approval.ContentKind)})",
                Body(approval.ContentName, approval.ContentKind, approval.OwnerName, approval.OwnerYours),
                BasisLocalization.Get("ui.close"),
                _ => { });
            BasisMenuDialoguePanel panel = BasisMainMenu.Instance.Dialogue;
            if (panel == null || panel == before || panel.IsReleased) return;
            List<BasisMenuDialoguePanel.DetailRow> rows = Record(approval);
            rows.AddRange(Rows(BasisCilboxPermissionText.Review(approval.Items, approval.Blocked ?? new List<string>(), approval.Uses()), true));
            panel.ShowDetails(rows);
        }

        public static List<BasisMenuDialoguePanel.DetailRow> Record(BasisCilboxPermissions.Approval approval)
        {
            string who = approval.OwnerYours
                ? BasisLocalization.Get("settings.cilboxPermissions.record.yours")
                : Person(approval.OwnerName, approval.OwnerUUID);
            bool approverKnown = !string.IsNullOrWhiteSpace(approval.ApprovedByName) || !string.IsNullOrEmpty(approval.ApprovedByUUID);
            string what = Lines(
                BasisLocalization.Get("settings.cilboxPermissions.record.name", CleanName(approval.ContentName), KindLabel(approval.ContentKind)),
                string.IsNullOrEmpty(approval.ContentId) ? null : BasisLocalization.Get("settings.cilboxPermissions.record.id", approval.ContentId),
                BasisLocalization.Get("settings.cilboxPermissions.record.key", approval.ContentHash));
            string when = Lines(
                BasisLocalization.Get("settings.cilboxPermissions.record.asked", Known(approval.RequestedUtc)),
                BasisLocalization.Get("settings.cilboxPermissions.record.allowed", Known(approval.ApprovedUtc)),
                approverKnown ? BasisLocalization.Get("settings.cilboxPermissions.record.by", Person(approval.ApprovedByName, approval.ApprovedByUUID)) : null,
                approval.Remembered ? BasisLocalization.Get("settings.cilboxPermissions.record.kept") : BasisLocalization.Get("settings.cilboxPermissions.sessionOnly"));
            string where = Lines(
                BasisLocalization.Get("settings.cilboxPermissions.record.world", Known(CleanText(approval.WorldName))),
                BasisLocalization.Get("settings.cilboxPermissions.record.server", string.IsNullOrEmpty(approval.RequestedUtc)
                    ? Known(null)
                    : string.IsNullOrEmpty(approval.Server) ? BasisLocalization.Get("settings.cilboxPermissions.record.offline") : CleanText(approval.Server)));
            return new List<BasisMenuDialoguePanel.DetailRow>
            {
                new BasisMenuDialoguePanel.DetailRow(BasisLocalization.Get("settings.cilboxPermissions.record.who"), who, true, true),
                new BasisMenuDialoguePanel.DetailRow(BasisLocalization.Get("settings.cilboxPermissions.record.what"), what, true, true),
                new BasisMenuDialoguePanel.DetailRow(BasisLocalization.Get("settings.cilboxPermissions.record.when"), when, true, true),
                new BasisMenuDialoguePanel.DetailRow(BasisLocalization.Get("settings.cilboxPermissions.record.where"), where, true, true),
            };
        }

        private static string Person(string name, string uuid)
        {
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrEmpty(uuid)) return Known(null);
            if (string.IsNullOrEmpty(uuid)) return CleanName(name);
            return string.IsNullOrWhiteSpace(name) ? uuid : $"{CleanName(name)} ({uuid})";
        }

        private static string Known(string value) => string.IsNullOrWhiteSpace(value) ? BasisLocalization.Get("settings.cilboxPermissions.record.unknown") : value;

        private static string CleanText(string value) => string.IsNullOrEmpty(value) ? value : value.Replace("<", string.Empty).Replace(">", string.Empty).Trim();

        private static string Lines(params string[] lines)
        {
            List<string> kept = new List<string>(lines.Length);
            foreach (string line in lines)
            {
                if (!string.IsNullOrEmpty(line)) kept.Add(line);
            }
            return string.Join("\n", kept);
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
