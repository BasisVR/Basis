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
        /// <summary>
        /// Indicator style for a button whose active state means "this spawned something, and
        /// clicking again closes it" — drawn as an outline rather than a fill so it reads as a
        /// toggle instead of a selection.
        /// </summary>
        public const string SpawnedIndicatorStyle = "Indicator Spawned";

        [UiStyleID(StyleComponentType.Button)] public string ColorStyle;

        [SerializeField] protected UiStyleImage Icon;
        [SerializeField] protected UiStyleImage Indicator;
        [SerializeField] protected UiStyleLabel Label;

        private string _indicatorStyleOverride;

        public void SetStyle(string styleId)
        {
            ColorStyle = styleId;
            ApplyActiveStyle();
        }

        /// <summary>
        /// Overrides the indicator style this button uses, independently of its
        /// <see cref="ColorStyle"/>. Survives re-styling; pass null to fall back to the style's own
        /// <see cref="ButtonStyle.IndicatorStyle"/>.
        /// </summary>
        public void SetIndicatorStyle(string styleId)
        {
            _indicatorStyleOverride = styleId;
            ApplyActiveStyle();
        }

        public override void ApplyActiveStyle()
        {
            if (TryGetComponent<Button>(out Button button))
            {
                ButtonStyle style = UiStyleSettings.GetActiveStyles().ButtonStyles.GetStyle(ColorStyle);
                if (style == null)
                {
                    return;
                }

                button.ApplyStyle(style.SelectionStyle);

                if (button.image) button.image.ApplyStyle(style.BackgroundStyle);
                if (Icon) Icon.SetStyle(style.IconStyle);
                if (Indicator) Indicator.SetStyle(string.IsNullOrEmpty(_indicatorStyleOverride)
                    ? style.IndicatorStyle
                    : _indicatorStyleOverride);
                if (Label) Label.SetStyle(style.LabelStyle);
            }
        }

        public void ShowIndicator(bool value)
        {
            if (Indicator) Indicator.Image.enabled = value;
        }
    }
}
