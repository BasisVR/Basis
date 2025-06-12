using UnityEngine;
using UnityEditor;

public class RevealHiddenObjects : Editor
{
    [MenuItem("Tools/Reveal Hidden Objects in Hierarchy")]
    private static void RevealHiddenObjectsInHierarchy()
    {
        // Get all GameObjects in the scene, including inactive ones
        GameObject[] allObjects = Resources.FindObjectsOfTypeAll<GameObject>();

        int revealedCount = 0;

        foreach (GameObject obj in allObjects)
        {
            // Skip objects that are part of the assets, not scene objects
            if (EditorUtility.IsPersistent(obj))
                continue;

            // Check if the GameObject is hidden in the hierarchy
            if ((obj.hideFlags & HideFlags.HideInHierarchy) != 0)
            {
                // Clear the HideInHierarchy flag to make it visible
                obj.hideFlags &= ~HideFlags.HideInHierarchy;
                revealedCount++;

                // Mark the object as dirty so the editor knows it has changed
                EditorUtility.SetDirty(obj);
            }
        }

        // Refresh the editor to show changes in the hierarchy
        EditorApplication.RepaintHierarchyWindow();

        Debug.Log($"Revealed {revealedCount} hidden objects in the hierarchy.");
    }
}