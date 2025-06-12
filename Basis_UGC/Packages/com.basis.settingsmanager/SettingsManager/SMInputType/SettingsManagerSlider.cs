using BattlePhaze.SettingsManager.DebugSystem;
using UnityEngine;
using System.Globalization;

namespace BattlePhaze.SettingsManager
{
    public static class SettingsManagerSlider
    {
        public static void SliderExecution(int optionIndex, SettingsManager manager, float currentValue)
        {
            if (!IsSliderOption(manager, optionIndex)) return;

            if (IsIntValue(manager, optionIndex))
            {
                currentValue = (int)currentValue;
            }

            manager.Options[optionIndex].SelectedValue = currentValue.ToString(manager.ManagerSettings.CInfo);

            if (TryGetSliderMaxValue(manager, optionIndex, out float sliderMaxValue))
            {
                bool isPercentage = manager.Options[optionIndex].ReturnedValueTextType == SettingsManagerEnums.TextReturn.SliderPercentage;
                SetTextDescription(manager, optionIndex, currentValue, sliderMaxValue, manager.Options[optionIndex], isPercentage);
                SettingsManagerStorageManagement.Save(manager);
                manager.SendOption(manager.Options[optionIndex]);
            }
            else
            {
                SettingsManagerDebug.LogError("Could not parse Slider Max Value. SliderExecution Failed!");
            }
        }

        public static void SetSliderDescription(int optionIndex, SettingsManager manager, float currentValue)
        {
            if (!IsSliderOption(manager, optionIndex))
            {
                return;
            }

            if (IsIntValue(manager, optionIndex))
            {
                currentValue = (int)currentValue;
            }

            if (TryGetSliderMaxValue(manager, optionIndex, out float sliderMaxValue))
            {
                bool isPercentage = manager.Options[optionIndex].ReturnedValueTextType == SettingsManagerEnums.TextReturn.SliderPercentage;
                SetTextDescription(manager, optionIndex, currentValue, sliderMaxValue, manager.Options[optionIndex], isPercentage);
            }
            else
            {
                SettingsManagerDebug.LogError("Could not parse Slider Max Value. SetSliderDescription Failed!");
            }
        }

        private static void SetTextDescription(SettingsManager manager, int optionIndex, float sliderValue, float sliderMaxValue, SettingsMenuInput option, bool isPercentage)
        {
            if (option.TextDescription != null)
            {
                option.ValueDescriptor = Information.SettingsManagerInformationConverter.PostProcessValue(option.Round, isPercentage, option.RoundTo, sliderValue, sliderMaxValue, option.MaxPercentage, option.MinPercentage);

                SettingsManagerDescriptionSystem.TxtDescriptionSetText(manager, optionIndex);
            }
        }

        public static void SliderOptionReadValue(SettingsManager manager, int optionIndex, out bool hasValue, out float value)
        {
            ReadValue(manager, optionIndex, out hasValue, out value);
        }

        public static void SliderOptionReadValue(SettingsManager manager, SettingsMenuInput option, out bool hasValue, out float value)
        {
            for (int Index = 0; Index < manager.Options.Count; Index++)
            {
                if (manager.Options[Index] == option)
                {
                    ReadValue(manager, Index, out hasValue, out value);
                    return;
                }
            }
            hasValue = false;
            value = 0;
        }

        private static void ReadValue(SettingsManager manager, int optionIndex, out bool hasValue, out float value)
        {
            hasValue = false;
            value = 0;
            foreach (Types.SettingsManagerAbstractTypeSlider slider in manager.SettingsManagerAbstractTypeSlider)
            {
                if (slider != null)
                {
                    slider.SliderOptionReadValue(manager, optionIndex, out hasValue, out value);
                    if (hasValue) return;
                }
            }
        }

        public static void SliderEnabledState(SettingsManager manager, int optionIndex, bool outcome)
        {
            foreach (Types.SettingsManagerAbstractTypeSlider slider in manager.SettingsManagerAbstractTypeSlider)
            {
                slider?.SliderEnabledState(manager, optionIndex, outcome);
            }
        }

        public static void SliderGetOptionsGameobject(SettingsManager manager, int optionIndex, out bool hasValue, out GameObject value)
        {
            hasValue = false;
            value = null;
            foreach (Types.SettingsManagerAbstractTypeSlider slider in manager.SettingsManagerAbstractTypeSlider)
            {
                if (slider != null)
                {
                    slider.SliderGetOptionsGameobject(manager, optionIndex, out hasValue, out value);
                    if (hasValue) return;
                }
            }
        }

        public static void InitializeSlider(SettingsManager manager, int optionIndex)
        {
            var option = manager.Options[optionIndex];
            float sliderValue = ParseFloat(option.SelectedValue, manager.ManagerSettings.CInfo);
            float sliderMinValue = ParseFloat(option.SliderMinValue, manager.ManagerSettings.CInfo);
            float sliderMaxValue = ParseFloat(option.SliderMaxValue, manager.ManagerSettings.CInfo);

            SetTextDescription(manager, optionIndex, sliderValue, sliderMaxValue, option, option.ReturnedValueTextType == SettingsManagerEnums.TextReturn.SliderPercentage);

            foreach (Types.SettingsManagerAbstractTypeSlider slider in manager.SettingsManagerAbstractTypeSlider)
            {
                if (slider != null)
                {
                    slider.SliderOptionSetValue(manager, optionIndex, sliderValue, sliderMinValue, sliderMaxValue);
                    slider.SliderOnValueChanged(manager, optionIndex, out bool requestSuccessful);
                    if (requestSuccessful) return;
                }
            }
        }

        private static bool IsSliderOption(SettingsManager manager, int optionIndex)
        {
            return manager.Options[optionIndex].Type == SettingsManagerEnums.IsType.Slider;
        }

        private static bool IsIntValue(SettingsManager manager, int optionIndex)
        {
            return manager.Options[optionIndex].ParseController == SettingsManagerEnums.ItemParse.intValue;
        }

        private static bool TryGetSliderMaxValue(SettingsManager manager, int optionIndex, out float sliderMaxValue)
        {
            return float.TryParse(manager.Options[optionIndex].SliderMaxValue, NumberStyles.Any, manager.ManagerSettings.CInfo, out sliderMaxValue);
        }

        private static float ParseFloat(string value, CultureInfo cultureInfo)
        {
            return float.TryParse(value, NumberStyles.Any, cultureInfo, out float result) ? result : 0;
        }
    }
}