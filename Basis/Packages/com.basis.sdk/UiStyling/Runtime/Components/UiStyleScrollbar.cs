using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI.Styling
{
    [RequireComponent(typeof(Scrollbar))]
    public class UiStyleScrollbar : BaseUiStyleComponent
    {
        [UiStyleID(StyleComponentType.Scrollbar)] public string ColorStyle;
        [SerializeField] protected Image Background;

        public void SetStyle(string styleId)
        {
            ColorStyle = styleId;
            ApplyActiveStyle();
        }
        
        public override void ApplyActiveStyle()
        {
            Scrollbar field = GetComponent<Scrollbar>();
            ScrollbarStyle style = UiStyleSettings.GetActiveStyles().ScrollbarStyles.GetStyle(ColorStyle);
            if (style == null) return;
            
            field.ApplyStyle(style.SelectionStyle);
            if (field.image) field.image.ApplyStyle(style.HandleStyle);
            if (Background) Background.ApplyStyle(style.BackgroundStyle);
        }
    }
}
