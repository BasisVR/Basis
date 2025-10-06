using UnityEngine;

namespace Basis.VowganUI.Styling
{
    public abstract class StylePaletteComponent : MonoBehaviour
    {
        public StylePaletteObject.Style Style;
        public abstract void ApplyColor(StylePaletteObject palette);

#if UNITY_EDITOR
        private void OnValidate()
        {
            ApplyColor(StylePaletteObject.ActivePalette);
        }
#endif

    }
}
