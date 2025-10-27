using UnityEngine;

namespace Basis.VowganUI.Styling
{
    [CreateAssetMenu(fileName = "StyleSettings", menuName = "Basis/Style Palette Settings")]
    public class StyleSettings : ScriptableObject
    {
        public static StyleSettings Instance
        {
            get
            {
                if (!_instance)
                {
                    _instance = Resources.Load<StyleSettings>(StyleSettingsResourcesPath);
                }

                return _instance;
            }
        }

        public static string StyleSettingsResourcesPath = "StyleSettings";
        private static StyleSettings _instance;

        public StylePaletteObject ActivePalette;

        public static void SetActivePalette(StylePaletteObject palette)
        {
            if (!palette) return;
            Instance.ActivePalette = palette;

            StyleComponent[] components = FindObjectsByType<StyleComponent>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

            foreach (StyleComponent component in components)
            {
                component.ApplyColor();
            }
        }
    }
}
