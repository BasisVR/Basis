using System.Collections.Generic;
using Basis.BasisUI;
using UnityEngine;

/// <summary>
/// Builds the media player section of Settings &gt; Developer and registers it with
/// the framework through <see cref="SettingsProvider.DeveloperSectionBuilders"/>, so
/// the framework carries no reference to this package.
/// </summary>
public static class SettingsProviderMedia
{
    const string TitleKey = "settings.media.title";

    [RuntimeInitializeOnLoadMethod]
    static void Register()
    {
        // Detach first: with domain reload disabled in the editor these statics survive
        // into the next play session, and a second Add would draw the section twice.
        SettingsProvider.DeveloperSectionBuilders.Remove((TitleKey, BuildSection));
        SettingsProvider.DeveloperSectionBuilders.Add((TitleKey, BuildSection));
        SettingsProvider.DeveloperResetActions.Remove(ResetDefaults);
        SettingsProvider.DeveloperResetActions.Add(ResetDefaults);
        BasisMediaSettings.EnsureLoaded();
    }

    public static void BuildSection(RectTransform container)
    {
        PanelElementDescriptor group =
            PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
        group.SetBackgroundVisible(false);
        group.SetDescription(BasisLocalization.Get("settings.media.description"));

        PanelDropdown decode = PanelDropdown.CreateNewEntry(group.ContentParent);
        decode.Descriptor.SetTitle(BasisLocalization.Get("settings.media.decode"));
        decode.Descriptor.SetTooltip(BasisLocalization.Get("settings.media.decode.tooltip"));
        decode.AssignLocalizedEntries(
            new List<string>
            {
                BasisMediaSettings.DecodeHardwareWithFallback,
                BasisMediaSettings.DecodeHardwareOnly,
                BasisMediaSettings.DecodeSoftwareOnly,
            },
            new List<string>
            {
                "settings.media.decode.hardwareFallback",
                "settings.media.decode.hardwareOnly",
                "settings.media.decode.softwareOnly",
            });
        decode.AssignBinding(BasisMediaSettings.DecodePreference);

        PanelSlider maxActive = PanelSlider.CreateNew(group.ContentParent);
        maxActive.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
            BasisLocalization.Get("settings.media.maxActive"), 0f, 8f, true, 0, ValueDisplayMode.Raw));
        maxActive.Descriptor.SetTooltip(BasisLocalization.Get("settings.media.maxActive.tooltip"));
        maxActive.OnValueChanged = v =>
            BasisMediaSettings.MaxActivePlayers.SetValue(Mathf.RoundToInt(v));
        maxActive.SetValueWithoutNotify(BasisMediaSettings.MaxActivePlayers.RawValue);

        PanelSlider bufferDepth = PanelSlider.CreateNew(group.ContentParent);
        bufferDepth.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
            BasisLocalization.Get("settings.media.bufferDepth"), 0f, 4000f, true, 0, ValueDisplayMode.Raw));
        bufferDepth.Descriptor.SetTooltip(BasisLocalization.Get("settings.media.bufferDepth.tooltip"));
        bufferDepth.OnValueChanged = v =>
            BasisMediaSettings.BufferDepthMs.SetValue(Mathf.RoundToInt(v));
        bufferDepth.SetValueWithoutNotify(BasisMediaSettings.BufferDepthMs.RawValue);

        // Caption defaults. Each player follows these until the viewer decides
        // that player's captions from the Media Players panel, so this is where
        // somebody who always wants captions says so once.
        PanelToggle captions = PanelToggle.CreateNewEntry(group.ContentParent);
        captions.Descriptor.SetTitle(BasisLocalization.Get("settings.media.captions"));
        captions.Descriptor.SetTooltip(BasisLocalization.Get("settings.media.captions.tooltip"));
        captions.AssignBinding(BasisMediaSettings.CaptionsEnabled);

        PanelSlider captionText = PanelSlider.CreateNew(group.ContentParent);
        captionText.SetSliderSettings(PanelSlider.SliderSettings.Percentage(
            BasisLocalization.Get("settings.media.captionTextOpacity")));
        captionText.SetValueWithoutNotify(Mathf.Clamp01(BasisMediaSettings.CaptionTextOpacity.RawValue) * 100f);
        captionText.OnValueChanged = v =>
            BasisMediaSettings.CaptionTextOpacity.SetValue(Mathf.Clamp01(v / 100f));

        PanelSlider captionBackground = PanelSlider.CreateNew(group.ContentParent);
        captionBackground.SetSliderSettings(PanelSlider.SliderSettings.Percentage(
            BasisLocalization.Get("settings.media.captionBackgroundOpacity")));
        captionBackground.SetValueWithoutNotify(Mathf.Clamp01(BasisMediaSettings.CaptionBackgroundOpacity.RawValue) * 100f);
        captionBackground.OnValueChanged = v =>
            BasisMediaSettings.CaptionBackgroundOpacity.SetValue(Mathf.Clamp01(v / 100f));

        PanelElementDescriptor engineStatus =
            PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, group.ContentParent);
        engineStatus.SetTitle(BasisLocalization.Get("settings.media.engine.title"));
        engineStatus.SetDescription(DescribeEngine());
        engineStatus.IsolateAsCanvas();
    }

    /// <summary>Platform plus how many video routes the engine actually probed as
    /// hardware, which says whether "hardware only" can play anything here.</summary>
    static string DescribeEngine()
    {
        BmCapabilitySet caps = BasisMediaCapabilities.Set;
        if (caps == null)
            return BasisLocalization.Get("settings.media.engine.unavailable");

        int hardware = 0, software = 0;
        if (caps.video != null)
        {
            foreach (BmVideoCap route in caps.video)
            {
                if (route == null) continue;
                if (route.route == "hardware") hardware++;
                else software++;
            }
        }

        return BasisLocalization.Get("settings.media.engine.available",
            caps.platform, hardware, software);
    }

    static void ResetDefaults()
    {
        BasisMediaSettings.DecodePreference.ResetToDefault();
        BasisMediaSettings.CaptionsEnabled.ResetToDefault();
        BasisMediaSettings.CaptionTextOpacity.ResetToDefault();
        BasisMediaSettings.CaptionBackgroundOpacity.ResetToDefault();
        BasisMediaSettings.MaxActivePlayers.ResetToDefault();
        BasisMediaSettings.BufferDepthMs.ResetToDefault();
    }
}
