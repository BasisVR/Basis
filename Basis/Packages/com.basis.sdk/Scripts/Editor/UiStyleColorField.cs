using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Basis.BasisUI.Styling
{
    [CustomPropertyDrawer(typeof(UiStyleColor))]
    public class UiStyleColorField : PropertyDrawer
    {
        public override VisualElement CreatePropertyGUI(SerializedProperty property)
        {
            VisualElement root = new VisualElement();
            VisualTreeAsset uxml = Resources.Load<VisualTreeAsset>(nameof(UiStyleColorField));
            uxml.CloneTree(root);

            Label label = root.Q<Label>();
            ColorField colorField = root.Q<ColorField>("ColorField");
            EnumField styleField = root.Q<EnumField>("StyleField");
            EnumField typeField = root.Q<EnumField>("TypeField");

            SerializedProperty propColor = property.FindPropertyRelative(nameof(UiStyleColor.Color));
            SerializedProperty propPaletteStyle = property.FindPropertyRelative(nameof(UiStyleColor.PaletteStyle));
            SerializedProperty propType = property.FindPropertyRelative(nameof(UiStyleColor.Type));

            label.text = property.displayName;
            colorField.BindProperty(propColor);
            styleField.BindProperty(propPaletteStyle);
            typeField.BindProperty(propType);

            typeField.RegisterValueChangedCallback(_ => UpdateVisibilityFromField());
            root.TrackPropertyValue(propType, _ => UpdateVisibilityFromField());
            root.schedule.Execute(UpdateVisibilityFromField).ExecuteLater(0);
            
            return root;

            void UpdateVisibilityFromField()
            {
                if (typeField?.value == null) return;

                UiStyleColorType type = (UiStyleColorType)typeField.value;
                colorField.style.display = type == UiStyleColorType.Custom  ? DisplayStyle.Flex : DisplayStyle.None;
                styleField.style.display = type == UiStyleColorType.Palette ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }
    }
}
