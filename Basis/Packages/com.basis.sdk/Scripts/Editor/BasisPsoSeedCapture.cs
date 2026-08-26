using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class BasisPsoSeedCapture
{
    [MenuItem("Basis/Build/Shaders/Capture PSO Seed", false, 364)]
    public static void Capture()
    {
        string traced = Path.Combine(Application.persistentDataPath, "GraphicsState");
        string picked = EditorUtility.OpenFilePanel("Select traced PSO cache (.gpsc)", Directory.Exists(traced) ? traced : Application.persistentDataPath, "gpsc");
        if (string.IsNullOrEmpty(picked))
        {
            return;
        }
        if (!TryResolveApi(picked, out GraphicsDeviceType api))
        {
            EditorUtility.DisplayDialog("Capture PSO Seed", $"Could not read a GraphicsDeviceType out of '{Path.GetFileName(picked)}'. Expected basis_pso.<Api>.<UnityVersion>.gpsc", "OK");
            return;
        }
        int variants = CountVariants(picked);
        if (variants == 0)
        {
            EditorUtility.DisplayDialog("Capture PSO Seed", $"'{Path.GetFileName(picked)}' loaded zero variants, so there is nothing to ship.", "OK");
            return;
        }
        string destination = $"Assets/Resources/{BasisGraphicsStatePrewarm.SeedResourceNameFor(api)}.bytes";
        Directory.CreateDirectory(Path.GetDirectoryName(destination));
        File.Copy(picked, destination, true);
        AssetDatabase.ImportAsset(destination, ImportAssetOptions.ForceUpdate);
        BasisDebug.Log($"Capture PSO Seed: {api}, {variants} variant(s), {new FileInfo(destination).Length / 1024} KB -> {destination}");
        EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<Object>(destination));
    }

    private static bool TryResolveApi(string path, out GraphicsDeviceType api)
    {
        api = default;
        string[] parts = Path.GetFileName(path).Split('.');
        return parts.Length > 1 && System.Enum.TryParse(parts[1], false, out api);
    }

    private static int CountVariants(string path)
    {
        try
        {
            GraphicsStateCollection collection = new GraphicsStateCollection();
            collection.LoadFromFile(path);
            return collection.variantCount;
        }
        catch (System.Exception e)
        {
            BasisDebug.LogWarning($"Capture PSO Seed: could not read '{path}' ({e.Message})");
            return 0;
        }
    }
}
