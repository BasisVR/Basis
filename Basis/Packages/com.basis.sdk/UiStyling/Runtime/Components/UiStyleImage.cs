using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI.Styling
{
    public static class UiStyleImageExtensions
    {
        /// <summary>
        /// Apply a style to a given Image component by Style ID.
        /// </summary>
        public static void ApplyStyle(this Image image, string styleId)
        {
            ImageStyle style = UiStyleSettings.GetActiveStyles().ImageStyles.GetStyle(styleId);
            image.ApplyStyle(style);
        }

        /// <summary>
        /// Apply a style to a given Image component by Style Reference.
        /// </summary>
        public static void ApplyStyle(this Image image, ImageStyle style)
        {
            if (style == null) return;
            image.color = style.Color;
            image.pixelsPerUnitMultiplier = style.PixelsPerUnitMultiplier;
            if (style.Sprite) image.sprite = style.Sprite;
        }
    }


    /// <summary>
    /// Apply a style to a given Image component.
    /// </summary>
    [RequireComponent(typeof(Image))]
    public class UiStyleImage : BaseUiStyleComponent
    {
        [UiStyleID(StyleComponentType.Image)] public string ColorStyle;

        public Image Image
        {
            get
            {
                if (!_image) _image = GetComponent<Image>();
                return _image;
            }
        }
        private Image _image;

        public void SetStyle(string styleId)
        {
            ColorStyle = styleId;
            ApplyActiveStyle();
        }

        public override void ApplyActiveStyle()
        {
            if (Image) Image.ApplyStyle(ColorStyle);
        }
    }
}
