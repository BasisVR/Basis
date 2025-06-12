using BattlePhaze.SettingsManager.DebugSystem;
using System;
using UnityEngine;
namespace BattlePhaze.SettingsManager
{
    public static class SettingsManagerDescriptionSystem
    {
        public static void ExplanationSetup(SettingsManager manager)
        {
            foreach (var option in manager.Options)
            {
                try
                {
                    switch (option.Type)
                    {
                        case SettingsManagerEnums.IsType.Dynamic:
                            break;
                        case SettingsManagerEnums.IsType.Toggle:
                            SettingsManagerToggle.ToggleOnValueGetOptionsGameobject(manager.Options.IndexOf(option), manager, out bool toggleSuccess, out GameObject toggleOptionsGO);
                            AddOrUpdateSettingsManagerExplanationComponent(manager, option, toggleOptionsGO, toggleSuccess);
                            break;
                        case SettingsManagerEnums.IsType.DropDown:
                            bool dropDownSuccess = SettingsManagerDropDown.GetOptionsGameobject(manager, manager.Options.IndexOf(option), out GameObject dropDownOptionsGO);
                            AddOrUpdateSettingsManagerExplanationComponent(manager, option, dropDownOptionsGO, dropDownSuccess);
                            break;
                        case SettingsManagerEnums.IsType.Slider:
                            SettingsManagerSlider.SliderGetOptionsGameobject(manager, manager.Options.IndexOf(option), out bool sliderSuccess, out GameObject sliderOptionsGO);
                            AddOrUpdateSettingsManagerExplanationComponent(manager, option, sliderOptionsGO, sliderSuccess);
                            break;
                    }

                    if (option.TextDescription != null)
                    {
                        GameObject descriptionGO = TxtDescriptionReturnGameobject(manager, manager.Options.IndexOf(option));
                        AddOrUpdateSettingsManagerExplanationComponent(manager, option, descriptionGO, true);
                    }
                }
                catch (Exception ex)
                {
                    SettingsManagerDebug.LogError(ex.StackTrace);
                }
            }
        }

        private static void AddOrUpdateSettingsManagerExplanationComponent(SettingsManager manager, SettingsMenuInput option, GameObject gameObject, bool success)
        {
            if (success && gameObject != null)
            {
                var explanationComponent = gameObject.GetComponent<ExplanationManagement.SettingsManagerExplanation>();
                if (explanationComponent == null)
                {
                    explanationComponent = gameObject.AddComponent<ExplanationManagement.SettingsManagerExplanation>();
                }
                explanationComponent.Manager = manager;
                explanationComponent.OptionIndex = manager.Options.IndexOf(option);
            }
        }
        public static void ExplanationSystem(SettingsManager manager, string value)
        {
            if (manager.ManagerSettings.ExplanationText == null)
            {
                return;
            }

            foreach (var attachedManager in manager.SettingsManagerAbstractTypeManagement)
            {
                if (attachedManager == null)
                {
                    continue;
                }

                attachedManager.SettingsManagerHoverText(manager, manager.ManagerSettings.ExplanationText, value, out var hasValue);
                if (hasValue)
                {
                    return;
                }
            }
        }
        public static GameObject TxtDescriptionReturnGameobject(SettingsManager Manager, int OptionIndex)
        {
            for (int textsIndex = 0; textsIndex < Manager.SettingsManagerAbstractTypeText.Count; textsIndex++)
            {
                if (Manager.SettingsManagerAbstractTypeText[textsIndex] != null)
                {
                    if (Manager.SettingsManagerAbstractTypeText[textsIndex].TextDescriptionGetOptionsGameobject(Manager, OptionIndex, out GameObject Ref))
                    {
                        return Ref;
                    }
                }
            }
            return null;
        }
        public static void TxtDescriptionSetText(SettingsManager Manager, int OptionIndex)
        {
            for (int textsIndex = 0; textsIndex < Manager.SettingsManagerAbstractTypeText.Count; textsIndex++)
            {
                if (Manager.SettingsManagerAbstractTypeText[textsIndex] != null)
                {
                    Manager.SettingsManagerAbstractTypeText[textsIndex].TextDescriptionSet(Manager, OptionIndex);
                }
            }
        }
    }
}