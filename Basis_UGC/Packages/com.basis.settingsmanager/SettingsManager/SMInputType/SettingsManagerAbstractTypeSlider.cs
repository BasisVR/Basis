using UnityEngine;

namespace BattlePhaze.SettingsManager.Types
{
    abstract public class SettingsManagerAbstractTypeSlider : SettingsManagerAbstractType
    {
        abstract public void SliderOnValueChanged(SettingsManager Manager, int OptionIndex, out bool HasValue);
        abstract public void SliderOptionSetValue(SettingsManager Manager, int OptionIndex, float Value, float Min, float Max);
        abstract public void SliderOptionReadValue(SettingsManager Manager, int OptionIndex, out bool HasValue, out float Value);
        abstract public void SliderOptionReadMinValue(SettingsManager Manager, int OptionIndex, out bool HasValue, out float Value);
        abstract public void SliderOptionReadMaxValue(SettingsManager Manager, int OptionIndex, out bool HasValue, out float Value);
        abstract public void SliderGetOptionsGameobject(SettingsManager Manager, int OptionIndex, out bool HasValue, out GameObject GameObject);
        abstract public void SliderEnabledState(SettingsManager Manager, int OptionIndex, bool state);
    }
}