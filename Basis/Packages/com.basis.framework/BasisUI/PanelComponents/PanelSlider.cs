using System;
using System.Globalization;
using TMPro;
using UnityEngine;
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

    public class PanelSlider : PanelDataComponent<float>
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


        public static class SliderStyles
        {
            public static string Default => "Packages/com.basis.sdk/Prefabs/Panel Elements/PE Slider.prefab";
            public static string Entry => "Packages/com.basis.sdk/Prefabs/Panel Elements/PE Slider - Entry Variant.prefab";
        }

        public Slider SliderComponent;


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
            switch (Settings.DisplayMode)
            {
                case ValueDisplayMode.Percentage:
                    float range = SliderComponent.maxValue - SliderComponent.minValue;
                    float normalized = (range > 0f) ? (Value - SliderComponent.minValue) / range : 0f;
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
