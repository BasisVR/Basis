using System;
using Basis.Scripts.Device_Management;
using TMPro;
using UnityEngine;

namespace Basis.BasisUI
{
    public partial class BasisMenuVirtualKeyboardPanel
    {
        public const string RequestTitleKey = "keyboard.request.title";
        public const string RequestWarningTitleKey = "keyboard.request.warning.title";
        public const string RequestWarningKey = "keyboard.request.warning";
        public const int MaxRequestLength = 4096;
        private object requestOwner;
        private Action<string> requestSubmitted;
        private Action requestCancelled;
        private PanelTextField requestWarning;
        private TMP_InputField RequestField => DisplayField ? DisplayField._inputField : null;
        public static bool IsTextRequestOpen(object owner) => owner != null && HasInstance && Instance.requestOwner == owner;
        public static string TextRequestText(object owner) => IsTextRequestOpen(owner) && Instance.RequestField ? Instance.RequestField.text : null;
        public static bool OpenTextRequest(object owner, string asker, string prompt, string text, int characterLimit, TMP_InputField.ContentType contentType, Action<string> onSubmitted, Action onCancelled)
        {
            if (owner == null) return false;
            BasisMenuVirtualKeyboardPanel panel = CreateNew(null, null);
            if (!panel) return false;
            panel.EndTextRequest(true, true);
            TMP_InputField field = panel.RequestField;
            if (!field) return false;
            int limit = characterLimit > 0 ? Mathf.Min(characterLimit, MaxRequestLength) : MaxRequestLength;
            string start = text ?? string.Empty;
            if (start.Length > limit) start = start.Substring(0, limit);
            field.characterLimit = limit;
            field.contentType = contentType;
            field.readOnly = false;
            field.interactable = true;
            field.SetTextWithoutNotify(start);
            field.stringPosition = start.Length;
            field.onSubmit.AddListener(panel.HandleRequestFieldSubmit);
            if (panel.DisplayField._placeholderLabel) panel.DisplayField._placeholderLabel.text = string.IsNullOrWhiteSpace(prompt) ? BasisLocalization.Get(DisplayPlaceholderKey) : NoParse(prompt);
            string shown = NoParse(string.IsNullOrWhiteSpace(asker) ? "?" : asker);
            panel.Descriptor.SetTitle(BasisLocalization.Get(RequestTitleKey, shown));
            panel.ShowRequestWarning(shown);
            panel.requestOwner = owner;
            panel.requestSubmitted = onSubmitted;
            panel.requestCancelled = onCancelled;
            panel.InputField = null;
            panel.TMPInputField = field;
            panel.UpdateDisplay();
            if (!BasisDeviceManagement.IsCurrentModeVR() && !BasisDeviceManagement.IsMobileHardware()) field.ActivateInputField();
            return true;
        }
        public static void CloseTextRequest(object owner)
        {
            if (!IsTextRequestOpen(owner)) return;
            Instance.requestCancelled = null;
            Instance.ReleaseInstance();
        }
        private void HandleRequestFieldSubmit(string _)
        {
            if (requestOwner == null || IsReleased) return;
            SubmitTextRequest();
            ReleaseInstance();
        }
        private bool SubmitTextRequest()
        {
            if (requestOwner == null) return false;
            Action<string> submitted = requestSubmitted;
            TMP_InputField field = RequestField;
            string text = field ? field.text : string.Empty;
            requestCancelled = null;
            EndTextRequest(!IsReleased, false);
            Call(submitted, text);
            return true;
        }
        private void EndTextRequest(bool restore, bool notify)
        {
            if (requestOwner == null) return;
            Action cancelled = requestCancelled;
            requestOwner = null;
            requestSubmitted = null;
            requestCancelled = null;
            TMP_InputField field = RequestField;
            if (field)
            {
                field.onSubmit.RemoveListener(HandleRequestFieldSubmit);
                if (TMPInputField == field) TMPInputField = null;
                if (restore)
                {
                    field.DeactivateInputField();
                    field.SetTextWithoutNotify(string.Empty);
                    field.characterLimit = 0;
                    field.contentType = TMP_InputField.ContentType.Standard;
                    field.readOnly = true;
                    field.interactable = false;
                    if (DisplayField._placeholderLabel) DisplayField._placeholderLabel.text = BasisLocalization.Get(DisplayPlaceholderKey);
                    Descriptor.SetTitle(Title);
                    if (requestWarning && !requestWarning.IsReleased) requestWarning.ReleaseInstance();
                }
            }
            requestWarning = null;
            if (notify) Call(cancelled);
        }
        private void ShowRequestWarning(string asker)
        {
            if (!requestWarning || requestWarning.IsReleased)
            {
                requestWarning = PanelTextField.CreateNew(PanelTextField.TextFieldStyles.EntryWarning, Descriptor.ContentParent);
                if (!requestWarning) return;
                if (requestWarning._inputField) requestWarning._inputField.gameObject.SetActive(false);
                requestWarning.Descriptor.TitleLabel.color = Color.yellow;
                requestWarning.Descriptor.DescriptionLabel.color = Color.yellow;
                requestWarning.Descriptor.SetHeight(70);
            }
            requestWarning.Descriptor.SetTitle(BasisLocalization.Get(RequestWarningTitleKey));
            requestWarning.Descriptor.SetDescription(BasisLocalization.Get(RequestWarningKey, asker));
            if (_headerRow) requestWarning.transform.SetSiblingIndex(_headerRow.transform.GetSiblingIndex() + 1);
        }
        private static string NoParse(string value) => "<noparse>" + value.Replace("</noparse>", string.Empty) + "</noparse>";
        private static void Call(Action<string> callback, string text)
        {
            if (callback == null) return;
            try
            {
                callback(text);
            }
            catch (Exception e)
            {
                BasisDebug.LogErrorUnreported($"[Keyboard] A text request submit callback threw: {e}");
            }
        }
        private static void Call(Action callback)
        {
            if (callback == null) return;
            try
            {
                callback();
            }
            catch (Exception e)
            {
                BasisDebug.LogErrorUnreported($"[Keyboard] A text request cancel callback threw: {e}");
            }
        }
    }
}
