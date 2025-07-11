using System;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;
public static class BasisAssetBundlePipeline
{
    // Define static delegates
    public delegate void BeforeBuildGameobjectHandler(GameObject prefab, BasisAssetBundleObject settings);
    public delegate void BeforeBuildSceneHandler(Scene prefab, BasisAssetBundleObject settings);
    public delegate void AfterBuildHandler(string assetBundleName);
    public delegate void BuildErrorHandler(Exception ex, GameObject prefab, bool wasModified, string temporaryStorage);

    // Static delegates
    public static BeforeBuildGameobjectHandler OnBeforeBuildPrefab;
    public static AfterBuildHandler OnAfterBuildPrefab;
    public static BuildErrorHandler OnBuildErrorPrefab;

    public static BeforeBuildSceneHandler OnBeforeBuildScene;
    public static AfterBuildHandler OnAfterBuildScene;
    public static BuildErrorHandler OnBuildErrorScene;
    public static async Task<(bool, (BasisBundleGenerated, AssetBundleBuilder.InformationHash))> BuildAssetBundle(GameObject originalPrefab, BasisAssetBundleObject settings, string Password, BuildTarget Target)
    {
        return await BuildAssetBundle(false, originalPrefab, new Scene(), settings, Password, Target);
    }

    public static async Task<(bool, (BasisBundleGenerated, AssetBundleBuilder.InformationHash))> BuildAssetBundle(Scene scene, BasisAssetBundleObject settings, string Password, BuildTarget Target)
    {
        return await BuildAssetBundle(true, null, scene, settings, Password, Target);
    }
    public static async Task<(bool, (BasisBundleGenerated, AssetBundleBuilder.InformationHash))> BuildAssetBundle(bool isScene, GameObject asset, Scene scene, BasisAssetBundleObject settings, string Password, BuildTarget Target)
    {
#if UNITY_EDITOR_LINUX
        ScriptingImplementation ResetTo = ScriptingImplementation.Mono2x;
#else
        ScriptingImplementation ResetTo = ScriptingImplementation.IL2CPP;
#endif
        if (EditorUserBuildSettings.activeBuildTarget != Target)
        {
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildPipeline.GetBuildTargetGroup(Target), Target);
        }
        string targetDirectory = Path.Combine(settings.AssetBundleDirectory, Target.ToString());
        TemporaryStorageHandler.ClearTemporaryStorage(targetDirectory);
        TemporaryStorageHandler.EnsureDirectoryExists(targetDirectory);

        bool wasModified = false;
        string assetPath = null;
        string uniqueID = null;
        GameObject prefab = null;
        try
        {
            if (isScene)
            {
                OnBeforeBuildScene?.Invoke(scene, settings);
                assetPath = TemporaryStorageHandler.SaveScene(scene, settings, out uniqueID);
            }
            else
            {
                prefab = Object.Instantiate(asset);
                OnBeforeBuildPrefab?.Invoke(prefab, settings);
                assetPath = TemporaryStorageHandler.SavePrefabToTemporaryStorage(prefab, settings, ref wasModified, out uniqueID);

                if (prefab != null)
                {
                    GameObject.DestroyImmediate(prefab);
                }
            }
            AssetBundleBuild Build =  new AssetBundleBuild() {  assetBundleName = uniqueID, assetNames = new string[] { assetPath } };
            AssetBundleBuild[] Builds = new AssetBundleBuild[] { Build };
            (BasisBundleGenerated, AssetBundleBuilder.InformationHash) value = await AssetBundleBuilder.BuildAssetBundle(Builds,targetDirectory, settings, uniqueID, isScene ? "Scene" : "GameObject", Password, Target);
            TemporaryStorageHandler.ClearTemporaryStorage(settings.TemporaryStorage);
            AssetDatabase.Refresh();

            if (isScene)
            {
                OnAfterBuildScene?.Invoke(uniqueID);
            }
            else
            {
                OnAfterBuildPrefab?.Invoke(uniqueID);
            }

            BuildTarget buildTarget = EditorUserBuildSettings.activeBuildTarget;
            BuildTargetGroup targetGroup = BuildPipeline.GetBuildTargetGroup(buildTarget);
            var namedBuildTarget = UnityEditor.Build.NamedBuildTarget.FromBuildTargetGroup(targetGroup);
            if (ResetTo != PlayerSettings.GetScriptingBackend(namedBuildTarget))
            {
                PlayerSettings.SetScriptingBackend(namedBuildTarget, ResetTo);
            }
            return new(true, value);
        }
        catch (Exception ex)
        {
            if (isScene)
            {
                OnBuildErrorScene?.Invoke(ex, null, false, settings.TemporaryStorage);
                Debug.LogError($"Error while building AssetBundle from scene: {ex.Message}\n{ex.StackTrace}");
            }
            else
            {
                OnBuildErrorPrefab?.Invoke(ex, asset, wasModified, settings.TemporaryStorage);
                BasisBundleErrorHandler.HandleBuildError(ex, asset, wasModified, settings.TemporaryStorage);
                EditorUtility.DisplayDialog("Failed To Build", "Please check the console for the full issue: " + ex, "Will do");
            }
            BuildTarget buildTarget = EditorUserBuildSettings.activeBuildTarget;
            BuildTargetGroup targetGroup = BuildPipeline.GetBuildTargetGroup(buildTarget);
            var namedBuildTarget = UnityEditor.Build.NamedBuildTarget.FromBuildTargetGroup(targetGroup);
            if (ResetTo != PlayerSettings.GetScriptingBackend(namedBuildTarget))
            {
                PlayerSettings.SetScriptingBackend(namedBuildTarget, ResetTo);
            }
            return new(false, (null, new AssetBundleBuilder.InformationHash()));
        }
    }
}
