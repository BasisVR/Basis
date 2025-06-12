namespace BattlePhaze.SettingsManager
{
    using BattlePhaze.SettingsManager.Style;
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using UnityEditor;
    using UnityEngine;
    /// <summary>
    /// Settings manager custom UI editor
    /// </summary>
    [CustomEditor(typeof(SettingsManager))]
    public class SettingsManagerEditor : Editor
    {
        public static SettingsManager manager;
        public bool ManagerVisable;
        public bool ModuleSettingsVisible;
        public bool InputSettingsVisable;
        public SettingsManagerEnums.SupportedRenderPipelines PreCachedPipeline;
        public override void OnInspectorGUI()
        {
            try
            {
                manager = (SettingsManager)target;
                SettingsmanagerStyle.Style();
                EditorGUI.BeginChangeCheck();
                SettingsManager.Instance = manager;
                if (manager.ManagerSettings.SuccessfullyGenerated == false)
                {
                    manager.ManagerSettings.SuccessfullyGenerated = true;
                    EditorUtility.DisplayDialog("Settings Manager Dialog", "Please Select a Render Pipeline To use in Settings manager", "Sure thing");
                }
                DrawBuildPipelineSettings();
                DrawSettingsList();
                DrawManagerSettings();
                DrawModuleDisplay();
                DrawTypeList();
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(target, "Settings Manager Panel");
                    EditorUtility.SetDirty(manager);
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }
        private void DrawBuildPipelineSettings()
        {
            EditorGUILayout.BeginVertical(SettingsmanagerStyle.BackGroundStyling);
            GUILayout.Label(new GUIContent("<b> Settings Manager 4</b>", "Settings Manager By BattlePhaze"), SettingsmanagerStyle.TextLargeStyling);
            PreCachedPipeline = manager.ManagerSettings.CurrentPipeline;
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent("Active Render pipeline"), SettingsmanagerStyle.DescriptorStyling);
            manager.ManagerSettings.CurrentPipeline = (SettingsManagerEnums.SupportedRenderPipelines)EditorGUILayout.EnumPopup(manager.ManagerSettings.CurrentPipeline, SettingsmanagerStyle.ButtonDominateStyling);
            EditorGUILayout.EndHorizontal();
            SettingsManagerEditorDefineManagement.SetDefines(this, manager);
            EditorGUILayout.Space(12);
            EditorGUILayout.EndVertical();
        }
        private void DrawSettingsList()
        {
            EditorGUILayout.Space();
            EditorGUILayout.BeginVertical(SettingsmanagerStyle.BackGroundStyling);
            SettingsManagerEditorList.SettingsList(this, manager);
            EditorGUILayout.EndVertical();
        }
        private void DrawManagerSettings()
        {
            EditorGUILayout.Space();
            EditorGUILayout.BeginVertical(SettingsmanagerStyle.BackGroundStyling);
            SettingsManagerEditorSettings.ManagerSettings(this);
            EditorGUILayout.EndVertical();
        }
        private void DrawModuleDisplay()
        {
            EditorGUILayout.Space();
            EditorGUILayout.BeginVertical(SettingsmanagerStyle.BackGroundStyling);
            SettingManagerEditorModuleDisplay.DisplayModule(this);
            EditorGUILayout.EndVertical();
        }
        private void DrawTypeList()
        {
            EditorGUILayout.Space();
            EditorGUILayout.BeginVertical(SettingsmanagerStyle.BackGroundStyling);
            SettingsManagerEditorList.TypeList(this, manager);
            EditorGUILayout.EndVertical();
        }
        public void Slider(int OptionIndex)
        {
            float FloatSliderMinValue = 0;
            float FloatSliderMaxValue = 0;
            // Parse slider min and max values
            if (float.TryParse(manager.Options[OptionIndex].SliderMinValue, System.Globalization.NumberStyles.Any, manager.ManagerSettings.CInfo, out FloatSliderMinValue))
                if (float.TryParse(manager.Options[OptionIndex].SliderMaxValue, System.Globalization.NumberStyles.Any, manager.ManagerSettings.CInfo, out FloatSliderMaxValue))

                    // Show UI for various slider settings
                    ShowRoundingToggle(manager.Options[OptionIndex]);
            ShowRoundToField(manager.Options[OptionIndex]);
            ShowMaxPercentageField(manager.Options[OptionIndex]);
            ShowMinPercentageField(manager.Options[OptionIndex]);
            ShowSliderMinValueField(ref FloatSliderMinValue);
            ShowSliderMaxValueField(ref FloatSliderMaxValue);
            ShowTextReturnTypeDropdown(manager.Options[OptionIndex]);

            // Update slider value in all relevant objects
            SettingsManagerSlider.SliderOptionReadValue(manager, OptionIndex, out bool hasValue, out float CurrentValue);
            for (int textsIndex = 0; textsIndex < manager.SettingsManagerAbstractTypeSlider.Count; textsIndex++)
            {
                if (manager.SettingsManagerAbstractTypeSlider[textsIndex] != null)
                {
                    manager.SettingsManagerAbstractTypeSlider[textsIndex].SliderOptionSetValue(manager, OptionIndex, CurrentValue, FloatSliderMinValue, FloatSliderMaxValue);
                }
            }

            // Update slider settings
            manager.Options[OptionIndex].SliderMinValue = FloatSliderMinValue.ToString(manager.ManagerSettings.CInfo);
            manager.Options[OptionIndex].SliderMaxValue = FloatSliderMaxValue.ToString(manager.ManagerSettings.CInfo);

            // Set default slider values for current platform
            PlatformDefaultSliderValues(OptionIndex);
        }
        private void ShowTextReturnTypeDropdown(SettingsMenuInput options)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Text Return Type", SettingsmanagerStyle.DescriptorStyling);
            options.ReturnedValueTextType = (SettingsManagerEnums.TextReturn)EditorGUILayout.EnumPopup(string.Empty, options.ReturnedValueTextType, SettingsmanagerStyle.EnumStyling);
            EditorGUILayout.EndHorizontal();
        }
        private void ShowRoundingToggle(SettingsMenuInput options)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Do Rounding?", SettingsmanagerStyle.DescriptorStyling);
            options.Round = EditorGUILayout.Toggle(options.Round);
            EditorGUILayout.EndHorizontal();
        }
        private void ShowRoundToField(SettingsMenuInput options)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Round To", SettingsmanagerStyle.DescriptorStyling);
            options.RoundTo = EditorGUILayout.IntField(options.RoundTo);
            EditorGUILayout.EndHorizontal();
        }
        private void ShowMaxPercentageField(SettingsMenuInput options)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Max Percentage", SettingsmanagerStyle.DescriptorStyling);
            options.MaxPercentage = EditorGUILayout.FloatField(options.MaxPercentage);
            EditorGUILayout.EndHorizontal();
        }
        private void ShowMinPercentageField(SettingsMenuInput options)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Min Percentage", SettingsmanagerStyle.DescriptorStyling);
            options.MinPercentage = EditorGUILayout.FloatField(options.MinPercentage);
            EditorGUILayout.EndHorizontal();
        }
        private void ShowSliderMinValueField(ref float sliderMinValue)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Minimum Value", SettingsmanagerStyle.DescriptorStyling);
            sliderMinValue = EditorGUILayout.FloatField(sliderMinValue);
            EditorGUILayout.EndHorizontal();
        }
        private void ShowSliderMaxValueField(ref float sliderMaxValue)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Maximum Value", SettingsmanagerStyle.DescriptorStyling);
            sliderMaxValue = EditorGUILayout.FloatField(sliderMaxValue);
            EditorGUILayout.EndHorizontal();
        }
        public void DropDownValueSystem(int optionIndex)
        {
            var editorBasedUIToggles = manager.Options[optionIndex].EditorBasedUIToggles;
            OptionSet(ref editorBasedUIToggles.ValueToggle, "Real and User Values");
            if (!editorBasedUIToggles.ValueToggle)
            {
                return;
            }
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Add new value", SettingsmanagerStyle.ButtonStyling))
            {
                SMSelectableValues.AddSelection( manager.Options[optionIndex].SelectableValueList, string.Empty, string.Empty);
            }

            if (GUILayout.Button("Remove value", SettingsmanagerStyle.ButtonStyling))
            {
                var selectableValueList = manager.Options[optionIndex].SelectableValueList;

                if (selectableValueList.Count != 0)
                {
                    selectableValueList.RemoveAt(selectableValueList.Count - 1);
                }
            }

            if (GUILayout.Button("Set Default Values", SettingsmanagerStyle.ButtonStyling))
            {
                var selectableValueList = manager.Options[optionIndex].SelectableValueList;

                SMSelectableValues.AddSelection( selectableValueList, "Very Low", "Very Low");
                SMSelectableValues.AddSelection( selectableValueList, "Low", "Low");
                SMSelectableValues.AddSelection( selectableValueList, "Medium", "Medium");
                SMSelectableValues.AddSelection( selectableValueList, "High", "High");
                SMSelectableValues.AddSelection( selectableValueList, "Ultra", "Ultra");
            }

            EditorGUILayout.EndHorizontal();

            GUIStyle TempStyle = new GUIStyle(SettingsmanagerStyle.ValueStyling);
            TempStyle.normal.textColor = SettingsmanagerStyle.OrangeColor;
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Real Value", TempStyle);
            GUILayout.Label("User Value", TempStyle);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.BeginVertical();

            foreach (var selectableValue in manager.Options[optionIndex].SelectableValueList)
            {
                selectableValue.RealValue = EditorGUILayout.TextField(string.Empty, selectableValue.RealValue, SettingsmanagerStyle.ValueStyling);
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.BeginVertical();

            foreach (var selectableValue in manager.Options[optionIndex].SelectableValueList)
            {
                selectableValue.UserValue = EditorGUILayout.TextField(string.Empty, selectableValue.UserValue, SettingsmanagerStyle.ValueStyling);
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
        }
        public void PlatformDefaultDropDownValues(int optionIndex)
        {
            // Check if optionIndex is within the bounds of Options
            if (optionIndex < 0 || optionIndex >= manager.Options.Count)
            {
                Debug.LogError("Option index is out of range.");
                return;
            }

            var option = manager.Options[optionIndex];

            // Ensure option is not null
            if (option == null)
            {
                Debug.LogError("Option is null.");
                return;
            }

            OptionSet(ref option.EditorBasedUIToggles.DefaultPlatform, "Platform Default Values");
            if (!option.EditorBasedUIToggles.DefaultPlatform)
            {
                return;
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Add Platform Default", SettingsmanagerStyle.ButtonStyling))
            {
                if (option.PlatFormDefaultState == null)
                {
                    option.PlatFormDefaultState = new List<SMPlatFormDefault>();
                }
                option.PlatFormDefaultState.Add(new SMPlatFormDefault());
            }
            if (GUILayout.Button("Remove Platform Default", SettingsmanagerStyle.ButtonStyling))
            {
                if (option.PlatFormDefaultState != null && option.PlatFormDefaultState.Count > 0)
                {
                    option.PlatFormDefaultState.RemoveAt(option.PlatFormDefaultState.Count - 1);
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginVertical();
            if (option.PlatFormDefaultState != null)
            {
                for (int index = 0; index < option.PlatFormDefaultState.Count; index++)
                {
                    var platformState = option.PlatFormDefaultState[index];

                    if (platformState == null)
                    {
                        continue; // Skip this iteration if platformState is null
                    }

                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Label("Platform", SettingsmanagerStyle.DescriptorStyling);
                    platformState.Platform = DisplayEnumPopup(platformState.Platform, SettingsmanagerStyle.EnumStyling);
                    EditorGUILayout.EndHorizontal();

                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Label("Graphics Vendor", SettingsmanagerStyle.DescriptorStyling);
                    platformState.GraphicsVendor = DisplayEnumPopup(platformState.GraphicsVendor, SettingsmanagerStyle.EnumStyling);
                    EditorGUILayout.EndHorizontal();

                    if (option.SelectableValueList != null && option.SelectableValueList.Count > 0)
                    {
                        EditorGUILayout.BeginHorizontal();
                        GUILayout.Label("Default Value", SettingsmanagerStyle.DescriptorStyling);
                        int selectedIndex = FindIndexOfSelectableValue(platformState.SetString, option.SelectableValueList);
                        List<string> selectableValues = GetSelectableValues(option.SelectableValueList);
                        selectedIndex = EditorGUILayout.Popup(selectedIndex, selectableValues.ToArray(), SettingsmanagerStyle.ValueStyling);

                        // Check if selectedIndex is within the bounds of SelectableValueList
                        if (selectedIndex >= 0 && selectedIndex < option.SelectableValueList.Count)
                        {
                            platformState.SetString = option.SelectableValueList[selectedIndex].RealValue;
                        }
                        EditorGUILayout.EndHorizontal();
                    }

                    EditorGUILayout.Space();
                }
            }
            EditorGUILayout.EndVertical();
        }
        public void SelectBuildPipeline(int optionIndex)
        {
            var option = manager.Options[optionIndex];
            OptionSet(ref option.EditorBasedUIToggles.SupportedRenderPipelines, "Supported Render Pipeline");
            if (!option.EditorBasedUIToggles.SupportedRenderPipelines)
            {
                return;
            }
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Add Supported Pipeline", SettingsmanagerStyle.ButtonStyling))
            {
                option.SupportedRenderPipeline.Add(SettingsManagerEnums.SupportedRenderPipelines.BuiltIn);
            }
            if (GUILayout.Button("Remove Supported Pipeline", SettingsmanagerStyle.ButtonStyling))
            {
                if (option.SupportedRenderPipeline.Count != 0)
                {
                    option.SupportedRenderPipeline.RemoveAt(option.SupportedRenderPipeline.Count - 1);
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginVertical();
            for (int i = 0; i < option.SupportedRenderPipeline.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Supported Pipeline", SettingsmanagerStyle.DescriptorStyling);
                option.SupportedRenderPipeline[i] = DisplayEnumPopup(option.SupportedRenderPipeline[i], SettingsmanagerStyle.EnumStyling);
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndVertical();
        }
        private int FindIndexOfSelectableValue(string value, List<SMSelectableValues> selectableValues)
        {
            return selectableValues.FindIndex(sv => sv.RealValue == value);
        }
        private List<string> GetSelectableValues(List<SMSelectableValues> selectableValues)
        {
            List<string> realValues = new List<string>(selectableValues.Count);
            foreach (SMSelectableValues selectable in selectableValues)
            {
                realValues.Add(selectable.RealValue);
            }
            return realValues;
        }
        public void PlatformDefaultSaveValues()
        {
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Add Platform Default", SettingsmanagerStyle.ButtonStyling))
            {
                manager.PlatformSaveDefault.Add(new SMPlatFormDefaultSave());
            }
            if (GUILayout.Button("Remove Platform Default", SettingsmanagerStyle.ButtonStyling))
            {
                if (manager.PlatformSaveDefault.Count > 0)
                {
                    manager.PlatformSaveDefault.RemoveAt(manager.PlatformSaveDefault.Count - 1);
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginVertical();
            EditorGUILayout.Space();
            EditorGUILayout.Space();
            EditorGUILayout.BeginHorizontal();
            DisplayLabel("Default Save Type", SettingsmanagerStyle.DescriptorStyling);
            List<string> saveTypes = GetAllNormalSaveTypes();
            manager.DefaultSaveType.SaveType = DisplaySaveTypePopup(manager.DefaultSaveType.SaveType, saveTypes);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space();
            EditorGUILayout.Space();
            foreach (var platformSave in manager.PlatformSaveDefault)
            {
                EditorGUILayout.BeginHorizontal();
                DisplayLabel("Platform", SettingsmanagerStyle.DescriptorStyling);
                platformSave.Platform = DisplayEnumPopup(platformSave.Platform, SettingsmanagerStyle.EnumStyling);
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                DisplayLabel("Save Type", SettingsmanagerStyle.DescriptorStyling);
                if (saveTypes.Count != 0)
                {
                    platformSave.SaveType = DisplaySaveTypePopup(platformSave.SaveType, saveTypes);
                }
                else
                {
                    DisplayLabel("Please first assign a save module", SettingsmanagerStyle.EnumStyling);
                }
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.Space();
            }
            EditorGUILayout.EndVertical();
        }
        private void DisplayLabel(string labelText, GUIStyle style)
        {
            GUILayout.Label(labelText, style);
        }
        private T DisplayEnumPopup<T>(T enumValue, GUIStyle style) where T : struct, System.Enum
        {
            return (T)EditorGUILayout.EnumPopup(enumValue, style);
        }
        private string DisplaySaveTypePopup(string currentModule, List<string> modules)
        {
            if (modules.Count == 0)
            {
                return string.Empty;
            }

            int selectedIndex = SaveTypeToIndex(currentModule, modules);
            selectedIndex = Mathf.Clamp(selectedIndex, 0, modules.Count - 1);
            selectedIndex = EditorGUILayout.Popup(string.Empty, selectedIndex, modules.ToArray(), SettingsmanagerStyle.EnumStyling);
            return IndexToSaveModule(selectedIndex, modules);
        }
        public string IndexToSaveModule(int ModuleIndex, List<string> Module)
        {
            return Module[ModuleIndex];
        }
        public int SaveTypeToIndex(string currentModule, List<string> module)
        {
            return module.IndexOf(currentModule);
        }
        public List<string> GetAllNormalSaveTypes()
        {
            List<string> saveModuleNames = new List<string>();
            foreach (var saveModule in manager.SaveModules)
            {
                if (saveModule != null)
                {
                    if (saveModule.Type() == SMSaveModuleBase.SaveSystemType.Normal)
                    {
                        saveModuleNames.Add(saveModule.ModuleName());
                    }
                }
                else
                {
                    saveModuleNames.Add(string.Empty);
                }
            }
            return saveModuleNames;
        }
        public void OptionSet(ref bool Value, string Data)
        {
            if (Value)
            {
                Data = SettingsmanagerStyle.DropDownOpen + Data;
            }
            else
            {
                Data = SettingsmanagerStyle.DropDownClosed + Data;
            }
            if (GUILayout.Button(Data, SettingsmanagerStyle.FoldOutMini))
            {
                Value = !Value;
            }
        }
        public void PlatformDefaultSliderValues(int optionIndex)
        {
            var options = manager.Options[optionIndex];
            OptionSet(ref options.EditorBasedUIToggles.DefaultPlatform, "Platform Default values");
            if (!options.EditorBasedUIToggles.DefaultPlatform)
            {
                return;
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Add Platform Default", SettingsmanagerStyle.ButtonStyling))
            {
                options.PlatFormDefaultState.Add(new SMPlatFormDefault());
            }
            if (GUILayout.Button("Remove Platform Default", SettingsmanagerStyle.ButtonStyling) && options.PlatFormDefaultState.Count > 0)
            {
                options.PlatFormDefaultState.RemoveAt(options.PlatFormDefaultState.Count - 1);
            }
            EditorGUILayout.EndHorizontal();

            foreach (SMPlatFormDefault platform in options.PlatFormDefaultState)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Platform", SettingsmanagerStyle.DescriptorStyling);
                platform.Platform = (RuntimePlatform)EditorGUILayout.EnumPopup(platform.Platform, SettingsmanagerStyle.EnumStyling);
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Platform Slider value", SettingsmanagerStyle.DescriptorStyling);
                float sliderMaxValue = ParseSliderValue(options.SliderMaxValue);
                float sliderMinValue = ParseSliderValue(options.SliderMinValue);
                float SliderValue = ParseSliderValue(platform.SetString);

                var newValue = EditorGUILayout.Slider(SliderValue, sliderMinValue, sliderMaxValue);
                platform.SetString = FormatSliderValue(newValue);
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.Space();
            }
        }
        private float ParseSliderValue(string value)
        {
            float result = 0;
            if (float.TryParse(value, System.Globalization.NumberStyles.Number, manager.ManagerSettings.CInfo, out float parsedValue))
            {
                result = parsedValue;
            }
            return result;
        }
        private string FormatSliderValue(float value)
        {
            return value.ToString(manager.ManagerSettings.CInfo);
        }
        public void PlatformDefaultToggleValues(int optionIndex)
        {
            var option = manager.Options[optionIndex];
            OptionSet(ref option.EditorBasedUIToggles.DefaultPlatform, "Platform Default values");
            if (!option.EditorBasedUIToggles.DefaultPlatform)
            {
                return;
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Add Platform Default", SettingsmanagerStyle.ButtonStyling))
            {
                option.PlatFormDefaultState.Add(new SMPlatFormDefault());
            }
            if (GUILayout.Button("Remove Platform Default", SettingsmanagerStyle.ButtonStyling) && option.PlatFormDefaultState.Count > 0)
            {
                option.PlatFormDefaultState.RemoveAt(option.PlatFormDefaultState.Count - 1);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginVertical();
            foreach (var platform in option.PlatFormDefaultState)
            {
                EditorGUILayout.BeginHorizontal();
                DisplayLabelAndEnumPopup("Platform", ref platform.Platform, SettingsmanagerStyle.EnumStyling);
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                DisplayLabelAndEnumPopup("Graphics Vendor", ref platform.GraphicsVendor, SettingsmanagerStyle.EnumStyling);
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                DisplayLabelAndToggle("Platform toggle value", ref platform.SetString, manager.ManagerSettings.CInfo);
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.Space();
            }
            EditorGUILayout.EndVertical();
        }
        private void DisplayLabelAndEnumPopup<T>(string label, ref T value, GUIStyle style) where T : struct, Enum
        {
            GUILayout.Label(label, SettingsmanagerStyle.DescriptorStyling);
            value = (T)EditorGUILayout.EnumPopup(value, style);
        }
        private void DisplayLabelAndToggle(string label, ref string stringValue, CultureInfo cultureInfo)
        {
            bool state = bool.TryParse(stringValue, out state) ? state : false;
            GUILayout.Label(label, SettingsmanagerStyle.DescriptorStyling);
            state = EditorGUILayout.Toggle(string.Empty, state);
            stringValue = state.ToString(cultureInfo);
        }
        public void ExcludeFromPlatform(int optionIndex)
        {
            var editorBasedUIToggles = manager.Options[optionIndex].EditorBasedUIToggles;
            OptionSet(ref editorBasedUIToggles.ExcludePlatform, "Exclude From Platforms");
            if (!editorBasedUIToggles.ExcludePlatform)
            {
                return;
            }
            ShowPlatformExclusionUI(optionIndex);
        }
        private void ShowPlatformExclusionUI(int optionIndex)
        {
            var excludeFromPlatforms = manager.Options[optionIndex].ExcludeFromThesePlatforms;

            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Add Platform exclusion", SettingsmanagerStyle.ButtonStyling))
            {
                excludeFromPlatforms.Add(new SMExcludeFromPlatforms());
            }

            if (GUILayout.Button("Remove Platform exclusion", SettingsmanagerStyle.ButtonStyling) && excludeFromPlatforms.Count > 0)
            {
                excludeFromPlatforms.RemoveAt(excludeFromPlatforms.Count - 1);
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginVertical();

            foreach (var platform in excludeFromPlatforms)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Platform", SettingsmanagerStyle.DescriptorStyling);
                platform.Platform = (RuntimePlatform)EditorGUILayout.EnumPopup(platform.Platform, SettingsmanagerStyle.EnumStyling);
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.Space();
            }

            EditorGUILayout.EndVertical();
        }
        public void CompileTypeModules(SettingsManager manager)
        {
            foreach (var attachedManager in manager.SettingsManagerAbstractTypeManagement)
            {
                attachedManager?.ManagerCompile();
            }
        }
    }
}