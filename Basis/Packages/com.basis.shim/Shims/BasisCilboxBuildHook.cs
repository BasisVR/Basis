#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using Basis.Scripts.BasisSdk;
using Cilbox;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Basis-owned extension point for editor preparation that must happen immediately before Cilbox
/// discovers and serializes Cilboxable behaviours. Feature packages may subscribe without adding
/// feature-specific knowledge to Basis or modifying Cilbox's public serialization API.
/// </summary>
public static class BasisCilboxBuildEvents
{
    public static event Action<Scene> OnBeforeCilboxSerialize;

    internal static void InvokeBeforeCilboxSerialize(Scene scene)
    {
        Action<Scene> handlers = OnBeforeCilboxSerialize;
        if (handlers == null)
        {
            return;
        }

        List<Exception> failures = null;
        Delegate[] invocationList = handlers.GetInvocationList();
        for (int i = 0; i < invocationList.Length; i++)
        {
            Action<Scene> handler = (Action<Scene>)invocationList[i];
            try
            {
                handler(scene);
            }
            catch (Exception ex)
            {
                failures ??= new List<Exception>();
                string owner = handler.Method.DeclaringType != null
                    ? handler.Method.DeclaringType.FullName
                    : "<unknown>";
                InvalidOperationException wrapped = new InvalidOperationException(
                    $"Cilbox pre-serialization handler {owner}.{handler.Method.Name} failed.", ex);
                failures.Add(wrapped);
            }
        }

        // Run every independent feature preparer so one package cannot hide failures in packages
        // registered after it, but still fail the overall build rather than shipping partial output.
        if (failures != null)
        {
            throw new AggregateException("One or more Cilbox pre-serialization handlers failed.", failures);
        }
    }
}

public class BasisCilboxBuildHook
{
    [InitializeOnLoadMethod]
    private static void Initialize()
    {
        BasisAssetBundlePipeline.OnBeforeBuildPrefabSerialization -= HandleBeforeBuildPrefab;
        BasisAssetBundlePipeline.OnBeforeBuildPrefabSerialization += HandleBeforeBuildPrefab;
        BasisAvatarSDKInspector.OnBeforeTestInEditorFinalize -= HandleBeforeTestInEditor;
        BasisAvatarSDKInspector.OnBeforeTestInEditorFinalize += HandleBeforeTestInEditor;
    }

    private static void HandleBeforeTestInEditor(GameObject prefabRoot)
    {
        HandleBeforeBuildPrefab(prefabRoot, null);
    }

    private static void HandleBeforeBuildPrefab(GameObject prefabRoot, BasisAssetBundleObject settings)
    {
        if (prefabRoot == null)
        {
            return;
        }

        bool hasNativeCilboxables = HasCilboxableComponents(prefabRoot);
        CilboxProxy[] existingProxies = prefabRoot.GetComponentsInChildren<CilboxProxy>(true);
        if (!hasNativeCilboxables && existingProxies.Length == 0)
        {
            return;
        }

        Debug.Log("Basis build finalization: preparing Cilbox data on the isolated build clone.");

        Scene originalScene = prefabRoot.scene;
        Scene originalActiveScene = SceneManager.GetActiveScene();
        Transform originalParent = prefabRoot.transform.parent;
        int originalSiblingIndex = originalParent != null ? prefabRoot.transform.GetSiblingIndex() : -1;
        Scene temporaryScene = default;
        bool detachedFromParent = false;

        try
        {
            temporaryScene = CreateTemporaryScene();

            if (originalParent != null)
            {
                prefabRoot.transform.SetParent(null, true);
                detachedFromParent = true;
            }

            SceneManager.MoveGameObjectToScene(prefabRoot, temporaryScene);
            SceneManager.SetActiveScene(temporaryScene);

            Cilbox.Cilbox contentCilbox = FindCilboxInScene(temporaryScene);
            if (contentCilbox == null)
            {
                throw new InvalidOperationException(
                    "Basis detected Cilboxable scripts/proxies, but this content has no Cilbox component. " +
                    "Add the appropriate Basis Cilbox component before building or using Test In Editor.");
            }

            if (hasNativeCilboxables && existingProxies.Length > 0)
            {
                throw new InvalidOperationException(
                    "Basis cannot safely finalize content that mixes already-converted CilboxProxy components " +
                    "with newly-authored Cilboxable behaviours. Structural processors must finish adding " +
                    "Cilboxable behaviours before Cilbox conversion runs.");
            }

            if (hasNativeCilboxables)
            {
                // This is deliberately before Cilbox searches and serializes the scene. Consumers may
                // update serialized state on the isolated clone while Basis remains feature-agnostic.
                BasisCilboxBuildEvents.InvokeBeforeCilboxSerialize(temporaryScene);
                CilboxScenePostprocessor.OnPostprocessScene(temporaryScene);
            }
            else
            {
                // Entering Play Mode can already have converted the authored scene before Test In Editor
                // clones it. Preserve that serialized program, but bind the clone's proxies to its own
                // Cilbox so the loaded avatar never depends on a world/authoring-scene host.
                RebindExistingProxiesToContentCilbox(existingProxies, contentCilbox);
            }
        }
        finally
        {
            Scene restoreScene = ResolveRestoreScene(originalScene, originalActiveScene, temporaryScene);
            if (prefabRoot != null && prefabRoot.scene.IsValid() && prefabRoot.scene == temporaryScene &&
                restoreScene.IsValid() && restoreScene.isLoaded)
            {
                SceneManager.MoveGameObjectToScene(prefabRoot, restoreScene);
            }

            if (detachedFromParent && prefabRoot != null && originalParent != null &&
                originalScene.IsValid() && originalScene.isLoaded && prefabRoot.scene == originalScene)
            {
                prefabRoot.transform.SetParent(originalParent, true);
                int siblingIndex = Mathf.Clamp(originalSiblingIndex, 0, Math.Max(0, originalParent.childCount - 1));
                prefabRoot.transform.SetSiblingIndex(siblingIndex);
            }

            // Restore a stable active scene before closing/unloading the temporary active scene.
            if (originalActiveScene.IsValid() && originalActiveScene.isLoaded && originalActiveScene != temporaryScene)
            {
                SceneManager.SetActiveScene(originalActiveScene);
            }
            else if (restoreScene.IsValid() && restoreScene.isLoaded && restoreScene != temporaryScene)
            {
                SceneManager.SetActiveScene(restoreScene);
            }

            if (temporaryScene.IsValid() && temporaryScene.isLoaded &&
                (prefabRoot == null || prefabRoot.scene != temporaryScene))
            {
                CloseTemporaryScene(temporaryScene);
            }
            else if (temporaryScene.IsValid() && temporaryScene.isLoaded && prefabRoot != null &&
                     prefabRoot.scene == temporaryScene)
            {
                Debug.LogError(
                    "Basis could not move the build clone out of its temporary Cilbox scene because no " +
                    "other loaded scene remained. The staging scene was left loaded to avoid destroying the clone.");
            }
        }
    }

    private static Scene CreateTemporaryScene()
    {
        if (Application.isPlaying)
        {
            return SceneManager.CreateScene($"BasisCilboxTemp-{Guid.NewGuid():N}");
        }

        return EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
    }

    private static void CloseTemporaryScene(Scene temporaryScene)
    {
        if (Application.isPlaying)
        {
            // The clone has already been moved back to its owner scene. The asynchronous unload only
            // removes the now-empty staging scene; callers are synchronous so there is nothing to await.
            SceneManager.UnloadSceneAsync(temporaryScene);
        }
        else
        {
            EditorSceneManager.CloseScene(temporaryScene, true);
        }
    }

    private static Scene ResolveRestoreScene(Scene originalScene, Scene originalActiveScene, Scene temporaryScene)
    {
        if (originalScene.IsValid() && originalScene.isLoaded && originalScene != temporaryScene)
        {
            return originalScene;
        }

        if (originalActiveScene.IsValid() && originalActiveScene.isLoaded && originalActiveScene != temporaryScene)
        {
            return originalActiveScene;
        }

        int sceneCount = SceneManager.sceneCount;
        for (int i = 0; i < sceneCount; i++)
        {
            Scene candidate = SceneManager.GetSceneAt(i);
            if (candidate.IsValid() && candidate.isLoaded && candidate != temporaryScene)
            {
                return candidate;
            }
        }

        return default;
    }

    internal static bool HasCilboxableComponents(GameObject root)
    {
        if (root == null)
        {
            return false;
        }

        MonoBehaviour[] components = root.GetComponentsInChildren<MonoBehaviour>(true);
        for (int i = 0; i < components.Length; i++)
        {
            MonoBehaviour component = components[i];
            if (component != null && CilboxUtil.HasCilboxableAttribute(component.GetType()))
            {
                return true;
            }
        }

        return false;
    }

    private static Cilbox.Cilbox FindCilboxInScene(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded)
        {
            return null;
        }

        GameObject[] roots = scene.GetRootGameObjects();
        for (int i = 0; i < roots.Length; i++)
        {
            GameObject root = roots[i];
            if (root == null)
            {
                continue;
            }

            Cilbox.Cilbox cilbox = root.GetComponentInChildren<Cilbox.Cilbox>(true);
            if (cilbox != null)
            {
                return cilbox;
            }
        }

        return null;
    }

    private static void RebindExistingProxiesToContentCilbox(
        CilboxProxy[] proxies,
        Cilbox.Cilbox contentCilbox)
    {
        string sourceAssembly = null;
        for (int i = 0; i < proxies.Length; i++)
        {
            CilboxProxy proxy = proxies[i];
            if (proxy == null || proxy.box == null || string.IsNullOrEmpty(proxy.box.assemblyData))
            {
                continue;
            }

            string candidateAssembly = proxy.box.assemblyData;
            if (sourceAssembly == null)
            {
                sourceAssembly = candidateAssembly;
            }
            else if (sourceAssembly != candidateAssembly)
            {
                throw new InvalidOperationException(
                    "The cloned content contains Cilbox proxies backed by different assemblies; Basis cannot " +
                    "deterministically rebind them to one content Cilbox.");
            }
        }

        if (string.IsNullOrEmpty(sourceAssembly))
        {
            sourceAssembly = contentCilbox.assemblyData;
        }

        if (string.IsNullOrEmpty(sourceAssembly))
        {
            throw new InvalidOperationException(
                "The cloned content already contains CilboxProxy components, but no serialized Cilbox assembly " +
                "data is available to run them.");
        }

        if (contentCilbox.assemblyData != sourceAssembly)
        {
            contentCilbox.assemblyData = sourceAssembly;
            contentCilbox.ForceReinit();
            EditorUtility.SetDirty(contentCilbox);
        }

        for (int i = 0; i < proxies.Length; i++)
        {
            CilboxProxy proxy = proxies[i];
            if (proxy == null || proxy.box == contentCilbox)
            {
                continue;
            }

            proxy.box = contentCilbox;
            EditorUtility.SetDirty(proxy);
        }
    }
}

/// <summary>
/// Runs Basis-owned Cilbox preparation before Cilbox's callbackOrder 0 scene processor. Unity may
/// invoke scene processors for player builds or Play Mode scene preparation, so subscribers must
/// scope work to the supplied scene and tolerate repeated preparation of separate scene instances.
/// </summary>
internal sealed class BasisCilboxPreSerializeSceneProcessor : IProcessSceneWithReport
{
    public int callbackOrder => -100;

    public void OnProcessScene(Scene scene, BuildReport report)
    {
        // Avoid invoking arbitrary feature preparers for unrelated scenes. Cilbox itself will return
        // immediately for the same condition at callbackOrder 0.
        if (CilboxUtil.GetAllBehavioursThatNeedCilboxing(scene).Length == 0)
        {
            return;
        }

        BasisCilboxBuildEvents.InvokeBeforeCilboxSerialize(scene);
    }
}
#endif
