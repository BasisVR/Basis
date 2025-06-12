using System.Collections.Generic;
using UnityEngine;
namespace BattlePhaze.SettingsManager.Intergrations
{
    public class SMDResolution : SettingsManagerOption
    {
        [SerializeField]
        public List<Resolution> AvailableResolution = new List<Resolution>();
        [SerializeField]
        public List<Resolution> SortedResolution = new List<Resolution>();
        public string LastResolution;
        public override void ReceiveOption(SettingsMenuInput option, SettingsManager manager)
        {
#if !UNITY_ANDROID
            if (!NameReturn(0, option))
            {
                return;
            }
            SettingsManagerDropDown.Clear(manager, option.OptionIndex);
            SortedResolution.Clear();
            option.SelectableValueList.Clear();

            List<Resolution> availableResolutions = new List<Resolution>(Screen.resolutions);
            foreach (Resolution resolution in availableResolutions)
            {
                string resolutionString = resolution.width + "x" + resolution.height;
                if (!SMSelectableValues.GetRealValueArray(option.SelectableValueList).Contains(resolutionString))
                {
                    SMSelectableValues.AddSelection(option.SelectableValueList, resolutionString, resolutionString);
                    SettingsManagerDropDown.AddDropDownOption(manager, option.OptionIndex, resolutionString);
                    SortedResolution.Add(resolution);
                }
            }

            int selectedIndex = -1;
            if (!string.IsNullOrEmpty(option.SelectedValue))
            {
                for (int i = 0; i < option.SelectableValueList.Count; i++)
                {
                    if (option.SelectableValueList[i].RealValue == option.SelectedValue)
                    {
                        selectedIndex = i;
                        break;
                    }
                }
            }

            if (selectedIndex == -1)
            {
                for (int i = 0; i < SortedResolution.Count; i++)
                {
                    if (SortedResolution[i].height == Screen.currentResolution.height
                        && SortedResolution[i].width == Screen.currentResolution.width
                        && SortedResolution[i].refreshRateRatio.Equals(Screen.currentResolution.refreshRateRatio))
                    {
                        selectedIndex = i;
                        option.SelectedValue = SortedResolution[i].width + "x" + SortedResolution[i].height;
                        break;
                    }
                }
            }

            if (selectedIndex != -1)
            {
                if (LastResolution != option.SelectedValue)
                {
                    LastResolution = option.SelectedValue;
                    SetResolution(SortedResolution[selectedIndex].width, SortedResolution[selectedIndex].height, option.OptionIndex, selectedIndex, manager);
                }
            }
            else if (SortedResolution.Count != 0)
            {
                selectedIndex = option.SelectableValueList.Count - 1;
                option.SelectedValue = option.SelectableValueList[selectedIndex].RealValue;
                SetResolution(SortedResolution[SortedResolution.Count - 1].width, SortedResolution[SortedResolution.Count - 1].height,option.OptionIndex, selectedIndex, manager);
            }
#endif
        }
#if !UNITY_ANDROID
        public void SetResolution(int Width, int Height, int OptionIndex, int SelectableValue, SettingsManager Manager)
        {
            SettingsManagerDropDown.SetOptionsValue(Manager, OptionIndex, SelectableValue, true);

            if (Screen.width != Width || Screen.height != Height)
            {
                BasisDebug.Log("setting res " + Screen.width + " now " + Width + " | " + Screen.height + " now" + Height);
                Screen.SetResolution(Width, Height, Screen.fullScreenMode);
            }
        }
#endif
    }
}
