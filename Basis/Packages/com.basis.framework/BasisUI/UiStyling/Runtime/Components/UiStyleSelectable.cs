using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI.Styling
{
    [RequireComponent(typeof(Selectable))]
    public class UiStyleSelectable : BaseUiStyleComponent
    {
        [UiStyleID(StyleComponentType.Selectable)] public string SelectableStyle;

        public Selectable Selectable
        {
            get
            {
                if (!_selectable) _selectable = GetComponent<Selectable>();
                return _selectable;
            }
        }
        private Selectable _selectable;

        public void SetStyle(string styleId)
        {
            SelectableStyle = styleId;
            ApplyActiveStyle();
        }

        public override void ApplyActiveStyle()
        {
            if (Selectable) Selectable.ApplyStyle(SelectableStyle);
        }
    }
}
