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
        Meters
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
        public OnEndDragListener OnEndDragListener;

        [field: SerializeField] public SliderSettings Settings { get; protected set; }


        public static class SliderStyles
        {
            public static string Default => "Packages/com.basis.framework/BasisUI/Prefabs/Panel Elements/PE Slider.prefab";
            public static string Entry => "Packages/com.basis.framework/BasisUI/Prefabs/Panel Elements/PE Slider - Entry Variant.prefab";
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
            OnEndDragListener.OnDragComplete += OnSliderDragComplete;
        }

        // Applies visually, does not write to settings.
        private void OnSliderValueChanged(float value)
        {
            Value = value;
            ApplyValue();
        }

        // Applies to settings once the user is done moving the slider.
        private void OnSliderDragComplete()
        {
            Value = SliderComponent.value;
            SettingsBinding?.SetValue(Value);
            ApplyValue();
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
            SliderComponent.SetValueWithoutNotify(value);
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

                case ValueDisplayMode.Raw:
                    CurrentValueLabel.text = Value.ToString("0." + new string('#', Settings.DecimalPlaces));
                    break;

                case ValueDisplayMode.Meters:
                    CurrentValueLabel.text = Value.ToString("0." + new string('#', Settings.DecimalPlaces)) + " m";
                    break;
            }
        }
    }
}
