using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

namespace Basis.BasisUI.Styling
{
    #region Base Styles

    [Serializable]
    public abstract class StyleData
    {
        public string Title = "New Style";
        public static implicit operator bool(StyleData data) => data != null;
    }

    [Serializable]
    public class ImageStyle : StyleData
    {
        public UiStyleColor Color;
        public Sprite Sprite;
        [Min(0.01f)] public float PixelsPerUnitMultiplier = 1;
    }

    [Serializable]
    public class LabelStyle : StyleData
    {
        public UiStyleColor Color;
        public TMP_FontAsset Font;
        [Min(0)] public float FontSize = 16;
    }

    [Serializable]
    public class SelectableStyle : StyleData
    {
        public UiStyleColor NormalColor;
        public UiStyleColor HighlightedColor;
        public UiStyleColor PressedColor;
        public UiStyleColor SelectedColor;
        public UiStyleColor DisabledColor;
    }

    #endregion

    #region Compound

    [Serializable]
    public class ButtonStyle : StyleData
    {
        [UiStyleID(StyleComponentType.Selectable)] public string SelectionStyle;
        [UiStyleID(StyleComponentType.Image)] public string BackgroundStyle;
        [UiStyleID(StyleComponentType.Image)] public string IconStyle;
        [UiStyleID(StyleComponentType.Image)] public string IndicatorStyle;
        [UiStyleID(StyleComponentType.Label)] public string LabelStyle;
    }

    [Serializable]
    public class ToggleStyle : StyleData
    {
        [UiStyleID(StyleComponentType.Selectable)] public string SelectionStyle;
        [UiStyleID(StyleComponentType.Image)] public string BackgroundStyle;
        [UiStyleID(StyleComponentType.Image)] public string IndicatorStyle;
        [UiStyleID(StyleComponentType.Image)] public string CheckmarkStyle;
        [UiStyleID(StyleComponentType.Label)] public string LabelStyle;
    }

    [Serializable]
    public class SliderStyle : StyleData
    {
        [UiStyleID(StyleComponentType.Selectable)] public string SelectionStyle;
        [UiStyleID(StyleComponentType.Image)] public string BackgroundStyle;
    }

    [Serializable]
    public class ScrollbarStyle : StyleData
    {
        [UiStyleID(StyleComponentType.Selectable)] public string SelectionStyle;
        [UiStyleID(StyleComponentType.Image)] public string BackgroundStyle;
        [UiStyleID(StyleComponentType.Image)] public string HandleStyle;
    }

    [Serializable]
    public class DropdownStyle : StyleData
    {
        [UiStyleID(StyleComponentType.Selectable)] public string SelectionStyle;
        [UiStyleID(StyleComponentType.Image)] public string BackgroundStyle;

        [UiStyleID(StyleComponentType.Image)] public string IconStyle;
        [UiStyleID(StyleComponentType.Label)] public string LabelStyle;

        [UiStyleID(StyleComponentType.Image)] public string OptionsBackgroundStyle;
        [UiStyleID(StyleComponentType.Scrollbar)] public string ScrollbarStyle;
        [UiStyleID(StyleComponentType.Toggle)] public string TemplateEntryStyle;
    }

    #endregion

    [Serializable]
    public struct StyleList<T> where T : StyleData
    {
        public List<T> Styles;

        public T GetStyle(string title)
        {
            return Styles.FirstOrDefault(style => style.Title == title);
        }

        public string[] GetInfo()
        {
            string[] titles = new string[Styles.Count];
            for (int i = 0; i < Styles.Count; i++)
            {
                titles[i] = Styles[i].Title;
            }

            return titles;
        }
    }

    public enum StyleComponentType
    {
        // Base
        Image,
        Label,
        Selectable,
        // Compound
        Button,
        Toggle,
        Slider,
        Scrollbar,
        Dropdown,
    }

    [CreateAssetMenu(fileName = "Style Library", menuName = "WorldUI/Style Library")]
    public class UiStyleLibrary : ScriptableObject
    {
        [Header("Base Styles")]
        public StyleList<ImageStyle> ImageStyles;
        public StyleList<LabelStyle> LabelStyles;
        public StyleList<SelectableStyle> SelectableStyles;

        [Header("Compound Styles")]
        public StyleList<ButtonStyle> ButtonStyles;
        public StyleList<ToggleStyle> ToggleStyles;
        public StyleList<SliderStyle> SliderStyles;
        public StyleList<ScrollbarStyle> ScrollbarStyles;
        public StyleList<DropdownStyle> DropdownStyles;

        [ContextMenu("Set as Active Style")]
        public void SetAsActive()
        {
            UiStyleSettings.SetActiveStyles(this);
        }
        public string[] GetInfo(StyleComponentType type)
        {
            switch (type)
            {
                // Base
                case StyleComponentType.Image: return ImageStyles.GetInfo();
                case StyleComponentType.Label: return LabelStyles.GetInfo();
                case StyleComponentType.Selectable: return SelectableStyles.GetInfo();
                // Compound
                case StyleComponentType.Button: return ButtonStyles.GetInfo();
                case StyleComponentType.Toggle: return ToggleStyles.GetInfo();
                case StyleComponentType.Slider: return SliderStyles.GetInfo();
                case StyleComponentType.Scrollbar: return ScrollbarStyles.GetInfo();
                case StyleComponentType.Dropdown: return DropdownStyles.GetInfo();
                default:
                    throw new ArgumentOutOfRangeException(nameof(type), type, null);
            }
        }

        private void OnValidate()
        {
            if (UiStyleSettings.GetActiveStyles() == this)
                UiStyleSettings.UpdateAllStyleComponents();
        }
    }
}
