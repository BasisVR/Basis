using UnityEngine;

namespace Basis.VowganUI.Styling
{
    public abstract class StylePaletteComponent : MonoBehaviour
    {
        public PaletteStyle Style;

        public void SetStyling(PaletteStyle style)
        {
            Style = style;
            ApplyColor();
        }

        public abstract void ApplyColor();

#if UNITY_EDITOR
        private void OnValidate()
        {
            ApplyColor();
        }
#endif

    }
}
