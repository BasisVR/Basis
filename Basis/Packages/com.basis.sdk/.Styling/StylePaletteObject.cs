using System;

using UnityEngine;

namespace Basis.BasisUI.StylingOLD
{
    public enum PaletteStyle
    {
        BackgroundColor1,
        BackgroundColor2,
        BackgroundColor3,
        LayerColor,
        AccentColor,
        FontColor1,
        FontColor2,
        InputFieldColor,
        ButtonColor,
        WhiteColor,
        ClearColor,
        BlackColor,
        SuccessColor,
        CautionColor,
        DangerColor,
    }

    [CreateAssetMenu(fileName = "Style Palette", menuName = "Basis/Style Palette")]
    public class StylePaletteObject : ScriptableObject
    {
        public Color BackgroundColor1 = new(0.16f, 0.16f, 0.17f);
        public Color BackgroundColor2 = new(0.19f, 0.2f, 0.2f);
        public Color BackgroundColor3 = new(0.19f, 0.2f, 0.2f);
        public Color LayerColor = new(0.2f, 0.2f, 0.21f, 0.5f);
        public Color AccentColor = new(0.14f, 0.46f, 0.93f);
        public Color FontColor1 = new(0.9f, 0.92f, 0.93f);
        public Color FontColor2 = new(0.7f, 0.71f, 0.73f);
        public Color InputFieldColor = new(0.13f, 0.13f, 0.15f);
        public Color ButtonColor = new(0.31f, 0.32f, 0.32f);
        public Color WhiteColor = new(0.98f, 1f, 1f);
        public Color ClearColor = new(0,0,0,0);
        public Color BlackColor = new(0.02f, 0.02f, 0.04f);
        public Color SuccessColor = new(0.09f, 0.8f, 0.47f);
        public Color CautionColor = new(1f, 0.82f, 0.34f, 0.99f);
        public Color DangerColor = new(0.97f, 0.34f, 0.34f);


        [ContextMenu("Set as Active Palette")]
        public void SetAsActive()
        {
            StyleSettings.SetActivePalette(this);
        }

        public static Color GetCurrentColor(PaletteStyle style)
        {
            return StyleSettings.Instance.ActivePalette.GetColor(style);
        }

        public Color GetColor(PaletteStyle style)
        {
            return style switch
            {
                PaletteStyle.BackgroundColor1 => BackgroundColor1,
                PaletteStyle.BackgroundColor2 => BackgroundColor2,
                PaletteStyle.BackgroundColor3 => BackgroundColor3,
                PaletteStyle.LayerColor => LayerColor,
                PaletteStyle.AccentColor => AccentColor,
                PaletteStyle.FontColor1 => FontColor1,
                PaletteStyle.FontColor2 => FontColor2,
                PaletteStyle.InputFieldColor => InputFieldColor,
                PaletteStyle.ButtonColor => ButtonColor,
                PaletteStyle.WhiteColor => WhiteColor,
                PaletteStyle.ClearColor => ClearColor,
                PaletteStyle.BlackColor => BlackColor,
                PaletteStyle.SuccessColor => SuccessColor,
                PaletteStyle.CautionColor => CautionColor,
                PaletteStyle.DangerColor => DangerColor,
                _ => throw new ArgumentOutOfRangeException(nameof(style), style, null)
            };
        }
    }
}
