using UnityEngine;

namespace BattlePhaze.SettingsManager
{
    public static class SettingsManagerDynamics
    {
        public static void DynamicExecution(int OptionIndex, SettingsManager Manager, int CurrentIndex)
        {
            if (Manager == null)
            {
                Debug.LogError("SettingsManager instance cannot be null.");
                return;
            }

            if (Manager.Options == null)
            {
                Debug.LogError("Options list cannot be null.");
                return;
            }

            if (OptionIndex < 0 || OptionIndex >= Manager.Options.Count)
            {
                Debug.LogError($"OptionIndex {OptionIndex} is out of range for ");
                return;
            }

            var option = Manager.Options[OptionIndex];
            if (option == null)
            {
                Debug.LogError($"Option at index {OptionIndex} is null.");
                return;
            }

            if (option.SelectableValueList == null)
            {
                Debug.LogError($"SelectableValueList for option '{option.Name}' is null.");
                return;
            }

            if (CurrentIndex < 0 || CurrentIndex >= option.SelectableValueList.Count)
            {
                Debug.LogError($"CurrentIndex {CurrentIndex} is out of range for option '{option.Name}'.");
                return;
            }

            option.SelectedValue = option.SelectableValueList[CurrentIndex]?.RealValue;

            SettingsManagerDescriptionSystem.TxtDescriptionSetText(Manager, OptionIndex);
            SettingsManagerStorageManagement.Save(Manager);
            Manager.SendOption(option);
        }
    }
}
