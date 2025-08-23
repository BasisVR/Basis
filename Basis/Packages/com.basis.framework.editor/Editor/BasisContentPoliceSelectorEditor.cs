using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(ContentPoliceSelector))]
public class BasisContentPoliceSelectorEditor : Editor
{
    public List<Type> monoBehaviourTypes;
    public string[] typeNames;
    public bool[] selectedFlags;
    public ContentPoliceSelector selector;

    private string searchQuery = ""; // <-- search filter

    public void OnEnable()
    {
        // Get all MonoBehaviour types in the project
        monoBehaviourTypes = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.IsSubclassOf(typeof(UnityEngine.Component)) && !type.IsAbstract)
            .ToList();

        // Convert the type names into a string array for the dropdown
        typeNames = monoBehaviourTypes.Select(type => type.FullName).ToArray();

        // Initialize selected flags based on the existing selectedTypes in the ScriptableObject
        selector = (ContentPoliceSelector)target;
        selectedFlags = new bool[typeNames.Length];

        for (int i = 0; i < typeNames.Length; i++)
        {
            selectedFlags[i] = selector.selectedTypes.Contains(typeNames[i]);
        }
    }

    public override void OnInspectorGUI()
    {
        EditorGUILayout.LabelField("Allowed Components Used On Avatars", EditorStyles.boldLabel);

        // 🔍 Search bar
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Search:", GUILayout.Width(50));
        searchQuery = EditorGUILayout.TextField(searchQuery).ToLower();
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space();

        int typeCount = typeNames.Length;
        for (int index = 0; index < typeCount; index++)
        {
            string typeName = typeNames[index];
            if (!string.IsNullOrEmpty(searchQuery) && !typeName.ToLower().Contains(searchQuery))
            {
                continue; // Skip if it doesn't match the search
            }

            bool previousFlag = selectedFlags[index];
            selectedFlags[index] = EditorGUILayout.ToggleLeft(typeName, selectedFlags[index]);

            if (previousFlag != selectedFlags[index])
            {
                if (selectedFlags[index])
                {
                    selector.selectedTypes.Add(typeName);
                }
                else
                {
                    selector.selectedTypes.Remove(typeName);
                }

                EditorUtility.SetDirty(selector);
            }
        }
    }
}
