using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Basis.Scripts.BasisSdk;
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
                if (settings.RebakeOcclusionCulling)
                {
                    if (settings.RebakeOcclusionCullingInThese.Contains(Target))
                    {
                        StaticOcclusionCulling.Compute();
                    }
                    else
                    {
                        StaticOcclusionCulling.Clear();
                    }
                }
                OnBeforeBuildScene?.Invoke(scene, settings);

                assetPath = TemporaryStorageHandler.SaveScene(scene, settings, out uniqueID);
            }
            else
            {
                prefab = Object.Instantiate(asset);
                DestroyEditorOnlyInAvatar(prefab);
                OnBeforeBuildPrefab?.Invoke(prefab, settings);
                PostProcessAvatar(prefab);
                assetPath = TemporaryStorageHandler.SavePrefabToTemporaryStorage(prefab, settings, ref wasModified, out uniqueID);

                if (prefab != null)
                {
                    GameObject.DestroyImmediate(prefab);
                }
            }
            AssetBundleBuild Build = new AssetBundleBuild() { assetBundleName = uniqueID, assetNames = new string[] { assetPath } };
            AssetBundleBuild[] Builds = new AssetBundleBuild[] { Build };
            (BasisBundleGenerated, AssetBundleBuilder.InformationHash) value = await AssetBundleBuilder.BuildAssetBundle(Builds, targetDirectory, settings, uniqueID, isScene ? "Scene" : "GameObject", Password, Target);
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
            if (ScriptingImplementation.Mono2x != PlayerSettings.GetScriptingBackend(namedBuildTarget))
            {
                PlayerSettings.SetScriptingBackend(namedBuildTarget, ScriptingImplementation.Mono2x);
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
            if (ScriptingImplementation.Mono2x != PlayerSettings.GetScriptingBackend(namedBuildTarget))
            {
                PlayerSettings.SetScriptingBackend(namedBuildTarget, ScriptingImplementation.Mono2x);
            }
            return new(false, (null, new AssetBundleBuilder.InformationHash()));
        }
    }

    public static void PostProcessAvatar(GameObject prefab)
    {
        if (prefab.TryGetComponent<BasisAvatar>(out BasisAvatar avatar))
        {
            var processing = avatar.ProcessingAvatarOptions;
            if (processing == null) return;

            if (!processing.doNotAutoRenameBones)
            {
                ProcessAutoRenameBones(prefab);
            }

            // We do not want to keep this data at runtime.
            avatar.ProcessingAvatarOptions = null;
        }
    }

    private static void ProcessAutoRenameBones(GameObject prefab)
    {
        if (!prefab.TryGetComponent<Animator>(out Animator animator))
        {
            return;
        }
        if (animator.avatar == null)
        {
            return;
        }

        var allHumanoidBoneTransforms = AllValidBonesOf(animator).ToHashSet();
        var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
        if (hips != null && hips.parent != null)
        {
            // Animation Rigging also fails if the "Armature" object itself has a duplicated name. Not sure why exactly.
            allHumanoidBoneTransforms.Add(hips.parent);
        }
        var allHumanoidBoneNames = allHumanoidBoneTransforms
            .Select(transform => transform.name)
            .ToHashSet();

        var allNonHumanoidBonesNamedSimilarly = prefab.GetComponentsInChildren<Transform>()
            .Where(transform => !allHumanoidBoneTransforms.Contains(transform))
            .Where(transform => allHumanoidBoneNames.Contains(transform.name))
            .ToList();

        if (allNonHumanoidBonesNamedSimilarly.Count == 0) return;

        var duplicateMessage = string.Join(", ", allNonHumanoidBonesNamedSimilarly.Select(transform => transform.name).Distinct().OrderBy(t => t));
        BasisDebug.Log($"This avatar has duplicate humanoid bone names ({duplicateMessage}), we will auto-rename them to avoid an issue caused by AnimationRigging.");

        foreach (var grouping in allNonHumanoidBonesNamedSimilarly.GroupBy(transform => transform.name))
        {
            var originalName = grouping.Key;
            var elements = grouping.ToList();
            for (var index = 0; index < elements.Count; index++)
            {
                var element = elements[index];

                var number = index + 1;
                element.name = $"{originalName}_{number}";
            }
        }
    }

    private static List<Transform> AllValidBonesOf(Animator animator)
    {
        var results = new List<Transform>();
        if (animator.avatar == null) return results;

        for (var bone = HumanBodyBones.Hips; bone < HumanBodyBones.LastBone; bone++)
        {
            var t = animator.GetBoneTransform(bone);
            if (t != null)
            {
                results.Add(t);
            }
        }

        return results;
    }

    public static void DestroyEditorOnlyInAvatar(GameObject avatar)
    {
        // We need to do this instead of iterating on avatar.transform so that we can destroy
        // the objects that we're currently iterating through.
        var transforms = Enumerable.Range(0, avatar.transform.childCount)
            .Select(i => avatar.transform.GetChild(i))
            .ToList();
        foreach (Transform t in transforms)
        {
            DestroyIfEditorOnlyRecursive(t.gameObject);
        }
    }

    private static void DestroyIfEditorOnlyRecursive(GameObject subject)
    {
        if (subject.CompareTag("EditorOnly"))
        {
            Object.DestroyImmediate(subject);
        }
        else
        {
            foreach (Transform child in subject.transform)
            {
                DestroyIfEditorOnlyRecursive(child.gameObject);
            }
        }
    }
}
