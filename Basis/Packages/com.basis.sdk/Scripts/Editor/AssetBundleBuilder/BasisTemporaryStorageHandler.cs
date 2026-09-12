using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
public static class TemporaryStorageHandler
{
    public static string SavePrefabToTemporaryStorage(GameObject prefab, BasisAssetBundleObject settings, ref bool wasModified, out string uniqueID)
    {
        EnsureDirectoryExists(settings.TemporaryStorage);
        PersistTransientRendererMaterials(prefab, settings.TemporaryStorage);

        uniqueID = BasisGenerateUniqueID.GenerateUniqueID();
        string prefabPath = Path.Combine(settings.TemporaryStorage, $"{uniqueID}.prefab");
        prefab = PrefabUtility.SaveAsPrefabAsset(prefab, prefabPath);
        wasModified = true;
        return prefabPath;
    }

    /// <summary>
    /// Prefab assets cannot safely retain references to in-memory Material instances. Build processors
    /// are allowed to clone/modify materials on the isolated prefab, so persist those transient clones
    /// into the same temporary storage before crossing the prefab serialization boundary.
    /// </summary>
    private static void PersistTransientRendererMaterials(GameObject prefab, string temporaryStorage)
    {
        if (prefab == null)
        {
            throw new ArgumentNullException(nameof(prefab));
        }

        var remappedMaterials = new Dictionary<EntityId, Material>();
        Renderer[] renderers = prefab.GetComponentsInChildren<Renderer>(true);
        for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
        {
            Renderer renderer = renderers[rendererIndex];
            if (renderer == null)
            {
                continue;
            }

            Material[] materials = renderer.sharedMaterials;
            bool changed = false;
            for (int slot = 0; slot < materials.Length; slot++)
            {
                Material material = materials[slot];
                if (material == null)
                {
                    continue;
                }

                EntityId entityId = material.GetEntityId();
                if (remappedMaterials.TryGetValue(entityId, out Material remapped))
                {
                    materials[slot] = remapped;
                    changed = true;
                    continue;
                }

                if (EditorUtility.IsPersistent(material))
                {
                    continue;
                }

                Material persisted = PersistMaterial(material, temporaryStorage);
                remappedMaterials.Add(entityId, persisted);
                materials[slot] = persisted;
                changed = true;
            }

            if (changed)
            {
                renderer.sharedMaterials = materials;
                EditorUtility.SetDirty(renderer);
            }
        }
    }

    private static Material PersistMaterial(Material source, string temporaryStorage)
    {
        string sourceName = source.name;
        string assetName = SanitizeAssetFileName(sourceName);
        string path = AssetDatabase.GenerateUniqueAssetPath(
            Path.Combine(temporaryStorage, $"{assetName}.mat").Replace('\\', '/'));
        HideFlags originalHideFlags = source.hideFlags;

        try
        {
            // Persist the generated instance itself rather than cloning it again. Any other references
            // to this same build-time material then become durable as well.
            source.hideFlags = HideFlags.None;
            AssetDatabase.CreateAsset(source, path);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            Material loaded = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (loaded == null || !EditorUtility.IsPersistent(loaded))
            {
                throw new InvalidOperationException($"Unity did not persist the generated material at '{path}'.");
            }

            return loaded;
        }
        catch (Exception ex)
        {
            if (source != null && EditorUtility.IsPersistent(source))
            {
                AssetDatabase.DeleteAsset(path);
            }
            else if (AssetDatabase.LoadAssetAtPath<Material>(path) != null)
            {
                AssetDatabase.DeleteAsset(path);
            }
            else if (source != null)
            {
                source.hideFlags = originalHideFlags;
            }

            throw new InvalidOperationException(
                $"Failed to persist transient build material '{sourceName}' before saving the staged prefab. " +
                "The build was stopped rather than serializing a missing material reference.", ex);
        }
    }

    private static string SanitizeAssetFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "GeneratedMaterial";
        }

        char[] invalidChars = Path.GetInvalidFileNameChars();
        char[] chars = value.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalidChars, chars[i]) >= 0 || chars[i] == '/' || chars[i] == '\\')
            {
                chars[i] = '_';
            }
        }

        string sanitized = new string(chars).Trim();
        return string.IsNullOrEmpty(sanitized) ? "GeneratedMaterial" : sanitized;
    }
    // SaveScene lived here, but it handed the build the scene's original path while only the bundle
    // name was unique, which is what let two worlds built from one scene collide. Scenes are now
    // staged by BasisSceneBuildName instead.
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
