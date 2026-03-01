#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Basis.Scripts.BasisSdk;
using Cilbox;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public class BasisCilboxBuildHook
{
    [InitializeOnLoadMethod]
    private static void Initialize()
    {
        //Debug.Log("BasisCilboxBuildHook initialized.");
        BasisBundleBuild.PreBuildBundleEvents -= HandlePreBuildBundle;
        BasisBundleBuild.PreBuildBundleEvents += HandlePreBuildBundle;
    }

    private static Task HandlePreBuildBundle(BasisContentBase basisContentBase, List<BuildTarget> targets)
    {
        Debug.Log("BasisCilboxBuildHook prebuild callback invoked.");
        GameObject contentRoot = basisContentBase != null ? basisContentBase.gameObject : null;
        bool contentHasCilboxable = HasCilboxableComponents(contentRoot);
        if (!contentHasCilboxable)
        {
            Debug.Log("BasisCilboxBuildHook: no Cilboxable scripts found on bundle root, skipping.");
            return Task.CompletedTask;
        }

        if (contentRoot == null)
        {
            Debug.LogWarning("BasisCilboxBuildHook: bundle root was null, skipping.");
            return Task.CompletedTask;
        }

        Debug.Log("Basis build prehook: generating Cilbox assembly data in an isolated temporary scene.");
        Scene originalScene = contentRoot.scene;
        Transform originalParent = contentRoot.transform.parent;
        int originalSiblingIndex = originalParent != null ? contentRoot.transform.GetSiblingIndex() : -1;
        Dictionary<int, string> cilboxAssemblySnapshot = CaptureCilboxAssemblySnapshot();
        List<GameObject> temporarilyDisabledRoots = new List<GameObject>();
        Scene temporaryScene = default;
        Cilbox.Cilbox temporarySceneCilbox = null;
        GameObject temporaryCilboxHost = null;
        bool detachedFromParent = false;
        try
        {
            temporaryScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            if (originalParent != null)
            {
                contentRoot.transform.SetParent(null, true);
                detachedFromParent = true;
            }

            SceneManager.MoveGameObjectToScene(contentRoot, temporaryScene);
            SceneManager.SetActiveScene(temporaryScene);

            DeactivateOtherSceneRoots(temporaryScene, temporarilyDisabledRoots);

            temporarySceneCilbox = FindCilboxInScene(temporaryScene);
            if (temporarySceneCilbox == null)
            {
                Type fallbackCilboxType = GetFirstLoadedCilboxType();
                if (fallbackCilboxType != null)
                {
                    temporaryCilboxHost = new GameObject("BasisCilboxTempHost");
                    SceneManager.MoveGameObjectToScene(temporaryCilboxHost, temporaryScene);
                    temporarySceneCilbox = temporaryCilboxHost.AddComponent(fallbackCilboxType) as Cilbox.Cilbox;
                    if (temporarySceneCilbox != null)
                    {
                        temporarySceneCilbox.exportDebuggingData = false;
                    }
                }
            }

            if (temporarySceneCilbox == null)
            {
                Debug.LogWarning("Basis build detected Cilboxable scripts, but no Cilbox component was found. Skipping Cilbox prebuild assembly.");
                return Task.CompletedTask;
            }

            CilboxScenePostprocessor.OnPostprocessScene();
            EnsureTemporarySceneHasAssemblyData(temporarySceneCilbox, cilboxAssemblySnapshot);
            RebindProxiesToTemporarySceneCilbox(contentRoot, temporarySceneCilbox);
            RestoreExternalCilboxAssemblyData(cilboxAssemblySnapshot, temporaryScene);
            EditorUtility.SetDirty(contentRoot);
            AssetDatabase.SaveAssets();
        }
        finally
        {
            RestoreDisabledRoots(temporarilyDisabledRoots);

            if (originalScene.IsValid() && originalScene.isLoaded && contentRoot != null && contentRoot.scene.IsValid() && contentRoot.scene == temporaryScene)
            {
                SceneManager.MoveGameObjectToScene(contentRoot, originalScene);
            }

            if (detachedFromParent && contentRoot != null && originalParent != null && contentRoot.scene.IsValid() && contentRoot.scene == originalScene)
            {
                contentRoot.transform.SetParent(originalParent, true);
                int siblingIndex = Mathf.Clamp(originalSiblingIndex, 0, originalParent.childCount - 1);
                contentRoot.transform.SetSiblingIndex(siblingIndex);
            }

            if (temporaryCilboxHost != null)
            {
                UnityEngine.Object.DestroyImmediate(temporaryCilboxHost);
            }

            if (temporaryScene.IsValid() && temporaryScene.isLoaded && (contentRoot == null || contentRoot.scene != temporaryScene))
            {
                EditorSceneManager.CloseScene(temporaryScene, true);
            }

            if (originalScene.IsValid() && originalScene.isLoaded)
            {
                SceneManager.SetActiveScene(originalScene);
            }
        }
        return Task.CompletedTask;
    }

    private static void CleanupStaleCilboxHelpers()
    {
        GameObject[] allObjects = Resources.FindObjectsOfTypeAll<GameObject>();
        int length = allObjects.Length;
        for (int i = 0; i < length; i++)
        {
            GameObject go = allObjects[i];
            if (go == null)
            {
                continue;
            }

            if (!go.scene.IsValid() || !go.scene.isLoaded)
            {
                continue;
            }

            if (go.name == "CilboxDirtier" || go.name.StartsWith("CilboxAsm "))
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }
    }

    private static bool HasCilboxableComponents(GameObject root)
    {
        if (root == null)
        {
            return false;
        }

        MonoBehaviour[] components = root.GetComponentsInChildren<MonoBehaviour>(true);
        int length = components.Length;
        for (int i = 0; i < length; i++)
        {
            MonoBehaviour component = components[i];
            if (component == null)
            {
                continue;
            }

            object[] attributes = component.GetType().GetCustomAttributes(typeof(CilboxableAttribute), true);
            if (attributes != null && attributes.Length > 0)
            {
                return true;
            }
        }
        return false;
    }

    private static Dictionary<int, string> CaptureCilboxAssemblySnapshot()
    {
        Dictionary<int, string> snapshot = new Dictionary<int, string>();
        Cilbox.Cilbox[] allCilboxes = Resources.FindObjectsOfTypeAll<Cilbox.Cilbox>();
        int length = allCilboxes.Length;
        for (int i = 0; i < length; i++)
        {
            Cilbox.Cilbox cilbox = allCilboxes[i];
            if (cilbox == null)
            {
                continue;
            }

            snapshot[cilbox.GetInstanceID()] = cilbox.assemblyData;
        }

        return snapshot;
    }

    private static void DeactivateOtherSceneRoots(Scene keepScene, List<GameObject> disabledRoots)
    {
        int sceneCount = SceneManager.sceneCount;
        for (int sceneIndex = 0; sceneIndex < sceneCount; sceneIndex++)
        {
            Scene scene = SceneManager.GetSceneAt(sceneIndex);
            if (!scene.IsValid() || !scene.isLoaded || scene == keepScene)
            {
                continue;
            }

            GameObject[] roots = scene.GetRootGameObjects();
            int rootLength = roots.Length;
            for (int rootIndex = 0; rootIndex < rootLength; rootIndex++)
            {
                GameObject root = roots[rootIndex];
                if (root == null || !root.activeSelf)
                {
                    continue;
                }

                root.SetActive(false);
                disabledRoots.Add(root);
            }
        }
    }

    private static void RestoreDisabledRoots(List<GameObject> disabledRoots)
    {
        int length = disabledRoots.Count;
        for (int i = 0; i < length; i++)
        {
            GameObject root = disabledRoots[i];
            if (root != null)
            {
                root.SetActive(true);
            }
        }
    }

    private static Cilbox.Cilbox FindCilboxInScene(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded)
        {
            return null;
        }

        GameObject[] roots = scene.GetRootGameObjects();
        int length = roots.Length;
        for (int i = 0; i < length; i++)
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

    private static Type GetFirstLoadedCilboxType()
    {
        Cilbox.Cilbox[] allCilboxes = Resources.FindObjectsOfTypeAll<Cilbox.Cilbox>();
        int length = allCilboxes.Length;
        for (int i = 0; i < length; i++)
        {
            Cilbox.Cilbox cilbox = allCilboxes[i];
            if (cilbox != null)
            {
                return cilbox.GetType();
            }
        }

        return null;
    }

    private static void EnsureTemporarySceneHasAssemblyData(Cilbox.Cilbox temporarySceneCilbox, Dictionary<int, string> snapshot)
    {
        if (temporarySceneCilbox == null)
        {
            return;
        }

        if (!string.IsNullOrEmpty(temporarySceneCilbox.assemblyData))
        {
            return;
        }

        Cilbox.Cilbox[] allCilboxes = Resources.FindObjectsOfTypeAll<Cilbox.Cilbox>();
        int length = allCilboxes.Length;
        for (int i = 0; i < length; i++)
        {
            Cilbox.Cilbox cilbox = allCilboxes[i];
            if (cilbox == null || cilbox == temporarySceneCilbox || string.IsNullOrEmpty(cilbox.assemblyData))
            {
                continue;
            }

            int id = cilbox.GetInstanceID();
            if (snapshot.TryGetValue(id, out string original) && original == cilbox.assemblyData)
            {
                continue;
            }

            temporarySceneCilbox.assemblyData = cilbox.assemblyData;
            temporarySceneCilbox.ForceReinit();
            EditorUtility.SetDirty(temporarySceneCilbox);
            return;
        }
    }

    private static void RebindProxiesToTemporarySceneCilbox(GameObject contentRoot, Cilbox.Cilbox temporarySceneCilbox)
    {
        if (contentRoot == null || temporarySceneCilbox == null)
        {
            return;
        }

        CilboxProxy[] proxies = contentRoot.GetComponentsInChildren<CilboxProxy>(true);
        int length = proxies.Length;
        for (int i = 0; i < length; i++)
        {
            CilboxProxy proxy = proxies[i];
            if (proxy == null || proxy.box == temporarySceneCilbox)
            {
                continue;
            }

            proxy.box = temporarySceneCilbox;
            EditorUtility.SetDirty(proxy);
        }
    }

    private static void RestoreExternalCilboxAssemblyData(Dictionary<int, string> snapshot, Scene keepScene)
    {
        Cilbox.Cilbox[] allCilboxes = Resources.FindObjectsOfTypeAll<Cilbox.Cilbox>();
        int length = allCilboxes.Length;
        for (int i = 0; i < length; i++)
        {
            Cilbox.Cilbox cilbox = allCilboxes[i];
            if (cilbox == null || !cilbox.gameObject.scene.IsValid() || cilbox.gameObject.scene == keepScene)
            {
                continue;
            }

            int id = cilbox.GetInstanceID();
            if (!snapshot.TryGetValue(id, out string originalAssemblyData))
            {
                continue;
            }

            if (cilbox.assemblyData == originalAssemblyData)
            {
                continue;
            }

            cilbox.assemblyData = originalAssemblyData;
            cilbox.ForceReinit();
            EditorUtility.SetDirty(cilbox);
        }
    }

}
#endif
