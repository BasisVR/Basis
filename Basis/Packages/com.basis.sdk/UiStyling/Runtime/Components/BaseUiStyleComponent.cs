using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI.Styling
{
    public static class UiStyleSelectableExtensions
    {
        /// <summary>
        /// Apply transition styles to a given Selectable component by Style ID.
        /// </summary>
        public static void ApplyStyle(this Selectable selectable, string styleId)
        {
            SelectableStyle style = UiStyleSettings.GetActiveStyles().SelectableStyles.GetStyle(styleId);
            selectable.ApplyStyle(style);
        }

        /// <summary>
        /// Apply transition styles to a given Selectable component by Style Reference.
        /// </summary>
        public static void ApplyStyle(this Selectable selectable, SelectableStyle style)
        {
            if (style == null) return;
            ColorBlock block = selectable.colors;
            block.normalColor = style.NormalColor;
            block.highlightedColor = style.HighlightedColor;
            block.pressedColor = style.PressedColor;
            block.selectedColor = style.SelectedColor;
            block.disabledColor = style.DisabledColor;

            selectable.colors = block;
        }
    }

    /// <summary>
    /// Base class used for implementing custom styled components.
    /// </summary>
    public abstract class BaseUiStyleComponent : MonoBehaviour
    {
        public abstract void ApplyActiveStyle();

        private void OnValidate()
        {
            if (!enabled) return;
            ApplyActiveStyle();
        }
    }
}
