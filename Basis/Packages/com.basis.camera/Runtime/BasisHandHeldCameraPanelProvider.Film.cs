using System.Collections.Generic;
using UnityEngine;

namespace Basis.BasisUI.HandHeldCamera
{
    /// <summary>
    /// The grading that makes a stock rather than a filter: how big the grain is and where it
    /// sits, what colour a highlight glows, what the corners darken toward, which way the colour
    /// splits between shadow and highlight, and how far off the floor the blacks are lifted.
    ///
    /// <para>These are the controls the camera kinds are built out of, and they are here as
    /// controls for the same reason every other value a mode writes is: a mode is a starting point,
    /// not a lock. Pick Disposable, then pull the halation off it, and the panel says Custom and
    /// keeps your camera.</para>
    ///
    /// <para><b>Colours are edited as a hue and a strength</b>, not as three channels. Each of the
    /// four has a neutral it has to be able to return to exactly — white for the bloom tint, black
    /// for the vignette, grey for both ends of the split — and one slider that runs from that
    /// neutral out to a colour is both the shorter control and the one that cannot land somewhere
    /// meaningless. The cost is that seeding is an approximation: a colour is decomposed back into
    /// the nearest hue and strength, so a preset's exact tint is displayed rather than reproduced.
    /// Seeding never writes, so the shot is untouched until somebody actually moves a slider — at
    /// which point they have edited the mode and Custom is the right answer anyway.</para>
    /// </summary>
    public partial class BasisHandHeldCameraPanelProvider
    {
        /// <summary>
        /// The grain ladder the panel offers, coarsest last. URP ships ten textures whose names are
        /// its own internal numbering; four steps is what anybody actually chooses between, and the
        /// setting still stores the raw value so a file naming one of the other six keeps it.
        /// </summary>
        private static readonly int[] GrainTypeValues =
        {
            (int)UnityEngine.Rendering.Universal.FilmGrainLookup.Thin2,
            (int)UnityEngine.Rendering.Universal.FilmGrainLookup.Medium1,
            (int)UnityEngine.Rendering.Universal.FilmGrainLookup.Large01,
            (int)UnityEngine.Rendering.Universal.FilmGrainLookup.Large02,
        };

        private static readonly string[] GrainTypeKeys =
        {
            "camera.filmGrain.type.fine",
            "camera.filmGrain.type.medium",
            "camera.filmGrain.type.large",
            "camera.filmGrain.type.coarse",
        };

        // How saturated a colour the strength slider reaches at its top. Chosen per control rather
        // than shared: a vignette that reached full saturation would be a coloured ring rather than
        // a dark corner, and a split tone that did would be a duotone.
        private const float VignetteColourSaturation = 0.8f;
        private const float VignetteColourValue = 0.25f;
        private const float BloomTintSaturation = 0.9f;
        private const float SplitToningSaturation = 0.7f;

        private PanelDropdown _grainTypeDropdown;
        private PanelSlider _grainResponseSlider;
        private PanelSlider _bloomTintHueSlider;
        private PanelSlider _bloomTintStrengthSlider;
        private PanelSlider _vignetteColourHueSlider;
        private PanelSlider _vignetteColourStrengthSlider;
        private PanelToggle _vignetteRoundedToggle;
        private PanelSlider _splitShadowHueSlider;
        private PanelSlider _splitShadowStrengthSlider;
        private PanelSlider _splitHighlightHueSlider;
        private PanelSlider _splitHighlightStrengthSlider;
        private PanelSlider _splitBalanceSlider;
        private PanelSlider _filmLiftSlider;

        private bool? _lastVignetteRounded;

        // ---------- Building ----------

        /// <summary>The film grading: the Film Look section.</summary>
        private void BuildFilmColourControls(RectTransform content)
        {
            _filmLiftSlider = PanelSlider.CreateNew(content);
            _filmLiftSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.filmLift"),
                BasisHandHeldCameraUI.MinFilmLift * 100f, BasisHandHeldCameraUI.MaxFilmLift * 100f,
                false, 1, ValueDisplayMode.Percentage));
            _filmLiftSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.filmLift.description"));
            _filmLiftSlider.OnValueChanged = v => _activeCamera?.HandHeld.ChangeFilmLift(v / 100f);

            _splitShadowHueSlider = PanelSlider.CreateNew(content);
            _splitShadowHueSlider.SetSliderSettings(PanelSlider.SliderSettings.Degrees(
                BasisLocalization.Get("camera.splitToning.shadows"), 0f, 360f, true, 0));
            _splitShadowHueSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.splitToning.shadows.description"));
            _splitShadowHueSlider.OnValueChanged = _ => PushSplitToning();

            _splitShadowStrengthSlider = PanelSlider.CreateNew(content);
            _splitShadowStrengthSlider.SetSliderSettings(PanelSlider.SliderSettings.Percentage(
                BasisLocalization.Get("camera.splitToning.shadowStrength")));
            _splitShadowStrengthSlider.OnValueChanged = _ => PushSplitToning();

            _splitHighlightHueSlider = PanelSlider.CreateNew(content);
            _splitHighlightHueSlider.SetSliderSettings(PanelSlider.SliderSettings.Degrees(
                BasisLocalization.Get("camera.splitToning.highlights"), 0f, 360f, true, 0));
            _splitHighlightHueSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.splitToning.highlights.description"));
            _splitHighlightHueSlider.OnValueChanged = _ => PushSplitToning();

            _splitHighlightStrengthSlider = PanelSlider.CreateNew(content);
            _splitHighlightStrengthSlider.SetSliderSettings(PanelSlider.SliderSettings.Percentage(
                BasisLocalization.Get("camera.splitToning.highlightStrength")));
            _splitHighlightStrengthSlider.OnValueChanged = _ => PushSplitToning();

            _splitBalanceSlider = PanelSlider.CreateNew(content);
            _splitBalanceSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.splitToning.balance"),
                BasisHandHeldCameraUI.MinSplitToningBalance, BasisHandHeldCameraUI.MaxSplitToningBalance,
                false, 0, ValueDisplayMode.Raw));
            _splitBalanceSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.splitToning.balance.description"));
            _splitBalanceSlider.OnValueChanged = v => _activeCamera?.HandHeld.ChangeSplitToningBalance(v);
        }

        private void BuildGrainShapeControls(RectTransform content)
        {
            _grainTypeDropdown = PanelDropdown.CreateNewEntry(content);
            _grainTypeDropdown.Descriptor.SetTitle(BasisLocalization.Get("camera.filmGrain.type"));
            _grainTypeDropdown.Descriptor.SetTooltip(BasisLocalization.Get("camera.filmGrain.type.description"));
            _grainTypeDropdown.AssignLocalizedEntries(
                new List<string>(GrainTypeKeys), new List<string>(GrainTypeKeys));
            _grainTypeDropdown.OnValueChanged = _ =>
            {
                if (_activeCamera == null || _grainTypeDropdown == null) return;

                int index = Mathf.Clamp(_grainTypeDropdown.Index, 0, GrainTypeValues.Length - 1);
                _activeCamera.HandHeld.ChangeFilmGrainType(GrainTypeValues[index]);
            };

            _grainResponseSlider = PanelSlider.CreateNew(content);
            _grainResponseSlider.SetSliderSettings(PanelSlider.SliderSettings.Percentage(
                BasisLocalization.Get("camera.filmGrain.response")));
            _grainResponseSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.filmGrain.response.description"));
            _grainResponseSlider.OnValueChanged = v => _activeCamera?.HandHeld.ChangeFilmGrainResponse(v / 100f);
        }

        private void BuildBloomTintControls(RectTransform content)
        {
            _bloomTintHueSlider = PanelSlider.CreateNew(content);
            _bloomTintHueSlider.SetSliderSettings(PanelSlider.SliderSettings.Degrees(
                BasisLocalization.Get("camera.bloomTint"), 0f, 360f, true, 0));
            _bloomTintHueSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.bloomTint.description"));
            _bloomTintHueSlider.OnValueChanged = _ => PushBloomTint();

            _bloomTintStrengthSlider = PanelSlider.CreateNew(content);
            _bloomTintStrengthSlider.SetSliderSettings(PanelSlider.SliderSettings.Percentage(
                BasisLocalization.Get("camera.bloomTint.strength")));
            _bloomTintStrengthSlider.OnValueChanged = _ => PushBloomTint();
        }

        private void BuildVignetteColourControls(RectTransform content)
        {
            _vignetteColourHueSlider = PanelSlider.CreateNew(content);
            _vignetteColourHueSlider.SetSliderSettings(PanelSlider.SliderSettings.Degrees(
                BasisLocalization.Get("camera.vignetteColour"), 0f, 360f, true, 0));
            _vignetteColourHueSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.vignetteColour.description"));
            _vignetteColourHueSlider.OnValueChanged = _ => PushVignetteColour();

            _vignetteColourStrengthSlider = PanelSlider.CreateNew(content);
            _vignetteColourStrengthSlider.SetSliderSettings(PanelSlider.SliderSettings.Percentage(
                BasisLocalization.Get("camera.vignetteColour.strength")));
            _vignetteColourStrengthSlider.OnValueChanged = _ => PushVignetteColour();

            _vignetteRoundedToggle = PanelToggle.CreateNewEntry(content);
            _vignetteRoundedToggle.Descriptor.SetTitle(BasisLocalization.Get("camera.vignetteRounded"));
            _vignetteRoundedToggle.Descriptor.SetTooltip(BasisLocalization.Get("camera.vignetteRounded.description"));
            _vignetteRoundedToggle.OnValueChanged = v =>
            {
                _activeCamera?.HandHeld.ChangeVignetteRounded(v);
                _lastVignetteRounded = v;
            };
        }

        // ---------- Composing a colour out of two sliders ----------

        private void PushSplitToning()
        {
            if (_activeCamera == null) return;

            _activeCamera.HandHeld.ChangeSplitToning(
                ComposeSplitTone(_splitShadowHueSlider, _splitShadowStrengthSlider),
                ComposeSplitTone(_splitHighlightHueSlider, _splitHighlightStrengthSlider));
        }

        /// <summary>
        /// A split tone at half value, so no strength is exactly URP's neutral grey and the effect
        /// tints rather than darkens or lifts. Only the hue and how far from grey it goes are the
        /// operator's; the brightness is what makes it a tint at all.
        /// </summary>
        private static Color ComposeSplitTone(PanelSlider hue, PanelSlider strength) =>
            Color.HSVToRGB(
                Mathf.Repeat(Read(hue), 360f) / 360f,
                Read(strength) / 100f * SplitToningSaturation,
                0.5f);

        /// <summary>
        /// What a slider is showing, or zero where the page has been torn down under a callback.
        /// Every one of these controls is read in pairs, so a half-built pair has to answer with the
        /// neutral rather than throw.
        /// </summary>
        private static float Read(PanelSlider slider) =>
            slider != null && slider.SliderComponent != null ? slider.SliderComponent.value : 0f;

        private void PushBloomTint()
        {
            if (_activeCamera == null) return;

            // Lerped from white rather than built at full value, because the neutral for a tint that
            // multiplies the glow is white — a strength of nothing has to leave the bloom alone.
            Color hue = Color.HSVToRGB(
                Mathf.Repeat(Read(_bloomTintHueSlider), 360f) / 360f, BloomTintSaturation, 1f);

            float strength = Read(_bloomTintStrengthSlider) / 100f;
            _activeCamera.HandHeld.ChangeBloomTint(Color.Lerp(Color.white, hue, strength));
        }

        private void PushVignetteColour()
        {
            if (_activeCamera == null) return;

            // Value rather than a lerp: the neutral here is black, and scaling the brightness down
            // to nothing reaches it exactly while keeping the hue meaningful all the way down.
            float strength = Read(_vignetteColourStrengthSlider) / 100f;
            _activeCamera.HandHeld.ChangeVignetteColour(Color.HSVToRGB(
                Mathf.Repeat(Read(_vignetteColourHueSlider), 360f) / 360f,
                VignetteColourSaturation,
                strength * VignetteColourValue));
        }

        // ---------- Seeding ----------

        /// <summary>
        /// Points every film control at what the camera is currently holding. Nothing here notifies,
        /// so opening the panel on a camera in a mode cannot nudge it out of that mode.
        /// </summary>
        private void SeedFilmControls(BasisHandHeldCameraMetaData metaData)
        {
            if (metaData == null || _activeCamera == null) return;

            if (metaData.filmGrain != null)
            {
                _grainTypeDropdown?.SetValueWithoutNotify(GrainTypeKeys[NearestGrainStep((int)metaData.filmGrain.type.value)]);
                _grainResponseSlider?.SetValueWithoutNotify(metaData.filmGrain.response.value * 100f);
            }

            if (metaData.bloom != null)
            {
                // Distance from white, which is this control's neutral. A tint's saturation IS its
                // strength here, so the two come apart cleanly.
                Color.RGBToHSV(metaData.bloom.tint.value, out float bloomHue, out float bloomSaturation, out _);
                _bloomTintHueSlider?.SetValueWithoutNotify(Mathf.Round(bloomHue * 360f));
                _bloomTintStrengthSlider?.SetValueWithoutNotify(Mathf.Clamp01(bloomSaturation / BloomTintSaturation) * 100f);
            }

            if (metaData.vignette != null)
            {
                Color.RGBToHSV(metaData.vignette.color.value, out float vignetteHue, out _, out float vignetteValue);
                _vignetteColourHueSlider?.SetValueWithoutNotify(Mathf.Round(vignetteHue * 360f));
                _vignetteColourStrengthSlider?.SetValueWithoutNotify(
                    Mathf.Clamp01(vignetteValue / VignetteColourValue) * 100f);

                SyncToggle(_vignetteRoundedToggle, metaData.vignette.rounded.value, ref _lastVignetteRounded);
            }

            if (metaData.splitToning != null)
            {
                SeedSplitTone(metaData.splitToning.shadows.value, _splitShadowHueSlider, _splitShadowStrengthSlider);
                SeedSplitTone(metaData.splitToning.highlights.value, _splitHighlightHueSlider, _splitHighlightStrengthSlider);
                _splitBalanceSlider?.SetValueWithoutNotify(metaData.splitToning.balance.value);
            }

            if (metaData.liftGammaGain != null)
            {
                _filmLiftSlider?.SetValueWithoutNotify(metaData.liftGammaGain.lift.value.w * 100f);
            }
        }

        private static void SeedSplitTone(Color tone, PanelSlider hue, PanelSlider strength)
        {
            Color.RGBToHSV(tone, out float toneHue, out float toneSaturation, out _);
            hue?.SetValueWithoutNotify(Mathf.Round(toneHue * 360f));
            strength?.SetValueWithoutNotify(Mathf.Clamp01(toneSaturation / SplitToningSaturation) * 100f);
        }

        // ---------- Reset defaults ----------

        /// <summary>
        /// Where the options gesture returns each film control. Decomposed out of the camera
        /// defaults by the same maths that seeds them, rather than written out as zeroes: every
        /// colour here has a neutral that is white, black or grey instead of nothing, and a hand
        /// written default that drifted from the settings object is a reset that quietly grades the
        /// shot. Without these the whole Film Look section — and the grading controls the bloom,
        /// vignette and grain sections borrow from it — had no default to go back to, so the
        /// gesture was never offered on them at all.
        /// </summary>
        private void AssignFilmResetDefaults(BasisHandHeldCameraUI.CameraSettings defaults)
        {
            _grainTypeDropdown?.SetResetDefault(GrainTypeKeys[NearestGrainStep(defaults.filmGrainType)]);
            _grainResponseSlider?.SetResetDefault(defaults.filmGrainResponse * 100f);

            Color.RGBToHSV(defaults.bloomTint, out float bloomHue, out float bloomSaturation, out _);
            _bloomTintHueSlider?.SetResetDefault(Mathf.Round(bloomHue * 360f));
            _bloomTintStrengthSlider?.SetResetDefault(Mathf.Clamp01(bloomSaturation / BloomTintSaturation) * 100f);

            Color.RGBToHSV(defaults.vignetteColour, out float vignetteHue, out _, out float vignetteValue);
            _vignetteColourHueSlider?.SetResetDefault(Mathf.Round(vignetteHue * 360f));
            _vignetteColourStrengthSlider?.SetResetDefault(Mathf.Clamp01(vignetteValue / VignetteColourValue) * 100f);
            _vignetteRoundedToggle?.SetResetDefault(defaults.vignetteRounded);

            SetSplitToneResetDefault(defaults.splitToningShadows, _splitShadowHueSlider, _splitShadowStrengthSlider);
            SetSplitToneResetDefault(defaults.splitToningHighlights, _splitHighlightHueSlider, _splitHighlightStrengthSlider);
            _splitBalanceSlider?.SetResetDefault(defaults.splitToningBalance);

            _filmLiftSlider?.SetResetDefault(defaults.filmLift * 100f);
        }

        private static void SetSplitToneResetDefault(Color tone, PanelSlider hue, PanelSlider strength)
        {
            Color.RGBToHSV(tone, out float toneHue, out float toneSaturation, out _);
            hue?.SetResetDefault(Mathf.Round(toneHue * 360f));
            strength?.SetResetDefault(Mathf.Clamp01(toneSaturation / SplitToningSaturation) * 100f);
        }

        /// <summary>
        /// The rung of the four-step ladder nearest a raw URP grain texture. A file naming one of the
        /// six textures the ladder skips still shows the closest thing to it rather than snapping the
        /// dropdown to its first row.
        /// </summary>
        private static int NearestGrainStep(int type)
        {
            int best = 0;
            int bestDistance = int.MaxValue;

            for (int Index = 0; Index < GrainTypeValues.Length; Index++)
            {
                int distance = Mathf.Abs(GrainTypeValues[Index] - type);
                if (distance >= bestDistance) continue;

                bestDistance = distance;
                best = Index;
            }

            return best;
        }

        private void ClearFilmReferences()
        {
            _grainTypeDropdown = null;
            _grainResponseSlider = null;
            _bloomTintHueSlider = null;
            _bloomTintStrengthSlider = null;
            _vignetteColourHueSlider = null;
            _vignetteColourStrengthSlider = null;
            _vignetteRoundedToggle = null;
            _splitShadowHueSlider = null;
            _splitShadowStrengthSlider = null;
            _splitHighlightHueSlider = null;
            _splitHighlightStrengthSlider = null;
            _splitBalanceSlider = null;
            _filmLiftSlider = null;

            _lastVignetteRounded = null;
        }

#if UNITY_INCLUDE_TESTS
        /// <summary>The grain ladder, so the labels and the values it stands in for can be paired up.</summary>
        public static int[] GrainTypeValuesForTest => GrainTypeValues;

        public static string[] GrainTypeKeysForTest => GrainTypeKeys;
#endif
    }
}
