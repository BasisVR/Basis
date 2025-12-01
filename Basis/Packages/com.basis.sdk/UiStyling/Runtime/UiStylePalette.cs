using System;
using UnityEngine;

namespace Basis.BasisUI.Styling
{
    public enum UiPaletteStyle
    {
        BackgroundColor1,
        BackgroundColor2,
        BackgroundColor3,
        LayerColor,
        AccentColor,
        FontColor1,
        FontColor2,
        FontColor3,
        InputFieldColor,
        ButtonColor,
        WhiteColor,
        ClearColor,
        BlackColor,
        SuccessColor,
        CautionColor,
        DangerColor,
        Scrollbar,
    }

    [CreateAssetMenu(fileName = "Style Palette", menuName = "WorldUI/Style Palette")]
    public class UiStylePalette : ScriptableObject
    {

        public Color BackgroundColor1 = new Color(0.096f, 0.096f, 0.12f);
        public Color BackgroundColor2 = new Color(0.111f, 0.111f, 0.125f);
        public Color BackgroundColor3 = new Color(0.154f, 0.155f, 0.163f);
        public Color LayerColor = new Color(0.2f, 0.2f, 0.21f, 0.5f);
        public Color AccentColor = new Color(0.14f, 0.46f, 0.93f);
        public Color FontColor1 = new Color(0.9f, 0.92f, 0.93f);
        public Color FontColor2 = new Color(0.7f, 0.71f, 0.73f);
        public Color FontColor3 = new Color(0.65f, 0.67f, 0.69f);
        public Color InputFieldColor = new Color(0.13f, 0.13f, 0.15f);
        public Color ButtonColor = new Color(0.31f, 0.32f, 0.32f);
        public Color WhiteColor = new Color(0.98f, 1f, 1f);
        public Color ClearColor = new Color(0,0,0,0);
        public Color BlackColor = new Color(0.02f, 0.02f, 0.04f);
        public Color SuccessColor = new Color(0.09f, 0.8f, 0.47f);
        public Color CautionColor = new Color(1f, 0.82f, 0.34f, 0.99f);
        public Color DangerColor = new Color(0.97f, 0.34f, 0.34f);
        public Color Scrollbar = new Color(0.62f, 0.63f, 0.64f);


        [ContextMenu("Set as Active Palette")]
        public void SetAsActive()
        {
            UiStyleSettings.SetActivePalette(this);
        }

        public static Color GetCurrentColor(UiPaletteStyle style)
        {
            return UiStyleSettings.GetActivePalette().GetColor(style);
        }

        public Color GetColor(UiPaletteStyle style)
        {
            switch (style)
            {
                case UiPaletteStyle.BackgroundColor1: return BackgroundColor1;
                case UiPaletteStyle.BackgroundColor2: return BackgroundColor2;
                case UiPaletteStyle.BackgroundColor3: return BackgroundColor3;
                case UiPaletteStyle.LayerColor: return LayerColor;
                case UiPaletteStyle.AccentColor: return AccentColor;
                case UiPaletteStyle.FontColor1: return FontColor1;
                case UiPaletteStyle.FontColor2: return FontColor2;
                case UiPaletteStyle.FontColor3: return FontColor3;
                case UiPaletteStyle.InputFieldColor: return InputFieldColor;
                case UiPaletteStyle.ButtonColor: return ButtonColor;
                case UiPaletteStyle.WhiteColor: return WhiteColor;
                case UiPaletteStyle.ClearColor: return ClearColor;
                case UiPaletteStyle.BlackColor: return BlackColor;
                case UiPaletteStyle.SuccessColor: return SuccessColor;
                case UiPaletteStyle.CautionColor: return CautionColor;
                case UiPaletteStyle.DangerColor: return DangerColor;
                case UiPaletteStyle.Scrollbar: return Scrollbar;
                default:
                    throw new ArgumentOutOfRangeException(nameof(style), style, null);
            }
        }

        private void OnValidate()
        {
            if (UiStyleSettings.GetActivePalette() == this)
                UiStyleSettings.UpdateAllStyleComponents();
        }
    }
}
