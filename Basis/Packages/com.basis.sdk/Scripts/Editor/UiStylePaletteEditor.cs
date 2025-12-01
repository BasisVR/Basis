using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Basis.BasisUI.Styling
{
    [CanEditMultipleObjects]
    [CustomEditor(typeof(UiStylePalette))]
    public class UiStylePaletteEditor : UnityEditor.Editor
    {
        public override VisualElement CreateInspectorGUI()
        {
            VisualElement root = new VisualElement();

            if (UiStyleSettings.GetActivePalette() != target)
            {
                Button button = new Button(() => ((UiStylePalette)target).SetAsActive());
                button.text = "Set as Active Palette";
                root.Add(button);
            }

            InspectorElement.FillDefaultInspector(root, serializedObject, this);
            return root;
        }
    }

    [CanEditMultipleObjects]
    [CustomEditor(typeof(UiStyleLibrary))]
    public class UiStyleLibraryEditor : UnityEditor.Editor
    {
        public override VisualElement CreateInspectorGUI()
        {
            VisualElement root = new VisualElement();

            if (UiStyleSettings.GetActiveStyles() != target)
            {
                Button button = new Button(() => ((UiStyleLibrary)target).SetAsActive());
                button.text = "Set as Active Library";
                root.Add(button);
            }

            InspectorElement.FillDefaultInspector(root, serializedObject, this);
            return root;
        }
    }
}
