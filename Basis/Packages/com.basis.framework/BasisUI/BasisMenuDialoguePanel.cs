using System;
using System.Collections.Generic;
using Basis.BTween;
using Basis.Scripts.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI
{
    public class BasisMenuDialoguePanel : BasisMenuPanel
    {

        public static class DialogueStyles
        {
            public static string Default => "Packages/com.basis.sdk/Prefabs/Dialogue Panel.prefab";
        }

        public static PanelData DialoguePanelData => new PanelData
        {
            Title = "Dialogue",
            PanelSize = new Vector2(700, 500),
            // Sits in front of the page it covers like every other overlay popup. Coplanar with
            // the page (z 0) its collider ties with the page's on ray distance, which used to be
            // the only thing deciding which of the two took a press.
            PanelPosition = new Vector3(0, -100, -5),
        };

        public static string AcceptDefault = "Accept";
        public static string DeclineDefault = "Decline";

        private const float DescriptionHeightPadding = 24f;
        private const float TitleWidthPadding = 16f;
        private const float PanelWidthStep = 100f;

        public string Title;
        public string Description;
        public string Accept;
        public string Decline;

        /// <summary>
        /// How much attention this dialogue deserves, shown with the same tint the settings cards
        /// use to grade themselves. Set before <see cref="FillDialogue"/>; the default leaves the
        /// dialogue in the plain colours its prefab ships with.
        /// </summary>
        public BasisPanelSeverity Severity = BasisPanelSeverity.None;

        /// <summary>
        /// Which bucket this dialogue lands in when it reaches the notification center,
        /// so the history list can be filtered down to it.
        /// </summary>
        public BasisNotificationCategory Category = BasisNotificationCategory.System;

        public bool BlocksOtherActions;

        /// <summary>
        /// When false, closing the dialogue dismisses it silently instead of parking
        /// it in the notification center.
        /// </summary>
        public bool CaptureOnClose = true;

        public PanelButton AcceptButton;
        public PanelButton DeclineButton;
        public PanelButton AlternateButton;
        public Action<bool> Callback;

        public string Alternate;
        public Action AlternateCallback;


        private bool _resolved;

        public override void OnCreateEvent()
        {
            base.OnCreateEvent();
            AcceptButton.OnClicked += () => Resolve(true);
            DeclineButton.OnClicked += () => Resolve(false);

            // Closing this dialogue without choosing accept/decline (switching tabs,
            // closing the menu, the panel being destroyed) routes it to the
            // notification center as a pending entry that can be brought back up,
            // instead of silently dropping the request.
            OnInstanceReleased += CaptureIfUnresolved;

            // An incoming dialogue may have forced the menu open, which tore down the page
            // and virtual keyboard the user had up; put them back now it is done. No-ops for
            // the in-menu confirmations that never displaced anything.
            OnInstanceReleased += BasisMenuPromptRestore.RestoreAfterPrompt;
        }

        private void Resolve(bool accepted)
        {
            _resolved = true;
            BasisNotificationCenter.LogResolved(
                Title,
                Description,
                AddressableAssets.Sprites.Information,
                accepted ? BasisNotificationStatus.Accepted : BasisNotificationStatus.Denied,
                Category);
            Callback?.Invoke(accepted);
            ReleaseInstance();
        }

        /// <summary>
        /// Adds a third button to the dialogue between Accept and Decline. Choosing it
        /// resolves the dialogue and invokes <paramref name="onChosen"/> instead of the
        /// binary accept/decline callback. The button is created at runtime so existing
        /// two-button callers are unaffected.
        /// </summary>
        public void EnableAlternate(string label, Action onChosen)
        {
            if (string.IsNullOrEmpty(label) || onChosen == null) return;
            if (AcceptButton == null || DeclineButton == null) return;

            Alternate = label;
            AlternateCallback = onChosen;

            if (AlternateButton != null)
            {
                AlternateButton.Descriptor.SetTitle(label);
                return;
            }

            AlternateButton = PanelButton.CreateNew(AcceptButton.rectTransform.parent);
            AlternateButton.Descriptor.SetTitle(label);
            // The accent-blue standard style, matching the Accept/Decline button family
            // instead of the neutral grey the base button prefab ships with.
            AlternateButton.ButtonStyling.SetStyle("Button Standard");
            AlternateButton.rectTransform.SetSiblingIndex(DeclineButton.rectTransform.GetSiblingIndex());
            MatchButtonMetrics(AcceptButton, AlternateButton);
            AlternateButton.OnClicked += ResolveAlternate;
        }

        public PanelButton AddOption(string label, Action onChosen, bool closes = true)
        {
            if (string.IsNullOrEmpty(label) || onChosen == null) return null;
            if (AcceptButton == null || DeclineButton == null) return null;

            PanelButton button = PanelButton.CreateNew(AcceptButton.rectTransform.parent);
            button.Descriptor.SetTitle(label);
            button.ButtonStyling.SetStyle("Button Standard");
            button.rectTransform.SetSiblingIndex(DeclineButton.rectTransform.GetSiblingIndex());
            MatchButtonMetrics(AcceptButton, button);
            button.OnClicked += () =>
            {
                onChosen();
                if (closes) Resolve(true);
            };
            return button;
        }

        /// <summary>One row of the list <see cref="ShowDetails"/> puts under the description.</summary>
        public readonly struct DetailRow
        {
            public readonly string Title;
            public readonly string Value;

            public DetailRow(string title, string value)
            {
                Title = title;
                Value = value;
            }
        }

        private const float DetailRowHeight = 96f;
        private const float DetailListMinHeight = 120f;
        private const float DetailListMaxHeight = 420f;
        private const float DetailPanelMaxHeight = 800f;
        private const float DetailTopGap = 16f;
        private const float DetailBottomInset = 8f;
        private const float DetailSideInset = 32f;

        /// <summary>
        /// Puts a scrollable list of title/value rows between the description and the buttons, and
        /// grows the dialogue to give it room, so a confirmation can show exactly what it is about
        /// to act on. Built at runtime like <see cref="EnableAlternate"/>, so plain two-button
        /// callers are unaffected. Call after <see cref="FillDialogue"/> — the list is placed under
        /// however many lines the description wrapped to.
        /// </summary>
        public void ShowDetails(IReadOnlyList<DetailRow> rows)
        {
            if (rows == null || rows.Count == 0) return;
            if (Descriptor == null || !Descriptor.HasDescription) return;

            RectTransform host = Descriptor.DescriptionLabel.rectTransform.parent as RectTransform;
            if (host == null) return;

            float textWidth = Mathf.Max(100f, Data.PanelSize.x - 32f - 64f);
            float questionHeight = Mathf.Clamp(
                Mathf.Ceil(Descriptor.DescriptionLabel.GetPreferredValues(Description ?? string.Empty, textWidth, 0f).y),
                40f, 200f);

            float desiredList = Mathf.Clamp(rows.Count * DetailRowHeight + DetailBottomInset, DetailListMinHeight, DetailListMaxHeight);
            float panelHeight = Mathf.Clamp(220f + questionHeight + DetailTopGap + desiredList + DetailBottomInset,
                Data.PanelSize.y, DetailPanelMaxHeight);
            if (panelHeight > Data.PanelSize.y)
            {
                PanelData data = Data;
                data.PanelSize = new Vector2(data.PanelSize.x, panelHeight);
                Data = data;
                rectTransform.sizeDelta = data.PanelSize;
                BasisGraphicUIRayCaster.SetBoxColliderToRectTransform(gameObject);
            }

            PanelElementDescriptor scroll = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.ScrollViewVertical, host);
            RectTransform scrollArea = scroll.rectTransform;
            scrollArea.anchorMin = Vector2.zero;
            scrollArea.anchorMax = Vector2.one;
            scrollArea.offsetMin = new Vector2(DetailSideInset, DetailBottomInset);
            scrollArea.offsetMax = new Vector2(-DetailSideInset, -(questionHeight + DetailTopGap));

            RectTransform content = scroll.ContentParent;
            ScrollRect scrollView = content != null ? content.GetComponentInParent<ScrollRect>() : null;
            if (scrollView != null && scrollView.viewport != null)
            {
                RectTransform viewport = scrollView.viewport;
                viewport.anchorMin = Vector2.zero;
                viewport.anchorMax = Vector2.one;
                viewport.offsetMin = Vector2.zero;
                viewport.offsetMax = new Vector2(-25f, 0f); // clear of the vertical scrollbar
                if (!viewport.TryGetComponent(out RectMask2D _))
                {
                    viewport.gameObject.AddComponent<RectMask2D>();
                }
            }

            for (int Index = 0; Index < rows.Count; Index++)
            {
                PanelElementDescriptor row = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Entry, content);
                row.SetTitle(rows[Index].Title);
                row.SetDescription(rows[Index].Value);
                ReleaseControlSlot(row);
            }

            if (content != null)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(content);
            }
            if (scrollView != null)
            {
                scrollView.verticalNormalizedPosition = 1f;
            }
        }

        /// <summary>
        /// An Entry card reserves a 300-wide control slot even when nothing is put in it; on a
        /// dialogue-width row that squeezes the labels into wrapping. These rows are text only, so
        /// give the labels the whole card.
        /// </summary>
        private static void ReleaseControlSlot(PanelElementDescriptor row)
        {
            if (row == null || row.Header == null) return;

            Transform slot = row.Header.Find("Title/Element");
            if (slot != null) slot.gameObject.SetActive(false);
        }

        private void ResolveAlternate()
        {
            _resolved = true;
            BasisNotificationCenter.LogResolved(
                Title,
                Description,
                AddressableAssets.Sprites.Information,
                BasisNotificationStatus.Accepted,
                Category);
            AlternateCallback?.Invoke();
            ReleaseInstance();
        }

        private static void MatchButtonMetrics(PanelButton template, PanelButton target)
        {
            target.rectTransform.sizeDelta = new Vector2(
                target.rectTransform.sizeDelta.x, template.rectTransform.sizeDelta.y);

            LayoutElement from = template.GetComponent<LayoutElement>();
            LayoutElement to = target.Layout;
            if (to == null) return;

            if (from != null)
            {
                to.minWidth = from.minWidth;
                to.preferredWidth = from.preferredWidth;
                to.flexibleWidth = from.flexibleWidth;
                to.minHeight = from.minHeight;
                to.preferredHeight = from.preferredHeight;
                to.flexibleHeight = from.flexibleHeight;
            }
            else
            {
                to.minWidth = 0f;
                to.flexibleWidth = 1f;
                to.preferredHeight = template.rectTransform.sizeDelta.y;
            }
        }

        private void CaptureIfUnresolved()
        {
            if (_resolved) return;
            _resolved = true;

            if (!CaptureOnClose) return;

            // Snapshot the dialogue contents so the captured entry can rebuild the
            // exact same prompt with its original callback still attached.
            string title = Title;
            string description = Description;
            string accept = Accept;
            string deny = Decline;
            BasisPanelSeverity severity = Severity;
            BasisNotificationCategory category = Category;
            Action<bool> callback = Callback;

            BasisNotificationCenter.AddPending(
                title,
                description,
                AddressableAssets.Sprites.Information,
                reopen: () =>
                {
                    if (!BasisMainMenu.Instance) BasisMainMenu.Open();
                    if (!BasisMainMenu.Instance) return;
                    if (BasisMainMenu.Instance.Dialogue) BasisMainMenu.Instance.Dialogue.ReleaseInstance();
                    // CreateInternal bypasses ignore mode so re-open always shows.
                    BasisMainMenu.Instance.Dialogue = CreateInternal(title, description, accept, deny, callback, severity, category);
                },
                onDismiss: () => callback?.Invoke(false),
                category: category);
        }

        /// <summary>
        /// Instantiate a new Panel and load in the corresponding panel data.
        /// When ignore mode is on the prompt is routed to the notification center
        /// instead of being shown, and this returns null.
        /// </summary>
        public static BasisMenuDialoguePanel CreateNew(
            string title,
            string description,
            string accept,
            string deny,
            Action<bool> callback,
            bool divertible = false,
            BasisPanelSeverity severity = BasisPanelSeverity.None,
            BasisNotificationCategory category = BasisNotificationCategory.System)
        {
            // Only "divertible" (incoming/unsolicited) popups route to the notification
            // list under do-not-disturb or while the admin/moderator panel is open.
            // User-initiated confirmations (the default) always show.
            if (divertible && BasisNotificationCenter.RouteToNotifications)
            {
                return SuppressToNotifications(title, description, accept, deny, callback, severity, category);
            }
            return CreateInternal(title, description, accept, deny, callback, severity, category);
        }

        /// <summary>
        /// Instantiate a new Panel and load in the corresponding panel data.
        /// When ignore mode is on the prompt is routed to the notification center
        /// instead of being shown, and this returns null.
        /// </summary>
        public static BasisMenuDialoguePanel CreateNew(
            string title,
            string description,
            string accept,
            Action<bool> callback,
            bool divertible = false,
            BasisPanelSeverity severity = BasisPanelSeverity.None,
            BasisNotificationCategory category = BasisNotificationCategory.System)
        {
            if (divertible && BasisNotificationCenter.RouteToNotifications)
            {
                return SuppressToNotifications(title, description, accept, null, callback, severity, category);
            }
            return CreateInternal(title, description, accept, null, callback, severity, category);
        }

        /// <summary>
        /// Actually instantiate and show the dialogue, bypassing ignore mode. Used both
        /// by the normal path and when a suppressed/captured prompt is re-opened.
        /// </summary>
        private static BasisMenuDialoguePanel CreateInternal(
            string title,
            string description,
            string accept,
            string deny,
            Action<bool> callback,
            BasisPanelSeverity severity = BasisPanelSeverity.None,
            BasisNotificationCategory category = BasisNotificationCategory.System)
        {
            if (!BasisMainMenu.Instance)
            {
                return null;
            }

            // The dialogue parents under the menu's panel root. That root can be gone
            // while the menu chrome is torn down — and because the global exception
            // notifier opens dialogues, it can reach here mid-teardown. Bail quietly
            // instead of passing a null parent into CreateNew, whose "Parent Missing!"
            // LogError would feed straight back into the notifier.
            var menuInstance = BasisMainMenu.Instance.MenuObjectInstance;
            if (menuInstance == null || menuInstance.PanelRoot == null)
            {
                return null;
            }

            Component parent = menuInstance.PanelRoot;

            BasisMenuDialoguePanel panel = CreateNew<BasisMenuDialoguePanel>(DialogueStyles.Default, parent);
            if (panel == null)
            {
                return null;
            }
            panel.LoadData(DialoguePanelData);
            panel.Callback = callback;
            panel.Severity = severity;
            panel.Category = category;
            panel.SetLayer(PanelLayer.Overlay);
            panel.FillDialogue(title, description, accept, deny);
            BasisPanelMoveHandle.Attach(panel, nameof(BasisMenuDialoguePanel));
            panel.FitHeaderToContent();

            // Pop-in animation for dialogues
            UIAnimations.PopIn(panel);

            return panel;
        }

        /// <summary>
        /// Register an unshown prompt as a pending notification that can be brought up
        /// on demand. Returns null since no panel is created.
        /// </summary>
        private static BasisMenuDialoguePanel SuppressToNotifications(
            string title,
            string description,
            string accept,
            string deny,
            Action<bool> callback,
            BasisPanelSeverity severity = BasisPanelSeverity.None,
            BasisNotificationCategory category = BasisNotificationCategory.System)
        {
            BasisNotificationCenter.AddPending(
                title,
                description,
                AddressableAssets.Sprites.Information,
                reopen: () =>
                {
                    if (!BasisMainMenu.Instance) BasisMainMenu.Open();
                    if (!BasisMainMenu.Instance) return;
                    if (BasisMainMenu.Instance.Dialogue) BasisMainMenu.Instance.Dialogue.ReleaseInstance();
                    BasisMainMenu.Instance.Dialogue = CreateInternal(title, description, accept, deny, callback, severity, category);
                },
                onDismiss: () => callback?.Invoke(false),
                category: category);
            return null;
        }

        /// <summary>
        /// Fits the dialogue to its description using the standard main-provider panel as its
        /// maximum envelope. Long dialogues grow upward from the same bottom edge as a standard
        /// provider, then widen in steps if the text still overflows.
        /// </summary>
        public void FitDescriptionToContent()
        {
            if (Descriptor == null || !Descriptor.HasDescription)
            {
                return;
            }

            TextMeshProUGUI descriptionLabel = Descriptor.DescriptionLabel;
            if (descriptionLabel == null)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(rectTransform);
                return;
            }

            FitHeaderToContent();
            descriptionLabel.enableAutoSizing = false;
            RebuildDialogueLayout(descriptionLabel);
            float baseDescriptionHeight = GetUsableDescriptionHeight(descriptionLabel);
            if (descriptionLabel.preferredHeight <= baseDescriptionHeight + 0.5f)
            {
                return;
            }

            Vector2 basePanelSize = Data.PanelSize;
            Vector3 basePanelPosition = Data.PanelPosition;
            PanelData standardPanel = PanelData.Standard(string.Empty);
            float maximumHeight = standardPanel.PanelSize.y;
            float maximumWidth = standardPanel.PanelSize.x;
            float bottomEdge = standardPanel.PanelPosition.y - standardPanel.PanelSize.y * 0.5f;

            Vector2 panelSize = CalculateHeightFirstPanelSize(
                basePanelSize,
                baseDescriptionHeight,
                descriptionLabel.preferredHeight,
                maximumHeight);
            ApplyPanelSize(panelSize, basePanelPosition, bottomEdge);
            RebuildDialogueLayout(descriptionLabel);

            while (DescriptionOverflows(descriptionLabel) && panelSize.x + 0.5f < maximumWidth)
            {
                panelSize.x = Mathf.Min(maximumWidth, panelSize.x + PanelWidthStep);
                ApplyPanelSize(panelSize, basePanelPosition, bottomEdge);
                RebuildDialogueLayout(descriptionLabel);
            }

            Vector2 fittedSize = CalculateHeightFirstPanelSize(
                basePanelSize,
                baseDescriptionHeight,
                descriptionLabel.preferredHeight,
                maximumHeight);
            fittedSize.x = panelSize.x;
            if (!Mathf.Approximately(fittedSize.y, panelSize.y))
            {
                panelSize = fittedSize;
                ApplyPanelSize(panelSize, basePanelPosition, bottomEdge);
                RebuildDialogueLayout(descriptionLabel);
            }

        }

        public void FitHeaderToContent()
        {
            if (Descriptor == null || Descriptor.TitleLabel == null)
            {
                return;
            }

            TextMeshProUGUI titleLabel = Descriptor.TitleLabel;
            LayoutRebuilder.ForceRebuildLayoutImmediate(rectTransform);
            Canvas.ForceUpdateCanvases();
            titleLabel.ForceMeshUpdate();

            float overflowWidth = titleLabel.preferredWidth - titleLabel.rectTransform.rect.width;
            if (overflowWidth <= 0.5f)
            {
                return;
            }

            float maximumWidth = PanelData.Standard(string.Empty).PanelSize.x;
            float requiredWidth = Data.PanelSize.x + overflowWidth + TitleWidthPadding;
            float targetWidth = Mathf.Min(
                maximumWidth,
                Mathf.Ceil(requiredWidth / PanelWidthStep) * PanelWidthStep);
            if (targetWidth <= Data.PanelSize.x + 0.5f)
            {
                return;
            }

            PanelData data = Data;
            data.PanelSize.x = targetWidth;
            Data = data;
            rectTransform.sizeDelta = data.PanelSize;
            BasisGraphicUIRayCaster.SetBoxColliderToRectTransform(gameObject);

            LayoutRebuilder.ForceRebuildLayoutImmediate(rectTransform);
            Canvas.ForceUpdateCanvases();
            titleLabel.ForceMeshUpdate();
        }

        public static Vector2 CalculateHeightFirstPanelSize(
            Vector2 basePanelSize,
            float currentDescriptionHeight,
            float preferredDescriptionHeight,
            float maximumPanelHeight)
        {
            if (preferredDescriptionHeight <= currentDescriptionHeight + 0.5f)
            {
                return basePanelSize;
            }

            float additionalHeight =
                preferredDescriptionHeight
                - currentDescriptionHeight
                + DescriptionHeightPadding;
            float clampedMaximumHeight = Mathf.Max(basePanelSize.y, maximumPanelHeight);
            return new Vector2(
                basePanelSize.x,
                Mathf.Min(clampedMaximumHeight, basePanelSize.y + additionalHeight));
        }

        private void ApplyPanelSize(Vector2 panelSize, Vector3 basePanelPosition, float bottomEdge)
        {
            PanelData data = Data;
            data.PanelSize = panelSize;
            data.PanelPosition = new Vector3(
                basePanelPosition.x,
                bottomEdge + panelSize.y * 0.5f,
                basePanelPosition.z);
            Data = data;
            rectTransform.sizeDelta = panelSize;
            transform.localPosition = data.PanelPosition;
            BasisGraphicUIRayCaster.SetBoxColliderToRectTransform(gameObject);
        }

        private void RebuildDialogueLayout(TextMeshProUGUI descriptionLabel)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(rectTransform);
            Canvas.ForceUpdateCanvases();
            descriptionLabel.ForceMeshUpdate();
        }

        private bool DescriptionOverflows(TextMeshProUGUI descriptionLabel)
        {
            return descriptionLabel.preferredHeight > GetUsableDescriptionHeight(descriptionLabel) + 0.5f;
        }

        private float GetUsableDescriptionHeight(TextMeshProUGUI descriptionLabel)
        {
            float height = descriptionLabel.rectTransform.rect.height;
            RectTransform footer = AcceptButton != null
                ? AcceptButton.rectTransform.parent as RectTransform
                : null;
            if (footer == null)
            {
                return height;
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(footer);
            return Mathf.Max(0f, height - footer.rect.height);
        }

        public void FillDialogue(string title, string description, string accept, string decline = null)
        {
            Title = title;
            Description = description;
            Accept = accept;

            Descriptor.SetTitle(title);
            Descriptor.SetDescription(description);
            // Captured after the text is in place and never re-graded — the dialogue's severity is
            // fixed by whatever raised it. A None severity leaves the prefab's colours untouched.
            BasisPanelTint.Apply(BasisPanelTint.Capture(Descriptor), Severity, false);

            AcceptButton.Descriptor.SetTitle(Accept);

            if (!string.IsNullOrEmpty(decline))
            {
                Decline = decline;
                DeclineButton.Descriptor.SetTitle(decline);
                DeclineButton.gameObject.SetActive(true);
            }
            else
            {
                DeclineButton.gameObject.SetActive(false);
            }

            FitDescriptionToContent();
        }
    }
}
