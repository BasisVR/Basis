#if UNITY_EDITOR
using UnityEditor;
#endif

using UnityEngine;
using UnityEngine.UIElements;

namespace Basis.BasisUI.Styling
{
    public static class UiStyleUtilities
    {
        /// <summary>
        /// Runtime-safe undo recording for a given component.
        /// This does not record an Undo action at runtime, but will safely compile for Runtime code.
        /// </summary>
        public static void RecordComponent(Component component)
        {
#if UNITY_EDITOR
            Undo.RecordObject(component, $"Recorded undo for {component}");
#endif
        }

        /// <summary>
        /// Toggle a given VisualElement's display On or Off.
        /// </summary>
        public static void SetActive(this VisualElement element, bool value)
        {
            element.style.display = value ? DisplayStyle.Flex : DisplayStyle.None;
        }
    }
}
