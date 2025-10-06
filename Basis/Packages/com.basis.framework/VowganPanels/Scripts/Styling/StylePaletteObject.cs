using System;
using TMPro;

#if UNITY_EDITOR
using UnityEditor;
#endif

using UnityEngine;

namespace Basis.VowganUI.Styling
{
    [CreateAssetMenu(fileName = "Style Palette", menuName = "Basis/Style Palette")]
    public class StylePaletteObject : ScriptableObject
    {
        public enum Style
        {
            BackgroundColor1,
            BackgroundColor2,
            PopupBackgroundColor,
            AccentColor,
            FontColor1,
            FontColor2,
            TextFieldColor,
            ButtonColor,
            WhiteColor,
            BlackColor,
            PositiveColor,
            NegativeColor,
        }

        public Color BackgroundColor1 = new(0.16f, 0.16f, 0.17f);
        public Color BackgroundColor2 = new(0.19f, 0.2f, 0.2f);
        public Color PopupBackgroundColor = new(0.2f, 0.2f, 0.21f);
        public Color AccentColor = new(0.14f, 0.46f, 0.93f);
        public Color FontColor1 = new(0.9f, 0.92f, 0.93f);
        public Color FontColor2 = new(0.7f, 0.71f, 0.73f);
        public Color TextFieldColor = new(0.13f, 0.13f, 0.15f);
        public Color ButtonColor = new(0.31f, 0.32f, 0.32f);
        public Color WhiteColor = new(0.98f, 1f, 1f);
        public Color BlackColor = new(0.02f, 0.02f, 0.04f);
        public Color PositiveColor = new(0.09f, 0.8f, 0.47f);
        public Color NegativeColor = new(0.97f, 0.34f, 0.34f);

        [Serializable]
        public class TextStyle
        {
            public float Size = 16;
            public TextAlignmentOptions Alignment;
        }



#if UNITY_EDITOR
        public const string ACTIVE_PALETTE = "BasisVR/Styling/ActivePalette";
        [ContextMenu("Set as Active Palette")]
        public void SetAsActive()
        {
            EditorBuildSettings.AddConfigObject(ACTIVE_PALETTE, this, true);
        }

        public static StylePaletteObject ActivePalette =>
            EditorBuildSettings.TryGetConfigObject(ACTIVE_PALETTE, out StylePaletteObject active) ? active : null;
#endif

        public Color GetColor(Style style)
        {
            return style switch
            {
                Style.BackgroundColor1 => BackgroundColor1,
                Style.BackgroundColor2 => BackgroundColor2,
                Style.PopupBackgroundColor => PopupBackgroundColor,
                Style.AccentColor => AccentColor,
                Style.FontColor1 => FontColor1,
                Style.FontColor2 => FontColor2,
                Style.TextFieldColor => TextFieldColor,
                Style.ButtonColor => ButtonColor,
                Style.WhiteColor => WhiteColor,
                Style.BlackColor => BlackColor,
                Style.PositiveColor => PositiveColor,
                Style.NegativeColor => NegativeColor,
                _ => throw new ArgumentOutOfRangeException(nameof(style), style, null)
            };
        }
    }
}
