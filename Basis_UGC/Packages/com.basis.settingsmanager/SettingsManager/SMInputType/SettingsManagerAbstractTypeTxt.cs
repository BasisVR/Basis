using UnityEngine;
namespace BattlePhaze.SettingsManager.Types
{
    abstract public class SettingsManagerAbstractTypeText : SettingsManagerAbstractType
    {
        abstract public bool TextDescriptionGetOptionsGameobject(SettingsManager Manager, int optionIndex, out GameObject GameObject);
        abstract public void TextDescriptionSet(SettingsManager Manager, int OptionIndex);
        abstract public void TextGameObjectGet(SettingsManager Manager, int OptionIndex, GameObject Object);
    }
}