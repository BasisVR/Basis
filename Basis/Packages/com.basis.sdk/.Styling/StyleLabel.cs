using TMPro;
using UnityEngine;


namespace Basis.BasisUI.StylingOLD
{
    [RequireComponent(typeof(TextMeshProUGUI))]
    public class StyleLabel : StyleComponent
    {
        public override void ApplyColor()
        {
            TextMeshProUGUI label = GetComponent<TextMeshProUGUI>();
            if (!label) return;

            Color color = StylePaletteObject.GetCurrentColor(UseActiveStyle ? ActiveStyle : NormalStyle);

            if (label.color == color) return;
            StyleUtilities.RecordUndo(label, "Set label color.");
            label.color = color;
        }
    }
}
