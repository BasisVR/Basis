using System.IO;
using System.Text;
using Basis.BasisUI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.ImagePickup
{
    internal static class BasisImagePickupRejectionPopup
    {
        private const string RejectionTitleKey = "imagePickup.popup.rejected.title";
        private const string LimitTitleKey = "imagePickup.popup.limit.title";
        private const string BatchNoticeTitleKey =
            "imagePickup.popup.batchNotice.title";
        private const string AnimatedBatchTitleKey =
            "imagePickup.popup.animatedBatch.title";
        private const string AcceptLabelKey = "imagePickup.popup.accept";
        private const string UnknownFileKey = "imagePickup.popup.unknownFile";
        private const string DefaultReasonKey = "imagePickup.popup.defaultReason";
        private const string RejectionDescriptionKey =
            "imagePickup.popup.rejection.description";
        private const string LimitSummaryKey = "imagePickup.popup.limit.summary";
        private const string LimitAllowedSomeKey =
            "imagePickup.popup.limit.allowedSome";
        private const string LimitNoneImportedKey =
            "imagePickup.popup.limit.noneImported";
        private const string AnimatedWarningKey = "imagePickup.popup.animated.warning";
        private const float DescriptionHeightPadding = 24f;
        private const float PanelViewportMargin = 80f;
        private const float PanelWidthStep = 100f;
        private const float FallbackMaximumPanelHeight = 900f;
        private const float FallbackMaximumPanelWidth = 1100f;
        private const float MinimumDescriptionFontSize = 18f;

        public static void Show(string path, string reason)
        {
            ShowDialogue(BasisLocalization.Get(RejectionTitleKey), BuildDescription(path, reason));
        }

        public static void ShowImageLimit(int currentCount, int requestedCount)
        {
            ShowDialogue(BasisLocalization.Get(LimitTitleKey), BuildBatchNotice(currentCount, requestedCount, 0, 0));
        }

        public static void ShowBatchNotice(int currentCount, int requestedCount, int allowedCount, int animatedCount)
        {
            bool limited = allowedCount < requestedCount;
            bool animationWarning =
                animatedCount > BasisImagePickupSettings.AnimationBatchWarningThreshold;
            if (!limited && !animationWarning)
                return;

            string titleKey =
                limited && animationWarning ? BatchNoticeTitleKey
                : limited ? LimitTitleKey
                : AnimatedBatchTitleKey;
            string title = BasisLocalization.Get(titleKey);
            ShowDialogue(title, BuildBatchNotice(currentCount, requestedCount, allowedCount, animatedCount));
        }

        internal static string BuildDescription(string path, string reason)
        {
            string fileName;
            try
            {
                fileName = Path.GetFileName(path);
            }
            catch
            {
                fileName = path;
            }

            if (string.IsNullOrWhiteSpace(fileName))
                fileName = BasisLocalization.Get(UnknownFileKey);
            if (string.IsNullOrWhiteSpace(reason))
                reason = BasisLocalization.Get(DefaultReasonKey);

            return BasisLocalization.Get(RejectionDescriptionKey, EscapeRichText(fileName), EscapeRichText(reason));
        }

        internal static string BuildBatchNotice(
            int currentCount,
            int requestedCount,
            int allowedCount,
            int animatedCount
        )
        {
            int limit = BasisImagePickupSettings.MaxConcurrentImagesPerSender;
            currentCount = Mathf.Max(0, currentCount);
            requestedCount = Mathf.Max(0, requestedCount);
            allowedCount = Mathf.Clamp(allowedCount, 0, requestedCount);
            animatedCount = Mathf.Max(0, animatedCount);

            var description = new StringBuilder(384);
            if (allowedCount < requestedCount)
            {
                description.Append(BasisLocalization.Get(LimitSummaryKey, limit, currentCount));

                if (allowedCount > 0)
                {
                    description.Append(BasisLocalization.Get(LimitAllowedSomeKey, allowedCount, requestedCount));
                }
                else
                {
                    description.Append(BasisLocalization.Get(LimitNoneImportedKey));
                }
            }

            if (animatedCount > BasisImagePickupSettings.AnimationBatchWarningThreshold)
            {
                if (description.Length > 0)
                    description.Append("\n\n");
                description.Append(BasisLocalization.Get(AnimatedWarningKey, animatedCount));
            }

            return description.ToString();
        }

        private static void ShowDialogue(string title, string description)
        {
            bool menuWasAlreadyOpen = BasisMainMenu.Instance != null;

            if (!menuWasAlreadyOpen)
            {
                BasisMainMenu.Open();
            }
            if (BasisMainMenu.Instance == null)
            {
                return;
            }

            if (BasisMainMenu.Instance.Dialogue)
            {
                BasisMainMenu.Instance.Dialogue.ReleaseInstance();
            }

            BasisMainMenu.Instance.OpenDialogue(
                title,
                description,
                BasisLocalization.Get(AcceptLabelKey),
                _ =>
                {
                    if (!menuWasAlreadyOpen)
                    {
                        BasisMainMenu.Close();
                    }
                }
            );

            BasisMenuDialoguePanel dialogue = BasisMainMenu.Instance.Dialogue;
            if (dialogue != null)
            {
                ResizeDialogueToContent(dialogue, title);
            }
        }

        private static void ResizeDialogueToContent(BasisMenuDialoguePanel dialogue, string title)
        {
            BasisMenuPanel.PanelData panelData =
                BasisMenuDialoguePanel.DialoguePanelData;
            panelData.Title = title;
            dialogue.LoadData(panelData);

            TextMeshProUGUI descriptionLabel = dialogue.Descriptor.DescriptionLabel;
            if (descriptionLabel == null)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(dialogue.rectTransform);
                return;
            }

            float originalFontSize = descriptionLabel.fontSize;
            descriptionLabel.enableAutoSizing = false;
            RebuildDialogueLayout(dialogue, descriptionLabel);
            float baseDescriptionHeight = descriptionLabel.rectTransform.rect.height;

            RectTransform parentRect = dialogue.rectTransform.parent as RectTransform;
            float maximumHeight = GetMaximumPanelDimension(
                parentRect != null ? parentRect.rect.height : 0f,
                panelData.PanelSize.y,
                FallbackMaximumPanelHeight
            );
            float maximumWidth = GetMaximumPanelDimension(
                parentRect != null ? parentRect.rect.width : 0f,
                panelData.PanelSize.x,
                FallbackMaximumPanelWidth
            );

            Vector2 basePanelSize = panelData.PanelSize;
            panelData.PanelSize = CalculateHeightFirstPanelSize(
                basePanelSize,
                baseDescriptionHeight,
                descriptionLabel.preferredHeight,
                maximumHeight
            );
            dialogue.LoadData(panelData);
            RebuildDialogueLayout(dialogue, descriptionLabel);

            while (DescriptionOverflows(descriptionLabel) && panelData.PanelSize.x + 0.5f < maximumWidth)
            {
                panelData.PanelSize.x = Mathf.Min(maximumWidth, panelData.PanelSize.x + PanelWidthStep);
                dialogue.LoadData(panelData);
                RebuildDialogueLayout(dialogue, descriptionLabel);
            }

            Vector2 fittedSize = CalculateHeightFirstPanelSize(
                basePanelSize,
                baseDescriptionHeight,
                descriptionLabel.preferredHeight,
                maximumHeight
            );
            fittedSize.x = panelData.PanelSize.x;
            if (!Mathf.Approximately(fittedSize.y, panelData.PanelSize.y))
            {
                panelData.PanelSize = fittedSize;
                dialogue.LoadData(panelData);
                RebuildDialogueLayout(dialogue, descriptionLabel);
            }

            if (DescriptionOverflows(descriptionLabel))
            {
                descriptionLabel.enableAutoSizing = true;
                descriptionLabel.fontSizeMax = originalFontSize;
                descriptionLabel.fontSizeMin = Mathf.Min(originalFontSize, MinimumDescriptionFontSize);
                RebuildDialogueLayout(dialogue, descriptionLabel);
            }
        }

        internal static Vector2 CalculateHeightFirstPanelSize(
            Vector2 basePanelSize,
            float currentDescriptionHeight,
            float preferredDescriptionHeight,
            float maximumPanelHeight
        )
        {
            if (preferredDescriptionHeight <= currentDescriptionHeight + 0.5f)
                return basePanelSize;

            float additionalHeight =
                preferredDescriptionHeight
                - currentDescriptionHeight
                + DescriptionHeightPadding;
            float clampedMaximumHeight = Mathf.Max(basePanelSize.y, maximumPanelHeight);
            return new Vector2(basePanelSize.x, Mathf.Min(clampedMaximumHeight, basePanelSize.y + additionalHeight));
        }

        private static float GetMaximumPanelDimension(
            float viewportDimension,
            float baseDimension,
            float fallbackMaximum
        )
        {
            float viewportMaximum =
                viewportDimension > PanelViewportMargin
                    ? viewportDimension - PanelViewportMargin
                    : fallbackMaximum;
            return Mathf.Max(baseDimension, Mathf.Min(viewportMaximum, fallbackMaximum));
        }

        private static void RebuildDialogueLayout(BasisMenuDialoguePanel dialogue, TextMeshProUGUI descriptionLabel)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(dialogue.rectTransform);
            Canvas.ForceUpdateCanvases();
            descriptionLabel.ForceMeshUpdate();
        }

        private static bool DescriptionOverflows(TextMeshProUGUI descriptionLabel)
        {
            return descriptionLabel.preferredHeight
                > descriptionLabel.rectTransform.rect.height + 0.5f;
        }

        private static string EscapeRichText(string value)
        {
            return value
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");
        }
    }
}
