using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI.Styling
{
    [RequireComponent(typeof(Slider))]
    public class UiStyleSlider : BaseUiStyleComponent
    {
        [UiStyleID(StyleComponentType.Slider)] public string ColorStyle;
        [SerializeField] protected Image _background;
        
        public void SetStyle(string styleId)
        {
            ColorStyle = styleId;
            ApplyActiveStyle();
        }

        public override void ApplyActiveStyle()
        {
            Slider field = GetComponent<Slider>();
            SliderStyle style = UiStyleSettings.GetActiveStyles().SliderStyles.GetStyle(ColorStyle);
            if (style == null) return;
            
            field.ApplyStyle(style.SelectionStyle);
            if (_background) _background.ApplyStyle(style.BackgroundStyle);
        }
    }
}
