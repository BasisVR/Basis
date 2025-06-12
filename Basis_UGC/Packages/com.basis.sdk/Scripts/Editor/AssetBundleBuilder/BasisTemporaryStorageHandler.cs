using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
//taken from UnityEngine.Rendering.SceneExtensions
internal static class SceneExtensions
{
    static PropertyInfo s_SceneGUID = typeof(Scene).GetProperty("guid", BindingFlags.NonPublic | BindingFlags.Instance);
    public static string GetGUID(this Scene scene)
    {
        Debug.Assert(s_SceneGUID != null, "Reflection for scene GUID failed");
        return (string)s_SceneGUID.GetValue(scene);
    }
}

public static class TemporaryStorageHandler
{
    public static string SavePrefabToTemporaryStorage(GameObject prefab, BasisAssetBundleObject settings, ref bool wasModified, out string uniqueID)
    {
        EnsureDirectoryExists(settings.TemporaryStorage);
        uniqueID = BasisGenerateUniqueID.GenerateUniqueID();
        string prefabPath = Path.Combine(settings.TemporaryStorage, $"{uniqueID}.prefab");
        prefab = PrefabUtility.SaveAsPrefabAsset(prefab, prefabPath);
        wasModified = true;

        return prefabPath;
    }
    public static string SaveScene(Scene sceneToCopy, BasisAssetBundleObject settings, out string uniqueID)
    {
        // Generate a unique ID
        uniqueID = BasisGenerateUniqueID.GenerateUniqueID();

        // Attempt to save the scene
        if (EditorSceneManager.SaveScene(sceneToCopy))
        {
            // Return the path it was saved to
            return sceneToCopy.path;
        }

        // If save fails, clear the ID and return null
        uniqueID = null;
        return null;
    }
    public static string SaveSceneToTemporaryStorage(Scene sceneToCopy, BasisAssetBundleObject settings, out string uniqueID)
    {
      //  string actualScenePath = sceneToCopy.path;
        EnsureDirectoryExists(settings.TemporaryStorage);

        uniqueID = BasisGenerateUniqueID.GenerateUniqueID();
        string tempScenePath = Path.Combine(settings.TemporaryStorage, $"{uniqueID}.unity");

        if (EditorSceneManager.SaveScene(sceneToCopy, tempScenePath, true))
        {
            //   Scene tempScene = EditorSceneManager.OpenScene(tempScenePath, OpenSceneMode.Single);
            // ProcessSceneProbeVolume(tempScene);
            // EditorSceneManager.SaveScene(tempScene);

            // EditorSceneManager.OpenScene(actualScenePath, OpenSceneMode.Single);
            return tempScenePath;
        }

        return null;
    }
    private static void ProcessSceneProbeVolume(Scene scene)
    {
        List<GameObject> rootObjects = new List<GameObject>();
        scene.GetRootGameObjects(rootObjects);
        string GUID = scene.GetGUID();
        foreach (GameObject obj in rootObjects)
        {
            if (obj.TryGetComponent(out ProbeVolumePerSceneData probeVolumeData))
            {
                BasisDebug.Log("Found ProbeVolumePerSceneData");
                SetSceneGUID(probeVolumeData, GUID);
                CloneAndAssignBakingSet(probeVolumeData, GUID);
                EditorUtility.SetDirty(probeVolumeData);
            }
        }
    }
    private static void SetSceneGUID(ProbeVolumePerSceneData data, string GetGUID)
    {
        var guidField = typeof(ProbeVolumePerSceneData).GetField("sceneGUID", BindingFlags.NonPublic | BindingFlags.Instance);
        if (guidField != null)
        {
            guidField.SetValue(data, GetGUID);
            BasisDebug.Log($"Set ProbeVolumePerSceneData GUID To {GetGUID}");
        }
    }
    private static void CloneAndAssignBakingSet(ProbeVolumePerSceneData data, string ReplaceWithSceneGUID)
    {
        ProbeVolumeBakingSet originalSet = data.bakingSet;
        ProbeVolumeBakingSet duplicateSet = CloneAndUpdateSceneGUID(originalSet, ReplaceWithSceneGUID);

        FieldInfo serializedBakingSetField = typeof(ProbeVolumePerSceneData).GetField("serializedBakingSet", BindingFlags.NonPublic | BindingFlags.Instance);
        if (serializedBakingSetField != null)
        {
            serializedBakingSetField.SetValue(data, duplicateSet);
            BasisDebug.Log("Successfully set internal serializedBakingSet");
        }
        else
        {
            BasisDebug.LogError("Failed to find serializedBakingSet field via reflection");
        }
    }
    [Serializable]
    public struct SerializedPerSceneCellList
    {
        public string sceneGUID;
        public List<int> cellList;
    }

    public static ProbeVolumeBakingSet CloneAndUpdateSceneGUID(ProbeVolumeBakingSet originalSet, string ReplaceWithSceneGUID)
    {
        ProbeVolumeBakingSet newSet = new ProbeVolumeBakingSet();
        FieldInfo[] fields = typeof(ProbeVolumeBakingSet).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);

        // Clone fields from the originalSet to newSet
        foreach (FieldInfo field in fields)
        {
            object value = field.GetValue(originalSet);
            field.SetValue(newSet, value);
        }

        // Update scene GUIDs in the cloned set
        for (int index = 0; index < newSet.sceneGUIDs.Count; index++)
        {
            string sceneGUID = newSet.sceneGUIDs[index];
            BasisDebug.Log("Scene ID was " + sceneGUID);

            // Use reflection to update the scene GUID list in the new set
            UpdateSceneGUIDUsingReflection(newSet, index, ReplaceWithSceneGUID);
        }

        // Reflection to replace sceneGUIDs in m_SerializedPerSceneCellList
        // Get the protected/private field m_SerializedPerSceneCellList
        FieldInfo serializedPerSceneCellListField = typeof(ProbeVolumeBakingSet).GetField("m_SerializedPerSceneCellList", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);

        if (serializedPerSceneCellListField != null)
        {
            // Get the value of m_SerializedPerSceneCellList (which should be a List<SerializedPerSceneCellList>)
            var serializedPerSceneCellList = serializedPerSceneCellListField.GetValue(newSet) as List<SerializedPerSceneCellList>;

            if (serializedPerSceneCellList != null)
            {
                // Iterate over the list and update the sceneGUID for each SerializedPerSceneCellList item
                for (int i = 0; i < serializedPerSceneCellList.Count; i++)
                {
                    var item = serializedPerSceneCellList[i];
                    // Use reflection to access and set the sceneGUID in the struct
                    FieldInfo sceneGUIDField = item.GetType().GetField("sceneGUID", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (sceneGUIDField != null)
                    {
                        BasisDebug.Log($"Set m_SerializedPerSceneCellList Scene GUID to {ReplaceWithSceneGUID}");
                        sceneGUIDField.SetValueDirect(__makeref(item), ReplaceWithSceneGUID);  // Set value for the struct
                    }
                }
            }
        }

        return newSet;
    }


    private static void UpdateSceneGUIDUsingReflection(ProbeVolumeBakingSet newSet, int index, string ReplaceWithSceneGUID)
    {
        // Assuming UpdateSceneGUIDUsingReflection updates the scene GUID of a specific index in some field of newSet
        // If the m_SerializedPerSceneCellList has a corresponding reference, this would update that
        FieldInfo sceneGUIDListField = typeof(ProbeVolumeBakingSet).GetField("sceneGUIDs", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.FlattenHierarchy);

        if (sceneGUIDListField != null)
        {
            var sceneGUIDs = (List<string>)sceneGUIDListField.GetValue(newSet);
            if (sceneGUIDs != null && index < sceneGUIDs.Count)
            {
                sceneGUIDs[index] = ReplaceWithSceneGUID;
                BasisDebug.Log($"Set sceneGUIDs Scene GUID to {ReplaceWithSceneGUID}");
                sceneGUIDListField.SetValue(newSet, sceneGUIDs);
            }
        }
    }
    /// <summary>
    /// Deletes the scene from the temporary storage if it exists.
    /// </summary>
    /// <param name="uniqueID">The unique ID of the scene to delete.</param>
    /// <param name="settings">An object containing temporary storage path settings.</param>
    /// <returns>Returns true if the file was successfully deleted, false if it does not exist or failed to delete.</returns>
    public static bool DeleteTemporaryStorageScene(string uniqueID, BasisAssetBundleObject settings)
    {
        string tempScenePath = Path.Combine(settings.TemporaryStorage, $"{uniqueID}.unity");

        if (File.Exists(tempScenePath))
        {
            try
            {
                File.Delete(tempScenePath);
                return true;
            }
            catch (IOException ex)
            {
                Debug.LogError($"Failed to delete scene file: {ex.Message}");
                return false;
            }
        }

        Debug.LogWarning("Scene file not found.");
        return false;
    }
    public static void EnsureDirectoryExists(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }
    }
    public static void ClearTemporaryStorage(string tempStoragePath)
    {
        if (Directory.Exists(tempStoragePath))
        {
            Directory.Delete(tempStoragePath, true);
        }
    }
}
