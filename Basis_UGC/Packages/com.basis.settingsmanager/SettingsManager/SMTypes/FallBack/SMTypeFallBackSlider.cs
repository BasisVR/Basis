using BattlePhaze.SettingsManager.DebugSystem;
using BattlePhaze.SettingsManager.Types;
using System;
using System.Collections.Generic;
using UnityEngine;
namespace BattlePhaze.SettingsManager
{
    public class SMTypeFallBackSlider : SettingsManagerAbstractTypeSlider
    {
        public SettingsManager SettingsManager;
        /// <summary>
        /// Slider Option Set Value
        /// </summary>
        /// <param name="Manager"></param>
        /// <param name="OptionIndex"></param>
        /// <param name="Value"></param>
        public override void SliderOptionSetValue(SettingsManager Manager, int OptionIndex, float Value, float Min, float Max)
        {
            try
            {
                if (Manager != null)
                {
                    if (Manager.Options[OptionIndex] != null)
                    {
                        if (Manager.Options[OptionIndex].TextDescription == null)
                        {
                            int Index = SettingsManager.FindOrAddOption(OptionIndex);
                            SMWorkAround WorkAround = SettingsManager.WorkArounds[Index];
                            WorkAround.SelectedValue = Value.ToString(Manager.ManagerSettings.CInfo);
                            WorkAround.Min = Min;
                            WorkAround.Max = Max;
                        }
                    }
                }
            }
            catch(Exception E)
            {
                SettingsManagerDebug.LogError(E.StackTrace);
            }
        }
        /// <summary>
        /// Slider Option Set Value
        /// </summary>
        /// <param name="Manager"></param>
        /// <param name="OptionIndex"></param>
        /// <param name="Value"></param>
        public override void SliderOptionReadValue(SettingsManager Manager, int OptionIndex, out bool HasValue, out float Value)
        {
            HasValue = false;
            Value = 0f;
            if (Manager.Options[OptionIndex].ObjectInput == null)
            {
                int Index = SettingsManager.FindOrAddOption(OptionIndex);
                SMWorkAround WorkAround = SettingsManager.WorkArounds[Index];
                float FloatValue;
                if (float.TryParse(WorkAround.SelectedValue, System.Globalization.NumberStyles.Any, Manager.ManagerSettings.CInfo, out FloatValue))
                {
                    HasValue = true;
                    Value = FloatValue;
                }
            }
        }
        /// <summary>
        /// Slider Option Set Value
        /// </summary>
        /// <param name="Manager"></param>
        /// <param name="OptionIndex"></param>
        /// <param name="Value"></param>
        public override void SliderOptionReadMaxValue(SettingsManager Manager, int OptionIndex, out bool HasValue, out float Value)
        {
            HasValue = false;
            Value = 0f;
            if (Manager.Options[OptionIndex].ObjectInput == null)
            {
                SMWorkAround WorkAround = SettingsManager.WorkArounds[SettingsManager.FindOrAddOption(OptionIndex)];
                HasValue = true;
                Value = WorkAround.Max;
            }
        }
        /// <summary>
        /// Slider Option Set Value
        /// </summary>
        /// <param name="Manager"></param>
        /// <param name="OptionIndex"></param>
        /// <param name="Value"></param>
        public override void SliderOptionReadMinValue(SettingsManager Manager, int OptionIndex, out bool HasValue, out float Value)
        {
            HasValue = false;
            Value = 0f;
            if (Manager.Options[OptionIndex].ObjectInput == null)
            {
                SMWorkAround WorkAround = SettingsManager.WorkArounds[SettingsManager.FindOrAddOption(OptionIndex)];
                HasValue = true;
                Value = WorkAround.Min;
            }
        }
        /// <summary>
        /// Slider Option Return Gameobject
        /// </summary>
        /// <param name="Manager"></param>
        /// <param name="OptionIndex"></param>
        /// <param name="Value"></param>
        public override void SliderGetOptionsGameobject(SettingsManager Manager, int OptionIndex, out bool HasValue, out GameObject GameObject)
        {
            HasValue = false;
            GameObject = null;
        }
        /// <summary>
        /// Slider Enabled State
        /// </summary>
        /// <param name="Manager"></param>
        /// <param name="OptionIndex"></param>
        /// <param name="outcome"></param>
        public override void SliderEnabledState(SettingsManager Manager, int OptionIndex, bool outcome)
        {
            if (Manager.Options[OptionIndex].ObjectInput == null)
            {
            }
        }
        /// <summary>
        /// Slider On Value Changed
        /// </summary>
        /// <param name="Manager"></param>
        /// <param name="OptionIndex"></param>
        /// <param name="FunctionName"></param>
        public override void SliderOnValueChanged(SettingsManager Manager, int OptionIndex, out bool HasValue)
        {
            HasValue = false;
            if (Manager.Options[OptionIndex].ObjectInput == null)
            {
                HasValue = true;
            }
        }
        public override SettingsManagerEnums.IsTypeInterpreter GetActiveType()
        {
            return SettingsManagerEnums.IsTypeInterpreter.Slider;
        }
    }
}