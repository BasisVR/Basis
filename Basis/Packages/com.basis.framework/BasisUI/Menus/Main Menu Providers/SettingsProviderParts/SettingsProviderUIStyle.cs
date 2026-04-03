using Basis;
using Basis.BasisUI;
using Basis.BasisUI.Styling;
using System;
using System.Collections.Generic;
using UnityEngine;

public static class SettingsProviderUIStyle
{
    private static readonly Dictionary<UiPaletteStyle, Color> OriginalPaletteColors = new();

    [RuntimeInitializeOnLoadMethod]
    private static void Init()
    {
        CacheOriginals();
        ApplySavedPaletteColors();
    }

    private static void CacheOriginals()
    {
        UiStylePalette palette = UiStyleSettings.GetActivePalette();
        if (palette == null) return;

        foreach (UiPaletteStyle style in Enum.GetValues(typeof(UiPaletteStyle)))
        {
            OriginalPaletteColors[style] = palette.GetColor(style);
        }
    }

    private static void ApplySavedPaletteColors()
    {
        UiStylePalette palette = UiStyleSettings.GetActivePalette();
        if (palette == null) return;

        ApplyColorBinding(UiPaletteStyle.BackgroundColor1, BasisSettingsDefaults.UIPaletteBG1);
        ApplyColorBinding(UiPaletteStyle.BackgroundColor2, BasisSettingsDefaults.UIPaletteBG2);
        ApplyColorBinding(UiPaletteStyle.BackgroundColor3, BasisSettingsDefaults.UIPaletteBG3);
        ApplyColorBinding(UiPaletteStyle.LayerColor, BasisSettingsDefaults.UIPaletteLayer);
        ApplyColorBinding(UiPaletteStyle.AccentColor, BasisSettingsDefaults.UIPaletteAccent);
        ApplyColorBinding(UiPaletteStyle.FontColor1, BasisSettingsDefaults.UIPaletteFont1);
        ApplyColorBinding(UiPaletteStyle.FontColor2, BasisSettingsDefaults.UIPaletteFont2);
        ApplyColorBinding(UiPaletteStyle.FontColor3, BasisSettingsDefaults.UIPaletteFont3);
        ApplyColorBinding(UiPaletteStyle.InputFieldColor, BasisSettingsDefaults.UIPaletteInputField);
        ApplyColorBinding(UiPaletteStyle.ButtonColor, BasisSettingsDefaults.UIPaletteButton);
        ApplyColorBinding(UiPaletteStyle.WhiteColor, BasisSettingsDefaults.UIPaletteWhite);
        ApplyColorBinding(UiPaletteStyle.ClearColor, BasisSettingsDefaults.UIPaletteClear);
        ApplyColorBinding(UiPaletteStyle.BlackColor, BasisSettingsDefaults.UIPaletteBlack);
        ApplyColorBinding(UiPaletteStyle.SuccessColor, BasisSettingsDefaults.UIPaletteSuccess);
        ApplyColorBinding(UiPaletteStyle.CautionColor, BasisSettingsDefaults.UIPaletteCaution);
        ApplyColorBinding(UiPaletteStyle.DangerColor, BasisSettingsDefaults.UIPaletteDanger);
        ApplyColorBinding(UiPaletteStyle.Scrollbar, BasisSettingsDefaults.UIPaletteScrollbar);

        UiStyleSettings.UpdateAllStyleComponents();
    }

    private static void ApplyColorBinding(UiPaletteStyle style, BasisSettingsBinding<string> binding)
    {
        string hex = binding.RawValue;
        if (!string.IsNullOrEmpty(hex) && TryParseColor(hex, out Color color))
        {
            SetPaletteColor(style, color);
        }
    }

    public static PanelTabPage UIStyleTab(PanelTabGroup tabGroup)
    {
        PanelTabPage tab = PanelTabPage.CreateVertical(tabGroup.Descriptor.ContentParent);
        PanelElementDescriptor descriptor = tab.Descriptor;
        descriptor.SetIcon(AddressableAssets.Sprites.Settings);
        descriptor.SetTitle("UI Style");

        RectTransform container = descriptor.ContentParent;

        // Background Colors
        AddColorPicker(container, "Background 1", UiPaletteStyle.BackgroundColor1, BasisSettingsDefaults.UIPaletteBG1);
        AddColorPicker(container, "Background 2", UiPaletteStyle.BackgroundColor2, BasisSettingsDefaults.UIPaletteBG2);
        AddColorPicker(container, "Background 3", UiPaletteStyle.BackgroundColor3, BasisSettingsDefaults.UIPaletteBG3);
        AddColorPicker(container, "Layer", UiPaletteStyle.LayerColor, BasisSettingsDefaults.UIPaletteLayer);

        // UI Colors
        AddColorPicker(container, "Accent", UiPaletteStyle.AccentColor, BasisSettingsDefaults.UIPaletteAccent);
        AddColorPicker(container, "Button", UiPaletteStyle.ButtonColor, BasisSettingsDefaults.UIPaletteButton);
        AddColorPicker(container, "Input Field", UiPaletteStyle.InputFieldColor, BasisSettingsDefaults.UIPaletteInputField);
        AddColorPicker(container, "Scrollbar", UiPaletteStyle.Scrollbar, BasisSettingsDefaults.UIPaletteScrollbar);

        // Font Colors
        AddColorPicker(container, "Font Primary", UiPaletteStyle.FontColor1, BasisSettingsDefaults.UIPaletteFont1);
        AddColorPicker(container, "Font Secondary", UiPaletteStyle.FontColor2, BasisSettingsDefaults.UIPaletteFont2);
        AddColorPicker(container, "Font Tertiary", UiPaletteStyle.FontColor3, BasisSettingsDefaults.UIPaletteFont3);

        // Status Colors
        AddColorPicker(container, "Success", UiPaletteStyle.SuccessColor, BasisSettingsDefaults.UIPaletteSuccess);
        AddColorPicker(container, "Caution", UiPaletteStyle.CautionColor, BasisSettingsDefaults.UIPaletteCaution);
        AddColorPicker(container, "Danger", UiPaletteStyle.DangerColor, BasisSettingsDefaults.UIPaletteDanger);

        // Other Colors
        AddColorPicker(container, "White", UiPaletteStyle.WhiteColor, BasisSettingsDefaults.UIPaletteWhite);
        AddColorPicker(container, "Black", UiPaletteStyle.BlackColor, BasisSettingsDefaults.UIPaletteBlack);
        AddColorPicker(container, "Clear", UiPaletteStyle.ClearColor, BasisSettingsDefaults.UIPaletteClear);

        SettingsProvider.AddResetPageButton(container, "UI Style", ResetUIStyleDefaults);
        descriptor.ForceRebuild();
        return tab;
    }

    private static void AddColorPicker(RectTransform parent, string title,
        UiPaletteStyle style, BasisSettingsBinding<string> binding)
    {
        UiStylePalette palette = UiStyleSettings.GetActivePalette();
        Color currentColor = palette.GetColor(style);

        // Populate binding with current palette hex if no saved value
        if (string.IsNullOrEmpty(binding.RawValue))
        {
            binding.SetValueWithoutNotify(ColorUtility.ToHtmlStringRGBA(currentColor));
        }

        PanelElementDescriptor group = PanelElementDescriptor.CreateNew(
            PanelElementDescriptor.ElementStyles.Group, parent);
        group.SetTitle(title);

        RectTransform content = group.ContentParent;

        // Color preview swatch
        PanelImage preview = PanelImage.CreateNew(content);
        preview.Image.color = currentColor;
        preview.SetSize(new Vector2(200, 30));

        // Hue slider (0-360)
        Color.RGBToHSV(currentColor, out float h, out float s, out float v);
        float currentS = s;
        float currentV = v;
        float currentA = currentColor.a;

        PanelSlider hueSlider = PanelSlider.CreateNew(content);
        hueSlider.SetSliderSettings(new PanelSlider.SliderSettings(
            "Hue", "", 0, 360, true, 0, ValueDisplayMode.Degrees));
        hueSlider.SetValueWithoutNotify(Mathf.RoundToInt(h * 360));

        // Hex text field
        PanelTextField hexField = PanelTextField.CreateNewEntry(content);
        hexField.Descriptor.SetTitle("Hex");
        hexField.AssignBinding(binding);
        if (hexField._inputField != null)
        {
            hexField._inputField.characterLimit = 8;
        }

        // --- Wiring ---

        // Live preview while dragging hue slider
        hueSlider.SliderComponent.onValueChanged.AddListener(hue =>
        {
            Color c = Color.HSVToRGB(hue / 360f, currentS, currentV);
            c.a = currentA;
            SetPaletteColor(style, c);
            UiStyleSettings.UpdateAllStyleComponents();
            preview.Image.color = c;
            hexField._inputField?.SetTextWithoutNotify(ColorUtility.ToHtmlStringRGBA(c));
        });

        // Persist on slider release
        hueSlider.OnValueChanged += _ =>
        {
            Color c = Color.HSVToRGB(hueSlider.Value / 360f, currentS, currentV);
            c.a = currentA;
            binding.SetValue(ColorUtility.ToHtmlStringRGBA(c));
        };

        // Hex submit -> update hue slider + preview + palette
        hexField.OnValueChanged += hex =>
        {
            if (TryParseColor(hex, out Color parsed))
            {
                SetPaletteColor(style, parsed);
                UiStyleSettings.UpdateAllStyleComponents();
                preview.Image.color = parsed;

                Color.RGBToHSV(parsed, out float newH, out float newS, out float newV);
                currentS = newS;
                currentV = newV;
                currentA = parsed.a;
                hueSlider.SetValueWithoutNotify(Mathf.RoundToInt(newH * 360));
            }
        };
    }

    // --- Helpers ---

    private static bool TryParseColor(string hex, out Color color)
    {
        if (string.IsNullOrEmpty(hex))
        {
            color = default;
            return false;
        }
        if (!hex.StartsWith("#"))
            hex = "#" + hex;
        return ColorUtility.TryParseHtmlString(hex, out color);
    }

    private static void SetPaletteColor(UiPaletteStyle style, Color color)
    {
        UiStylePalette palette = UiStyleSettings.GetActivePalette();
        if (palette == null) return;

        switch (style)
        {
            case UiPaletteStyle.BackgroundColor1: palette.BackgroundColor1 = color; break;
            case UiPaletteStyle.BackgroundColor2: palette.BackgroundColor2 = color; break;
            case UiPaletteStyle.BackgroundColor3: palette.BackgroundColor3 = color; break;
            case UiPaletteStyle.LayerColor: palette.LayerColor = color; break;
            case UiPaletteStyle.AccentColor: palette.AccentColor = color; break;
            case UiPaletteStyle.FontColor1: palette.FontColor1 = color; break;
            case UiPaletteStyle.FontColor2: palette.FontColor2 = color; break;
            case UiPaletteStyle.FontColor3: palette.FontColor3 = color; break;
            case UiPaletteStyle.InputFieldColor: palette.InputFieldColor = color; break;
            case UiPaletteStyle.ButtonColor: palette.ButtonColor = color; break;
            case UiPaletteStyle.WhiteColor: palette.WhiteColor = color; break;
            case UiPaletteStyle.ClearColor: palette.ClearColor = color; break;
            case UiPaletteStyle.BlackColor: palette.BlackColor = color; break;
            case UiPaletteStyle.SuccessColor: palette.SuccessColor = color; break;
            case UiPaletteStyle.CautionColor: palette.CautionColor = color; break;
            case UiPaletteStyle.DangerColor: palette.DangerColor = color; break;
            case UiPaletteStyle.Scrollbar: palette.Scrollbar = color; break;
        }
    }

    private static void ResetUIStyleDefaults()
    {
        foreach (var kvp in OriginalPaletteColors)
        {
            SetPaletteColor(kvp.Key, kvp.Value);
        }

        BasisSettingsDefaults.UIPaletteBG1.ResetToDefault();
        BasisSettingsDefaults.UIPaletteBG2.ResetToDefault();
        BasisSettingsDefaults.UIPaletteBG3.ResetToDefault();
        BasisSettingsDefaults.UIPaletteLayer.ResetToDefault();
        BasisSettingsDefaults.UIPaletteAccent.ResetToDefault();
        BasisSettingsDefaults.UIPaletteFont1.ResetToDefault();
        BasisSettingsDefaults.UIPaletteFont2.ResetToDefault();
        BasisSettingsDefaults.UIPaletteFont3.ResetToDefault();
        BasisSettingsDefaults.UIPaletteInputField.ResetToDefault();
        BasisSettingsDefaults.UIPaletteButton.ResetToDefault();
        BasisSettingsDefaults.UIPaletteWhite.ResetToDefault();
        BasisSettingsDefaults.UIPaletteClear.ResetToDefault();
        BasisSettingsDefaults.UIPaletteBlack.ResetToDefault();
        BasisSettingsDefaults.UIPaletteSuccess.ResetToDefault();
        BasisSettingsDefaults.UIPaletteCaution.ResetToDefault();
        BasisSettingsDefaults.UIPaletteDanger.ResetToDefault();
        BasisSettingsDefaults.UIPaletteScrollbar.ResetToDefault();

        UiStyleSettings.UpdateAllStyleComponents();
    }
}
