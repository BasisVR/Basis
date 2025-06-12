using BattlePhaze.SettingsManager.DebugSystem;
using System.Collections.Generic;
using UnityEngine;

namespace BattlePhaze.SettingsManager
{
    [System.Serializable]
    abstract public class SettingsManagerOption : MonoBehaviour
    {
        [SerializeField]
        [HideInInspector]
        public List<SettingsManagerOptionValues> ManagerModuleOptions = new List<SettingsManagerOptionValues>();
        /// <summary>
        /// Takes the option provided from the settings manager.
        /// This allows custom script intergrations with the settings manager.
        /// </summary>
        /// <param name="Option"></param>
        abstract public void ReceiveOption(SettingsMenuInput Option, SettingsManager Manager = null);
        [System.Serializable]
        public struct SettingsManagerOptionValues
        {
            [SerializeField]
            public string OptionName;
            [SerializeField]
            public string Explanation;
            [SerializeField]
            public SettingsManagerEnums.ItemParse Parse;
            [SerializeField]
            public SettingsManagerEnums.IsType Type;
        }
        public bool NameReturn(int Value, SettingsMenuInput Input)
        {
            if (ManagerModuleOptions.Count != 0)
            {
                /// 3 > 2
                if (ManagerModuleOptions.Count > Value)
                {
                    if (Input.Name == ManagerModuleOptions[Value].OptionName)
                    {
                        return true;
                    }
                }
                else
                {
                    SettingsManagerDebug.LogError("Module Options was not large enough");
                }
            }
            else
            {
                SettingsManagerDebug.LogError("Module Options was empty");
            }
            return false;
        }
        public bool CheckIsOn(string Value)
        {
            if (Value == "true")
            {
                return true;
            }
            if (Value == "false")
            {
                return false;
            }
            return false;
        }
        public bool SliderReadOption(SettingsMenuInput Option, SettingsManager Manager, out float Value)
        {
            SettingsManagerSlider.SliderOptionReadValue(Manager, Option.OptionIndex, out bool HasValue, out Value);
            return HasValue;
        }
        public bool GetValue(SettingsMenuInput Option, int Value, out int SelectedIndex)
        {
            SelectedIndex = -1;
            if (NameReturn(Value, Option))
            {
                for (int Index = 0; Index < Option.RealValues.Count; Index++)
                {
                    if (Option.RealValues[Index] == Option.SelectedValue)
                    {
                        SelectedIndex = Index;
                        return true;
                    }
                }
            }
            return false;
        }
    }
}