using BattlePhaze.SettingsManager.Types;
using UnityEngine;
namespace BattlePhaze.SettingsManager
{
    public class SMTypeUnityText : SettingsManagerAbstractTypeText
    {
        public override void TextDescriptionSet(SettingsManager Manager, int OptionIndex)
        {
            if (SettingsManagerTypesHelper.TypeCompare(Manager.Options[OptionIndex].TextDescription, typeof(UnityEngine.UI.Text)))
            {
                UnityEngine.UI.Text TextDescription = (UnityEngine.UI.Text)Manager.Options[OptionIndex].TextDescription;
                TextDescription.text = Manager.Options[OptionIndex].ValueDescriptor;
            }
        }
        public override bool TextDescriptionGetOptionsGameobject(SettingsManager Manager, int OptionIndex, out GameObject GameObject)
        {
            GameObject = null;
            if (SettingsManagerTypesHelper.TypeCompare(Manager.Options[OptionIndex].TextDescription, typeof(UnityEngine.UI.Text)))
            {
                UnityEngine.UI.Text text = (UnityEngine.UI.Text)Manager.Options[OptionIndex].TextDescription;
                GameObject = text.gameObject;
                return true;
            }
            return false;
        }
        public override void TextGameObjectGet(SettingsManager Manager, int OptionIndex, GameObject Object)
        {
            if (Object != null && Object.GetComponent<UnityEngine.UI.Text>())
            {
                UnityEngine.UI.Text Text = (UnityEngine.UI.Text)Manager.Options[OptionIndex].ObjectInput;
                Text.text = Manager.Options[OptionIndex].ValueDescriptor;
                Text.name = Manager.Options[OptionIndex].ValueDescriptor;
            }
        }
        public override SettingsManagerEnums.IsTypeInterpreter GetActiveType()
        {
            return SettingsManagerEnums.IsTypeInterpreter.Text;
        }
    }
}