using TMPro;
using BattlePhaze.SettingsManager.Types;
using UnityEngine;
namespace BattlePhaze.SettingsManager.TypeModule
{
    public class SMTypeTMPText : SettingsManagerAbstractTypeText
    {
        public override void TextDescriptionSet(SettingsManager Manager, int OptionIndex)
        {
            if (SettingsManagerTypesHelper.TypeCompare(Manager.Options[OptionIndex].TextDescription, typeof(TextMeshProUGUI)))
            {
                TextMeshProUGUI TextDescription = (TextMeshProUGUI)Manager.Options[OptionIndex].TextDescription;
                TextDescription.text = Manager.Options[OptionIndex].ValueDescriptor;
            }
        }
        public override bool TextDescriptionGetOptionsGameobject(SettingsManager Manager, int OptionIndex,out GameObject GameObject)
        {
            GameObject = null;
            if (SettingsManagerTypesHelper.TypeCompare(Manager.Options[OptionIndex].TextDescription, typeof(TextMeshProUGUI)))
            {
                TextMeshProUGUI text = (TextMeshProUGUI)Manager.Options[OptionIndex].TextDescription;
                GameObject = text.gameObject;
                return true;
            }
            return false;
        }
        public override void TextGameObjectGet(SettingsManager Manager, int OptionIndex, GameObject Object)
        {
            if (Object != null && Object.GetComponent<TextMeshProUGUI>())
            {
                TextMeshProUGUI Text = (TextMeshProUGUI)Manager.Options[OptionIndex].ObjectInput;
                Text.text = Manager.Options[OptionIndex].ValueDescriptor;
            }
        }
        public override SettingsManagerEnums.IsTypeInterpreter GetActiveType()
        {
            return SettingsManagerEnums.IsTypeInterpreter.Text;
        }
    }
}