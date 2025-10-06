using TMPro;
using UnityEngine;


namespace Basis.VowganUI.Styling
{
    [RequireComponent(typeof(TextMeshProUGUI))]
    public class PaletteTextMeshProUGUI : StylePaletteComponent
    {
        public override void ApplyColor(StylePaletteObject palette)
        {
            TextMeshProUGUI label = GetComponent<TextMeshProUGUI>();
            if (!label) return;

            Color color = palette.GetColor(Style);

            if (label.color == color) return;
            StyleUtilities.RecordUndo(label, "Set label color.");
            label.color = color;
        }
    }
}
