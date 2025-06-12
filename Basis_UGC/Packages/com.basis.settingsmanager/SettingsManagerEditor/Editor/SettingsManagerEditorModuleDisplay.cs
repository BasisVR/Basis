using BattlePhaze.SettingsManager.Style;
using UnityEditor;
using UnityEngine;
namespace BattlePhaze.SettingsManager
{
    public static class SettingManagerEditorModuleDisplay
    {
        /// <summary>
        /// Displays settings for the modules
        /// </summary>
        public static void DisplayModule(SettingsManagerEditor editor)
        {
            string foldoutText = editor.ModuleSettingsVisible ? $"{SettingsmanagerStyle.DropDownOpen} Module Settings" : $"{SettingsmanagerStyle.DropDownClosed} Module Settings";
            if (GUILayout.Button(foldoutText, SettingsmanagerStyle.FoldoutHeaderLarge))
            {
                editor.ModuleSettingsVisible = !editor.ModuleSettingsVisible;
            }

            if (!editor.ModuleSettingsVisible)
            {
                return;
            }

            if (GUILayout.Button("Add New Settings Module", SettingsmanagerStyle.ButtonStyling))
            {
                SettingsManagerModuleSystem.AddSettingsModule(SettingsManagerEditor.manager);
            }

            for (int i = 0; i < SettingsManagerEditor.manager.SettingsManagerOptions.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Integration Module", SettingsmanagerStyle.DescriptorStyling);
                SettingsManagerOption option = (SettingsManagerOption)EditorGUILayout.ObjectField(string.Empty, SettingsManagerEditor.manager.SettingsManagerOptions[i], typeof(SettingsManagerOption), true);
                if (option != SettingsManagerEditor.manager.SettingsManagerOptions[i])
                {
                    SettingsManagerEditor.manager.SettingsManagerOptions[i] = option;
                    EditorUtility.SetDirty(SettingsManagerEditor.manager);
                }

                if (GUILayout.Button("Remove", SettingsmanagerStyle.ButtonCompactStyling))
                {
                    SettingsManagerModuleSystem.RemoveModule(i, SettingsManagerEditor.manager);
                }

                EditorGUILayout.EndHorizontal();
            }

            if (GUILayout.Button("Auto assign Scene modules", SettingsmanagerStyle.ButtonStyling))
            {
                SettingsManagerModuleSystem.AutoAssignModules(SettingsManagerEditor.manager);
            }

            if (GUILayout.Button("Sort Modules", SettingsmanagerStyle.ButtonStyling))
            {
                SettingsManagerModuleSystem.SortModules(SettingsManagerEditor.manager);
            }
        }
    }
}