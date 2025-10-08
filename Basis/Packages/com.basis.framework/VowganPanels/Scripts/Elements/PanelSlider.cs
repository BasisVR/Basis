using System;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.VowganUI
{
    public class PanelSlider : PanelElement //, IPanelBindableFloat
    {
        public static class Styles
        {
            public static string Default => "VowganUI/Elements/Slider";
        }

        public Slider SliderComponent;

        public string BasisSettingBinding;

        public static PanelSlider CreateNew(Component parent)
        {
            PanelSlider element = CreateNew<PanelSlider>(Styles.Default, parent);
            return element;
        }

        public override void OnCreateEvent()
        {
            base.OnCreateEvent();
            SliderComponent.onValueChanged.AddListener(OnSliderValueChanged);
        }

        private void OnSliderValueChanged(float value)
        {
            
        }
    }
}
