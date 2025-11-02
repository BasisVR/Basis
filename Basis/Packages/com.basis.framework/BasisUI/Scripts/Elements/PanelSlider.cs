using System;
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

    public class PanelSlider : PanelBindableElement<float>
    {

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
        public OnEndDragListener OnEndDragListener;
        public SliderSettings Settings => _settings;
        protected SliderSettings _settings;


        public static class SliderStyles
        {
            public static string Default => "Packages/com.basis.framework/BasisUI/Prefabs/Elements/Panel Slider.prefab";
        }

        public Slider SliderComponent;


        public static PanelSlider CreateNew(Component parent)
            => CreateNew<PanelSlider>(SliderStyles.Default, parent);

        public static PanelSlider CreateNew(string style, Component parent)
            => CreateNew<PanelSlider>(style, parent);


        public override void OnCreateEvent()
        {
            base.OnCreateEvent();
            ApplySliderSettings();
            OnEndDragListener.OnDragComplete += OnSliderDragComplete;
            // SliderComponent.onValueChanged.AddListener(OnSliderValueChanged);
        }

        private void OnSliderDragComplete()
        {
            SetValue(SliderComponent.value);
            ApplyVisual();
        }

        private void OnSliderValueChanged(float value)
        {
            SetValue(value);
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

        public override void SetValueWithoutNotify(float value)
        {
            base.SetValueWithoutNotify(value);
            SliderComponent.SetValueWithoutNotify(value);
            ApplyVisual();
        }

        public override void OnValueChanged()
        {
            base.OnValueChanged();
            if (!Mathf.Approximately(SliderComponent.value, RawValue))
            {
                SliderComponent.SetValueWithoutNotify(RawValue);
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
                    float normalized = (range > 0f) ? (RawValue - SliderComponent.minValue) / range : 0f;
                    CurrentValueLabel.text = $"{Mathf.RoundToInt(normalized * 100f)}%";
                    break;

                case ValueDisplayMode.Raw:
                    CurrentValueLabel.text = RawValue.ToString("0." + new string('#', Settings.DecimalPlaces));
                    break;

                case ValueDisplayMode.Meters:
                    CurrentValueLabel.text = RawValue.ToString("0." + new string('#', Settings.DecimalPlaces)) + " m";
                    break;
            }
        }
    }
}
