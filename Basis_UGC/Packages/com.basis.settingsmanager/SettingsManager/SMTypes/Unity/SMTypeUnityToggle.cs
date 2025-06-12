using BattlePhaze.SettingsManager.Types;
using UnityEngine;
using System;
namespace BattlePhaze.SettingsManager
{
    public class SMTypeUnityToggle : SettingsManagerAbstractTypeToggle
    {
        public void SetResetAction(SettingsManager Manager, int OptionIndex)
        {
            if (SettingsManagerTypesHelper.TypeCompare(Manager.Options[OptionIndex].ResetToDefault, typeof(UnityEngine.UI.Button)))
            {
                UnityEngine.UI.Button Button = (UnityEngine.UI.Button)Manager.Options[OptionIndex].ResetToDefault;
                if (Manager.Options[OptionIndex].ResetAction != null)
                {
                    Button.onClick.RemoveListener(Manager.Options[OptionIndex].ResetAction);
                }
                Manager.Options[OptionIndex].ResetAction = delegate { SettingsManagerStorageManagement.SetDefault(Manager, OptionIndex, true); };
                Button.onClick.AddListener(Manager.Options[OptionIndex].ResetAction);
            }
        }
        public override void ToggleIson(SettingsManager Manager, int OptionIndex, out bool HasValue, out int Value)
        {
            HasValue = false;
            Value = 0;
            if (SettingsManagerTypesHelper.TypeCompare(Manager.Options[OptionIndex].ObjectInput, typeof(UnityEngine.UI.Toggle)))
            {
                UnityEngine.UI.Toggle Toggle = (UnityEngine.UI.Toggle)Manager.Options[OptionIndex].ObjectInput;
                Value = Convert.ToInt32(Toggle.isOn);
                HasValue = true;
            }
        }
        public override void ToggleSetActive(SettingsManager Manager, int OptionIndex, bool state)
        {
            if (SettingsManagerTypesHelper.TypeCompare(Manager.Options[OptionIndex].ObjectInput, typeof(UnityEngine.UI.Toggle)))
            {
                UnityEngine.UI.Toggle Toggle = (UnityEngine.UI.Toggle)Manager.Options[OptionIndex].ObjectInput;
                Toggle.gameObject.SetActive(state);
            }
        }
        public override void ToggleOnValueChanged(SettingsManager Manager, int OptionIndex, out bool HasValue)
        {
            HasValue = false;
            if (SettingsManagerTypesHelper.TypeCompare(Manager.Options[OptionIndex].ObjectInput, typeof(UnityEngine.UI.Toggle)))
            {
                HasValue = true;
                UnityEngine.UI.Toggle Toggle = (UnityEngine.UI.Toggle)Manager.Options[OptionIndex].ObjectInput;
                if (SettingsManagerTypesHelper.TypeCompare(Manager.Options[OptionIndex].ApplyInput, typeof(UnityEngine.UI.Button)))
                {
                    UnityEngine.UI.Button Button = (UnityEngine.UI.Button)Manager.Options[OptionIndex].ApplyInput;
                    if (Manager.Options[OptionIndex].ApplyAction != null)
                    {
                        Button.onClick.RemoveListener(Manager.Options[OptionIndex].ApplyAction);
                    }
                    Manager.Options[OptionIndex].ApplyAction = delegate { SettingsManagerToggle.ToggleExecution(OptionIndex, Manager, Toggle.isOn); };
                    Button.onClick.AddListener(Manager.Options[OptionIndex].ApplyAction);
                }
                else
                {
                    if (Manager.Options[OptionIndex].BoolAction != null)
                    {
                        Toggle.onValueChanged.RemoveListener(Manager.Options[OptionIndex].BoolAction);
                    }
                    Manager.Options[OptionIndex].BoolAction = delegate { SettingsManagerToggle.ToggleExecution(OptionIndex, Manager, Toggle.isOn); };
                    Toggle.onValueChanged.AddListener(Manager.Options[OptionIndex].BoolAction);
                }
            }
            SetResetAction(Manager, OptionIndex);
        }
        public override void ToggleOnValueGetOptionsGameobject(SettingsManager Manager, int OptionIndex, out bool HasValue, out GameObject GameObject)
        {
            GameObject = null;
            HasValue = false;
            if (SettingsManagerTypesHelper.TypeCompare(Manager.Options[OptionIndex].ObjectInput, typeof(UnityEngine.UI.Toggle)))
            {
                UnityEngine.UI.Toggle Toggle = (UnityEngine.UI.Toggle)Manager.Options[OptionIndex].ObjectInput;
                GameObject = Toggle.gameObject;
                HasValue = true;
            }
        }
        public override void ToggleSetState(SettingsManager Manager, int OptionIndex, bool State)
        {
            if (SettingsManagerTypesHelper.TypeCompare(Manager.Options[OptionIndex].ObjectInput, typeof(UnityEngine.UI.Toggle)))
            {
                UnityEngine.UI.Toggle Toggle = (UnityEngine.UI.Toggle)Manager.Options[OptionIndex].ObjectInput;
                Toggle.isOn = State;
            }
        }
        public override SettingsManagerEnums.IsTypeInterpreter GetActiveType()
        {
            return SettingsManagerEnums.IsTypeInterpreter.Toggle;
        }
    }
}