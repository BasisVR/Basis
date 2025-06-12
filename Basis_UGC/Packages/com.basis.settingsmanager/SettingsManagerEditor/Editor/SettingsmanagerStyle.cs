using UnityEngine;
using UnityEditor;
namespace BattlePhaze.SettingsManager.Style
{
    public static class SettingsmanagerStyle
    {
        public static string DropDownClosed = "►";
        public static string DropDownOpen = "▼";
        public static Color32 PrimaryWhiteColor;
        public static Color32 SecondaryColor;
        public static Color32 HighlightColor;
        public static Color32 OrangeColor;
        public static Color32 FoldOutMiniColor;
        public static Color32 backgroundStylingColor;
        public static Color32 DescriptionColor;
        public static GUIStyle ButtonStyling;
        public static GUIStyle ButtonCompactStyling;
        public static GUIStyle ButtonDominateStyling;
        public static GUIStyle BackGroundStyling;
        public static GUIStyle TextLargeStyling;
        public static GUIStyle ValueStyling;
        public static GUIStyle EnumStyling;
        public static GUIStyle FoldOutMini;
        public static GUIStyle DescriptorStyling;
        public static GUIStyle FoldoutHeader;
        public static GUIStyle FoldoutHeaderLarge;
        public static void SetColors()
        {
            if (EditorGUIUtility.isProSkin)
            {
                PrimaryWhiteColor = new Color32(210, 210, 210, 255);
                SecondaryColor = new Color32(255, 122, 0, 255);
                HighlightColor = new Color32(255, 122, 0, 255);
                OrangeColor = new Color32(255, 140, 0, 255);
                FoldOutMiniColor = new Color32(50, 50, 50, 255);
                backgroundStylingColor = new Color32(0, 0, 0, 170);
                DescriptionColor = new Color32(50, 50, 50, 255);
            }
            else
            {
                PrimaryWhiteColor = new Color32(15, 15, 15, 255);
                SecondaryColor = new Color32(28, 28, 27, 255);
                HighlightColor = new Color32(255, 122, 0, 255);
                OrangeColor = new Color32(255, 140, 0, 255);
                FoldOutMiniColor = new Color32(171, 171, 171, 255);
                backgroundStylingColor = new Color32(255, 255, 255, 170);
                DescriptionColor = new Color32(171, 171, 171, 255);
            }
        }
        public static string OptionConstruct = "<size=14><b><color=black> [</color></b>";
        /// <summary>
        /// Used for setting a color of a Texture2D
        /// </summary>
        /// <param name="width"></param>
        /// <param name="height"></param>
        /// <param name="color"></param>
        /// <returns></returns>
        public static Texture2D CreateSolidColorTexture(int width, int height, Color32 color)
        {
            Texture2D texture = new Texture2D(width, height);
            Color[] pixels = new Color[width * height];

            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = color;
            }

            texture.SetPixels(pixels);
            texture.Apply();

            return texture;
        }
        /// <summary>
        /// Styling System for the displayed values
        /// </summary>
        public static void Style()
        {
            SetColors();
            // Button styles
            var buttonStyling = new GUIStyle(GUI.skin.button)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                stretchHeight = false,
                fixedHeight = 20,
                margin = new RectOffset(0, 0,0, 0),
                normal = { textColor = PrimaryWhiteColor },
            };

            var buttonCompactStyling = new GUIStyle(buttonStyling)
            {
                fontSize = 12,
                fontStyle = FontStyle.Normal,
                margin = new RectOffset(0, 5, 0, 0),
                fixedWidth = 80,
            };

            var buttonDominateStyling = new GUIStyle(GUI.skin.button)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 15,
                fontStyle = FontStyle.Bold,
                clipping = TextClipping.Overflow,
                fixedHeight = 25,
                margin = new RectOffset(5, 5, 0, 15),
                normal = { textColor = PrimaryWhiteColor },
            };

            ButtonStyling = buttonStyling;
            ButtonCompactStyling = buttonCompactStyling;
            ButtonDominateStyling = buttonDominateStyling;

            // Background style
            var backgroundStyling = new GUIStyle()
            {
                alignment = TextAnchor.MiddleLeft,
                fontStyle = FontStyle.Bold,
                stretchWidth = true,
                normal = { textColor = PrimaryWhiteColor, background = CreateSolidColorTexture(10, 10, backgroundStylingColor) },
            };

            BackGroundStyling = backgroundStyling;

            // Text styles
            var textLargeStyling = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 35,
                fontStyle = FontStyle.Bold,
                margin = new RectOffset(-45, 0, 5, 5),
                richText = true,
                normal = { textColor = HighlightColor },
            };

            TextLargeStyling = textLargeStyling;
            var descriptorStyling = new GUIStyle(GUI.skin.textArea)
            {
                alignment = TextAnchor.MiddleLeft,
                fontSize = 10,
                fontStyle = FontStyle.Bold,
                fixedWidth = 120,
                fixedHeight = 18,
                margin = new RectOffset(5, 0, 0, 0),
                normal = {
                background = CreateSolidColorTexture(45, 45,DescriptionColor)
                , textColor = SecondaryColor
                },
            };

            DescriptorStyling = descriptorStyling;
            ValueStyling = new GUIStyle(GUI.skin.textField);

            EnumStyling = new GUIStyle(EditorStyles.popup)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 10,
                fontStyle = FontStyle.Bold,
                normal = { textColor = PrimaryWhiteColor }
            };

            FoldOutMini = new GUIStyle(EditorStyles.foldout)
            {
                alignment = TextAnchor.MiddleLeft,
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                fixedHeight = 24,
                normal = { textColor = PrimaryWhiteColor, background = CreateSolidColorTexture(45, 45, FoldOutMiniColor) }
            };

            ButtonDominateStyling = new GUIStyle(GUI.skin.button)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 15,
                fontStyle = FontStyle.Bold,
                clipping = TextClipping.Overflow,
                fixedHeight = 25,
                margin = new RectOffset(5, 5, 0, 15),
                normal = { textColor = PrimaryWhiteColor }
            };

            FoldoutHeader = new GUIStyle(EditorStyles.toolbarButton)
            {
                richText = true,
                fontStyle = FontStyle.Bold,
                stretchWidth = true,
                fixedHeight = 18,
                margin = new RectOffset(5, 9, 5, 5),
                padding = new RectOffset(0, 0, 0, 3),
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = OrangeColor },
            };

            FoldoutHeaderLarge = new GUIStyle(EditorStyles.boldLabel)
            {
                richText = true,
                stretchWidth = true,
                fixedHeight = 30,
                margin = new RectOffset(18, 8, 0, 5),
                fontSize = 16,
                clipping = TextClipping.Clip,
                normal = { textColor = PrimaryWhiteColor },
            };
        }
        public static GUIContent FoldDownGeneration(SettingsManager Manager, int OptionIndex, bool Enabled)
        {
            string Icon;
            if (Enabled)
            {
                Icon = DropDownOpen;
            }
            else
            {
                Icon = DropDownClosed;
            }
            return new GUIContent(
                Icon +
                OptionConstruct +
                Manager.Options[OptionIndex].Type
                + "<b><color=black>]</color></b></size>"
                + "<color=#888888> Name:</color><size=14><color=black> [</color>"
                + Manager.Options[OptionIndex].Name
                + "<b><color=black>]</color></b></size>"
                + "<color=#888888> Current Value:</color><size=14><b><color=black> [</color></b>"
                + Manager.Options[OptionIndex].SelectedValue
                + "<b><color=black>]</color></b></size>"
                ,//tooltip
           "Type[" +
            Manager.Options[OptionIndex].Type +
            "] Name [" +
            Manager.Options[OptionIndex].Name +
            "] DefaultValue [" +
            Manager.Options[OptionIndex].SelectedValue +
            "] SelectedValue [" +
            Manager.Options[OptionIndex].SelectedValueDefault +
            "] Description [" +
            Manager.Options[OptionIndex].ValueDescriptor + "]");
        }
    }
}