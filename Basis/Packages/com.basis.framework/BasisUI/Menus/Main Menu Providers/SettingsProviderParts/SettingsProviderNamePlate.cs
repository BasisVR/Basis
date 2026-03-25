using Basis.Scripts.UI.NamePlate;
using UnityEngine;

namespace Basis.BasisUI
{
    /// <summary>
    /// Settings tab for remote nameplate appearance.
    /// Exposes an enable toggle plus width, size, and transparency controls.
    /// </summary>
    public static class SettingsProviderNamePlate
    {
        [RuntimeInitializeOnLoadMethod]
        static void Init()
        {
            BasisSettingsSystem.OnSettingsFinishedChanges += ApplyNamePlateSettings;
        }

        public static PanelTabPage NamePlateTab(PanelTabGroup tabGroup)
        {
            PanelTabPage tab = PanelTabPage.CreateVertical(tabGroup.Descriptor.ContentParent);
            PanelElementDescriptor descriptor = tab.Descriptor;

            descriptor.SetTitle("Nameplates");
            descriptor.SetDescription("Controls the appearance of nameplates above other players.");

            RectTransform container = descriptor.ContentParent;

            PanelElementDescriptor nameplateGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            nameplateGroup.SetTitle("Nameplates");
            nameplateGroup.SetDescription("Controls the appearance of nameplates above other players.");

            PanelToggle toggleEnabled = PanelToggle.CreateNewEntry(nameplateGroup);
            toggleEnabled.Descriptor.SetTitle("Show Nameplates");
            toggleEnabled.AssignBinding(BasisSettingsDefaults.NPEnabled);

            PanelToggle toggleMenuOnly = PanelToggle.CreateNewEntry(nameplateGroup);
            toggleMenuOnly.Descriptor.SetTitle("Only Show When Menu Open");
            toggleMenuOnly.AssignBinding(BasisSettingsDefaults.NPMenuOnly);

            PanelSlider sliderWidth = PanelSlider.CreateEntryAndBind(
                nameplateGroup,
                PanelSlider.SliderSettings.Advanced("Width", 10f, 60f, false, 1, ValueDisplayMode.Raw),
                BasisSettingsDefaults.NPWidth);

            PanelSlider sliderSize = PanelSlider.CreateEntryAndBind(
                nameplateGroup,
                PanelSlider.SliderSettings.Advanced("Size", 0.5f, 2f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.NPSize);

            PanelSlider sliderTransparency = PanelSlider.CreateEntryAndBind(
                nameplateGroup,
                PanelSlider.SliderSettings.Advanced("Transparency", 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.NPTransparency);

            // Hide appearance settings when nameplates are disabled
            bool isEnabled = BasisSettingsDefaults.NPEnabled.RawValue;
            toggleMenuOnly.Descriptor.SetActive(isEnabled);
            sliderWidth.Descriptor.SetActive(isEnabled);
            sliderSize.Descriptor.SetActive(isEnabled);
            sliderTransparency.Descriptor.SetActive(isEnabled);
            toggleEnabled.OnValueChanged += (val) =>
            {
                toggleMenuOnly.Descriptor.SetActive(val);
                sliderWidth.Descriptor.SetActive(val);
                sliderSize.Descriptor.SetActive(val);
                sliderTransparency.Descriptor.SetActive(val);
                nameplateGroup.ForceRebuild();
            };

            // ─────────────── RESET BUTTON ───────────────
            SettingsProvider.AddResetPageButton(container, "Nameplates", ResetNamePlateDefaults);

            descriptor.ForceRebuild();
            return tab;
        }

        private static void ResetNamePlateDefaults()
        {
            BasisSettingsDefaults.NPEnabled.ResetToDefault();
            BasisSettingsDefaults.NPMenuOnly.ResetToDefault();
            BasisSettingsDefaults.NPWidth.ResetToDefault();
            BasisSettingsDefaults.NPSize.ResetToDefault();
            BasisSettingsDefaults.NPTransparency.ResetToDefault();
            ApplyNamePlateSettings();
        }

        public static void ApplyNamePlateSettings()
        {
            if (BasisRemoteNamePlateDriver.Instance != null)
            {
                BasisRemoteNamePlateDriver.Instance.ApplyNamePlateSettingsFromUI();
            }
        }
    }
}
