using System.IO;
using Basis.Editor.Localization;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class BasisPsoSeedCapture
{
    [MenuItem("Basis/Build/Shaders/Capture PSO Seed", false, 364)]
    public static void Capture()
    {
        string traced = Path.Combine(Application.persistentDataPath, "GraphicsState");
        string picked = EditorUtility.OpenFilePanel(BasisEditorLocalization.Get("sdk.psoSeed.openTitle"), Directory.Exists(traced) ? traced : Application.persistentDataPath, "gpsc");
        if (string.IsNullOrEmpty(picked))
        {
            return;
        }
        if (!TryResolveApi(picked, out GraphicsDeviceType api))
        {
            EditorUtility.DisplayDialog(BasisEditorLocalization.Get("sdk.psoSeed.title"), BasisEditorLocalization.Get("sdk.psoSeed.unknownApi", Path.GetFileName(picked)), BasisEditorLocalization.Get("editor.common.ok"));
            return;
        }
        int variants = CountVariants(picked);
        if (variants == 0)
        {
            EditorUtility.DisplayDialog(BasisEditorLocalization.Get("sdk.psoSeed.title"), BasisEditorLocalization.Get("sdk.psoSeed.noVariants", Path.GetFileName(picked)), BasisEditorLocalization.Get("editor.common.ok"));
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
        for (int i = 0; i < parts.Length; i++)
        {
            if (System.Enum.TryParse(parts[i], false, out api)) return true;
        }
        return false;
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
