using BattlePhaze.SettingsManager.Types;
using UnityEngine;
namespace BattlePhaze.SettingsManager
{
    public class SMTypeFallBackText : SettingsManagerAbstractTypeText
    {
        /// <summary>
        /// Sets Text on Description Text UI;
        /// </summary>
        /// <param name="Manager"></param>
        /// <param name="OptionIndex"></param>
        public override void TextDescriptionSet(SettingsManager Manager, int OptionIndex)
        {
            if (Manager.Options[OptionIndex].TextDescription == null)
            {
                return;
            }
        }
        /// <summary>
        /// Get Gameobject
        /// <param name="Manager"></param>
        /// <param name="OptionIndex"></param>
        /// <returns></returns>
        public override bool TextDescriptionGetOptionsGameobject(SettingsManager Manager, int OptionIndex, out GameObject GameObject)
        {
            GameObject = null;
            return false;
        }
        /// <summary>
        /// GameObject Get Text
        /// </summary>
        /// <param name="Manager"></param>
        /// <param name="OptionIndex"></param>
        /// <returns></returns>
        public override void TextGameObjectGet(SettingsManager Manager, int OptionIndex, GameObject Object)
        {
            if (Manager.Options[OptionIndex].ObjectInput == null)
            {
                return;
            }
        }
        public override SettingsManagerEnums.IsTypeInterpreter GetActiveType()
        {
            return SettingsManagerEnums.IsTypeInterpreter.Text;
        }
    }
}