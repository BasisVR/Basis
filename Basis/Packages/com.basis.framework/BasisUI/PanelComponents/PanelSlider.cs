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
        MemorySize
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

        public static class SliderStyles
        {
            public static string Default => "Packages/com.basis.sdk/Prefabs/Panel Elements/PE Slider.prefab";
            public static string Entry => "Packages/com.basis.sdk/Prefabs/Panel Elements/PE Slider - Entry Variant.prefab";
        }

        public Slider SliderComponent;

        private RectTransform _handleRect;
        private Graphic _roundedFrontGraphic;
        private TweenScale _handleScaleTween;
        private TweenGraphicColor _fillColorTween;
        private TweenScale _labelPunchTween;
        private bool _isDragging;


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
            _isDragging = true;

            // Scale up handle on grab
            if (_handleRect != null)
            {
                if (_handleScaleTween != null && _handleScaleTween.Active) _handleScaleTween.Reset();
                _handleScaleTween = _handleRect.TweenScale(0.12f, _handleRect.localScale, Vector3.one * 1.25f)
                    .SetEase(Easing.OutBack);
            }
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (!Application.isPlaying) return;
            _isDragging = false;

            // Scale down handle on release
            if (_handleRect != null)
            {
                if (_handleScaleTween != null && _handleScaleTween.Active) _handleScaleTween.Reset();
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
                if (_labelPunchTween != null && _labelPunchTween.Active) _labelPunchTween.Reset();
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

        protected virtual void ApplySliderSettings()
        {
            Descriptor.SetTitle(Settings.Title);
            Descriptor.SetDescription(Settings.Description);

            SliderComponent.minValue = Settings.SliderMin;
            SliderComponent.maxValue = Settings.SliderMax;
            SliderComponent.wholeNumbers = Settings.UseWholeNumbers;

            if (MinValueLabel) MinValueLabel.text = Settings.SliderMin.ToString(CultureInfo.InvariantCulture);
            if (MaxValueLabel) MaxValueLabel.text = Settings.SliderMax.ToString(CultureInfo.InvariantCulture);
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
                Color targetFillColor = Color.Lerp(FillColorMin, FillColorMax, t);

                if (_isDragging)
                {
                    // Instant color while dragging for responsiveness
                    FillGraphic.color = targetFillColor;
                    if (_roundedFrontGraphic != null) _roundedFrontGraphic.color = targetFillColor;
                }
                else
                {
                    if (_fillColorTween != null && _fillColorTween.Active) _fillColorTween.Reset();
                    _fillColorTween = FillGraphic.TweenColor(0.15f, FillGraphic.color, targetFillColor)
                        .SetEase(Easing.OutCubic);
                    if (_roundedFrontGraphic != null) _roundedFrontGraphic.color = targetFillColor;
                }
            }

            switch (Settings.DisplayMode)
            {
                case ValueDisplayMode.Percentage:
                    float range2 = SliderComponent.maxValue - SliderComponent.minValue;
                    float normalized = (range2 > 0f) ? (Value - SliderComponent.minValue) / range2 : 0f;
                    CurrentValueLabel.text = $"{Mathf.RoundToInt(normalized * 100f)}%";
                    break;
                case ValueDisplayMode.percentageFromZero:
                    CurrentValueLabel.text = $"{Mathf.RoundToInt(Value * 100f)}%";
                    break;

                case ValueDisplayMode.Raw:
                    CurrentValueLabel.text = Value.ToString("0." + new string('#', Settings.DecimalPlaces));
                    break;

                case ValueDisplayMode.Meters:
                    CurrentValueLabel.text = Value.ToString("0." + new string('#', Settings.DecimalPlaces)) + " m";
                    break;
                    case ValueDisplayMode.Degrees:
                    CurrentValueLabel.text = Value.ToString("0." + new string('#', Settings.DecimalPlaces)) + "°";
                    break;
                case ValueDisplayMode.MemorySize:
                    CurrentValueLabel.text = FormatMemorySize(Value *1024 * 1024, Settings.DecimalPlaces);
                    break;
            }
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
    }
}
