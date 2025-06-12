using BattlePhaze.SettingsManager.Types;
using System.Collections.Generic;
using UnityEngine;
namespace BattlePhaze.SettingsManager
{
    public class SMTypeFallBackManagement : SettingsManagerAbstractTypeManagement
    {
        public override void ManagerCompile()
        {
            return; //Manager Compile is used for Compiling code that does not have a define for intergrations
        }
        /// <summary>
        /// Rebuild From Gameobject
        /// </summary>
        /// <param name="Manager"></param>
        /// <param name="OptionIndex"></param>
        public override void RebuildFromGameobject(SettingsManager Manager, int OptionIndex, ref UnityEngine.Object Object, out bool state)
        {
            state = false;
            if (Manager.Options[OptionIndex].ObjectInput == null)
            {
                return;
            }
        }

        public override SettingsManagerEnums.IsTypeInterpreter GetActiveType()
        {
            return SettingsManagerEnums.IsTypeInterpreter.Management;
        }

        public override void SettingsManagerHoverText(SettingsManager Manager, Object Text, string Value, out bool hasValue)
        {
            hasValue = false;
            return;
        }

        public override void OnClick(SettingsManager Manager, Object Object, string FunctionName, out bool HasValue)
        {
            HasValue = false;
            return;
        }

        public override void TextDescriptionEnabledState(SettingsManager Manager, Object Object, bool State, out bool HasValue)
        {
            HasValue = false;
            return;
        }

        public override void RebuildFromobject(SettingsManager Manager, ref Object Object, out bool HasValue)
        {
            HasValue = false;
            return;
        }

        public override void FindActiveObject(SettingsManager Manager, ref Object Object, out bool HasValue)
        {
            HasValue = false;
            return;
        }
    }
}