using TMPro;
using UnityEngine;


namespace Basis.VowganUI.Styling
{
    [RequireComponent(typeof(TextMeshProUGUI))]
    public class PaletteTextMeshProUGUI : StylePaletteComponent
    {
        public override void ApplyColor()
        {
            TextMeshProUGUI label = GetComponent<TextMeshProUGUI>();
            if (!label) return;

            Color color = StylePaletteObject.GetCurrentColor(Style);

            if (label.color == color) return;
            StyleUtilities.RecordUndo(label, "Set label color.");
            label.color = color;
        }
    }
}
