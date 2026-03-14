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
        BasisAssetBundlePipeline.OnBeforeBuildPrefab -= HandleBeforeBuildPrefab;
        BasisAssetBundlePipeline.OnBeforeBuildPrefab += HandleBeforeBuildPrefab;
    }

    private static void HandleBeforeBuildPrefab(GameObject prefabRoot, BasisAssetBundleObject settings)
    {
        if (prefabRoot == null || !HasCilboxableComponents(prefabRoot))
        {
            return;
        }

        Debug.Log("Basis build prehook: generating Cilbox assembly data on the isolated build clone.");
        Scene originalScene = prefabRoot.scene;
        Transform originalParent = prefabRoot.transform.parent;
        int originalSiblingIndex = originalParent != null ? prefabRoot.transform.GetSiblingIndex() : -1;
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
                prefabRoot.transform.SetParent(null, true);
                detachedFromParent = true;
            }

            SceneManager.MoveGameObjectToScene(prefabRoot, temporaryScene);
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
                return;
            }

            CilboxScenePostprocessor.OnPostprocessScene(temporaryScene);
            EnsureTemporarySceneHasAssemblyData(temporarySceneCilbox, cilboxAssemblySnapshot);
            RebindProxiesToTemporarySceneCilbox(prefabRoot, temporarySceneCilbox);
            RestoreExternalCilboxAssemblyData(cilboxAssemblySnapshot, temporaryScene);
        }
        finally
        {
            RestoreDisabledRoots(temporarilyDisabledRoots);

            if (originalScene.IsValid() && originalScene.isLoaded && prefabRoot != null && prefabRoot.scene.IsValid() && prefabRoot.scene == temporaryScene)
            {
                SceneManager.MoveGameObjectToScene(prefabRoot, originalScene);
            }

            if (detachedFromParent && prefabRoot != null && originalParent != null && prefabRoot.scene.IsValid() && prefabRoot.scene == originalScene)
            {
                prefabRoot.transform.SetParent(originalParent, true);
                int siblingIndex = Mathf.Clamp(originalSiblingIndex, 0, originalParent.childCount - 1);
                prefabRoot.transform.SetSiblingIndex(siblingIndex);
            }

            if (temporaryCilboxHost != null)
            {
                UnityEngine.Object.DestroyImmediate(temporaryCilboxHost);
            }

            if (temporaryScene.IsValid() && temporaryScene.isLoaded && (prefabRoot == null || prefabRoot.scene != temporaryScene))
            {
                EditorSceneManager.CloseScene(temporaryScene, true);
            }

            if (originalScene.IsValid() && originalScene.isLoaded)
            {
                SceneManager.SetActiveScene(originalScene);
            }
        }
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
