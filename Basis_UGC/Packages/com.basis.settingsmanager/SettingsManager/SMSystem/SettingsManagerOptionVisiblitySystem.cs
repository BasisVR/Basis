namespace BattlePhaze.SettingsManager
{
    public static class SettingsManagerOptionVisiblitySystem
    {
        /// <summary>
        /// Set option to disabled or enabled.
        /// </summary>
        /// <param name="OptionIndex"></param>
        /// <param name="EnabledState"></param>
        public static void SetOptionVisible(int OptionIndex, bool EnabledState, SettingsManager Manager)
        {
            SettingsManagerToggle.ToggleSetActive(OptionIndex, Manager, EnabledState);
            SettingsManagerDropDown.DropDownEnabledState(OptionIndex, EnabledState);
            SettingsManagerSlider.SliderEnabledState(Manager, OptionIndex, EnabledState);
            if (Manager.Options[OptionIndex].TextDescription != null)
            {
                DescriptonEnabledState(Manager, OptionIndex, EnabledState, out bool HasValue);
            }
        }
        public static void DescriptonEnabledState(SettingsManager Manager, int OptionIndex, bool EnabledState, out bool HasValue)
        {
            HasValue = false;
            for (int AttachedManagerIndex = 0; AttachedManagerIndex < Manager.SettingsManagerAbstractTypeManagement.Count; AttachedManagerIndex++)
            {
                if (Manager.SettingsManagerAbstractTypeManagement[AttachedManagerIndex] != null)
                {
                    Manager.SettingsManagerAbstractTypeManagement[AttachedManagerIndex].TextDescriptionEnabledState(Manager, Manager.Options[OptionIndex].TextDescription, EnabledState, out HasValue);
                    if (HasValue)
                    {
                        return;
                    }
                }
            }
        }
    }
}