using UnityEditor;
using UnityEngine;

namespace Basis.BasisUI.Styling
{
    [FilePath("ProjectSettings/Basis/UiStylingSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    public class UiStyleSettings : ScriptableSingleton<UiStyleSettings>
    {
        public UiStyleLibrary ActiveStyles;
        public UiStylePalette ActivePalette;

        public static void SetActiveStyles(UiStyleLibrary library)
        {
            instance.ActiveStyles = library;
            UpdateAllStyleComponents();
            instance.Save(false);
        }

        public static void SetActivePalette(UiStylePalette palette)
        {
            instance.ActivePalette = palette;
            UpdateAllStyleComponents();
            instance.Save(false);
        }

        public static void UpdateAllStyleComponents()
        {
            BaseUiStyleComponent[] components =
                FindObjectsByType<BaseUiStyleComponent>(FindObjectsInactive.Include, FindObjectsSortMode.None);

            foreach (BaseUiStyleComponent comp in components)
            {
                if (!comp.enabled) continue;
                UiStyleUtilities.RecordComponent(comp);
                comp.ApplyActiveStyle();
            }
        }
    }
}
