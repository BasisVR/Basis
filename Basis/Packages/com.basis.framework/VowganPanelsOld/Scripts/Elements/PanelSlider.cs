using System.Globalization;
using Basis.BasisUI;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Basis.VowganUIOld
{
    public class PanelSlider : AddressableUIInstanceBase
    {

        public static string ReferencePath => "BasisUI/CanvasElement/Slider";


        [Header("References")]
        public TextMeshProUGUI TitleLabel;
        public Slider SliderComponent;
        public TextMeshProUGUI ValueLabel;
        public TextMeshProUGUI MinLabel;
        public TextMeshProUGUI MaxLabel;
        public UnityEvent<float> OnValueChanged;

        protected string _valueFormatString = "{0:F0} %";
        public string ValueFormatString
        {
            get => _valueFormatString;
            set
            {
                _valueFormatString = value;
                RefreshValueLabel();
            }
        }


        [SerializeField] protected Vector2 _range = new Vector2(0, 1);
        public Vector2 Range
        {
            get => _range;
            set
            {
                _range = value;
                SliderComponent.minValue = value.x;
                SliderComponent.maxValue = value.y;
                RefreshMinMaxLabels();
            }
        }

        public float Value
        {
            get => SliderComponent.value;
            set => SliderComponent.value = value;
        }

        public bool UseWholeNumbers
        {
            get => SliderComponent.wholeNumbers;
            set => SliderComponent.wholeNumbers = value;
        }


        public void SetValueWithoutNotify(float value)
        {
            SliderComponent.SetValueWithoutNotify(value);
            RefreshValueLabel();
        }

        public static PanelSlider CreateNew(Component parent)
        {
            PanelSlider slider = CreateNew<PanelSlider>(ReferencePath, parent);
            return slider;
        }

        public static PanelSlider CreateNew(Component parent, string label)
        {
            PanelSlider slider = CreateNew(parent);
            slider.TitleLabel.text = label;
            return slider;
        }

        public static PanelSlider CreateNew(Component parent, string label, Vector2 range)
        {
            PanelSlider slider = CreateNew(parent);
            slider.TitleLabel.text = label;
            slider.Range = range;
            return slider;
        }

        public static PanelSlider CreateNew(Component parent, Vector2 icon)
        {
            PanelSlider slider = CreateNew(parent);
            slider.Range = icon;
            return slider;
        }



        public override void OnCreateEvent()
        {
            base.OnCreateEvent();
            RefreshValueLabel();
            RefreshMinMaxLabels();
            SliderComponent.onValueChanged.AddListener(OnSliderValueChanged);
        }

        protected virtual void OnSliderValueChanged(float value)
        {
            RefreshValueLabel();
            OnValueChanged?.Invoke(value);
        }

        protected virtual void RefreshValueLabel()
        {
            ValueLabel.text = string.Format(_valueFormatString, Value);
        }

        protected virtual void RefreshMinMaxLabels()
        {
            MinLabel.text = _range.x.ToString(CultureInfo.InvariantCulture);
            MaxLabel.text = _range.y.ToString(CultureInfo.InvariantCulture);
        }

    }
}
