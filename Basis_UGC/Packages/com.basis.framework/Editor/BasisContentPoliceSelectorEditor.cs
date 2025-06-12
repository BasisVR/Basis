using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

[CustomEditor(typeof(ContentPoliceSelector))]
public class BasisContentPoliceSelectorEditor : Editor
{
    public List<Type> monoBehaviourTypes;
    public string[] typeNames;
    public bool[] selectedFlags;
    public ContentPoliceSelector selector;

    // This method is called when the object is selected or changed
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

        // Clear selectedFlags and update based on currently selected types
        for (int i = 0; i < typeNames.Length; i++)
        {
            selectedFlags[i] = selector.selectedTypes.Contains(typeNames[i]);
        }
    }

    public override void OnInspectorGUI()
    {
        EditorGUILayout.LabelField("Allowed Components Used On Avatars", EditorStyles.boldLabel);
        int TypeCount = typeNames.Length;
        // Loop through the type names and create checkboxes
        for (int Index = 0; Index < TypeCount; Index++)
        {
            bool previousFlag = selectedFlags[Index];
            selectedFlags[Index] = EditorGUILayout.ToggleLeft(typeNames[Index], selectedFlags[Index]);

            // If the state of the checkbox has changed, update the selector's selectedTypes list
            if (previousFlag != selectedFlags[Index])
            {
                if (selectedFlags[Index])
                {
                    // Add to the list if selected
                    selector.selectedTypes.Add(typeNames[Index]);
                }
                else
                {
                    // Remove from the list if unselected
                    selector.selectedTypes.Remove(typeNames[Index]);
                }

                // Mark the object as dirty so the changes are saved
                EditorUtility.SetDirty(selector);
            }
        }
    }
}
