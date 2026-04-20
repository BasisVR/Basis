using Basis.BTween;
using TMPro;
using UnityEngine;

namespace Basis.BasisUI
{
    public class PanelTextField : PanelDataComponent<string>
    {

        [SerializeField] public TMP_InputField _inputField;
        [SerializeField] public TextMeshProUGUI _placeholderLabel;
        [SerializeField] protected string _placeholderText;
        [SerializeField] protected string _defaultValue;
        [SerializeField] protected TMP_InputField.ContentType _contentType = TMP_InputField.ContentType.Alphanumeric;

        private TweenScale _focusTween;

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
            _inputField.onValueChanged.AddListener(_ => SetValueWithoutNotify(_inputField.text));
            _inputField.onSelect.AddListener(_ => OnFieldFocused());
            _inputField.onDeselect.AddListener(_ => OnFieldUnfocused());
        }

        private void OnFieldFocused()
        {
            if (!Application.isPlaying) return;
            if (_focusTween != null && _focusTween.Active) _focusTween.Reset();

            _focusTween = transform.TweenScale(0.12f, transform.localScale, Vector3.one * 1.02f)
                .SetEase(Easing.OutCubic);
        }

        private void OnFieldUnfocused()
        {
            if (!Application.isPlaying) return;
            if (_focusTween != null && _focusTween.Active) _focusTween.Reset();

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
                if (_focusTween != null && _focusTween.Active) _focusTween.Reset();
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
        }
    }
}
