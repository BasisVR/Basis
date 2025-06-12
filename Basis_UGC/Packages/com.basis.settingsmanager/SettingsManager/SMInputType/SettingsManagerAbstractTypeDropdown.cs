using UnityEngine;

namespace BattlePhaze.SettingsManager.Types
{
    /// <summary>
    /// Settings Manager types Abstract Module
    /// </summary>
    abstract public class SettingsManagerAbstractTypeDropdown : SettingsManagerAbstractType
    {
        abstract public bool DropDownListener(SettingsManager Manager, int OptionIndex,bool IsDynamicHandler);
        abstract public void DropDownEnabledState(SettingsManager Manager, int optionIndex, bool EnabledState);
        abstract public void DropDownSetOptionsValue(SettingsManager Manager, int OptionIndex, int OptionsValueIndex, bool Silent);
        abstract public bool DropDownGetOptionsGameobject(SettingsManager Manager, int OptionIndex,  out GameObject GameObject);
        abstract public void DropDownClearDropDown(SettingsManager Manager, int OptionIndex);
        abstract public void DropDownAddOption(SettingsManager Manager, int OptionIndex, string Option);
    }
}