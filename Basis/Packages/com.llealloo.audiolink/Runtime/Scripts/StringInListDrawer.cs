#if UNITY_EDITOR
// A PropertyDrawer to show a selection from a list of strings for an attribute.
// Based on https://gist.github.com/ProGM/9cb9ae1f7c8c2a4bd3873e4df14a6687
using System;

using UnityEditor;

using UnityEngine;

namespace AudioLink.Editor
{
    public class StringInList : PropertyAttribute
    {
        public StringInList(params string[] list)
        {
            List = list;
        }

        public string[] List
        {
            get;
        }
    }

    [CustomPropertyDrawer(typeof(StringInList))]
    public class StringInListDrawer : PropertyDrawer
    {
        // Draw the property inside the given rect
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            string[] list = ((StringInList)attribute).List;
            if (property.propertyType == SerializedPropertyType.String)
            {
                int index = Mathf.Max(0, Array.IndexOf(list, property.stringValue));
                index = EditorGUI.Popup(position, property.displayName, index, list);

                property.stringValue = list[index];
            }
            else if (property.propertyType == SerializedPropertyType.Integer)
            {
                property.intValue = EditorGUI.Popup(position, property.displayName, property.intValue, list);
            }
            else
            {
                base.OnGUI(position, property, label);
            }
        }
    }
}
#endif
