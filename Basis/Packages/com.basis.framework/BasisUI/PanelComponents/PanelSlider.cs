using System;
using System.Globalization;
using Basis.BTween;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Basis.BasisUI
{
    public enum ValueDisplayMode
    {
        Percentage,
        Raw,
        Meters,
        Degrees,
        percentageFromZero,
        MemorySize,
        /// <summary>
        /// SI-style short form: 1k / 10k / 32.5k / 1.2M. Decimals are only shown
        /// when the scaled value isn't a whole number, so clean round values stay
        /// clean. Intended for large integer sliders like triangle / bone counts
        /// where "2000000" is unreadable but "2M" is obvious at a glance.
        /// </summary>
        Compact,
        Hz
    }

    public class PanelSlider : PanelDataComponent<float>, IPointerDownHandler, IPointerUpHandler
    {

        [Serializable]
        public struct SliderSettings
        {
            public string Title;
            public string Description;
            public float SliderMin;
            public float SliderMax;
            public bool UseWholeNumbers;
            [Min(0)] public int DecimalPlaces;
            public ValueDisplayMode DisplayMode;

            public SliderSettings(string title, string description, float sliderMin, float sliderMax, bool useWholeNumbers, int decimalPlaces, ValueDisplayMode displayMode)
            {
                Title = title;
                Description = description;
                SliderMin = sliderMin;
                SliderMax = sliderMax;
                UseWholeNumbers = useWholeNumbers;
                DecimalPlaces = decimalPlaces;
                DisplayMode = displayMode;
            }
            public static SliderSettings Advanced(string title, float sliderMin, float sliderMax, bool useWholeNumbers, int decimalPlaces, ValueDisplayMode displayMode)
            {
                return new SliderSettings
                {
                    Title = title,
                    SliderMin = sliderMin,
                    SliderMax = sliderMax,
                    UseWholeNumbers = useWholeNumbers,
                    DecimalPlaces = decimalPlaces,
                    DisplayMode = displayMode,
                };
            }
            public static SliderSettings Degrees(string title, float sliderMin, float sliderMax, bool useWholeNumbers, int decimalPlaces)
            {
                return new SliderSettings
                {
                    Title = title,
                    SliderMin = sliderMin,
                    SliderMax = sliderMax,
                    UseWholeNumbers = useWholeNumbers,
                    DecimalPlaces = decimalPlaces,
                    DisplayMode =  ValueDisplayMode.Degrees,
                };
            }

            public static SliderSettings Percentage(string title)
            {
                return new SliderSettings
                {
                    Title = title,
                    SliderMin = 0,
                    SliderMax = 100,
                    UseWholeNumbers = true,
                    DecimalPlaces = 0,
                    DisplayMode = ValueDisplayMode.Percentage,
                };
            }

            public static SliderSettings Distance(string title, float max)
            {
                return new SliderSettings
                {
                    Title = title,
                    SliderMin = 0,
                    SliderMax = max,
                    UseWholeNumbers = true,
                    DecimalPlaces = 0,
                    DisplayMode = ValueDisplayMode.Meters,
                };
            }

        }

        public TextMeshProUGUI CurrentValueLabel;
        public TextMeshProUGUI MinValueLabel;
        public TextMeshProUGUI MaxValueLabel;
        public SliderValueConfirmedListener SliderConfirmedListener;

        [field: SerializeField] public SliderSettings Settings { get; protected set; }

        [Header("Slider Fill")]
        public Graphic FillGraphic;
        public Color FillColorMin = new Color(0.35f, 0.55f, 0.85f, 1f);
        public Color FillColorMax = new Color(0.25f, 0.8f, 0.5f, 1f);
        [Tooltip("When set, the fill color is sampled from this gradient (t = normalized slider position) instead of lerping FillColorMin -> FillColorMax.")]
        public Gradient FillColorGradient;
        public bool UseFillColorGradient;

        public static class SliderStyles
        {
            public static string Default => "Packages/com.basis.sdk/Prefabs/Panel Elements/PE Slider.prefab";
            public static string Entry => "Packages/com.basis.sdk/Prefabs/Panel Elements/PE Slider - Entry Variant.prefab";
        }

        public Slider SliderComponent;

        protected override Selectable InteractableTarget => SliderComponent;

        private RectTransform _handleRect;
        private Graphic _roundedFrontGraphic;
        private TweenScale _handleScaleTween;
        private TweenGraphicColor _fillColorTween;
        private TweenScale _labelPunchTween;
        private bool _isDragging;

        protected override bool SupportsResetGesture => true;

        public static PanelSlider CreateNew(Component parent)
            => CreateNew<PanelSlider>(SliderStyles.Default, parent);


        public static PanelSlider CreateAndBind(
            Component parent,
            SliderSettings settings,
            BasisSettingsBinding<float> binding)
        {
            PanelSlider slider = CreateNew<PanelSlider>(SliderStyles.Default, parent);
            slider.SetSliderSettings(settings);
            slider.AssignBinding(binding);
            return slider;
        }

        public static PanelSlider CreateEntryAndBind(
            Component parent,
            SliderSettings settings,
            BasisSettingsBinding<float> binding)
        {
            PanelSlider slider = CreateNew<PanelSlider>(SliderStyles.Entry, parent);
            slider.SetSliderSettings(settings);
            slider.AssignBinding(binding);
            return slider;
        }

        public static PanelSlider CreateNew(string style, Component parent)
            => CreateNew<PanelSlider>(style, parent);


        public override void OnCreateEvent()
        {
            base.OnCreateEvent();
            ApplySliderSettings();
            SliderComponent.onValueChanged.AddListener(OnSliderValueChanged);
            SliderConfirmedListener.OnValueConfirmed += OnSliderConfirmed;

            // Cache handle rect for scale animations
            if (SliderComponent.handleRect != null)
            {
                _handleRect = SliderComponent.handleRect;
            }

            // Try to find fill graphic if not assigned
            if (FillGraphic == null && SliderComponent.fillRect != null)
            {
                FillGraphic = SliderComponent.fillRect.GetComponent<Graphic>();
            }

            // Color-match the rounded front cap to the fill so it blends seamlessly
            if (SliderComponent.fillRect != null)
            {
                Transform roundedFront = SliderComponent.fillRect.parent.Find("Rounded Front");
                if (roundedFront != null)
                {
                    _roundedFrontGraphic = roundedFront.GetComponent<Graphic>();
                }
            }
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (!Application.isPlaying) return;

            // Some input paths do report a real right button; honour it. BasisUIInput never fills
            // its right-button state, so the hovered poll is the actual desktop path.
            if (eventData.button == PointerEventData.InputButton.Right)
            {
                RequestReset();
                return;
            }

            BeginDragVisual();
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (!Application.isPlaying) return;
            EndDragVisual();
        }

        // Scale up handle on grab
        private void BeginDragVisual()
        {
            _isDragging = true;
            if (_handleRect != null)
            {
                if (_handleScaleTween != null && _handleScaleTween.Active && _handleScaleTween.Target == _handleRect) _handleScaleTween.Reset();
                _handleScaleTween = _handleRect.TweenScale(0.12f, _handleRect.localScale, Vector3.one * 1.25f)
                    .SetEase(Easing.OutBack);
            }
        }

        // Scale down handle on release
        private void EndDragVisual()
        {
            _isDragging = false;
            if (_handleRect != null)
            {
                if (_handleScaleTween != null && _handleScaleTween.Active && _handleScaleTween.Target == _handleRect) _handleScaleTween.Reset();
                _handleScaleTween = _handleRect.TweenScale(0.2f, _handleRect.localScale, Vector3.one)
                    .SetEase(Easing.OutBack);
            }
        }

        // Applies visually, does not write to settings.
        private void OnSliderValueChanged(float value)
        {
            Value = value;
            ApplyValue();
        }

        // Applies to settings once the user is done moving the slider.
        private void OnSliderConfirmed()
        {
            SetValue(SliderComponent.value);

            // Punch the value label when user confirms
            if (Application.isPlaying && CurrentValueLabel != null)
            {
                if (_labelPunchTween != null && _labelPunchTween.Active && _labelPunchTween.Target == CurrentValueLabel.transform) _labelPunchTween.Reset();
                Transform labelTransform = CurrentValueLabel.transform;
                _labelPunchTween = labelTransform.TweenScale(0.06f, labelTransform.localScale, Vector3.one * 1.15f)
                    .SetEase(Easing.OutCubic)
                    .AddCallback(() =>
                    {
                        if (labelTransform != null)
                        {
                            labelTransform.TweenScale(0.15f, Vector3.one * 1.15f, Vector3.one)
                                .SetEase(Easing.OutBack);
                        }
                    });
            }
        }

        public void SetSliderSettings(SliderSettings settings)
        {
            Settings = settings;
            ApplySliderSettings();
        }

        private string _cachedDecimalFormat;
        private int _cachedDecimalPlaces = -1;
        private string _lastCurrentValueText;
        private float _lastFormattedValue;
        private bool _hasLastFormattedValue;

        protected virtual void ApplySliderSettings()
        {
            Descriptor.SetTitle(Settings.Title);
            Descriptor.SetDescription(Settings.Description);

            SliderComponent.minValue = Settings.SliderMin;
            SliderComponent.maxValue = Settings.SliderMax;
            SliderComponent.wholeNumbers = Settings.UseWholeNumbers;

            // Value labels only ever display plain numbers — skip TMP rich-text parsing.
            if (MinValueLabel)
            {
                MinValueLabel.richText = false;
                MinValueLabel.SetText(Settings.SliderMin.ToString(CultureInfo.InvariantCulture));
            }
            if (MaxValueLabel)
            {
                MaxValueLabel.richText = false;
                MaxValueLabel.SetText(Settings.SliderMax.ToString(CultureInfo.InvariantCulture));
            }
            if (CurrentValueLabel)
            {
                CurrentValueLabel.richText = false;
            }

            // Prebuild the decimal format string once so ApplyValue doesn't allocate
            // a fresh "0.##" string every time the slider moves.
            if (_cachedDecimalPlaces != Settings.DecimalPlaces)
            {
                _cachedDecimalPlaces = Settings.DecimalPlaces;
                _cachedDecimalFormat = "0." + new string('#', Mathf.Max(0, Settings.DecimalPlaces));
            }
            _lastCurrentValueText = null;
            _hasLastFormattedValue = false;
        }

        public override void SetValueWithoutNotify(float value)
        {
            base.SetValueWithoutNotify(value);
            if (SliderComponent != null)
            {
                SliderComponent.SetValueWithoutNotify(value);
            }
            else
            {
                BasisDebug.LogError("Missing Slider Component!");
            }
        }

        protected override void ApplyValue()
        {
            base.ApplyValue();

            // Animate fill color based on normalized position
            if (Application.isPlaying && FillGraphic != null)
            {
                float range = SliderComponent.maxValue - SliderComponent.minValue;
                float t = (range > 0f) ? (Value - SliderComponent.minValue) / range : 0f;
                Color targetFillColor = (UseFillColorGradient && FillColorGradient != null)
                    ? FillColorGradient.Evaluate(t)
                    : Color.Lerp(FillColorMin, FillColorMax, t);

                if (_isDragging)
                {
                    // Instant color while dragging for responsiveness
                    FillGraphic.color = targetFillColor;
                    if (_roundedFrontGraphic != null) _roundedFrontGraphic.color = targetFillColor;
                }
                else
                {
                    if (_fillColorTween != null && _fillColorTween.Active && _fillColorTween.Target == FillGraphic) _fillColorTween.Reset();
                    _fillColorTween = FillGraphic.TweenColor(0.15f, FillGraphic.color, targetFillColor)
                        .SetEase(Easing.OutCubic);
                    if (_roundedFrontGraphic != null) _roundedFrontGraphic.color = targetFillColor;
                }
            }

            if (CurrentValueLabel == null) return;

            // The label is a pure function of Value, so an unchanged value skips the string
            // build entirely — the dedup below only saved the SetText, not the allocation.
            if (_hasLastFormattedValue && Value == _lastFormattedValue) return;
            _hasLastFormattedValue = true;
            _lastFormattedValue = Value;

            string next;
            switch (Settings.DisplayMode)
            {
                case ValueDisplayMode.Percentage:
                    float range2 = SliderComponent.maxValue - SliderComponent.minValue;
                    float normalized = (range2 > 0f) ? (Value - SliderComponent.minValue) / range2 : 0f;
                    next = $"{Mathf.RoundToInt(normalized * 100f)}%";
                    break;
                case ValueDisplayMode.percentageFromZero:
                    next = $"{Mathf.RoundToInt(Value * 100f)}%";
                    break;
                case ValueDisplayMode.Raw:
                    next = Value.ToString(_cachedDecimalFormat);
                    break;
                case ValueDisplayMode.Meters:
                    next = Value.ToString(_cachedDecimalFormat) + " m";
                    break;
                case ValueDisplayMode.Degrees:
                    next = Value.ToString(_cachedDecimalFormat) + "°";
                    break;
                case ValueDisplayMode.MemorySize:
                    next = FormatMemorySize(Value * 1024 * 1024, Settings.DecimalPlaces);
                    break;
                case ValueDisplayMode.Compact:
                    next = FormatCompact(Value);
                    break;
                case ValueDisplayMode.Hz:
                    next = Value.ToString(_cachedDecimalFormat) + " Hz";
                    break;
                default:
                    return;
            }

            // Dedup — dragging a slider fires ApplyValue many times per second, and the
            // rounded/formatted text is often identical between frames.
            if (_lastCurrentValueText == next) return;
            _lastCurrentValueText = next;
            CurrentValueLabel.SetText(next);
        }
        private static string FormatMemorySize(float bytes, int decimalPlaces = 2)
        {
            if (bytes < 0f)
                return "0 B";

            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int unitIndex = 0;

            while (bytes >= 1024f && unitIndex < units.Length - 1)
            {
                bytes /= 1024f;
                unitIndex++;
            }

            return bytes.ToString($"0.{new string('#', decimalPlaces)}") + " " + units[unitIndex];
        }

        /// <summary>
        /// SI-style short number (<c>Compact</c> display mode).
        ///   < 1000        → plain integer
        ///   1k – 999k     → "1k" / "32.5k" (one decimal only when non-zero)
        ///   &gt;= 1M      → "1M" / "2.5M"
        /// Negative values are formatted the same way with a leading minus.
        /// </summary>
        private static string FormatCompact(float value)
        {
            float abs = Mathf.Abs(value);
            if (abs >= 1_000_000f)
            {
                float scaled = value / 1_000_000f;
                return scaled.ToString(scaled % 1f == 0f ? "0" : "0.#", CultureInfo.InvariantCulture) + "M";
            }
            if (abs >= 1000f)
            {
                float scaled = value / 1000f;
                return scaled.ToString(scaled % 1f == 0f ? "0" : "0.#", CultureInfo.InvariantCulture) + "k";
            }
            return value.ToString("0", CultureInfo.InvariantCulture);
        }
    }
}
