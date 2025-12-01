using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI.Styling
{
    [RequireComponent(typeof(TMP_Dropdown))]
    public class UiStyleDropdown : BaseUiStyleComponent
    {
        [UiStyleID(StyleComponentType.Dropdown)]
        public string ColorStyle;

        [SerializeField] protected UiStyleImage Icon;
        [SerializeField] protected UiStyleLabel Label;
        [SerializeField] protected UiStyleImage OptionsBackground;
        [SerializeField] protected UiStyleScrollbar StyleScrollbar;
        [SerializeField] protected UiStyleToggle StyleTemplateEntry;

        public void SetStyle(string styleId)
        {
            ColorStyle = styleId;
            ApplyActiveStyle();
        }

        public override void ApplyActiveStyle()
        {
            TMP_Dropdown field = GetComponent<TMP_Dropdown>();
            DropdownStyle style = UiStyleSettings.GetActiveStyles().DropdownStyles.GetStyle(ColorStyle);
            if (!style) return;

            field.ApplyStyle(style.SelectionStyle);

            if (field.image) field.image.ApplyStyle(style.BackgroundStyle);
            if (Icon) Icon.SetStyle(style.IconStyle);
            if (Label) Label.SetStyle(style.LabelStyle);
            if (OptionsBackground) OptionsBackground.SetStyle(style.OptionsBackgroundStyle);

            if (StyleScrollbar) StyleScrollbar.SetStyle(style.ScrollbarStyle);
            if (StyleTemplateEntry) StyleTemplateEntry.SetStyle(style.TemplateEntryStyle);
        }
    }
}
