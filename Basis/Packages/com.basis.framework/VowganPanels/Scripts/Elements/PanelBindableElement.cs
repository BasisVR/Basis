using System;
using UnityEngine;

namespace Basis.VowganUI
{
    public interface IPanelBindableFloat
    {
        public Action<float> OnValueChangedCallback { get; set; }
        public float Value { get; }
        public string BasisSettingKey { get; }
        public void SetInitialValue(float initialValue);
        public void SetValue(float value);
        public void SetValueWithoutNotify(float value);
        public void OnValueChanged();
        public void BindToSetting(string key);
    }
    public interface IPanelBindableInt
    {
        public Action<int> OnValueChangedCallback { get; set; }
        public int Value { get; }
        public string BasisSettingKey { get; }
        public void SetInitialValue(int initialValue);
        public void SetValue(int value);
        public void SetValueWithoutNotify(int value);
        public void OnValueChanged();
        public void BindToSetting(string key);
    }
}
