using System;
using UnityEngine;
namespace BattlePhaze.SettingsManager
{
    /// <summary>
    /// Settings Manager DropDown
    /// </summary>
    public static class SettingsManagerDropDown
    {
        public static void DropDownExecution(int OptionIndex, SettingsManager Manager, int CurrentIndex)
        {
            Manager.Options[OptionIndex].SelectedValue = Manager.Options[OptionIndex].SelectableValueList[CurrentIndex].RealValue;
            SettingsManagerDescriptionSystem.TxtDescriptionSetText(Manager, OptionIndex);
            SettingsManager.Instance.SendOption(Manager.Options[OptionIndex]);
            if (Manager.Options[OptionIndex].MasterQualityState == SettingsManagerEnums.MasterQualityState.MasterQualityOption)
            {
                for (int OptionIndexLoop = 0; OptionIndexLoop < Manager.Options.Count; OptionIndexLoop++)
                {
                    if (Manager.Options[OptionIndexLoop].MasterQualityState == SettingsManagerEnums.MasterQualityState.WillEffectThis && Manager.Options[OptionIndexLoop].Type == SettingsManagerEnums.IsType.DropDown)
                    {
                        try
                        {
                            Manager.Options[OptionIndexLoop].SelectedValue = Manager.Options[OptionIndexLoop].SelectableValueList[CurrentIndex].RealValue;
                            SetOptionsValue(Manager, OptionIndexLoop, CurrentIndex, true);
                            SettingsManager.Instance.SendOption(Manager.Options[OptionIndexLoop]);
                        }
                        catch (Exception E)
                        {
                            DebugSystem.SettingsManagerDebug.Log(E.Message + " : " + Manager.Options[OptionIndexLoop].Name);
                        }
                    }
                }
            }
            SettingsManagerStorageManagement.Save(Manager);
        }
        public static void DropDownEnabledState(int OptionIndex, bool enabled)
        {
            SettingsManager Manager = SettingsManager.Instance;
            for (int textsIndex = 0; textsIndex < Manager.SettingsManagerAbstractTypeDropdown.Count; textsIndex++)
            {
                if (Manager.SettingsManagerAbstractTypeDropdown[textsIndex] != null)
                {
                    Manager.SettingsManagerAbstractTypeDropdown[textsIndex].DropDownEnabledState(Manager, OptionIndex, enabled);
                }
            }
        }
        public static void SetOptionsValue(SettingsManager Manager, int OptionIndex, int OptionsIndexValue, bool Silent)
        {
            for (int textsIndex = 0; textsIndex < Manager.SettingsManagerAbstractTypeDropdown.Count; textsIndex++)
            {
                if (Manager.SettingsManagerAbstractTypeDropdown[textsIndex] != null)
                {
                    Manager.SettingsManagerAbstractTypeDropdown[textsIndex].DropDownSetOptionsValue(Manager, OptionIndex, OptionsIndexValue, Silent);
                }
            }
        }
        public static bool GetOptionsGameobject(SettingsManager Manager, int OptionIndex, out GameObject Value)
        {
            Value = null;
            for (int textsIndex = 0; textsIndex < Manager.SettingsManagerAbstractTypeDropdown.Count; textsIndex++)
            {
                if (Manager.SettingsManagerAbstractTypeDropdown[textsIndex] != null)
                {
                    if (Manager.SettingsManagerAbstractTypeDropdown[textsIndex].DropDownGetOptionsGameobject(Manager, OptionIndex, out Value))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
        public static void Clear(SettingsManager Manager, int OptionIndex)
        {
            for (int textsIndex = 0; textsIndex < Manager.SettingsManagerAbstractTypeDropdown.Count; textsIndex++)
            {
                if (Manager.SettingsManagerAbstractTypeDropdown[textsIndex] != null)
                {
                    Manager.SettingsManagerAbstractTypeDropdown[textsIndex].DropDownClearDropDown(Manager, OptionIndex);
                }
            }
        }
        public static void AddDropDownOption(SettingsManager Manager, int OptionIndex, string Option)
        {
            for (int textsIndex = 0; textsIndex < Manager.SettingsManagerAbstractTypeDropdown.Count; textsIndex++)
            {
                if (Manager.SettingsManagerAbstractTypeDropdown[textsIndex] != null)
                {
                    Manager.SettingsManagerAbstractTypeDropdown[textsIndex].DropDownAddOption(Manager, OptionIndex, Option);
                }
            }
        }
        public static void InitializeDropDown(SettingsManager Manager, int OptionIndex)
        {
            for (int AbstractModulesListIndex = 0; AbstractModulesListIndex < Manager.SettingsManagerAbstractTypeDropdown.Count; AbstractModulesListIndex++)
            {
                if (Manager.SettingsManagerAbstractTypeDropdown[AbstractModulesListIndex] != null)
                {
                    switch (Manager.Options[OptionIndex].Type)
                    {
                        case SettingsManagerEnums.IsType.DropDown:
                            if (Manager.SettingsManagerAbstractTypeDropdown[AbstractModulesListIndex].DropDownListener(Manager, OptionIndex, false))
                            {
                                return;
                            }

                            break;
                        case SettingsManagerEnums.IsType.Dynamic:
                            if (Manager.SettingsManagerAbstractTypeDropdown[AbstractModulesListIndex].DropDownListener(Manager, OptionIndex, true))
                            {
                                return;
                            }
                            break;
                    }
                }
            }
        }
    }
}