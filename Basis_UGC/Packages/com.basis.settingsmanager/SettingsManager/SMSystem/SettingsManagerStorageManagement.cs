using BattlePhaze.SettingsManager.DebugSystem;
using System.Linq;
using UnityEngine;
namespace BattlePhaze.SettingsManager
{
    public static class SettingsManagerStorageManagement
    {
        public static void Read(SettingsManager Manager, int OptionIndex, bool ReadFromFile)
        {
            if (ReadFromFile)
            {
                Manager.SaveSystem.Load();
                Manager.Options[OptionIndex].SelectedValue = Manager.SaveSystem.Get(Manager.Options[OptionIndex].Name, string.Empty);
            }
            SettingsManagerDebug.Log("Loaded From File " + Manager.Options[OptionIndex].Name + " Loaded Value " + Manager.Options[OptionIndex].SelectedValue);
            if (string.IsNullOrEmpty(Manager.Options[OptionIndex].SelectedValue))
            {
                SetDefault(Manager, OptionIndex, false);
            }
        }
        public static void SetDefault(SettingsManager manager, int optionIndex, bool reevaluate)
        {
            string selectedValueDefault = null;

            foreach (var platformDefault in manager.Options[optionIndex].PlatFormDefaultState)
            {
                if (platformDefault.Platform == Application.platform && PlatformVendor(manager, platformDefault))
                {
                    selectedValueDefault = platformDefault.SetString;
                    SettingsManagerDebug.Log($"Apply Default Value {manager.Options[optionIndex].Name}: {selectedValueDefault}");
                    break;
                }
            }

            if (manager.Options[optionIndex].Type == SettingsManagerEnums.IsType.Dynamic)
            {
                manager.Options[optionIndex].SelectedValue = null;
                selectedValueDefault = null;
                SettingsManagerDebug.Log("Dynamic Option Handling Default");
            }
            else if (string.IsNullOrEmpty(selectedValueDefault))
            {
                selectedValueDefault = manager.Options[optionIndex].ValueDefault;
            }

            manager.Options[optionIndex].SelectedValueDefault = selectedValueDefault;
            manager.Options[optionIndex].SelectedValue = selectedValueDefault;

            SettingsManagerDebug.Log($"Setting Default for Option {manager.Options[optionIndex].Name}: {selectedValueDefault}");

            if (reevaluate)
            {
                manager.Initalize(true);
            }
        }
        public static bool PlatformVendor(SettingsManager manager, SMPlatFormDefault platformDefault)
        {
            if (platformDefault.GraphicsVendor == SettingsManagerEnums.GraphicsVendor.ALL)
            {
                return true;
            }

            string graphicsDeviceVendor = SystemInfo.graphicsDeviceVendor.ToString(manager.ManagerSettings.CInfo);
            if (platformDefault.GraphicsVendor.ToString().Contains(graphicsDeviceVendor))
            {
                return true;
            }

            return false;
        }
        public static void Save(SettingsManager manager)
        {
            manager.OnSettingsSaving?.Invoke();
            SettingsManager.OnSettingsSavingStatic?.Invoke();

            foreach (var option in manager.Options)
            {
                switch (option.Type)
                {
                    case SettingsManagerEnums.IsType.Disabled:
                        break;
                    case SettingsManagerEnums.IsType.DropDown:
                    case SettingsManagerEnums.IsType.Dynamic:
                        string valuesDescription = string.Join(" ", option.SelectableValueList.Select(v => v.RealValue));
                        string optionDescription = $"{option.ValueDescriptor} [{valuesDescription}]";
                        manager.SaveSystem.Set(option.Name, option.SelectedValue, optionDescription);
                        break;
                    case SettingsManagerEnums.IsType.Slider:
                        string sliderDescription = $"[Max {option.SliderMaxValue} Min {option.SliderMinValue}]";
                        manager.SaveSystem.Set(option.Name, option.SelectedValue, sliderDescription);
                        break;
                    case SettingsManagerEnums.IsType.Toggle:
                        string toggleDescription = $"{option.ValueDescriptor}[true false]";
                        manager.SaveSystem.Set(option.Name, option.SelectedValue, toggleDescription);
                        break;
                }

                // SettingsManagerDebug.Log("Option " + option.Name + " Set To " + option.SelectedValue);
            }

            manager.SaveSystem.Save();
            manager.OnSettingsSaved?.Invoke();
            SettingsManager.OnSettingsSavedStatic?.Invoke();
        }
    }
}
