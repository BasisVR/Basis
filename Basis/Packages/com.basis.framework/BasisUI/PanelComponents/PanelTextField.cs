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

        public static class TextFieldStyles
        {
            public static string Default => "Packages/com.basis.sdk/Prefabs/Panel Elements/PE Text Field.prefab";
            public static string Entry => "Packages/com.basis.sdk/Prefabs/Panel Elements/PE Text Field - Entry Variant.prefab";
            public static string LargeDefault => "Packages/com.basis.sdk/Prefabs/Panel Elements/PE Large Text Field.prefab";
        }

        public static PanelTextField CreateNew(Component parent)
            => CreateNew<PanelTextField>(TextFieldStyles.Default, parent);
        public static PanelTextField CreateNewLarge(Component parent)
    => CreateNew<PanelTextField>(TextFieldStyles.LargeDefault, parent);

        public static PanelTextField CreateNewEntry(Component parent)
            => CreateNew<PanelTextField>(TextFieldStyles.Entry, parent);

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
        }

        public override void OnComponentUsed()
        {
            base.OnComponentUsed();
            SetValue(_inputField.text);
        }

        protected override void ApplyValue()
        {
            base.ApplyValue();
            _inputField.SetTextWithoutNotify(Value);
        }
    }
}
