using Basis.Scripts.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI
{
    public static class BasisTextFieldPreview
    {
        private const float PanelWidth = 900f;
        private const float PanelHeight = 760f;
        private const float SideInset = 32f;
        private const float BottomInset = 8f;
        private const float TopGap = 16f;

        public static void Open(PanelTextField field, TMP_InputField inputField)
        {
            if (field == null || inputField == null) return;

            BasisMenuBase<BasisMainMenu> menu = BasisMenuBase<BasisMainMenu>.Instance;
            if (menu == null || menu.Dialogue != null) return;

            string title = field.Descriptor && !string.IsNullOrEmpty(field.Descriptor.Title) ? field.Descriptor.Title : BasisLocalization.Get("ui.textPreview.title");
            string body = BasisLocalization.Get("ui.textPreview.body");
            menu.OpenDialogue(title, body, BasisLocalization.Get("ui.ok"), _ => { });

            BasisMenuDialoguePanel dialogue = menu.Dialogue;
            if (dialogue == null) return;
            dialogue.CaptureOnClose = false;

            string text = inputField.text ?? string.Empty;
            Resize(dialogue);
            RectTransform content = CreateContentArea(dialogue, body);
            if (content != null)
            {
                CreateTextRow(content).SetRichDescription(text);
                BuildDetailsSection(content, field, inputField);
                BuildFormattingSection(content);
                LayoutRebuilder.ForceRebuildLayoutImmediate(content);
                ScrollRect scrollView = content.GetComponentInParent<ScrollRect>();
                if (scrollView != null) scrollView.verticalNormalizedPosition = 1f;
            }
            AddButtons(dialogue, field, inputField, text);
        }

        private static void Resize(BasisMenuDialoguePanel dialogue)
        {
            dialogue.Data.PanelSize = new Vector2(PanelWidth, PanelHeight);
            dialogue.rectTransform.sizeDelta = dialogue.Data.PanelSize;
            BasisGraphicUIRayCaster.SetBoxColliderToRectTransform(dialogue.gameObject);
        }

        private static RectTransform CreateContentArea(BasisMenuDialoguePanel dialogue, string body)
        {
            if (dialogue.Descriptor == null || !dialogue.Descriptor.HasDescription) return null;
            RectTransform host = dialogue.Descriptor.DescriptionLabel.rectTransform.parent as RectTransform;
            if (host == null) return null;

            float textWidth = Mathf.Max(100f, dialogue.Data.PanelSize.x - SideInset - 64f);
            float bodyHeight = Mathf.Clamp(Mathf.Ceil(dialogue.Descriptor.DescriptionLabel.GetPreferredValues(body ?? string.Empty, textWidth, 0f).y), 40f, 200f);

            PanelElementDescriptor scroll = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.ScrollViewVertical, host);
            RectTransform scrollArea = scroll.rectTransform;
            scrollArea.anchorMin = Vector2.zero;
            scrollArea.anchorMax = Vector2.one;
            scrollArea.offsetMin = new Vector2(SideInset, BottomInset);
            scrollArea.offsetMax = new Vector2(-SideInset, -(bodyHeight + TopGap));

            RectTransform content = scroll.ContentParent;
            ScrollRect scrollView = content != null ? content.GetComponentInParent<ScrollRect>() : null;
            if (scrollView != null && scrollView.viewport != null)
            {
                RectTransform viewport = scrollView.viewport;
                viewport.anchorMin = Vector2.zero;
                viewport.anchorMax = Vector2.one;
                viewport.offsetMin = Vector2.zero;
                viewport.offsetMax = new Vector2(-25f, 0f);
                if (!viewport.TryGetComponent(out RectMask2D _))
                {
                    viewport.gameObject.AddComponent<RectMask2D>();
                }
            }
            return content;
        }

        private static PanelElementDescriptor CreateTextRow(RectTransform content)
        {
            PanelElementDescriptor row = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Entry, content);
            if (row.Header != null)
            {
                Transform slot = row.Header.Find("Title/Element");
                if (slot != null) slot.gameObject.SetActive(false);
            }
            return row;
        }

        private static void BuildDetailsSection(RectTransform content, PanelTextField field, TMP_InputField inputField)
        {
            string about = field.Descriptor ? field.Descriptor.Description : null;
            string problem = field.ValidationMessage;
            int limit = inputField.characterLimit;
            int length = inputField.text != null ? inputField.text.Length : 0;
            string count = limit > 0 ? $"{length} / {limit}" : length.ToString();

            PanelSectionToggleHelpers.CreateCollapsibleFlatSection(content, BasisLocalization.Get("ui.textPreview.details"), () =>
            {
                if (!string.IsNullOrEmpty(about))
                {
                    CreateTextRow(content).SetDescription(about);
                }
                PanelElementDescriptor characters = CreateTextRow(content);
                characters.SetTitle(BasisLocalization.Get("ui.textPreview.characters"));
                characters.SetDescription(count);
                if (!string.IsNullOrEmpty(problem))
                {
                    PanelElementDescriptor validation = CreateTextRow(content);
                    validation.SetDescription(problem);
                    BasisPanelTint.Apply(BasisPanelTint.Capture(validation), BasisPanelSeverity.Hot, false);
                }
            }, !string.IsNullOrEmpty(problem));
        }

        private static void BuildFormattingSection(RectTransform content)
        {
            PanelSectionToggleHelpers.CreateCollapsibleFlatSection(content, BasisLocalization.Get("ui.textPreview.formatting"), () =>
            {
                CreateTextRow(content).SetRichDescription(string.Join("\n",
                    TagLine("<b>", "</b>", "ui.textPreview.tag.bold"),
                    TagLine("<i>", "</i>", "ui.textPreview.tag.italic"),
                    TagLine("<color=#FF8800>", "</color>", "ui.textPreview.tag.color"),
                    TagLine("<size=150%>", "</size>", "ui.textPreview.tag.size"),
                    TagLine("<s>", "</s>", "ui.textPreview.tag.strikethrough"),
                    TagLine("<u>", "</u>", "ui.textPreview.tag.underline")));
            }, false);
        }

        private static string TagLine(string open, string close, string wordKey)
        {
            string word = BasisLocalization.Get(wordKey);
            return $"<noparse>{open}{word}{close}</noparse>  →  {open}{word}{close}";
        }

        private static void AddButtons(BasisMenuDialoguePanel dialogue, PanelTextField field, TMP_InputField inputField, string text)
        {
            PanelButton copyButton = null;
            copyButton = dialogue.AddOption(BasisLocalization.Get("keyboard.key.copy"), () => BasisClipboard.Copy(text, copyButton), false);

            bool editable = inputField.interactable && !inputField.readOnly;
            if (editable && !string.IsNullOrEmpty(GUIUtility.systemCopyBuffer))
            {
                dialogue.AddOption(BasisLocalization.Get("keyboard.key.paste"), () =>
                {
                    string clip = GUIUtility.systemCopyBuffer;
                    if (field != null && inputField != null && !string.IsNullOrEmpty(clip))
                    {
                        field.ApplyDrivenValue(SanitizeForField(inputField, clip));
                    }
                });
            }
            if (editable && !string.IsNullOrEmpty(text))
            {
                dialogue.AddOption(BasisLocalization.Get("ui.clear"), () =>
                {
                    if (field != null) field.ApplyDrivenValue(string.Empty);
                });
            }
            if (field.HasResetDefault)
            {
                dialogue.AddOption(BasisLocalization.Get("ui.reset"), () =>
                {
                    if (field != null) field.ApplyResetToDefault();
                });
            }
        }

        private static string SanitizeForField(TMP_InputField inputField, string value)
        {
            if (inputField.lineType == TMP_InputField.LineType.SingleLine)
            {
                value = value.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
            }
            if (inputField.characterLimit > 0 && value.Length > inputField.characterLimit)
            {
                value = value.Substring(0, inputField.characterLimit);
            }
            return value;
        }
    }
}
