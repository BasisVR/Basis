using System;
using System.Collections.Generic;
using System.Linq;
using Basis.Editor.Localization;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(ContentPoliceSelector))]
public class BasisContentPoliceSelectorEditor : Editor
{
    [NonSerialized] public List<Type> monoBehaviourTypes;
    public string[] typeNames;
    public bool[] selectedFlags;
    public ContentPoliceSelector selector;

    private string searchQuery = ""; // <-- search filter

    public void OnEnable()
    {
        // Get all MonoBehaviour types in the project
        monoBehaviourTypes = UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies()
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
        BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.contentPolice.allowedComponents"));

        serializedObject.Update();
        EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(ContentPoliceSelector.IncludePackageApprovals)));
        EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(ContentPoliceSelector.Kind)));
        serializedObject.ApplyModifiedProperties();
        IReadOnlyDictionary<string, string> fromPackages = selector.IncludePackageApprovals ? BasisContentPoliceInclusions.Get(selector.Kind) : null;

        // 🔍 Search bar
        EditorGUILayout.BeginHorizontal();
        GUIContent searchLabel = new GUIContent(BasisEditorLocalization.Get("editor.contentPolice.search"));
        EditorGUILayout.LabelField(searchLabel, GUILayout.Width(Mathf.Max(50f, EditorStyles.label.CalcSize(searchLabel).x)));
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

            if (!selectedFlags[index] && fromPackages != null && fromPackages.TryGetValue(typeName, out string source))
            {
                using (new EditorGUI.DisabledScope(true)) EditorGUILayout.ToggleLeft(BasisEditorLocalization.Get("editor.contentPolice.fromPackage", typeName, source), true);
                continue;
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
