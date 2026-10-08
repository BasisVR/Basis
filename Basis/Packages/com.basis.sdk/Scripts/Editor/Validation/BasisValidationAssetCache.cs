using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Editor-lifetime cache for the AssetDatabase work the SDK validators would otherwise repeat on
/// every pass.
///
/// <para>The validators resolve an asset path and an importer for every texture and every mesh they
/// look at. Both walk the asset database, and <c>AssetImporter.GetAtPath</c> deserializes the
/// <c>.meta</c> behind it, which is why an avatar with a few dozen textures made its own inspector
/// the most expensive thing on screen.</para>
///
/// <para>Everything here is dropped wholesale the moment anything is imported, moved or deleted —
/// including by the validators' own auto-fixes, which reimport — so a stale importer is never
/// handed out. <see cref="Invalidated"/> lets open inspectors know they should look again.</para>
/// </summary>
public static class BasisValidationAssetCache
{
    private static readonly Dictionary<EntityId, string> AssetPaths = new Dictionary<EntityId, string>(128);
    private static readonly Dictionary<string, AssetImporter> Importers = new Dictionary<string, AssetImporter>(128);
    private static readonly Dictionary<EntityId, int[]> ShaderTextureProperties = new Dictionary<EntityId, int[]>(32);
    private static readonly List<int> PropertyScratch = new List<int>(32);

    // 0 = not looked yet, 1 = present, 2 = missing. The lookup is a disk hit and the answer cannot
    // change without restarting the editor, so it is worth doing exactly once.
    private static int _il2cppState;

    /// <summary>Raised after an import invalidates the cache, so open validators can re-run.</summary>
    public static event Action Invalidated;

    public static void Invalidate()
    {
        AssetPaths.Clear();
        Importers.Clear();
        ShaderTextureProperties.Clear();

        try
        {
            Invalidated?.Invoke();
        }
        catch (Exception e)
        {
            Debug.LogError($"[BasisValidationAssetCache] Invalidated handler threw: {e}");
        }
    }

    public static string PathOf(UnityEngine.Object asset)
    {
        if (asset == null)
        {
            return string.Empty;
        }

        EntityId key = asset.GetEntityId();
        if (AssetPaths.TryGetValue(key, out string path))
        {
            return path;
        }

        path = AssetDatabase.GetAssetPath(asset) ?? string.Empty;
        AssetPaths[key] = path;
        return path;
    }

    /// <summary>
    /// Importer at a path, or null when the path has none of that type. Negative results are cached
    /// too — a scene mesh with no importer must not cost a database lookup on every pass either.
    /// </summary>
    public static T ImporterAt<T>(string path) where T : AssetImporter
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        if (!Importers.TryGetValue(path, out AssetImporter importer))
        {
            importer = AssetImporter.GetAtPath(path);
            Importers[path] = importer;
        }

        return importer as T;
    }

    public static T ImporterFor<T>(UnityEngine.Object asset) where T : AssetImporter
    {
        return ImporterAt<T>(PathOf(asset));
    }

    /// <summary>
    /// Property ids of every texture slot a shader declares.
    ///
    /// <para>Ids rather than names: the caller wants <c>Material.GetTexture</c>, and the name
    /// overload hashes the string on every call while <c>Shader.GetPropertyName</c> allocates one
    /// to begin with. Reflecting over a Poiyomi-class shader is hundreds of properties, so this is
    /// the difference between a real cost and none.</para>
    /// </summary>
    public static int[] TexturePropertyIds(Shader shader)
    {
        if (shader == null)
        {
            return Array.Empty<int>();
        }

        EntityId key = shader.GetEntityId();
        if (ShaderTextureProperties.TryGetValue(key, out int[] cached))
        {
            return cached;
        }

        PropertyScratch.Clear();
        int propertyCount = shader.GetPropertyCount();
        for (int Index = 0; Index < propertyCount; Index++)
        {
            if (shader.GetPropertyType(Index) == ShaderPropertyType.Texture)
            {
                PropertyScratch.Add(shader.GetPropertyNameId(Index));
            }
        }

        int[] ids = PropertyScratch.Count == 0 ? Array.Empty<int>() : PropertyScratch.ToArray();
        ShaderTextureProperties[key] = ids;
        return ids;
    }

    /// <summary>
    /// True when this editor install has no IL2CPP backend, which means the author cannot build
    /// content for platforms that require it.
    /// </summary>
    public static bool Il2CppMissing
    {
        get
        {
            if (_il2cppState == 0)
            {
                bool exists;
                try
                {
                    string unityFolder = Path.GetDirectoryName(EditorApplication.applicationPath);
                    exists = !string.IsNullOrEmpty(unityFolder) && Directory.Exists(Path.Combine(unityFolder, "Data", "il2cpp"));
                }
                catch (Exception)
                {
                    exists = true; // Cannot tell — say nothing rather than cry wolf on every pass.
                }
                _il2cppState = exists ? 1 : 2;
            }
            return _il2cppState == 2;
        }
    }

    private sealed class ImportWatcher : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] movedTo, string[] movedFrom)
        {
            if (imported.Length == 0 && deleted.Length == 0 && movedTo.Length == 0 && movedFrom.Length == 0)
            {
                return;
            }
            Invalidate();
        }
    }
}
