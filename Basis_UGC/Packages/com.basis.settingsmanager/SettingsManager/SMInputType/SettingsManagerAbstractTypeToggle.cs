using UnityEngine;
namespace BattlePhaze.SettingsManager.Types
{
    abstract public class SettingsManagerAbstractTypeToggle : SettingsManagerAbstractType
    {
        abstract public void ToggleOnValueChanged(SettingsManager Manager, int OptionIndex, out bool HasValue);
        abstract public void ToggleIson(SettingsManager Manager, int OptionIndex, out bool HasValue, out int Value);
        abstract public void ToggleSetState(SettingsManager Manager, int OptionIndex, bool State);
        abstract public void ToggleOnValueGetOptionsGameobject(SettingsManager Manager, int OptionIndex, out bool HasValue, out GameObject GameObject);
        abstract public void ToggleSetActive(SettingsManager Manager, int OptionIndex, bool State);
    }
}