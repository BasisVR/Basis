using BattlePhaze.SettingsManager.Types;
using UnityEngine;
using UnityEngine.UI;

namespace BattlePhaze.SettingsManager
{
    public class SMTypeUnityManagement : SettingsManagerAbstractTypeManagement
    {
        public override void ManagerCompile()
        {
            return; //Manager Compile is used for Compiling code that does not have a define for intergrations
        }
        public override void RebuildFromGameobject(SettingsManager Manager, int OptionIndex, ref Object Object, out bool Success)
        {
            Success = false;
            if (SettingsManagerTypesHelper.TypeCompare(Object, typeof(GameObject)))
            {
                GameObject ObjectInput = (GameObject)Object;
                if (ObjectInput.GetComponent<Slider>())
                {
                    Object = ObjectInput.GetComponent<Slider>();
                    Success = true;
                    return;
                }
                if (ObjectInput.GetComponent<Toggle>())
                {
                    Object = ObjectInput.GetComponent<Toggle>();
                    Success = true;
                    return;
                }
                if (ObjectInput.GetComponent<Dropdown>())
                {
                    Object = ObjectInput.GetComponent<Dropdown>();
                    Success = true;
                    return;
                }
                if (ObjectInput.GetComponent<Button>())
                {
                    Object = ObjectInput.GetComponent<Button>();
                    Success = true;
                    return;
                }
                if (ObjectInput.GetComponent<Text>())
                {
                    Object = ObjectInput.GetComponent<Text>();
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
            if (SettingsManager.TypeCompare(Object, typeof(Button)))
            {
                HasValue = true;
            }
        }

        public override void TextDescriptionEnabledState(SettingsManager Manager, Object Object, bool State, out bool HasValue)
        {
            HasValue = false;
            if (SettingsManager.TypeCompare(Object, typeof(Text)))
            {
                HasValue = true;
                Text Text = (Text)Object;
                Text.enabled = State;
            }
        }

        public override void SettingsManagerHoverText(SettingsManager Manager, Object Object, string NewText, out bool HasValue)
        {
            HasValue = false;
            if (SettingsManager.TypeCompare(Object, typeof(Text)))
            {
                HasValue = true;
                Text Text = (Text)Object;
                Text.text = NewText;
            }
        }

        public override void RebuildFromobject(SettingsManager Manager, ref Object Object, out bool HasValue)
        {
            HasValue = false;
            if (SettingsManager.TypeCompare(Object, typeof(GameObject)))
            {
                GameObject ObjectInput = (GameObject)Object;
                if (ObjectInput.GetComponent<Text>())
                {
                    Object = ObjectInput.GetComponent<Text>();
                    HasValue = true;
                }
            }
        }
        public override void FindActiveObject(SettingsManager Manager, ref Object Object, out bool HasValue)
        {
            Object = GameObject.FindAnyObjectByType<Canvas>();
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
