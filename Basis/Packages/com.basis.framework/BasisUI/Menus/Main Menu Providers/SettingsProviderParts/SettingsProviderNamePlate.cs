using Basis.Scripts.UI.NamePlate;
using UnityEngine;
using Basis.Scripts.Settings;

namespace Basis.BasisUI
{
    /// <summary>
    /// Settings tab for remote nameplate appearance.
    /// Exposes an enable toggle plus size and transparency controls.
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

            descriptor.SetTitle(BasisLocalization.Get("settings.nameplates.title"));

            RectTransform container = descriptor.ContentParent;
            BuildNamePlateContent(container, descriptor);

            // ─────────────── RESET BUTTON ───────────────
            SettingsProvider.AddResetPageButton(container, "settings.tab.nameplates", ResetNamePlateDefaults);

            descriptor.ForceRebuild();
            return tab;
        }

        /// <summary>
        /// Builds the nameplate group + controls into <paramref name="container"/>
        /// without adding a reset button. Used by the standalone tab and by the
        /// merged Chat tab so both share one source of truth.
        /// </summary>
        public static void BuildNamePlateContent(RectTransform container, PanelElementDescriptor tabDescriptor = null)
        {
            PanelToggle toggleMenuOnly = null;
            PanelToggle toggleHoverMenuOnly = null;
            PanelSlider sliderSize = null;
            PanelSlider sliderTransparency = null;

            void ApplyAppearanceVisibility(bool enabled)
            {
                toggleMenuOnly.Descriptor.SetActive(enabled);
                toggleHoverMenuOnly.Descriptor.SetActive(enabled);
                sliderSize.Descriptor.SetActive(enabled);
                sliderTransparency.Descriptor.SetActive(enabled);
            }

            PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(container,
                BasisLocalization.Get("settings.nameplates.title"), () =>
            {
                PanelToggle toggleEnabled = PanelToggle.CreateNewEntry(container);
                toggleEnabled.Descriptor.SetTitle(BasisLocalization.Get("settings.nameplates.show"));
                toggleEnabled.Descriptor.SetTooltip(BasisLocalization.Get("settings.nameplates.show.tooltip"));
                toggleEnabled.AssignBinding(BasisSettingsDefaults.NPEnabled);

                toggleMenuOnly = PanelToggle.CreateNewEntry(container);
                toggleMenuOnly.Descriptor.SetTitle(BasisLocalization.Get("settings.nameplates.menuOnly"));
                toggleMenuOnly.Descriptor.SetTooltip(BasisLocalization.Get("settings.nameplates.menuOnly.tooltip"));
                toggleMenuOnly.AssignBinding(BasisSettingsDefaults.NPMenuOnly);

                toggleHoverMenuOnly = PanelToggle.CreateNewEntry(container);
                toggleHoverMenuOnly.Descriptor.SetTitle(BasisLocalization.Get("settings.nameplates.hoverMenuOnly"));
                toggleHoverMenuOnly.Descriptor.SetTooltip(BasisLocalization.Get("settings.nameplates.hoverMenuOnly.tooltip"));
                toggleHoverMenuOnly.AssignBinding(BasisSettingsDefaults.NPHoverMenuOnly);

                sliderSize = PanelSlider.CreateEntryAndBind(
                    container,
                    PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.nameplates.size"), 0.5f, 2f, false, 2, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.NPSize);
                sliderSize.Descriptor.SetTooltip(BasisLocalization.Get("settings.nameplates.size.tooltip"));

                sliderTransparency = PanelSlider.CreateEntryAndBind(
                    container,
                    PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.nameplates.transparency"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                    BasisSettingsDefaults.NPTransparency);
                sliderTransparency.Descriptor.SetTooltip(BasisLocalization.Get("settings.nameplates.transparency.tooltip"));

                // Hide appearance settings when nameplates are disabled
                ApplyAppearanceVisibility(BasisSettingsDefaults.NPEnabled.RawValue);
                toggleEnabled.OnValueChanged += (val) =>
                {
                    ApplyAppearanceVisibility(val);
                    tabDescriptor?.ForceRebuild();
                };
            }, false, visible =>
            {
                // Section expand re-shows every row; re-apply the enabled gate.
                if (visible)
                {
                    ApplyAppearanceVisibility(BasisSettingsDefaults.NPEnabled.RawValue);
                }
                tabDescriptor?.ForceRebuild();
            });
        }

        public static void ResetNamePlateDefaults()
        {
            BasisSettingsDefaults.NPEnabled.ResetToDefault();
            BasisSettingsDefaults.NPMenuOnly.ResetToDefault();
            BasisSettingsDefaults.NPHoverMenuOnly.ResetToDefault();
            BasisSettingsDefaults.NPSize.ResetToDefault();
            BasisSettingsDefaults.NPTransparency.ResetToDefault();
            ApplyNamePlateSettings();
        }

        public static void ApplyNamePlateSettings()
        {
            BasisRemoteNamePlateDriver.ApplyNamePlateSettingsFromUI();
#if !UNITY_SERVER
            BasisNetworkPIPCameraDriver.ApplyPipNamePlateSettingsFromUI();
#endif        
        }
    }
}
