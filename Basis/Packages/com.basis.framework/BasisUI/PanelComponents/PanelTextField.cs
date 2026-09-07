using Basis.BTween;
using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI
{
    public class PanelTextField : PanelDataComponent<string>
    {

        [SerializeField] public TMP_InputField _inputField;

        protected override Selectable InteractableTarget => _inputField;
        [SerializeField] public TextMeshProUGUI _placeholderLabel;
        [SerializeField] protected string _placeholderText;
        [SerializeField] protected string _defaultValue;
        [SerializeField] protected TMP_InputField.ContentType _contentType = TMP_InputField.ContentType.Alphanumeric;

        private TweenScale _focusTween;

        private Func<string, string> _validator;
        private BasisPanelTint.Handle _validationTint;
        private string _restingDescription;
        private string _shownMessage;
        private bool _validationArmed;
        private bool _gradeImmediately = true;
        private bool _revealed;

        /// <summary>
        /// Whether the field's rule accepts what is currently typed. Reports the truth even on a field
        /// that has not shown its complaint yet, so it is always safe to gate a submit on.
        /// </summary>
        public bool IsValid => CurrentProblem() == null;

        /// <summary>Why the field is rejected, or null when it is fine.</summary>
        public string ValidationMessage => CurrentProblem();

        /// <summary>Raised when the shown validity flips, so a confirm button can bind straight to it.</summary>
        public event Action<bool> OnValidityChanged;

        private string CurrentProblem()
        {
            if (_validator == null)
            {
                return null;
            }

            return BasisFieldValidation.NormalizeProblem(
                _validator(_inputField != null ? _inputField.text : Value));
        }

        public static class TextFieldStyles
        {
            public static string Default => "Packages/com.basis.sdk/Prefabs/Panel Elements/PE Text Field.prefab";
            public static string Entry => "Packages/com.basis.sdk/Prefabs/Panel Elements/PE Text Field - Entry Variant.prefab";
            public static string EntryVertical => "Packages/com.basis.sdk/Prefabs/Panel Elements/PE Text Field - Vertical Entry Variant.prefab";
            public static string EntryWarning => "Packages/com.basis.sdk/Prefabs/Panel Elements/PE Text Field - Entry Warning Variant.prefab";
            public static string LargeDefault => "Packages/com.basis.sdk/Prefabs/Panel Elements/PE Large Text Field.prefab";
            public static string EntryWithNoTitle => "Packages/com.basis.sdk/Prefabs/Panel Elements/PE Text Field - Entry No Title Variant.prefab";

            public static string EntryVerticalHorizontalContent => "Packages/com.basis.sdk/Prefabs/Panel Elements/PE Text Field - Vertical Entry Variant With Horizontal Content.prefab";
        }

        public static PanelTextField CreateNew(Component parent)
            => CreateNew<PanelTextField>(TextFieldStyles.Default, parent);
        public static PanelTextField CreateNewLarge(Component parent)
    => CreateNew<PanelTextField>(TextFieldStyles.LargeDefault, parent);

        public static PanelTextField CreateNewEntry(Component parent)
            => CreateNew<PanelTextField>(TextFieldStyles.Entry, parent);

        public static PanelTextField CreateNew(string style, Component parent)
            => CreateNew<PanelTextField>(style, parent);

        /// <summary>
        /// Never prints what a password field holds, and keeps long free text (URLs, MOTDs) from
        /// swallowing the reset dialogue's change list.
        /// </summary>
        protected override string FormatSettingValue(string value)
        {
            TMP_InputField.ContentType contentType = _inputField != null ? _inputField.contentType : _contentType;
            if (contentType == TMP_InputField.ContentType.Password || contentType == TMP_InputField.ContentType.Pin)
            {
                return "•••";
            }

            if (string.IsNullOrEmpty(value)) return "—";
            return value.Length <= 60 ? value : value.Substring(0, 57) + "…";
        }

        private bool HasPreviewText
        {
            get
            {
                if (_inputField == null || string.IsNullOrEmpty(_inputField.text)) return false;
                TMP_InputField.ContentType contentType = _inputField.contentType;
                return contentType != TMP_InputField.ContentType.Password && contentType != TMP_InputField.ContentType.Pin;
            }
        }

        public override bool HasPanelOptions => HasPreviewText || HasResetDefault;

        public override void RequestReset()
        {
            if (HasPreviewText) BasisTextFieldPreview.Open(this, _inputField);
            else base.RequestReset();
        }

#if UNITY_EDITOR
        protected override void OnValidate()
        {
            base.OnValidate();
            if (Application.isPlaying) return;

            if (_inputField)
            {
                _inputField.text = _defaultValue;
                _inputField.contentType = _contentType;
            }
            if (_placeholderLabel) _placeholderLabel.text = _placeholderText;
        }
#endif

        protected override void Awake()
        {
            base.Awake();
            _inputField.onEndEdit.AddListener(_ => OnComponentUsed());
            _inputField.onValueChanged.AddListener(_ =>
            {
                SetValueWithoutNotify(_inputField.text);
                Validate();
            });
            _inputField.onSelect.AddListener(_ => OnFieldFocused());
            _inputField.onDeselect.AddListener(_ => OnFieldUnfocused());
        }

        /// <summary>
        /// Marks the field as one that cannot be left blank. It tints red and swaps its description for
        /// <paramref name="message"/> while it is empty, rather than the entry being silently dropped
        /// when the user goes to submit.
        /// <para><paramref name="gradeImmediately"/> decides when the complaint appears. Leave it true
        /// for a field backing a live setting, where blank means something is already broken. Pass
        /// false for an "add an entry" box the user may never intend to use — it then stays neutral
        /// until they type in it or a submit handler calls <see cref="Validate"/>.</para>
        /// <para>Call after the field's own description is set — the resting description is captured
        /// here so it can be restored once the field is filled in.</para>
        /// </summary>
        public void SetRequired(string message = null, bool gradeImmediately = true)
        {
            string resolved = string.IsNullOrEmpty(message)
                ? BasisLocalization.Get("ui.validation.required")
                : message;
            SetValidator(BasisFieldValidation.Required(resolved), gradeImmediately);
        }

        /// <summary>
        /// Installs a rule for this field. <paramref name="validator"/> returns null when the text is
        /// acceptable, or the message to show when it is not. Replaces any previous rule and grades
        /// what is already typed straight away.
        /// </summary>
        public void SetValidator(Func<string, string> validator, bool gradeImmediately = true)
        {
            if (!_validationArmed)
            {
                _validationArmed = true;
                _restingDescription = Descriptor != null ? Descriptor.Description : null;
                _validationTint = BasisPanelTint.Capture(Descriptor);
            }

            _validator = validator;
            _gradeImmediately = gradeImmediately;
            Validate(false, false);
        }

        /// <summary>Clears the rule and returns the field to its resting look.</summary>
        public void ClearValidator()
        {
            if (_validator == null)
            {
                return;
            }

            _validator = null;
            _revealed = false;
            bool wasInvalid = _shownMessage != null;
            _shownMessage = null;
            ApplyValidationVisuals(true);
            if (wasInvalid)
            {
                OnValidityChanged?.Invoke(true);
            }
        }

        /// <summary>
        /// Re-runs the rule against what is currently typed and returns whether it passed. Also reveals
        /// the message on a field that was still holding it back, so a submit handler can call this to
        /// both gate the action and show the user what is wrong.
        /// </summary>
        public bool Validate() => Validate(true, true);

        private bool Validate(bool animate, bool reveal)
        {
            if (_validator == null)
            {
                return true;
            }

            string message = CurrentProblem();

            if (reveal)
            {
                _revealed = true;
            }

            string shown = BasisFieldValidation.ResolveShown(message, _gradeImmediately, _revealed);
            if (!string.Equals(shown, _shownMessage))
            {
                bool wasValid = _shownMessage == null;
                _shownMessage = shown;
                ApplyValidationVisuals(animate);

                bool isValid = shown == null;
                if (isValid != wasValid)
                {
                    OnValidityChanged?.Invoke(isValid);
                }
            }

            return message == null;
        }

        private void ApplyValidationVisuals(bool animate)
        {
            if (Descriptor == null)
            {
                return;
            }

            if (_shownMessage == null)
            {
                BasisPanelTint.Clear(_validationTint, animate);
                Descriptor.SetDescription(_restingDescription);
            }
            else
            {
                BasisPanelTint.Apply(_validationTint, BasisPanelSeverity.Hot, animate);
                Descriptor.SetDescription(_shownMessage);
            }
        }

        private void OnFieldFocused()
        {
            if (!Application.isPlaying) return;
            if (_focusTween != null && _focusTween.Active && _focusTween.Target == transform) _focusTween.Reset();

            _focusTween = transform.TweenScale(0.12f, transform.localScale, Vector3.one * 1.02f)
                .SetEase(Easing.OutCubic);
        }

        private void OnFieldUnfocused()
        {
            if (!Application.isPlaying) return;
            if (_focusTween != null && _focusTween.Active && _focusTween.Target == transform) _focusTween.Reset();

            _focusTween = transform.TweenScale(0.15f, transform.localScale, Vector3.one)
                .SetEase(Easing.OutCubic);
        }

        public override void OnComponentUsed()
        {
            base.OnComponentUsed();
            SetValue(_inputField.text);

            // Punch on submit
            if (Application.isPlaying)
            {
                if (_focusTween != null && _focusTween.Active && _focusTween.Target == transform) _focusTween.Reset();
                _focusTween = transform.TweenScale(0.06f, transform.localScale, Vector3.one * 1.03f)
                    .SetEase(Easing.OutCubic)
                    .AddCallback(() =>
                    {
                        if (this != null)
                        {
                            transform.TweenScale(0.12f, Vector3.one * 1.03f, Vector3.one)
                                .SetEase(Easing.OutBack);
                        }
                    });
            }
        }

        protected override void ApplyValue()
        {
            base.ApplyValue();
            _inputField.SetTextWithoutNotify(Value);
            Validate(true, false);
        }
    }
}
