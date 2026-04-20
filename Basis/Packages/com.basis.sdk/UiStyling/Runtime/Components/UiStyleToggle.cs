using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI.Styling
{
    [RequireComponent(typeof(Toggle))]
    public class UiStyleToggle : BaseUiStyleComponent
    {
        [UiStyleID(StyleComponentType.Toggle)] public string ColorStyle;

        [SerializeField] protected UiStyleImage Indicator;
        [SerializeField] protected UiStyleImage Checkmark;
        [SerializeField] protected UiStyleLabel Label;

        public void SetStyle(string styleId)
        {
            ColorStyle = styleId;
            ApplyActiveStyle();
        }

        public override void ApplyActiveStyle()
        {
            Toggle toggle = GetComponent<Toggle>();
            ToggleStyle style = UiStyleSettings.GetActiveStyles().ToggleStyles.GetStyle(ColorStyle);
            if (style == null) return;

            toggle.ApplyStyle(style.SelectionStyle);

            if (toggle.image) toggle.image.ApplyStyle(style.BackgroundStyle);
            if (Indicator) Indicator.SetStyle(style.IndicatorStyle);
            if (Checkmark) Checkmark.SetStyle(style.CheckmarkStyle);
            if (Label) Label.SetStyle(style.LabelStyle);
        }
    }
}
