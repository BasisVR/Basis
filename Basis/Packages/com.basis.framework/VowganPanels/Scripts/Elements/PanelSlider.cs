using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.VowganUI
{
    public enum ValueDisplayMode
    {
        Percentage,
        Raw,
        Meters
    }

    public class PanelSlider : PanelBindableElement
    {

        public override PanelBindingType BindingType => PanelBindingType.Float;


        [Serializable]
        public struct SliderSettings
        {
            public string Title;
            public float SliderMin;
            public float SliderMax;
            public bool UseWholeNumbers;
            public int DecimalPlaces;
            public ValueDisplayMode DisplayMode;
        }

        public TextMeshProUGUI CurrentValueLabel;
        public SliderSettings Settings => _settings;
        protected SliderSettings _settings;


        public static class SliderStyles
        {
            public static string Default => "VowganUI/Elements/Slider";
        }

        public Slider SliderComponent;
        public static PanelSlider CreateNew(Component parent)
        {
            PanelSlider element = CreateNew<PanelSlider>(SliderStyles.Default, parent);
            return element;
        }

        public override void OnCreateEvent()
        {
            base.OnCreateEvent();
            ApplySliderSettings();
            SliderComponent.onValueChanged.AddListener(OnSliderValueChanged);
        }

        private void OnSliderValueChanged(float value)
        {
            SetFloatValue(value);
            ApplyVisual();
        }

        public void SetSliderSettings(SliderSettings settings)
        {
            _settings = settings;
            ApplySliderSettings();
        }

        protected virtual void ApplySliderSettings()
        {
            SliderComponent.minValue = Settings.SliderMin;
            SliderComponent.maxValue = Settings.SliderMax;
            SliderComponent.wholeNumbers = Settings.UseWholeNumbers;
        }

        public override void SetFloatValueWithoutNotify(float value)
        {
            base.SetFloatValueWithoutNotify(value);
            SliderComponent.SetValueWithoutNotify(value);
            ApplyVisual();
        }

        public override void OnValueChanged()
        {
            base.OnValueChanged();
            if (!Mathf.Approximately(SliderComponent.value, FloatValue))
            {
                SliderComponent.SetValueWithoutNotify(FloatValue);
                ApplyVisual();
            }
        }

        protected void ApplyVisual()
        {
            TitleLabel.text = Settings.Title;


            switch (Settings.DisplayMode)
            {
                case ValueDisplayMode.Percentage:
                    float range = SliderComponent.maxValue - SliderComponent.minValue;
                    float normalized = (range > 0f) ? (FloatValue - SliderComponent.minValue) / range : 0f;
                    CurrentValueLabel.text = $"{Mathf.RoundToInt(normalized * 100f)}%";
                    break;

                case ValueDisplayMode.Raw:
                    CurrentValueLabel.text = FloatValue.ToString("0." + new string('#', Settings.DecimalPlaces));
                    break;

                case ValueDisplayMode.Meters:
                    CurrentValueLabel.text = FloatValue.ToString("0." + new string('#', Settings.DecimalPlaces)) + " m";
                    break;
            }
        }

    }
}
