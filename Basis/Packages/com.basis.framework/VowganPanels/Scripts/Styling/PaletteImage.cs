using System;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.VowganUI.Styling
{
    [RequireComponent(typeof(Image))]
    public class PaletteImage : StylePaletteComponent
    {
        public override void ApplyColor(StylePaletteObject palette)
        {
            if (!palette) return;

            Image image = GetComponent<Image>();
            if (!image) return;

            Color color = palette.GetColor(Style);

            if (image.color == color) return;
            StyleUtilities.RecordUndo(image, "Set image color.");
            image.color = color;
        }
    }
}
