using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using BattlePhaze.SettingsManager.Style;
using System;

namespace BattlePhaze.SettingsManager
{
    public static class SettingsManagerEditorList
    {
        #region TypeUnity
        public static void TypeList(SettingsManagerEditor editor, SettingsManager manager)
        {
            string FoldOutText;
            if (editor.InputSettingsVisable)
            {
                FoldOutText = SettingsmanagerStyle.DropDownOpen + " Input Type Modules";
            }
            else
            {
                FoldOutText = SettingsmanagerStyle.DropDownClosed + " Input Type Modules";
            }
            if (GUILayout.Button(FoldOutText, SettingsmanagerStyle.FoldoutHeaderLarge))
            {
                editor.InputSettingsVisable = !editor.InputSettingsVisable;
            }
            if (editor.InputSettingsVisable)
            {
                DrawAddRemoveButtons(editor, manager.SettingsManagerAbstractTypeToggle, "Input Module Toggle Type");
                DrawAddRemoveButtons(editor, manager.SettingsManagerAbstractTypeDropdown, "Input Module Dropdown Type");
                DrawAddRemoveButtons(editor, manager.SettingsManagerAbstractTypeSlider, "Input Module Slider Type");
                DrawAddRemoveButtons(editor, manager.SettingsManagerAbstractTypeText, "Input Module Text Type");
                DrawAddRemoveButtons(editor, manager.SettingsManagerAbstractTypeManagement, "Input Module Management Type");

                if (GUILayout.Button("Auto Assign Input Type Modules", SettingsmanagerStyle.ButtonStyling))
                {
                    SettingsManagerModuleSystem.AutoAssignInputTypes(manager);
                }
                if (GUILayout.Button("Compile Type Lists", SettingsmanagerStyle.ButtonStyling))
                {
                    editor.CompileTypeModules(manager);
                }
            }
        }

        private static void DrawAddRemoveButtons<T>(SettingsManagerEditor editor, IList<T> list, string label) where T : UnityEngine.Object
        {
            if (GUILayout.Button("Add New " + label, SettingsmanagerStyle.ButtonStyling))
            {
                list.Add(null);
            }
            if (list.Count == 0)
            {
                GUILayout.Label("No Modules found Please Add!", SettingsmanagerStyle.ValueStyling);
            }
            for (int i = 0; i < list.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Type Module", SettingsmanagerStyle.DescriptorStyling);
                list[i] = EditorGUILayout.ObjectField(string.Empty, list[i], typeof(T), true) as T;
                if (GUILayout.Button("Remove", SettingsmanagerStyle.ButtonCompactStyling))
                {
                    list.RemoveAt(i);
                }
                EditorGUILayout.EndHorizontal();
            }
        }
        #endregion
        #region Listing
        /// <summary>
        /// Settings List out
        /// </summary>
        public static void SettingsList(SettingsManagerEditor editor, SettingsManager manager)
        {

            EditorGUILayout.LabelField("Settings", SettingsmanagerStyle.FoldoutHeaderLarge);
            EditorGUILayout.Space();

            if (GUILayout.Button("Add New", SettingsmanagerStyle.ButtonStyling))
            {
                manager.Options.Add(new SettingsMenuInput());
            }

            EditorGUILayout.Space();

            for (int optionIndex = 0; optionIndex < manager.Options.Count; optionIndex++)
            {
                GUIContent GUIContent = SettingsmanagerStyle.FoldDownGeneration(manager, optionIndex, manager.Options[optionIndex].EditorBasedUIToggles.EditorVisable);
                if (GUILayout.Button(GUIContent, SettingsmanagerStyle.FoldoutHeader))
                {
                    manager.Options[optionIndex].EditorBasedUIToggles.EditorVisable = !manager.Options[optionIndex].EditorBasedUIToggles.EditorVisable;
                }
                if (manager.Options[optionIndex].EditorBasedUIToggles.EditorVisable)
                {
                    EditorGUILayout.BeginVertical();
                    DisplayOptionsList(editor, manager, optionIndex);
                    EditorGUILayout.EndVertical();
                }

                EditorGUILayout.Space();
            }
        }
        /// <summary>
        /// Display the options list
        /// </summary>
        /// <param name="editor"></param>
        /// <param name="manager"></param>
        /// <param name="optionIndex"></param>
        public static void DisplayOptionsList(SettingsManagerEditor editor, SettingsManager manager, int optionIndex)
        {
            SettingsMenuInput option = manager.Options[optionIndex];
            EditorGUILayout.BeginHorizontal();
            option.Type = (SettingsManagerEnums.IsType)EditorGUILayout.EnumPopup(option.Type, SettingsmanagerStyle.ButtonDominateStyling);
            if (GUILayout.Button("Up↑", SettingsmanagerStyle.ButtonDominateStyling) && optionIndex - 1 >= 0)
            {
                SwapOptions(optionIndex, optionIndex - 1, manager);
            }

            if (GUILayout.Button("Down↓", SettingsmanagerStyle.ButtonDominateStyling) && optionIndex + 1 <= manager.Options.Count - 1)
            {
                SwapOptions(optionIndex, optionIndex + 1, manager);
            }

            if (GUILayout.Button("X", SettingsmanagerStyle.ButtonDominateStyling))
            {
                if (EditorUtility.DisplayDialog("Removing Option", $"Warning {option.Name} is about to be removed", "Continue", "Cancel"))
                {
                    manager.Options.RemoveAt(optionIndex);
                    return;
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Name", SettingsmanagerStyle.DescriptorStyling);
            option.Name = EditorGUILayout.TextField(string.Empty, option.Name, SettingsmanagerStyle.ValueStyling);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Description", SettingsmanagerStyle.DescriptorStyling);
            option.ValueDescriptor = EditorGUILayout.TextField(string.Empty, option.ValueDescriptor, SettingsmanagerStyle.ValueStyling);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Hover Explanation", SettingsmanagerStyle.DescriptorStyling);
            option.Explanation = EditorGUILayout.TextField(string.Empty, option.Explanation, SettingsmanagerStyle.ValueStyling);
            EditorGUILayout.EndHorizontal();

            //Default Option
            DisplayDefaultOption(option,manager);   


            // Display object reference fields
            if (option.Type == SettingsManagerEnums.IsType.Slider)
            {
                var title = option.ReturnedValueTextType == SettingsManagerEnums.TextReturn.SliderPercentage ? "Slider Percentage" : "Raw Slider Value";
                SettingsManagerEditorReference.ObjectReferenceDisplay(manager, optionIndex, ref option.TextDescription, title);
            }
            else
            {
                SettingsManagerEditorReference.ObjectReferenceDisplay(manager, optionIndex, ref option.TextDescription, "Text Description");
            }
            switch (option.Type)
            {
                case  SettingsManagerEnums.IsType.DropDown:
                    SettingsManagerEditorReference.ObjectReferenceDisplay(manager, optionIndex, ref option.ObjectInput);
                    break;
                case SettingsManagerEnums.IsType.Dynamic:
                    SettingsManagerEditorReference.ObjectReferenceDisplay(manager, optionIndex, ref option.ObjectInput);
                    break;
                case SettingsManagerEnums.IsType.Slider:
                    SettingsManagerEditorReference.ObjectReferenceDisplay(manager, optionIndex, ref option.ObjectInput);
                    break;
                case SettingsManagerEnums.IsType.Toggle:
                    SettingsManagerEditorReference.ObjectReferenceDisplay(manager, optionIndex, ref option.ObjectInput);
                    break;
            }
            SettingsManagerEditorReference.ObjectReferenceDisplay(manager, optionIndex, ref option.ResetToDefault, "Reset Reference");
            SettingsManagerEditorReference.ObjectReferenceDisplay(manager, optionIndex, ref option.ApplyInput, "Apply Reference");

            switch (option.Type)
            {
                case SettingsManagerEnums.IsType.Disabled:
                    break;

                case SettingsManagerEnums.IsType.DropDown:
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Label("Parse type", SettingsmanagerStyle.DescriptorStyling);
                    option.ParseController = (SettingsManagerEnums.ItemParse)EditorGUILayout.EnumPopup(string.Empty, option.ParseController, SettingsmanagerStyle.EnumStyling);
                    EditorGUILayout.EndHorizontal();

                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Label("Master Quality", SettingsmanagerStyle.DescriptorStyling);
                    option.MasterQualityState = (SettingsManagerEnums.MasterQualityState)EditorGUILayout.EnumPopup(string.Empty, option.MasterQualityState, SettingsmanagerStyle.EnumStyling);
                    EditorGUILayout.EndHorizontal();
                    editor.DropDownValueSystem(optionIndex);
                    editor.PlatformDefaultDropDownValues(optionIndex);
                    editor.ExcludeFromPlatform(optionIndex);

                    break;

                case SettingsManagerEnums.IsType.Dynamic:
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Label("Parse type", SettingsmanagerStyle.DescriptorStyling);
                    option.ParseController = (SettingsManagerEnums.ItemParse)EditorGUILayout.EnumPopup(string.Empty, option.ParseController, SettingsmanagerStyle.EnumStyling);
                    EditorGUILayout.EndHorizontal();

                    option.ParseController = SettingsManagerEnums.ItemParse.NormalValue;
                    editor.DropDownValueSystem(optionIndex);
                    break;

                case SettingsManagerEnums.IsType.Slider:
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Label("Parse type", SettingsmanagerStyle.DescriptorStyling);
                    option.ParseController = (SettingsManagerEnums.ItemParse)EditorGUILayout.EnumPopup(string.Empty, option.ParseController, SettingsmanagerStyle.EnumStyling);
                    EditorGUILayout.EndHorizontal();

                    editor.Slider(optionIndex);
                    break;

                case SettingsManagerEnums.IsType.Toggle:

                    editor.PlatformDefaultToggleValues(optionIndex);
                    editor.ExcludeFromPlatform(optionIndex);
                    option.ParseController = SettingsManagerEnums.ItemParse.NormalValue;
                    break;
            }
            editor.SelectBuildPipeline(optionIndex);
        }
        private static void SwapOptions(int indexA, int indexB, SettingsManager manager)
        {
            var optionA = manager.Options[indexA];
            manager.Options[indexA] = manager.Options[indexB];
            manager.Options[indexB] = optionA;
        }
        public static void DisplayDefaultOption(SettingsMenuInput option,SettingsManager manager)
        {
            switch (option.Type)
            {
                case SettingsManagerEnums.IsType.DropDown:
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Label("Default Value", SettingsmanagerStyle.DescriptorStyling);
                    if (option.RealValues.Count != 0)
                    {
                        int Index = 0;
                        if (option.ValueDefault != string.Empty)
                        {

                            if (ConvertStringArrayToIndex(option.ValueDefault, option.RealValues.ToArray(), out Index))
                            {
                                Index = EditorGUILayout.Popup(Index, option.RealValues.ToArray(), SettingsmanagerStyle.EnumStyling);
                                option.ValueDefault = option.RealValues[Index];
                            }
                            else
                            {
                                Index = EditorGUILayout.Popup(Index, option.RealValues.ToArray(), SettingsmanagerStyle.EnumStyling);
                                if (Index >= 0)
                                {
                                    option.ValueDefault = option.RealValues[Index];
                                }
                            }
                        }
                        else
                        {
                            option.ValueDefault = option.RealValues[Index];
                        }
                    }
                    else
                    {
                        //Debug.LogError("Log");
                    }
                    EditorGUILayout.EndHorizontal();
                    break;
                case SettingsManagerEnums.IsType.Dynamic:
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Label("Default Value", SettingsmanagerStyle.DescriptorStyling);
                    if (option.RealValues.Count != 0)
                    {

                        int Index = 0;
                        if (option.ValueDefault != string.Empty)
                        {
                            if (ConvertStringArrayToIndex(option.ValueDefault, option.RealValues.ToArray(), out Index))
                            {
                                Index = EditorGUILayout.Popup(Index, option.RealValues.ToArray(), SettingsmanagerStyle.EnumStyling);
                                option.ValueDefault = option.RealValues[Index];
                            }
                        }
                    }
                    EditorGUILayout.EndHorizontal();
                    break;
                case SettingsManagerEnums.IsType.Slider:
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Label("Default Value", SettingsmanagerStyle.DescriptorStyling);
                    float SliderValue = 0;
                    float sliderMaxValue = 0;
                    float sliderMinValue = 0;
                    if (float.TryParse(option.SliderMinValue, System.Globalization.NumberStyles.Number, manager.ManagerSettings.CInfo, out float MinValue))
                    {
                        sliderMinValue = MinValue;
                    }
                    if (float.TryParse(option.SliderMaxValue, System.Globalization.NumberStyles.Number, manager.ManagerSettings.CInfo, out float MaxValue))
                    {
                        sliderMaxValue = MaxValue;
                    }
                    if (float.TryParse(option.ValueDefault, System.Globalization.NumberStyles.Number, manager.ManagerSettings.CInfo, out float Value))
                    {
                        SliderValue = Value;
                    }
                    var newValue = EditorGUILayout.Slider(SliderValue, sliderMinValue, sliderMaxValue);
                    option.ValueDefault = Information.SettingsManagerInformationConverter.PostProcessValue(option.Round,false, option.RoundTo, newValue, sliderMaxValue, option.MaxPercentage, option.MinPercentage);
                   // option.ValueDefault = newValue.ToString(manager.ManagerSettings.CInfo);
                    EditorGUILayout.EndHorizontal();
                    break;
                case SettingsManagerEnums.IsType.Toggle:
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Label("Default Value", SettingsmanagerStyle.DescriptorStyling);
                    bool.TryParse(option.ValueDefault, out bool result);
                    option.ValueDefault = EditorGUILayout.Toggle(string.Empty, result).ToString(manager.ManagerSettings.CInfo);
                    EditorGUILayout.EndHorizontal();
                    break;
            }
        }
        public static bool ConvertStringArrayToIndex(string compare, string[] values, out int index)
        {
            index = Array.IndexOf(values, compare);
            return index >= 0;
        }

        #endregion
    }
}