using BattlePhaze.SettingsManager.EditorDefine;
using BattlePhaze.SettingsManager.Style;
using System;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;
namespace BattlePhaze.SettingsManager
{
    public static class SettingsManagerEditorSettings
    {
        public static bool HasRan;
        /// <summary>
        /// Manage settings system
        /// </summary>
        public static void ManagerSettings(SettingsManagerEditor editor)
        {
            if (GUILayout.Button(GetFoldoutText(editor), SettingsmanagerStyle.FoldoutHeaderLarge))
            {
                editor.ManagerVisable = !editor.ManagerVisable;
            }

            if (editor.ManagerVisable)
            {
                if (HasRan == false)
                {
                    HasRan = true;
                    SettingsManagerOptionInputCreator.GetAllCreators(out SettingsManagerEditor.manager.ManagerSettings.SettingsManagerObjectpaths, out SettingsManagerEditor.manager.ManagerSettings.SettingsManagerObjects);
                }
                DrawDontDestroyOnLoad();
                DrawHoverExplanation();
                DrawActiveCanvas();
                DrawSaveModules();
                RebuildManagerUI();
                editor.PlatformDefaultSaveValues();
                ShowCultureInfo();
                ShowFileName();
                ShowLocationAndDeleteFile();
                InstantiableTypes();
                UpdateOptionValuesOption(editor);
            }
        }
        public static void UpdateOptionValuesOption(SettingsManagerEditor editor)
        {
            if (GUILayout.Button("Update Option Values", SettingsmanagerStyle.ButtonStyling))
            {
                SettingsManagerDefines.DefineWarmup(SettingsManagerEditor.manager.ManagerSettings.CurrentPipeline);
                editor.CompileTypeModules(SettingsManagerEditor.manager);
                SettingsManagerEditor.manager.Initalize(true);
            }
        }
        public static void InstantiableTypes()
        {
            if (GUILayout.Button("Find All Premade UI Modules", SettingsmanagerStyle.ButtonStyling))
            {
                SettingsManagerOptionInputCreator.GetAllCreators(out SettingsManagerEditor.manager.ManagerSettings.SettingsManagerObjectpaths, out SettingsManagerEditor.manager.ManagerSettings.SettingsManagerObjects);
            }
        }
        private static string GetFoldoutText(SettingsManagerEditor editor)
        {
            return editor.ManagerVisable ? SettingsmanagerStyle.DropDownOpen + " Manager Settings" : SettingsmanagerStyle.DropDownClosed + " Manager Settings";
        }
        private static void DrawDontDestroyOnLoad()
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("DontDestroyOnLoad", SettingsmanagerStyle.DescriptorStyling);
            SettingsManagerEditor.manager.ManagerSettings.MarkAsDontDestroyOnLoad = (SettingsManagerEnums.DestroyOnLoadSettings)EditorGUILayout.EnumPopup(SettingsManagerEditor.manager.ManagerSettings.MarkAsDontDestroyOnLoad, SettingsmanagerStyle.EnumStyling);
            EditorGUILayout.EndHorizontal();
        }
        private static void DrawHoverExplanation()
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Hover Explanation", SettingsmanagerStyle.DescriptorStyling);
            SettingsManagerEditor.manager.ManagerSettings.ExplanationText = EditorGUILayout.ObjectField(string.Empty, SettingsManagerEditor.manager.ManagerSettings.ExplanationText, typeof(UnityEngine.Object), true);
            EditorGUILayout.EndHorizontal();
        }
        private static void DrawActiveCanvas()
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Active Canvas", SettingsmanagerStyle.DescriptorStyling);
            SettingsManagerEditor.manager.ManagerSettings.ActiveCanvasLikeObject = EditorGUILayout.ObjectField(string.Empty, SettingsManagerEditor.manager.ManagerSettings.ActiveCanvasLikeObject, typeof(UnityEngine.Object), true);
            if (SettingsManagerEditor.manager.ManagerSettings.ActiveCanvasLikeObject == null && GUILayout.Button("Attempt to find Canvas", SettingsmanagerStyle.ButtonStyling))
            {
                foreach (var ui in SettingsManagerEditor.manager.SettingsManagerAbstractTypeManagement.Where(ui => ui != null))
                {
                    ui.FindActiveObject(SettingsManagerEditor.manager, ref SettingsManagerEditor.manager.ManagerSettings.ActiveCanvasLikeObject, out bool Value);
                }
            }
            EditorGUILayout.EndHorizontal();
        }
        private static void DrawSaveModules()
        {
            EditorGUILayout.BeginHorizontal();
            if (SettingsManagerEditor.manager.SaveModules.Count == 0)
            {
                GUILayout.Label("No Save Modules Found please add a save module!", SettingsmanagerStyle.ValueStyling);
            }
            if (GUILayout.Button("Add Save Module", SettingsmanagerStyle.ButtonStyling))
            {
                SettingsManagerEditor.manager.SaveModules.Add(null);
            }
            if (GUILayout.Button("Auto Find Save Modules", SettingsmanagerStyle.ButtonStyling))
            {
                SettingsManagerEditor.manager.SaveModules.Clear();
                SMSaveModuleBase[] Base = GameObject.FindObjectsByType<SMSaveModuleBase>(FindObjectsInactive.Include, FindObjectsSortMode.InstanceID);
                SettingsManagerEditor.manager.SaveModules.AddRange(Base);
            }
            EditorGUILayout.EndHorizontal();

            for (int Index = 0; Index < SettingsManagerEditor.manager.SaveModules.Count; Index++)
            {
                DrawSaveModule(Index);
            }
        }
        private static void DrawSaveModule(int index)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Save Module", SettingsmanagerStyle.DescriptorStyling);
            SettingsManagerEditor.manager.SaveModules[index] = (SMSaveModuleBase)EditorGUILayout.ObjectField(string.Empty, SettingsManagerEditor.manager.SaveModules[index], typeof(SMSaveModuleBase), true);
            if (GUILayout.Button("Remove Option", SettingsmanagerStyle.DescriptorStyling))
            {
                SettingsManagerEditor.manager.SaveModules.RemoveAt(index);
            }
            EditorGUILayout.EndHorizontal();
        }
        private static void RebuildManagerUI()
        {
            bool hasValue = false;
            foreach (var ui in SettingsManagerEditor.manager.SettingsManagerAbstractTypeManagement.Where(ui => ui != null))
            {
                ui.RebuildFromobject(SettingsManagerEditor.manager, ref SettingsManagerEditor.manager.ManagerSettings.ExplanationText, out hasValue);
            }
        }
        private static void ShowCultureInfo()
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Culture Info", SettingsmanagerStyle.DescriptorStyling);
            var cultureNames = Enum.GetNames(typeof(SettingsManagerEnums.CultureType)).ToList();
            SettingsManagerEditor.manager.ManagerSettings.CType = (SettingsManagerEnums.CultureType)EditorGUILayout.Popup((int)SettingsManagerEditor.manager.ManagerSettings.CType, cultureNames.ToArray(), SettingsmanagerStyle.EnumStyling);
            switch (SettingsManagerEditor.manager.ManagerSettings.CType)
            {
                case SettingsManagerEnums.CultureType.CurrentCulture:
                    SettingsManagerEditor.manager.ManagerSettings.CInfo = CultureInfo.CurrentCulture;
                    break;
                case SettingsManagerEnums.CultureType.CurrentUICulture:
                    SettingsManagerEditor.manager.ManagerSettings.CInfo = CultureInfo.CurrentUICulture;
                    break;
                case SettingsManagerEnums.CultureType.InstalledUICulture:
                    SettingsManagerEditor.manager.ManagerSettings.CInfo = CultureInfo.InstalledUICulture;
                    break;
                case SettingsManagerEnums.CultureType.InvariantCulture:
                    SettingsManagerEditor.manager.ManagerSettings.CInfo = CultureInfo.InvariantCulture;
                    break;
            }
            EditorGUILayout.EndHorizontal();
        }
        private static void ShowFileName()
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("File Name", SettingsmanagerStyle.DescriptorStyling);
            SettingsManagerEditor.manager.ManagerSettings.FileName = EditorGUILayout.TextField(string.Empty, SettingsManagerEditor.manager.ManagerSettings.FileName, SettingsmanagerStyle.ValueStyling);
            EditorGUILayout.EndHorizontal();
        }
        private static void ShowLocationAndDeleteFile()
        {
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button($"Show All Save locations", SettingsmanagerStyle.ButtonStyling))
            {
                foreach (var Module in SettingsManagerEditor.manager.SaveModules)
                {
                    SettingsManagerEditor.manager.SaveSystem ??= new SaveSystem.SettingsManagerSaveSystem();
                    string Path = Module.Location(SettingsManagerEditor.manager, SettingsManagerEditor.manager.SaveSystem);
                    if (string.IsNullOrEmpty(Path) == false)
                    {
                        EditorUtility.RevealInFinder(Path);
                    }
                }
            }
            if (GUILayout.Button($"Delete All Save Data", SettingsmanagerStyle.ButtonStyling))
            {
                foreach (var Module in SettingsManagerEditor.manager.SaveModules)
                {
                    SettingsManagerEditor.manager.SaveSystem ??= new SaveSystem.SettingsManagerSaveSystem();
                    if (Module.Delete(SettingsManagerEditor.manager, SettingsManagerEditor.manager.SaveSystem))
                    {
                        DebugSystem.SettingsManagerDebug.Log("Deleted Save For System " + Module.ModuleName());
                    }
                    else
                    {
                        DebugSystem.SettingsManagerDebug.Log("Failed To Delete Save For " + Module.ModuleName());
                    }
                }
                ClearSelectedValues();
            }
            EditorGUILayout.EndHorizontal();
        }
        private static void ClearSelectedValues()
        {
            foreach (var option in SettingsManagerEditor.manager.Options)
            {
                option.SelectedValue = string.Empty;
                option.SelectedValueDefault = string.Empty;
            }
        }
    }
}