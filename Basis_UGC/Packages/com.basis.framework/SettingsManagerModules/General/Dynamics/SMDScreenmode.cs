using UnityEngine;

namespace BattlePhaze.SettingsManager.Intergrations
{
    public class SMDScreenmode : SettingsManagerOption
    {
        public SettingsManager Manager;

        // Define constant strings for screen mode names
        private const string FullscreenModeName = "Fullscreen";
        private const string MaximizedWindowModeName = "Maximized Window";
        private const string WindowedModeName = "Windowed";

        public override void ReceiveOption(SettingsMenuInput Option, SettingsManager Manager = null)
        {
#if UNITY_ANDROID
            return;
#else
            if (!NameReturn(0, Option))
            {
                return;
            }

            SettingsManagerDropDown.Clear(Manager, Option.OptionIndex);
            Option.SelectableValueList.Clear();

            // Use constants for dropdown options
            SettingsManagerDropDown.AddDropDownOption(Manager, Option.OptionIndex, FullscreenModeName);
            SMSelectableValues.AddSelection(Option.SelectableValueList, FullscreenModeName, FullscreenModeName);

            SettingsManagerDropDown.AddDropDownOption(Manager, Option.OptionIndex, MaximizedWindowModeName);
            SMSelectableValues.AddSelection(Option.SelectableValueList, MaximizedWindowModeName, MaximizedWindowModeName);

            SettingsManagerDropDown.AddDropDownOption(Manager, Option.OptionIndex, WindowedModeName);
            SMSelectableValues.AddSelection(Option.SelectableValueList, WindowedModeName, WindowedModeName);

            if (string.IsNullOrEmpty(Option.SelectedValue))
            {
                SettingsManagerDropDown.SetOptionsValue(Manager, 0, 0, true);
                Option.SelectedValue = Option.SelectableValueList[0].RealValue;
                BasisDebug.Log("Updating ScreenMode to " + Option.SelectedValue);
                Screen.fullScreenMode = FullScreenMode.Windowed;
            }
            else
            {
                BasisDebug.Log("Updating ScreenMode to " + Option.SelectedValue);
                for (int RealValuesIndex = 0; RealValuesIndex < Option.SelectableValueList.Count; RealValuesIndex++)
                {
                    if (Option.SelectableValueList[RealValuesIndex].RealValue == Option.SelectedValue)
                    {
                        // Set screen mode based on the selected value
                        switch (Option.SelectedValue)
                        {
                            case FullscreenModeName:
                                Screen.fullScreenMode = FullScreenMode.FullScreenWindow;
                                SettingsManagerDropDown.SetOptionsValue(Manager, Option.OptionIndex, RealValuesIndex, true);
                                BasisDebug.Log("set ScreenMode to " + Option.SelectedValue);
                                return;
                            case MaximizedWindowModeName:
                                Screen.fullScreenMode = FullScreenMode.MaximizedWindow;
                                SettingsManagerDropDown.SetOptionsValue(Manager, Option.OptionIndex, RealValuesIndex, true);
                                BasisDebug.Log("set ScreenMode to " + Option.SelectedValue);
                                return;
                            case WindowedModeName:
                                Screen.fullScreenMode = FullScreenMode.Windowed;
                                SettingsManagerDropDown.SetOptionsValue(Manager, Option.OptionIndex, RealValuesIndex, true);
                                BasisDebug.Log("set ScreenMode to " + Option.SelectedValue);
                                return;
                        }
                    }
                }
            }
#endif
        }
    }
}
