using Basis.BasisUI;

namespace Basis.Scripts.UI.NamePlate
{
    public static class BasisNamePlateSettings
    {
        public static bool NamePlateEnabled = true;
        public static bool NamePlateMenuOnly;
        public static bool NamePlateHoverMenuOnly;
        public static float NamePlateSize = 1f;
        public static float NamePlateTransparency = 0.45f;
        public static float ChatSize = 1f;

        public static void RefreshFromDefaults()
        {
            NamePlateEnabled = BasisSettingsDefaults.NPEnabled.RawValue;
            NamePlateMenuOnly = BasisSettingsDefaults.NPMenuOnly.RawValue;
            NamePlateHoverMenuOnly = BasisSettingsDefaults.NPHoverMenuOnly.RawValue;
            NamePlateSize = BasisSettingsDefaults.NPSize.RawValue;
            NamePlateTransparency = BasisSettingsDefaults.NPTransparency.RawValue;
            ChatSize = BasisSettingsDefaults.ChatSize.RawValue;
        }
    }
}
