using System;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.VowganUI.Styling
{
    [RequireComponent(typeof(Image))]
    public class PaletteImage : StylePaletteComponent
    {
        public override void ApplyColor()
        {
            Image image = GetComponent<Image>();
            if (!image) return;

            Color color = StylePaletteObject.GetCurrentColor(Style);

            if (image.color == color) return;
            StyleUtilities.RecordUndo(image, "Set image color.");
            image.color = color;
        }
    }
}
