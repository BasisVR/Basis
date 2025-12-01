using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Basis.BasisUI.Styling
{
    /// <summary>
    /// Drawer for displaying a custom dropdown of Styles associated with the Style Type.
    /// </summary>
    [CustomPropertyDrawer(typeof(UiStyleIDAttribute))]
    public class UiStyleIDAttributeDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            UiStyleIDAttribute styleAttribute = (UiStyleIDAttribute)attribute;

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(label);
            string[] titles = UiStyleSettings.GetActiveStyles().GetInfo(styleAttribute.Type);
            if (EditorGUILayout.DropdownButton(new GUIContent(property.stringValue), FocusType.Keyboard))
            {
                GenericMenu dropdown = new GenericMenu();
                foreach (string title in titles)
                {
                    dropdown.AddItem(new GUIContent(title), property.stringValue == title, () =>
                    {
                        property.stringValue = title;
                        property.serializedObject.ApplyModifiedProperties();
                    });
                }
                dropdown.ShowAsContext();
            }
            EditorGUILayout.EndHorizontal();

            bool hasStyle = Array.IndexOf(titles, property.stringValue) != -1;
            if (!hasStyle) EditorGUILayout.HelpBox("Style not found in active styles.", MessageType.Warning);

            EditorGUI.EndProperty();

            EditorGUI.LabelField(position, "TODO: Fix BasisDocInspector_UI to not force IMGUI.");
        }

        public override VisualElement CreatePropertyGUI(SerializedProperty property)
        {
            if (property.propertyType != SerializedPropertyType.String)
            {
                return new Label("StyleID Attribute should only be used on String fields.");
            }

            VisualElement root = new VisualElement();
            root.style.flexDirection = FlexDirection.Row;

            UiStyleIDAttribute styleAttribute = (UiStyleIDAttribute)attribute;
            DropdownField dropdown = new DropdownField();
            dropdown.AddToClassList("unity-base-field__aligned");
            dropdown.AddToClassList("unity-base-field__inspector-field");
            dropdown.style.flexGrow = 1;
            dropdown.label = property.displayName;
            dropdown.BindProperty(property);
            root.Add(dropdown);

            VisualElement missingStyleWarning = new VisualElement()
            {
                style =
                {
                    marginBottom = 4,
                    marginLeft = 4,
                    marginTop = 4,
                    marginRight = 0,
                    width = 16,
                    height = 16,
                    backgroundColor = new StyleColor(Color.red),
                    borderBottomLeftRadius = 10,
                    borderTopLeftRadius = 10,
                    borderTopRightRadius = 10,
                    borderBottomRightRadius = 10,
                }
            };
            missingStyleWarning.tooltip = "This still is no longer found on the style list.";
            root.Add(missingStyleWarning);

            dropdown.RegisterValueChangedCallback(_ => RefreshList());
            RefreshList();
            return root;

            void RefreshList()
            {
                string[] titles = UiStyleSettings.GetActiveStyles().GetInfo(styleAttribute.Type);
                dropdown.choices = titles.ToList();
                bool hasStyle = Array.IndexOf(titles, dropdown.value) == -1;

                missingStyleWarning.SetActive(hasStyle);
            }
        }
    }
}
