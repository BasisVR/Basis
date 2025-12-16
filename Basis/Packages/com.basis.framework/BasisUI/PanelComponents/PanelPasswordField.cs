using System;
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

        [SerializeField] protected TMP_InputField _inputField;
        [SerializeField] protected TextMeshProUGUI _placeholderField;
        [SerializeField] protected Toggle _showToggle;
        [SerializeField] protected Image _visibleIcon;
        [SerializeField] protected Image _invisibleIcon;
        [SerializeField] protected string _placeholder;
        [SerializeField] protected bool _readOnly;

        public static class PasswordFieldStyles
        {
            public static string Default => "Packages/com.basis.sdk/Prefabs/Panel Elements/PE Password Field.prefab";

            public static string Entry =>
                "Packages/com.basis.sdk/Prefabs/Panel Elements/PE Password Field - Entry Variant.prefab";
        }

        public static PanelPasswordField CreateNew(Component parent)
            => CreateNew<PanelPasswordField>(PasswordFieldStyles.Default, parent);


        public static PanelPasswordField CreateNewEntry(Component parent)
            => CreateNew<PanelPasswordField>(PasswordFieldStyles.Entry, parent);


        protected override void Awake()
        {
            base.Awake();
            _showToggle.onValueChanged.AddListener(SetValue);
            _inputField.onEndEdit.AddListener(_ => OnComponentUsed());
        }

        public override void OnComponentUsed()
        {
            base.OnComponentUsed();
            OnSubmit?.Invoke(_inputField.text);
        }

        protected override void ApplyValue()
        {
            base.ApplyValue();
            _inputField.contentType = Value ? TMP_InputField.ContentType.Standard : TMP_InputField.ContentType.Password;
            _inputField.ForceLabelUpdate();

            _visibleIcon.enabled = Value;
            _invisibleIcon.enabled = !Value;
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
