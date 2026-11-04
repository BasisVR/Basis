using Unity.Scripting.LifecycleManagement;
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
[NoAutoStaticsCleanup]
public static class BasisValidationAssetCache
{
    private static readonly Dictionary<EntityId, string> AssetPaths = new Dictionary<EntityId, string>(128);
    private static readonly Dictionary<string, AssetImporter> Importers = new Dictionary<string, AssetImporter>(128);
    private static readonly Dictionary<EntityId, int[]> ShaderTextureProperties = new Dictionary<EntityId, int[]>(32);
    private static readonly List<int> PropertyScratch = new List<int>(32);
    private static readonly Dictionary<BuildTarget, bool> ScriptingBackends = new Dictionary<BuildTarget, bool>(8);

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

    public static bool ScriptingBackendInstalled(BuildTarget target)
    {
        if (!ScriptingBackends.TryGetValue(target, out bool installed))
        {
            try
            {
                installed = BuildPipeline.IsBuildTargetSupported(BuildPipeline.GetBuildTargetGroup(target), target) && (BasisSDKConstants.ScriptingBackendFor(target) != ScriptingImplementation.CoreCLR || Directory.Exists(Path.Combine(BuildPipeline.GetPlaybackEngineDirectory(target, BuildOptions.None), "Variations", "coreclr", "Managed")));
            }
            catch (Exception)
            {
                installed = true;
            }
            ScriptingBackends[target] = installed;
        }
        return installed;
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
