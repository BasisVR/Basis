namespace BattlePhaze.SettingsManager
{
    using UnityEngine;
    /// <summary>
    /// Toggle System for Graphics Menu
    /// </summary>
    public static class SettingsManagerToggle
    {
        /// <summary>
        /// Exectutes a toggle
        /// </summary>
        /// <param name="OptionIndex">Option Index</param>
        /// <param name="Manager">Settings Manager</param>
        public static void ToggleExecution(int OptionIndex, SettingsManager Manager, bool NewValue)
        {
            bool On = true;
            bool Off = false;
            Manager.Options[OptionIndex].SelectedValue = NewValue.ToString(Manager.ManagerSettings.CInfo);
            if (Manager.Options[OptionIndex].SelectableValueList.Count == 0)
            {
               SMSelectableValues.AddSelection(Manager.Options[OptionIndex].SelectableValueList, On.ToString(Manager.ManagerSettings.CInfo), On.ToString(Manager.ManagerSettings.CInfo));
               SMSelectableValues.AddSelection(Manager.Options[OptionIndex].SelectableValueList, Off.ToString(Manager.ManagerSettings.CInfo), Off.ToString(Manager.ManagerSettings.CInfo));
            }
            SettingsManagerDescriptionSystem.TxtDescriptionSetText(Manager, OptionIndex);
            SettingsManagerStorageManagement.Save(Manager);
            Manager.SendOption(Manager.Options[OptionIndex]);
        }
        /// <summary>
        /// Toggle On value Get Options Gameobject
        /// </summary>
        public static void ToggleOnValueGetOptionsGameobject(int OptionIndex, SettingsManager Manager, out bool hasValue, out GameObject Value)
        {
            hasValue = false;
            Value = null;
            for (int ToggleSavedIndex = 0; ToggleSavedIndex < Manager.SettingsManagerAbstractTypeToggle.Count; ToggleSavedIndex++)
            {
                if (Manager.SettingsManagerAbstractTypeToggle[ToggleSavedIndex] != null)
                {
                    Manager.SettingsManagerAbstractTypeToggle[ToggleSavedIndex].ToggleOnValueGetOptionsGameobject(Manager, OptionIndex, out hasValue, out Value);
                    if (hasValue)
                    {
                        return;
                    }
                }
            }
        }
        /// <summary>
        /// Toggle On value Get Options Gameobject
        /// </summary>
        public static void ToggleSetActive(int OptionIndex, SettingsManager Manager, bool state)
        {
            for (int ToggleSavedIndex = 0; ToggleSavedIndex < Manager.SettingsManagerAbstractTypeToggle.Count; ToggleSavedIndex++)
            {
                if (Manager.SettingsManagerAbstractTypeToggle[ToggleSavedIndex] != null)
                {
                    Manager.SettingsManagerAbstractTypeToggle[ToggleSavedIndex].ToggleSetActive(Manager, OptionIndex, state);
                }
            }
        }
        /// <summary>
        /// Toggle
        /// </summary>
        /// <param name="OptionIndex">Option Index</param>
        /// <param name="Manager">Settings Manager</param>
        public static void ToggleIson(int OptionIndex, SettingsManager Manager, out bool hasValue, out int Value)
        {
            hasValue = false;
            Value = 0;
            for (int textsIndex = 0; textsIndex < Manager.SettingsManagerAbstractTypeToggle.Count; textsIndex++)
            {
                if (Manager.SettingsManagerAbstractTypeToggle[textsIndex] != null)
                {
                    Manager.SettingsManagerAbstractTypeToggle[textsIndex].ToggleIson(Manager, OptionIndex, out hasValue, out Value);
                    if (hasValue)
                    {
                        return;
                    }
                }
            }
        }
        public static void InitializeToggle(SettingsManager Manager, int OptionIndex)
        {
            SettingsMenuInput Option = Manager.Options[OptionIndex];
            bool.TryParse(Option.SelectedValue, out bool ToggleValue);
            for (int AbstractModulesListIndex = 0; AbstractModulesListIndex < Manager.SettingsManagerAbstractTypeToggle.Count; AbstractModulesListIndex++)
            {
                if (Manager.SettingsManagerAbstractTypeToggle[AbstractModulesListIndex] != null)
                {
                    Manager.SettingsManagerAbstractTypeToggle[AbstractModulesListIndex].ToggleSetState(Manager, OptionIndex, ToggleValue);
                    Manager.SettingsManagerAbstractTypeToggle[AbstractModulesListIndex].ToggleOnValueChanged(Manager, OptionIndex, out bool RequestSuccessful);
                    if (RequestSuccessful)
                    {
                        return;
                    }
                }
            }
        }
    }
}