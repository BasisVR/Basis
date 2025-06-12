using TMPro;
using BattlePhaze.SettingsManager.Types;
using UnityEngine;
namespace BattlePhaze.SettingsManager.TypeModule
{
    public class SMTypeTMPManagement : SettingsManagerAbstractTypeManagement
    {
        public override void ManagerCompile()
        {
            return; //Manager Compile is used for Compiling code that does not have a define for intergrations
        }
        public override void RebuildFromGameobject(SettingsManager Manager, int OptionIndex, ref UnityEngine.Object Object, out bool Success)
        {
            Success = false;
            if (SettingsManagerTypesHelper.TypeCompare(Object, typeof(GameObject)))
            {
                GameObject ObjectInput = (GameObject)Object;
                UnityEngine.UI.Slider Slider = ObjectInput.GetComponent<UnityEngine.UI.Slider>();
                if (Slider != null)
                {
                    Object = Slider;
                    Success = true;
                    return;
                }
                UnityEngine.UI.Toggle Toggle = ObjectInput.GetComponent<UnityEngine.UI.Toggle>();
                if (Toggle != null)
                {
                    Object = Toggle;
                    Success = true;
                    return;
                }
                TMP_Dropdown TMP_Dropdown = ObjectInput.GetComponent<TMP_Dropdown>();
                if (TMP_Dropdown != null)
                {
                    Object = TMP_Dropdown;
                    Success = true;
                    return;
                }
                TextMeshProUGUI TextMeshProUGUI = ObjectInput.GetComponent<TextMeshProUGUI>();
                if (TextMeshProUGUI != null)
                {
                    Object = TextMeshProUGUI;
                    Success = true;
                    return;
                }
            }
        }
        public override SettingsManagerEnums.IsTypeInterpreter GetActiveType()
        {
            return SettingsManagerEnums.IsTypeInterpreter.Management;
        }

        public override void OnClick(SettingsManager Manager, Object Object, string FunctionName, out bool HasValue)
        {
            HasValue = false;
            if (SettingsManager.TypeCompare(Object, typeof(TMP_Dropdown)))
            {
                HasValue = true;
            }
        }
        public override void TextDescriptionEnabledState(SettingsManager Manager, Object Object, bool State, out bool HasValue)
        {
            HasValue = false;
            if (SettingsManager.TypeCompare(Object, typeof(TextMeshProUGUI)))
            {
                TextMeshProUGUI Text = (TextMeshProUGUI)Object;
                Text.enabled = State;
                HasValue = true;
            }
        }

        public override void SettingsManagerHoverText(SettingsManager Manager, Object Object, string NewText, out bool HasValue)
        {
            HasValue = false;
            if (SettingsManager.TypeCompare(Object, typeof(TextMeshProUGUI)))
            {
                TextMeshProUGUI Text = (TextMeshProUGUI)Object;
                Text.text = NewText;
                HasValue = true;
            }
        }

        public override void RebuildFromobject(SettingsManager Manager, ref Object Object, out bool HasValue)
        {
            HasValue = false;
            if (SettingsManager.TypeCompare(Object, typeof(GameObject)))
            {
                GameObject ObjectInput = (GameObject)Object;
                if (ObjectInput.GetComponent<TextMeshProUGUI>())
                {
                    Object = ObjectInput.GetComponent<TextMeshProUGUI>();
                    HasValue = true;
                }
            }
        }

        public override void FindActiveObject(SettingsManager Manager, ref Object Object, out bool HasValue)
        {
            Object = GameObject.FindFirstObjectByType<Canvas>();
            if (Object != null)
            {
                HasValue = true;
            }
            else
            {
                HasValue = false;
            }
        }
    }
}
