using BattlePhaze.SettingsManager.Types;
using UnityEngine;
namespace BattlePhaze.SettingsManager
{
    public class SMTypeFallBackToggle : SettingsManagerAbstractTypeToggle
    {
        public SettingsManager SettingsManager;
        /// <summary>
        /// Toggle Is On
        /// </summary>
        /// <param name="Manager"></param>
        /// <param name="OptionIndex"></param>
        /// <returns></returns>
        public override void ToggleIson(SettingsManager Manager, int OptionIndex, out bool HasValue, out int Value)
        {
            HasValue = false;
            Value = 0;
            if (Manager.Options[OptionIndex].ObjectInput == null)
            {
                SMWorkAround WorkAround = SettingsManager.WorkArounds[SettingsManager.FindOrAddOption(OptionIndex)];
                int IntValue;
                if (int.TryParse(WorkAround.SelectedValue, System.Globalization.NumberStyles.Any, Manager.ManagerSettings.CInfo, out IntValue))
                {
                    HasValue = true;
                    Value = IntValue;
                }
            }
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="Manager"></param>
        /// <param name="OptionIndex"></param>
        /// <param name="state"></param>
        public override void ToggleSetActive(SettingsManager Manager, int OptionIndex, bool state)
        {
            if (Manager.Options[OptionIndex].ObjectInput == null)
            {
            }
        }
        /// <summary>
        /// Toggle On Value Changed
        /// </summary>
        /// <param name="Manager"></param>
        /// <param name="OptionIndex"></param>
        /// <param name="FunctionName"></param>
        public override void ToggleOnValueChanged(SettingsManager Manager, int OptionIndex, out bool HasValue)
        {
            HasValue = false;
            if (Manager.Options[OptionIndex].ObjectInput == null)
            {
                HasValue = true;
            }
        }
        public override void ToggleOnValueGetOptionsGameobject(SettingsManager Manager, int OptionIndex, out bool HasValue, out GameObject GameObject)
        {
            GameObject = null;
            HasValue = false;
        }
        public override void ToggleSetState(SettingsManager Manager, int OptionIndex, bool State)
        {
            if (Manager.Options[OptionIndex].ObjectInput == null)
            {
                SMWorkAround WorkAround = SettingsManager.WorkArounds[SettingsManager.FindOrAddOption(OptionIndex)];
                WorkAround.SelectedValue = State.ToString(Manager.ManagerSettings.CInfo);
            }
        }
        public override SettingsManagerEnums.IsTypeInterpreter GetActiveType()
        {
            return SettingsManagerEnums.IsTypeInterpreter.Toggle;
        }
    }
}