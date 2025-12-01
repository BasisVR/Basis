using TMPro;
using UnityEngine;

namespace Basis.BasisUI.Styling
{
    public static class UiStyleLabelExtensions
    {
        public static void ApplyStyle(this TextMeshProUGUI label, string styleId)
        {
            LabelStyle style = UiStyleSettings.GetActiveStyles()?.LabelStyles.GetStyle(styleId);
            label.ApplyStyle(style);
        }

        public static void ApplyStyle(this TextMeshProUGUI label, LabelStyle style)
        {
            if (style == null) return;
            label.color = style.Color;
            if (style.Font) label.font = style.Font;
            label.fontSize = style.FontSize;
        }
    }

    [RequireComponent(typeof(TextMeshProUGUI))]
    public class UiStyleLabel : BaseUiStyleComponent
    {
        [UiStyleID(StyleComponentType.Label)] public string ColorStyle;

        public void SetStyle(string styleId)
        {
            ColorStyle = styleId;
            ApplyActiveStyle();
        }

        public override void ApplyActiveStyle()
        {
            TextMeshProUGUI field = GetComponent<TextMeshProUGUI>();
            field.ApplyStyle(ColorStyle);
        }
    }
}
