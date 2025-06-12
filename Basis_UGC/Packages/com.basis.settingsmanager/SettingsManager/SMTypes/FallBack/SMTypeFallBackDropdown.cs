using BattlePhaze.SettingsManager.Types;
using System.Collections.Generic;
using UnityEngine;
namespace BattlePhaze.SettingsManager
{
    /// <summary>
    /// SM Type Unity Dropdo+wn
    /// </summary>
    public class SMTypeFallBackDropdown : SettingsManagerAbstractTypeDropdown
    {
        public SettingsManager DataHolder;
        /// <summary>
        /// DropDown Listener
        /// </summary>
        /// <param name="OptionIndex">Option Index</param>
        /// <param name="Dropdown"></param>
        public override bool DropDownListener(SettingsManager Manager, int OptionIndex, bool IsDynamic)
        {
            if (IsDynamic)
            {
                if (Manager.Options[OptionIndex].ObjectInput == null)
                {
                    return true;
                }

            }
            else
            {
                if (Manager.Options[OptionIndex].ObjectInput == null)
                {
                    DataHolder.WorkArounds[DataHolder.FindOrAddOption(OptionIndex)].SelectedValue = Manager.Options[OptionIndex].SelectedValue;
                    return true;
                }
            }
            return false;
        }
        /// <summary>
        /// DropDown Enabled State
        /// </summary>
        /// <param name="Manager"></param>
        /// <param name="OptionIndex"></param>
        /// <param name="EnabledState"></param>
        public override void DropDownEnabledState(SettingsManager Manager, int OptionIndex, bool EnabledState)
        {
            if (Manager.Options[OptionIndex].ObjectInput == null)
            {
                return;
            }
        }
        /// <summary>
        /// Set Options Value
        /// </summary>
        /// <param name="Manager"></param>
        /// <param name="OptionIndex"></param>
        /// <param name="OptionsIndexValue"></param>
        public override void DropDownSetOptionsValue(SettingsManager Manager, int OptionIndex, int OptionsIndexValue, bool Silent)
        {
            if (Manager.Options[OptionIndex].ObjectInput == null)
            {
                SMWorkAround WorkAround = DataHolder.WorkArounds[DataHolder.FindOrAddOption(OptionIndex)];
                WorkAround.SelectedValue = OptionsIndexValue.ToString(Manager.ManagerSettings.CInfo);
            }
        }
        /// <summary>
        /// Get Options Gameobject
        /// </summary>
        /// <param name="Manager"></param>
        /// <param name="OptionIndex"></param>
        /// <returns></returns>
        public override bool DropDownGetOptionsGameobject(SettingsManager Manager, int OptionIndex, out GameObject GameObject)
        {
            GameObject = null;
            return false;
        }
        /// <summary>
        /// Clear DropDown
        /// </summary>
        /// <param name="Manager"></param>
        /// <param name="OptionIndex"></param>
        public override void DropDownClearDropDown(SettingsManager Manager, int OptionIndex)
        {
        }
        /// <summary>
        /// Add DropDown Option
        /// </summary>
        /// <param name="Manager"></param>
        /// <param name="OptionIndex"></param>
        /// <param name="Option"></param>
        public override void DropDownAddOption(SettingsManager Manager, int OptionIndex, string Option)
        {
            if (Manager.Options[OptionIndex].TextDescription == null)
            {
                DataHolder.FindOrAddOption(OptionIndex);
            }
            return;
        }
        public override SettingsManagerEnums.IsTypeInterpreter GetActiveType()
        {
            return SettingsManagerEnums.IsTypeInterpreter.DropDown;
        }
    }
}