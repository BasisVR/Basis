using BattlePhaze.SettingsManager.Types;
using System.Linq;
using UnityEngine;
namespace BattlePhaze.SettingsManager
{
    public static class SettingsManagerModuleSystem
    {
        public static void AddSettingsModule(SettingsManager Manager)
        {
            Manager.SettingsManagerOptions.Add(null);
        }
        public static void RemoveModule(int OptionIndex, SettingsManager Manager)
        {
            Manager.SettingsManagerOptions.RemoveAt(OptionIndex);
        }
        public static void AutoAssignModules(SettingsManager Manager)
        {
            Manager.SettingsManagerOptions.Clear();
            SettingsManagerOption[] smo = GameObject.FindObjectsByType<SettingsManagerOption>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int SettingsManagerOptionIndex = 0; SettingsManagerOptionIndex < smo.Length; SettingsManagerOptionIndex++)
            {
                if (!Manager.SettingsManagerOptions.Contains(smo[SettingsManagerOptionIndex]))
                {
                    Manager.SettingsManagerOptions.Add(smo[SettingsManagerOptionIndex]);
                }
            }
        }
        public static void AutoAssignInputTypes(SettingsManager Manager)
        {
            Manager.SettingsManagerAbstractTypeToggle.Clear();
            Manager.SettingsManagerAbstractTypeDropdown.Clear();
            Manager.SettingsManagerAbstractTypeSlider.Clear();
            Manager.SettingsManagerAbstractTypeManagement.Clear();
            Manager.SettingsManagerAbstractTypeText.Clear();
            SettingsManagerAbstractType[] AbstractModule = GameObject.FindObjectsByType<SettingsManagerAbstractType>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int SettingsManagerOptionIndex = 0; SettingsManagerOptionIndex < AbstractModule.Length; SettingsManagerOptionIndex++)
            {
                switch (AbstractModule[SettingsManagerOptionIndex].GetActiveType())
                {
                    case SettingsManagerEnums.IsTypeInterpreter.DropDown:
                        if (!Manager.SettingsManagerAbstractTypeDropdown.Contains(AbstractModule[SettingsManagerOptionIndex]))
                        {
                            Manager.SettingsManagerAbstractTypeDropdown.Add((SettingsManagerAbstractTypeDropdown)AbstractModule[SettingsManagerOptionIndex]);
                        }
                        break;
                    case SettingsManagerEnums.IsTypeInterpreter.Slider:
                        if (!Manager.SettingsManagerAbstractTypeSlider.Contains(AbstractModule[SettingsManagerOptionIndex]))
                        {
                            Manager.SettingsManagerAbstractTypeSlider.Add((SettingsManagerAbstractTypeSlider)AbstractModule[SettingsManagerOptionIndex]);
                        }
                        break;
                    case SettingsManagerEnums.IsTypeInterpreter.Toggle:
                        if (!Manager.SettingsManagerAbstractTypeToggle.Contains(AbstractModule[SettingsManagerOptionIndex]))
                        {
                            Manager.SettingsManagerAbstractTypeToggle.Add((SettingsManagerAbstractTypeToggle)AbstractModule[SettingsManagerOptionIndex]);
                        }
                        break;
                    case SettingsManagerEnums.IsTypeInterpreter.Management:
                        if (!Manager.SettingsManagerAbstractTypeManagement.Contains(AbstractModule[SettingsManagerOptionIndex]))
                        {
                            Manager.SettingsManagerAbstractTypeManagement.Add((SettingsManagerAbstractTypeManagement)AbstractModule[SettingsManagerOptionIndex]);
                        }
                        break;
                    case SettingsManagerEnums.IsTypeInterpreter.Text:
                        if (!Manager.SettingsManagerAbstractTypeText.Contains(AbstractModule[SettingsManagerOptionIndex]))
                        {
                            Manager.SettingsManagerAbstractTypeText.Add((SettingsManagerAbstractTypeText)AbstractModule[SettingsManagerOptionIndex]);
                        }
                        break;
                }
            }
        }
        public static void SortModules(SettingsManager Manager)
        {
            Manager.Options = Manager.Options.OrderBy(go => go.Type).ToList();
        }
        public static void AddCompareOption(SettingsMenuInput optionSettings, SettingsManager manager)
        {
            if (string.IsNullOrEmpty(optionSettings.Name))
            {
                return;
            }

            foreach (var option in manager.Options)
            {
                if (option.Name == optionSettings.Name)
                {
                    if (option.SelectableValueList.Count == 0)
                    {
                        option.SelectableValueList = optionSettings.SelectableValueList;
                    }
                    return;
                }
            }

            manager.Options.Add(optionSettings);
        }
    }
}
