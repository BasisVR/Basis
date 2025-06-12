using UnityEngine;
namespace BattlePhaze.SettingsManager.Intergrations
{
    public class SMDMonitor : SettingsManagerOption
    {
        public override void ReceiveOption(SettingsMenuInput option, SettingsManager manager = null)
        {
#if UNITY_ANDROID
            return;
#else
            if (NameReturn(0, option))
            {
                SettingsManagerDropDown.Clear(manager, option.OptionIndex);
                option.SelectableValueList.Clear();

                for (int displayIndex = 0; displayIndex < Display.displays.Length; displayIndex++)
                {
                    string displayId = displayIndex.ToString(manager.ManagerSettings.CInfo);
                    SMSelectableValues.AddSelection(option.SelectableValueList, displayId, displayId);
                    SettingsManagerDropDown.AddDropDownOption(manager, option.OptionIndex, displayId);
                }

                if (string.IsNullOrEmpty(option.SelectedValue))
                {
                    SettingsManagerDropDown.SetOptionsValue(manager, option.OptionIndex, 0, true);
                    option.SelectedValue = option.SelectableValueList[0].RealValue;
                }
                else
                {
                    for (int displayIndex = 0; displayIndex < Display.displays.Length; displayIndex++)
                    {
                        if (option.SelectableValueList[displayIndex].RealValue == option.SelectedValue)
                        {
                            SettingsManagerDropDown.SetOptionsValue(manager, option.OptionIndex, displayIndex, true);
                           // Display.displays[displayIndex].Activate(,);
                            return;
                        }
                    }
                }
            }
#endif
        }
    }
}
