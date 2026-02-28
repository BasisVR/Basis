#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

public static class CreateURPBlitMaterial
{
    [MenuItem("Tools/URP/Create SRP Blit Material")]
    public static void Create()
    {
        // URP/Core package shader name (works in recent URP versions)
        Shader shader = Shader.Find("Hidden/Universal Render Pipeline/Blit");

        // Fallbacks across versions/packages
        if (shader == null) shader = Shader.Find("Hidden/BlitCopy");
        if (shader == null) shader = Shader.Find("Hidden/CoreBlit"); // some SRP variants

        if (shader == null)
        {
            Debug.LogError(
                "Could not find a URP/Core blit shader. " +
                "Make sure URP is installed/enabled, then search your project for 'Blit' shaders."
            );
            return;
        }

        var mat = new Material(shader);

        const string path = "Assets/SRP_Blit_Material.mat";
        AssetDatabase.CreateAsset(mat, path);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Selection.activeObject = mat;
        EditorGUIUtility.PingObject(mat);

        Debug.Log($"Created SRP blit material at: {path} (shader: {shader.name})");
    }
}
#endif
