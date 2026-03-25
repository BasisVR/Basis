using System;
using Basis.BTween;
using TMPro;

#if UNITY_EDITOR
using UnityEditor;
#endif

using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI
{
    public class PanelPasswordField : PanelDataComponent<bool>
    {

        public Action<string> OnSubmit;

        public string Password => _inputField.text;
        [SerializeField] public LayoutElement LayoutElement;
        [SerializeField] public TMP_InputField _inputField;
        [SerializeField] public TextMeshProUGUI _placeholderField;
        [SerializeField] protected Toggle _showToggle;
        [SerializeField] protected Image _visibleIcon;
        [SerializeField] protected Image _invisibleIcon;
        [SerializeField] protected string _placeholder;
        [SerializeField] protected bool _readOnly;

        private TweenScale _focusTween;
        private TweenScale _togglePunchTween;

        public static class PasswordFieldStyles
        {
            public static string Default => "Packages/com.basis.sdk/Prefabs/Panel Elements/PE Password Field.prefab";

            public static string Entry =>
                "Packages/com.basis.sdk/Prefabs/Panel Elements/PE Password Field - Entry Variant.prefab";
            public static string EntryVertical =>
                "Packages/com.basis.sdk/Prefabs/Panel Elements/PE Password Field - Vertical Entry Variant.prefab";

            public static string EntryLong => "Packages/com.basis.sdk/Prefabs/Panel Elements/PE Password Field - Entry Variant - Long.prefab";
        }

        public static PanelPasswordField CreateNew(Component parent)
            => CreateNew<PanelPasswordField>(PasswordFieldStyles.Default, parent);

        public static PanelPasswordField CreateNew(string Entry,Component parent)
    => CreateNew<PanelPasswordField>(Entry, parent);
        public static PanelPasswordField CreateNewEntry(Component parent)
            => CreateNew<PanelPasswordField>(PasswordFieldStyles.Entry, parent);


        protected override void Awake()
        {
            base.Awake();
            _showToggle.onValueChanged.AddListener(SetValue);
            _inputField.onEndEdit.AddListener(_ => OnComponentUsed());
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
        public void DisableIcons()
        {
            _visibleIcon.enabled = false;
            _invisibleIcon.enabled = false;
        }
        public override void OnComponentUsed()
        {
            base.OnComponentUsed();
            OnSubmit?.Invoke(_inputField.text);

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
            _inputField.contentType = Value ? TMP_InputField.ContentType.Standard : TMP_InputField.ContentType.Password;
            _inputField.ForceLabelUpdate();

            _visibleIcon.enabled = Value;
            _invisibleIcon.enabled = !Value;

            // Punch the toggle on visibility change
            if (Application.isPlaying && _showToggle != null)
            {
                if (_togglePunchTween != null && _togglePunchTween.Active) _togglePunchTween.Reset();
                Transform toggleTransform = _showToggle.transform;
                _togglePunchTween = toggleTransform.TweenScale(0.08f, toggleTransform.localScale, Vector3.one * 1.2f)
                    .SetEase(Easing.OutCubic)
                    .AddCallback(() =>
                    {
                        if (toggleTransform != null)
                        {
                            toggleTransform.TweenScale(0.14f, Vector3.one * 1.2f, Vector3.one)
                                .SetEase(Easing.OutBack);
                        }
                    });
            }
        }

        public void SetPassword(string password)
        {
            _inputField.SetTextWithoutNotify(password);
        }

#if UNITY_EDITOR
        protected override void OnValidate()
        {
            base.OnValidate();
            if (Application.isPlaying) return;

            _inputField.readOnly = _readOnly;

            if (_placeholderField && _placeholderField.text != _placeholder)
            {
                Undo.RecordObject(_placeholderField,
                    $"Assigned placeholder text to {_placeholderField.gameObject.name}: {_placeholder}");
                _placeholderField.text = _placeholder;
            }
        }
#endif
    }
}
