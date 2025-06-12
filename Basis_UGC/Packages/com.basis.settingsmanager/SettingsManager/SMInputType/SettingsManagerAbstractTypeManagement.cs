using UnityEngine;

namespace BattlePhaze.SettingsManager.Types
{
    abstract public class SettingsManagerAbstractTypeManagement : SettingsManagerAbstractType
    {
        abstract public void RebuildFromGameobject(SettingsManager Manager, int OptionIndex, ref UnityEngine.Object Object, out bool Success);
        abstract public void ManagerCompile();
        abstract public void SettingsManagerHoverText(SettingsManager Manager, UnityEngine.Object Text, string Value, out bool hasValue);

        abstract public void OnClick(SettingsManager Manager, Object Object, string FunctionName, out bool HasValue);
        abstract public void TextDescriptionEnabledState(SettingsManager Manager, Object Object, bool State, out bool HasValue);

        abstract public void RebuildFromobject(SettingsManager Manager, ref Object Object, out bool HasValue);

        abstract public void FindActiveObject(SettingsManager Manager, ref Object Object, out bool HasValue);

    }
}