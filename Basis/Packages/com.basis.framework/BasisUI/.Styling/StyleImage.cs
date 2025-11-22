using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI.StylingOLD
{
    [RequireComponent(typeof(Image))]
    public class StyleImage : StyleComponent
    {
        public override void ApplyColor()
        {
            Image image = GetComponent<Image>();
            if (!image) return;

            Color color = StylePaletteObject.GetCurrentColor(UseActiveStyle ? ActiveStyle : NormalStyle);

            // if (image.color == color) return;
            StyleUtilities.RecordUndo(image, "Set image color.");
            image.color = color;
        }
    }
}
