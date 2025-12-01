using System;
using UnityEngine;

namespace Basis.BasisUI.Styling
{
    public enum UiStyleColorType
    {
        Palette,
        Custom,
    }
    
    [Serializable]
    public struct UiStyleColor
    {
        public Color Color;
        public UiPaletteStyle PaletteStyle;
        public UiStyleColorType Type;
        
        public static implicit operator Color(UiStyleColor color)
        {
            switch (color.Type)
            {
                case UiStyleColorType.Palette:
                    return UiStyleSettings.GetActivePalette().GetColor(color.PaletteStyle);
                case UiStyleColorType.Custom:
                    return color.Color;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }
}
