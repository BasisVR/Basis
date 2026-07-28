using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI
{
    /// <summary>
    /// Hotbar tab for the <see cref="BasisNotificationCenter"/>. Opens a vertical page
    /// listing popups still waiting on the user (re-openable) and a history of past
    /// accept/deny/dismiss outcomes, plus a do-not-disturb toggle. A "new action"
    /// indicator dot lights the hotbar button while there are unseen notifications.
    /// </summary>
    public class NotificationProvider : BasisMenuActionProvider<BasisMainMenu>
    {
        [RuntimeInitializeOnLoadMethod]
        public static void AddToMenu()
        {
            BasisMenuBase<BasisMainMenu>.AddProvider(new NotificationProvider());
        }

        public const string TitleKey = "notifications.title";
        public static string TitleStatic => BasisLocalization.Get(TitleKey);
        public override string Title => TitleStatic;
        public override string IconAddress => AddressableAssets.Sprites.FileTray;
        public override int Order => 90;
        public override bool Hidden => false;

        private NotificationPanelController _controller;

        public override void OnButtonCreated(PanelButton button)
        {
            // Pulse the bell icon while there are unresolved pending notifications.
            // No selection/indicator dot — the pulse alone signals pending items.
            NotificationBellPulse pulse = button.gameObject.AddComponent<NotificationBellPulse>();
            pulse.Target = button.Descriptor.IconImage;
        }

        public override void RunAction()
        {
            // Toggle: clicking the active tab closes it.
            if (BasisMainMenu.ActiveMenuTitle == Title)
            {
                BasisMainMenu.CloseActivePanel();
                return;
            }

            BasisMenuPanel panel = BasisMainMenu.CreateActiveMenu(
                BasisMenuPanel.PanelData.Standard(Title),
                BasisMenuPanel.PanelStyles.Page);

            // Vertical scrollable page — same pattern as UserListProvider.
            PanelTabPage tab = PanelTabPage.CreateVertical(panel.Descriptor.ContentParent);
            tab.Descriptor.SetTitle(Title);
            tab.Descriptor.SetIcon(AddressableAssets.Sprites.FileTray);
            RectTransform root = tab.Descriptor.ContentParent;

            // Fixed control at the top: do-not-disturb toggle. Added before the
            // controller so it is preserved across list rebuilds.
            PanelToggle ignoreToggle = PanelToggle.CreateNewEntry(root);
            ignoreToggle.Descriptor.SetTitle(BasisLocalization.Get("notifications.ignore"));
            ignoreToggle.Descriptor.SetDescription(BasisLocalization.Get("notifications.ignore.description"));
            ignoreToggle.SetValueWithoutNotify(BasisNotificationCenter.IgnoreMode);
            ignoreToggle.OnValueChanged = value => BasisNotificationCenter.IgnoreMode = value;

            _controller = panel.gameObject.AddComponent<NotificationPanelController>();
            _controller.Root = root;
            _controller.TabDescriptor = tab.Descriptor;
            _controller.Initialize();

            panel.Descriptor.ForceRebuild();
        }

        public override void OnReleaseEvent()
        {
            _controller = null;
        }

        /// <summary>
        /// Manages the list below the fixed controls: rebuilds the Pending and History
        /// sections whenever the notification center changes, and cleans up its event
        /// subscription on destroy.
        /// </summary>
        /// <summary>
        /// Pulses the bell button's icon color while there are unresolved pending
        /// notifications so the main menu draws the eye. Self-contained: destroyed with
        /// the button, and restores the icon's original color when nothing is pending.
        /// (No dedicated has/has-no-notification icon exists in the icon set, so colour
        /// is the signal.)
        /// </summary>
        private sealed class NotificationBellPulse : MonoBehaviour
        {
            public Graphic Target;

            private static readonly Color PulseColor = new Color(1f, 0.62f, 0.2f, 1f);
            private const float Speed = 3.2f;

            private Color _baseColor = Color.white;
            private bool _captured;
            private bool _pulsing;

            private void Update()
            {
                if (Target == null) return;
                if (!_captured)
                {
                    _baseColor = Target.color;
                    _captured = true;
                }

                if (BasisNotificationCenter.PendingCount > 0)
                {
                    float t = (Mathf.Sin(Time.unscaledTime * Speed) + 1f) * 0.5f;
                    // SetColor is a render-only update; Graphic.color calls SetVerticesDirty and
                    // would rebuild + rebatch the whole hotbar canvas every frame while pending.
                    Target.canvasRenderer.SetColor(Color.Lerp(_baseColor, PulseColor, t));
                    _pulsing = true;
                }
                else if (_pulsing)
                {
                    Target.canvasRenderer.SetColor(_baseColor);
                    _pulsing = false;
                }
            }

            private void OnDisable()
            {
                if (Target != null && _captured)
                {
                    Target.canvasRenderer.SetColor(_baseColor);
                    _pulsing = false;
                }
            }
        }

        private sealed class NotificationPanelController : MonoBehaviour
        {
            public RectTransform Root;
            public PanelElementDescriptor TabDescriptor;

            // TMP rich-text colors for the history outcome badges.
            private const string AcceptedColor = "#22c55e";
            private const string DeniedColor = "#ef4444";
            private const string DismissedColor = "#9ca3af";

            // Dimmed styling for the per-row timestamp line.
            private const string TimeColor = "#9ca3af";

            // Children before this index are fixed controls (the ignore toggle) and are
            // preserved across rebuilds; everything from here down is list content.
            private int _firstItemSiblingIndex;

            public void Initialize()
            {
                _firstItemSiblingIndex = Root != null ? Root.childCount : 0;
                BasisNotificationCenter.Changed += Rebuild;
                Rebuild();
            }

            private void OnDestroy()
            {
                BasisNotificationCenter.Changed -= Rebuild;
            }

            private void Rebuild()
            {
                if (Root == null) return;

                for (int i = Root.childCount - 1; i >= _firstItemSiblingIndex; i--)
                {
                    Destroy(Root.GetChild(i).gameObject);
                }

                IReadOnlyList<BasisNotification> all = BasisNotificationCenter.All;

                // Pending (newest first) — still awaiting a yes/no, re-openable.
                // Shown directly as a collapsible section (open by default) instead of
                // gated behind a show/hide toggle.
                PanelSectionToggle pendingSection = PanelSectionToggle.CreateNewEntry(Root);
                pendingSection.SetTitle(BasisLocalization.Get("notifications.pending"));
                int pendingStart = Root.childCount;
                int pending = 0;
                for (int i = all.Count - 1; i >= 0; i--)
                {
                    if (all[i].Status != BasisNotificationStatus.Pending) continue;
                    BuildPendingRow(all[i]);
                    pending++;
                }
                if (pending == 0) AddEmpty(BasisLocalization.Get("notifications.empty.pending"));
                PanelSectionToggleHelpers.FinalizeBoxedSectionFromIndex(pendingSection, Root, pendingStart, true,
                    _ => TabDescriptor?.ForceRebuild());

                // History (newest first) — resolved outcomes.
                PanelSectionToggle historySection = PanelSectionToggle.CreateNewEntry(Root);
                historySection.SetTitle(BasisLocalization.Get("notifications.history"));
                int historyStart = Root.childCount;
                int history = 0;
                for (int i = all.Count - 1; i >= 0; i--)
                {
                    if (all[i].Status == BasisNotificationStatus.Pending) continue;
                    BuildHistoryRow(all[i]);
                    history++;
                }
                if (history == 0) AddEmpty(BasisLocalization.Get("notifications.empty.history"));
                PanelSectionToggleHelpers.FinalizeBoxedSectionFromIndex(historySection, Root, historyStart, true,
                    _ => TabDescriptor?.ForceRebuild());

                if (TabDescriptor != null) TabDescriptor.ForceRebuild();
            }

            private void AddEmpty(string text)
            {
                PanelElementDescriptor empty = PanelElementDescriptor.CreateNew(
                    PanelElementDescriptor.ElementStyles.Entry, Root);
                empty.SetTitle(string.Empty);
                empty.SetDescription(text);
            }

            private void BuildPendingRow(BasisNotification n)
            {
                PanelElementDescriptor row = PanelElementDescriptor.CreateNew(
                    PanelElementDescriptor.ElementStyles.Entry, Root);
                row.SetTitle(n.Title);
                row.SetDescription(WithTimestamp(n.Description, n.CreatedUtc));

                // Horizontal action row — same pattern as the library Yes/No dialogs.
                PanelTabGroup actions = PanelTabGroup.CreateNew(
                    row.ContentParent, LayoutDirection.HorizontalNoBackground);
                actions.Descriptor.SetHeight(60);

                // A generic dialog with no reopen factory can't be rebuilt — only dismissed.
                if (n.Reopen != null)
                {
                    PanelButton open = PanelButton.CreateNew(
                        PanelButton.ButtonStyles.AcceptButton, actions.TabButtonParent);
                    open.Descriptor.SetTitle(BasisLocalization.Get("notifications.open"));
                    open.OnClicked += () => BasisNotificationCenter.Reopen(n);
                }

                PanelButton dismiss = PanelButton.CreateNew(
                    PanelButton.ButtonStyles.CancelButton, actions.TabButtonParent);
                dismiss.Descriptor.SetTitle(BasisLocalization.Get("notifications.dismiss"));
                dismiss.OnClicked += () => BasisNotificationCenter.Dismiss(n);
            }

            private void BuildHistoryRow(BasisNotification n)
            {
                PanelElementDescriptor row = PanelElementDescriptor.CreateNew(
                    PanelElementDescriptor.ElementStyles.Entry, Root);

                row.SetTitle(string.IsNullOrEmpty(n.Title)
                    ? OutcomeBadge(n.Status)
                    : $"{n.Title}   {OutcomeBadge(n.Status)}");

                row.SetDescription(WithTimestamp(n.Description, n.ResolvedUtc ?? n.CreatedUtc));
            }

            // Appends a dimmed local-time stamp ("2:34 PM") below the description.
            private static string WithTimestamp(string description, DateTime utc)
            {
                string stamp = $"<color={TimeColor}><size=80%>{utc.ToLocalTime():h:mm tt}</size></color>";
                return string.IsNullOrEmpty(description) ? stamp : $"{description}\n{stamp}";
            }

            private static string OutcomeBadge(BasisNotificationStatus status)
            {
                switch (status)
                {
                    case BasisNotificationStatus.Accepted:
                        return $"<color={AcceptedColor}>✓ {BasisLocalization.Get("notifications.outcome.accepted")}</color>";
                    case BasisNotificationStatus.Denied:
                        return $"<color={DeniedColor}>✗ {BasisLocalization.Get("notifications.outcome.denied")}</color>";
                    default:
                        return $"<color={DismissedColor}>{BasisLocalization.Get("notifications.outcome.dismissed")}</color>";
                }
            }
        }
    }
}
