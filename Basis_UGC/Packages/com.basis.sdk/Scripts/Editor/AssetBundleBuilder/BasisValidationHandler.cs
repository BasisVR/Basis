using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public class BasisValidationHandler
{
    public static bool IsValidPrefab(GameObject prefab)
    {
        if (prefab == null)
        {
            BasisDebug.LogError("Prefab is null.");
            return false;
        }

        if (!PrefabUtility.IsPartOfPrefabInstance(prefab) && !PrefabUtility.IsPartOfPrefabAsset(prefab))
        {
            BasisDebug.Log($"GameObject '{prefab.name}' is not part of a prefab.");
            return false;
        }

        return true;
    }

    public static bool IsSceneValid(Scene scene)
    {
        if(scene.isDirty)
        {
            BasisDebug.Log("Saving Open Scene");
           EditorSceneManager.SaveScene(scene);
        }
        if (string.IsNullOrEmpty(scene.path))
        {
            BasisDebug.LogError("Scene Path was empty. Make sure the scene has been saved!");
            return false;
        }

        return true;
    }
}
