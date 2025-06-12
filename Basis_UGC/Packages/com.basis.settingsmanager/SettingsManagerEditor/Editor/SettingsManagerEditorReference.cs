using BattlePhaze.SettingsManager.DebugSystem;
using BattlePhaze.SettingsManager.Style;
using BattlePhaze.SettingsManager.Types;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
namespace BattlePhaze.SettingsManager
{
    public static class SettingsManagerEditorReference
    {
        public static void ObjectReferenceDisplay(SettingsManager Manager, int OptionIndex, ref UnityEngine.Object ObjectInput, string LabelType = "Option Reference")
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(LabelType, SettingsmanagerStyle.DescriptorStyling);

            if (ObjectInput == null && LabelType == "Option Reference")
            {
                List<SettingsManagerObject> ManagerObjects = new List<SettingsManagerObject>
                {
                    null
                };
                List<string> ManagerObjectsPaths = new List<string>
                {
                    string.Empty
                };

                foreach (var manager in Manager.ManagerSettings.SettingsManagerObjects)
                {
                    if (manager != null && manager.Type == Manager.Options[OptionIndex].Type)
                    {
                        ManagerObjects.Add(manager);
                        ManagerObjectsPaths.Add(Manager.ManagerSettings.SettingsManagerObjectpaths[Manager.ManagerSettings.SettingsManagerObjects.IndexOf(manager)]);
                    }
                }

                if (ManagerObjectsPaths.Count > 0)
                {
                    int selectedIndex = EditorGUILayout.Popup(0, ManagerObjectsPaths.ToArray());

                    if (selectedIndex > 0)
                    {
                        GameObject canvas;
                        if (SettingsManagerEditor.manager.ManagerSettings.ActiveCanvasLikeObject == null)
                        {
                            canvas = (GameObject)SettingsManagerEditor.manager.ManagerSettings.ActiveCanvasLikeObject;
                        }
                        else
                        {
                            canvas = GameObject.FindFirstObjectByType<Canvas>().gameObject;
                        }

                        if (canvas != null && !string.IsNullOrEmpty(ManagerObjectsPaths[selectedIndex]))
                        {
                            var uiObject = SettingsManagerOptionInputCreator.SpawnCreatedObject(ManagerObjectsPaths[selectedIndex], canvas.transform);

                            if (uiObject != null)
                            {
                                uiObject.Initalize(Manager.Options[OptionIndex]);
                                GameObject.Destroy(uiObject);
                            }
                            else
                            {
                                SettingsManagerDebug.LogError("Object Was Null");
                            }
                        }
                        else
                        {
                            SettingsManagerDebug.LogError("Canvas Could not be found!");
                        }
                    }
                }
            }

            if (SettingsManagerTypesHelper.TypeCompare(ObjectInput, typeof(GameObject)))
            {
                DebugSystem.SettingsManagerDebug.Log(ObjectInput.name + " is a Gameobject! Reverse Configuring");
                RebuildFromGameobject(Manager, OptionIndex, ref ObjectInput);
            }
            ObjectInput = EditorGUILayout.ObjectField(ObjectInput, typeof(UnityEngine.Object), true, GUILayout.MinWidth(150));
            if (ObjectInput != null)
            {
                GUILayout.Label("Type is [" + ObjectInput.GetType().ToString().Replace("UnityEngine.", string.Empty) + "]", SettingsmanagerStyle.DescriptorStyling);
            }
            EditorGUILayout.EndHorizontal();
        }
        public static void RebuildFromGameobject(SettingsManager Manager, int OptionIndex, ref UnityEngine.Object ObjectInput)
        {
            for (int AttachedManagerIndex = 0; AttachedManagerIndex < Manager.SettingsManagerAbstractTypeManagement.Count; AttachedManagerIndex++)
            {
                Manager.SettingsManagerAbstractTypeManagement[AttachedManagerIndex].RebuildFromGameobject(Manager, OptionIndex, ref ObjectInput, out bool Success);
                if (Success)
                {
                    return;
                }
            }
        }
        public static void HoverExplanationSystem(SettingsManager Manager, int OptionsIndex, string Label)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(Label, SettingsmanagerStyle.DescriptorStyling);
            Manager.Options[OptionsIndex].Explanation = EditorGUILayout.TextField(string.Empty, Manager.Options[OptionsIndex].Explanation, SettingsmanagerStyle.ValueStyling);
            EditorGUILayout.EndHorizontal();
        }
    }
}
