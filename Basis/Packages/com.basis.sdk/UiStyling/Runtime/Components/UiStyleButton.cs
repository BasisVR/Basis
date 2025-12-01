using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI.Styling
{
    /// <summary>
    /// Apply a style to a given Button component.
    /// </summary>
    [RequireComponent(typeof(Button))]
    public class UiStyleButton : BaseUiStyleComponent
    {
        [UiStyleID(StyleComponentType.Button)] public string ColorStyle;

        [SerializeField] protected UiStyleImage Icon;
        [SerializeField] protected UiStyleImage Indicator;
        [SerializeField] protected UiStyleLabel Label;

        public void SetStyle(string styleId)
        {
            ColorStyle = styleId;
            ApplyActiveStyle();
        }

        public override void ApplyActiveStyle()
        {
            Button button = GetComponent<Button>();
            ButtonStyle style = UiStyleSettings.GetActiveStyles().ButtonStyles.GetStyle(ColorStyle);
            if (style == null) return;

            button.ApplyStyle(style.SelectionStyle);

            if (button.image) button.image.ApplyStyle(style.BackgroundStyle);
            if (Icon) Icon.SetStyle(style.IconStyle);
            if (Indicator) Indicator.SetStyle(style.IndicatorStyle);
            if (Label) Label.SetStyle(style.LabelStyle);
        }

        public void ShowIndicator(bool value)
        {
            if (Indicator) Indicator.Image.enabled = value;
        }
    }
}
