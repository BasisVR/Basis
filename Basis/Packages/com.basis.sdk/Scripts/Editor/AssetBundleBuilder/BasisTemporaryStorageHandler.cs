using System.IO;
using Basis.Scripts.BasisSdk;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
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
    public static string SaveAssetToTemporaryStorage(Object asset, BasisAssetBundleObject settings, out string uniqueID)
    {
        if (asset is GameObject or Transform or Component) throw new InvalidDataException("Cannot save asset of type GameObject, Transform or Component.");

        EnsureDirectoryExists(settings.TemporaryStorage);
        uniqueID = BasisGenerateUniqueID.GenerateUniqueID();
        string assetPath = Path.Combine(settings.TemporaryStorage, $"{uniqueID}.asset");
        BasisObjectReferenceContainer referenceContainer = ScriptableObject.CreateInstance<BasisObjectReferenceContainer>();
        referenceContainer.references = new[] { asset };

        AssetDatabase.CreateAsset(referenceContainer, assetPath);

        return assetPath;
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
