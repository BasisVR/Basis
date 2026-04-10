#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using Basis.Scripts.BasisSdk;
using Basis.Shims;
using Cilbox;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public class BasisCilboxBuildHook
{
    private sealed class CilboxProxyReferenceSnapshot
    {
        public CilboxProxy Proxy;
        public Cilbox.Cilbox Box;
    }

    [Serializable]
    private sealed class PlayModeSceneCapture
    {
        public string ScenePath;
        public string SceneName;
        public int SceneIndex;
        public bool HasPersistedCapture;
        public string CapturedCallsJson;
    }

    [Serializable]
    private sealed class PlayModeSceneCaptureCollection
    {
        public PlayModeSceneCapture[] Scenes;
    }

    private sealed class PlayModeSceneCaptureLoadResult
    {
        public bool Found;
        public List<object> CapturedCalls;
    }

    private const string PlayModeCaptureSessionKey = "BasisCilboxBuildHook.PlayModeSceneCaptures";

    [InitializeOnLoadMethod]
    private static void Initialize()
    {
        BasisAssetBundlePipeline.OnBeforeBuildPrefab -= HandleBeforeBuildPrefab;
        BasisAssetBundlePipeline.OnBeforeBuildPrefab += HandleBeforeBuildPrefab;

        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    private static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingEditMode)
        {
            CaptureLoadedScenesForPlayMode();
            return;
        }

        if (state == PlayModeStateChange.EnteredPlayMode)
        {
            ProcessLoadedScenesForPlayMode();
            return;
        }

        if (state == PlayModeStateChange.EnteredEditMode)
        {
            SessionState.EraseString(PlayModeCaptureSessionKey);
        }
    }

    private static void CaptureLoadedScenesForPlayMode()
    {
        PlayModeSceneCaptureCollection collection = new PlayModeSceneCaptureCollection();
        List<PlayModeSceneCapture> sceneCaptures = new List<PlayModeSceneCapture>();

        int sceneCount = SceneManager.sceneCount;
        for (int sceneIndex = 0; sceneIndex < sceneCount; sceneIndex++)
        {
            Scene scene = SceneManager.GetSceneAt(sceneIndex);
            if (!SceneNeedsCilboxProcessing(scene))
            {
                continue;
            }

            try
            {
                List<object> capturedPersistentCalls = CilboxUnityEventRebinder.CaptureForScene(scene);
                sceneCaptures.Add(
                    new PlayModeSceneCapture
                    {
                        ScenePath = scene.path,
                        SceneName = scene.name,
                        SceneIndex = sceneIndex,
                        HasPersistedCapture = true,
                        CapturedCallsJson = CilboxUnityEventRebinder.SerializeCapturedCalls(capturedPersistentCalls)
                    }
                );
                Debug.Log(
                    $"[{nameof(BasisCilboxBuildHook)}] Prepared play-mode capture for scene {scene.name}: captured {capturedPersistentCalls.Count} cilbox UnityEvent calls."
                );
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to capture scene {scene.name} before entering play mode: {ex}");
            }
        }

        collection.Scenes = sceneCaptures.ToArray();
        SessionState.SetString(PlayModeCaptureSessionKey, JsonUtility.ToJson(collection));
    }

    private static void ProcessLoadedScenesForPlayMode()
    {
        CleanupStaleCilboxHelpers();
        PlayModeSceneCaptureCollection collection = LoadPlayModeCaptures();
        if (collection?.Scenes == null || collection.Scenes.Length == 0)
        {
            Debug.Log($"[{nameof(BasisCilboxBuildHook)}] No captured cilbox UnityEvent data was available for play mode.");
        }

        int sceneCount = SceneManager.sceneCount;
        for (int sceneIndex = 0; sceneIndex < sceneCount; sceneIndex++)
        {
            Scene scene = SceneManager.GetSceneAt(sceneIndex);
            if (!SceneNeedsCilboxProcessing(scene))
            {
                continue;
            }

            try
            {
                Dictionary<EntityId, string> cilboxAssemblySnapshot = CaptureCilboxAssemblySnapshot();
                PlayModeSceneCaptureLoadResult captureResult = LoadCapturedCallsForScene(collection, scene, sceneIndex);
                if (!captureResult.Found)
                {
                    Debug.LogWarning(
                        $"[{nameof(BasisCilboxBuildHook)}] Skipping play-mode cilbox processing for scene {scene.name} because no persisted UnityEvent capture was found."
                    );
                    continue;
                }

                List<object> capturedPersistentCalls = captureResult.CapturedCalls;
                Debug.Log(
                    $"[{nameof(BasisCilboxBuildHook)}] Play-mode processing scene {scene.name}: captured {capturedPersistentCalls.Count} cilbox UnityEvent calls."
                );
                TryProcessCilboxScene(scene, null, cilboxAssemblySnapshot, capturedPersistentCalls, out _, out _);
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to prepare scene {scene.name} for cilbox play mode processing: {ex}");
            }
        }
    }

    private static void HandleBeforeBuildPrefab(GameObject prefabRoot, BasisAssetBundleObject settings)
    {
        if (prefabRoot == null || !HasCilboxableComponents(prefabRoot))
        {
            return;
        }

        CleanupStaleCilboxHelpers();
        Debug.Log("Basis build prehook: generating Cilbox assembly data on the isolated build clone.");

        Scene originalScene = prefabRoot.scene;
        Transform originalParent = prefabRoot.transform.parent;
        int originalSiblingIndex = originalParent != null ? prefabRoot.transform.GetSiblingIndex() : -1;
        Dictionary<EntityId, string> cilboxAssemblySnapshot = CaptureCilboxAssemblySnapshot();
        List<CilboxProxyReferenceSnapshot> proxyReferenceSnapshot = CaptureProxyReferences(prefabRoot);
        List<GameObject> temporarilyDisabledRoots = new List<GameObject>();
        Scene temporaryScene = default;
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

            List<object> capturedPersistentCalls = CilboxUnityEventRebinder.CaptureForRoot(prefabRoot);
            Debug.Log(
                $"[{nameof(BasisCilboxBuildHook)}] Build processing root {prefabRoot.name}: captured {capturedPersistentCalls.Count} cilbox UnityEvent calls."
            );
            if (TryProcessCilboxScene(temporaryScene, prefabRoot, cilboxAssemblySnapshot, capturedPersistentCalls, out _, out temporaryCilboxHost))
            {
                RestoreExternalCilboxAssemblyData(cilboxAssemblySnapshot, temporaryScene);
            }
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

            RestoreProxyReferences(proxyReferenceSnapshot);

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

    private static bool TryProcessCilboxScene(
        Scene scene,
        GameObject contentRoot,
        Dictionary<EntityId, string> snapshot,
        List<object> capturedPersistentCalls,
        out Cilbox.Cilbox sceneCilbox,
        out GameObject temporaryCilboxHost
    )
    {
        sceneCilbox = FindCilboxInScene(scene);
        temporaryCilboxHost = null;
        if (sceneCilbox == null)
        {
            Type fallbackCilboxType = GetFirstLoadedCilboxType();
            if (fallbackCilboxType != null)
            {
                temporaryCilboxHost = new GameObject(contentRoot == null ? "BasisCilboxPlayModeHost" : "BasisCilboxTempHost");
                SceneManager.MoveGameObjectToScene(temporaryCilboxHost, scene);
                sceneCilbox = temporaryCilboxHost.AddComponent(fallbackCilboxType) as Cilbox.Cilbox;
                if (sceneCilbox != null)
                {
                    sceneCilbox.exportDebuggingData = false;
                }
            }
        }

        if (sceneCilbox == null)
        {
            Debug.LogWarning("Basis build detected Cilboxable scripts, but no Cilbox component was found. Skipping Cilbox processing.");
            return false;
        }

        if (contentRoot != null)
        {
            CilboxUnityEventRebinder.RemoveShimsFromRoot(contentRoot);
        }
        else
        {
            CilboxUnityEventRebinder.RemoveShimsFromScene(scene);
        }

        CilboxScenePostprocessor.OnPostprocessScene(scene);
        EnsureTemporarySceneHasAssemblyData(sceneCilbox, snapshot);

        if (contentRoot != null)
        {
            RebindProxiesToTemporarySceneCilbox(contentRoot, sceneCilbox);
        }
        else
        {
            RebindSceneProxiesToTemporarySceneCilbox(scene, sceneCilbox);
        }

        CilboxUnityEventRebinder.ApplyCapturedCalls(scene, capturedPersistentCalls);
        return true;
    }

    private static PlayModeSceneCaptureCollection LoadPlayModeCaptures()
    {
        string json = SessionState.GetString(PlayModeCaptureSessionKey, string.Empty);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonUtility.FromJson<PlayModeSceneCaptureCollection>(json);
        }
        catch (ArgumentException ex)
        {
            Debug.LogError($"[{nameof(BasisCilboxBuildHook)}] Failed to deserialize {PlayModeCaptureSessionKey}: {ex}");
            SessionState.EraseString(PlayModeCaptureSessionKey);
            return null;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[{nameof(BasisCilboxBuildHook)}] Unexpected error while deserializing {PlayModeCaptureSessionKey}: {ex}");
            SessionState.EraseString(PlayModeCaptureSessionKey);
            return null;
        }
    }

    private static PlayModeSceneCaptureLoadResult LoadCapturedCallsForScene(PlayModeSceneCaptureCollection collection, Scene scene, int sceneIndex)
    {
        PlayModeSceneCaptureLoadResult result = new PlayModeSceneCaptureLoadResult
        {
            Found = false,
            CapturedCalls = new List<object>()
        };

        if (collection?.Scenes == null)
        {
            return result;
        }

        int captureCount = collection.Scenes.Length;
        for (int i = 0; i < captureCount; i++)
        {
            PlayModeSceneCapture sceneCapture = collection.Scenes[i];
            if (sceneCapture == null)
            {
                continue;
            }

            bool pathMatches = !string.IsNullOrEmpty(sceneCapture.ScenePath) && string.Equals(sceneCapture.ScenePath, scene.path, StringComparison.Ordinal);
            bool nameAndIndexMatch = string.Equals(sceneCapture.SceneName, scene.name, StringComparison.Ordinal) && sceneCapture.SceneIndex == sceneIndex;
            bool fallbackNameMatch = string.IsNullOrEmpty(scene.path) && string.IsNullOrEmpty(sceneCapture.ScenePath) && string.Equals(sceneCapture.SceneName, scene.name, StringComparison.Ordinal);
            if (!pathMatches && !nameAndIndexMatch && !fallbackNameMatch)
            {
                continue;
            }

            if (!sceneCapture.HasPersistedCapture)
            {
                return result;
            }

            result.Found = true;
            result.CapturedCalls = CilboxUnityEventRebinder.DeserializeCapturedCalls(sceneCapture.CapturedCallsJson);
            return result;
        }

        return result;
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

            if (go.name == "CilboxDirtier" || go.name.StartsWith("CilboxAsm ", StringComparison.Ordinal))
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        CilboxUnityEventShim[] staleShims = Resources.FindObjectsOfTypeAll<CilboxUnityEventShim>();
        int shimCount = staleShims.Length;
        for (int i = 0; i < shimCount; i++)
        {
            CilboxUnityEventShim shim = staleShims[i];
            if (shim != null && shim.gameObject.scene.IsValid() && shim.gameObject.scene.isLoaded)
            {
                UnityEngine.Object.DestroyImmediate(shim);
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

    private static bool HasCilboxableComponents(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded)
        {
            return false;
        }

        GameObject[] roots = scene.GetRootGameObjects();
        int length = roots.Length;
        for (int i = 0; i < length; i++)
        {
            if (HasCilboxableComponents(roots[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SceneNeedsCilboxProcessing(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded)
        {
            return false;
        }

        return HasCilboxableComponents(scene) || FindCilboxInScene(scene) != null;
    }

    private static Dictionary<EntityId, string> CaptureCilboxAssemblySnapshot()
    {
        Dictionary<EntityId, string> snapshot = new Dictionary<EntityId, string>();
        Cilbox.Cilbox[] allCilboxes = Resources.FindObjectsOfTypeAll<Cilbox.Cilbox>();
        int length = allCilboxes.Length;
        for (int i = 0; i < length; i++)
        {
            Cilbox.Cilbox cilbox = allCilboxes[i];
            if (cilbox == null)
            {
                continue;
            }

            snapshot[cilbox.GetEntityId()] = cilbox.assemblyData;
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

    private static void EnsureTemporarySceneHasAssemblyData(Cilbox.Cilbox temporarySceneCilbox, Dictionary<EntityId, string> snapshot)
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

            EntityId id = cilbox.GetEntityId();
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
        RebindProxiesToTemporarySceneCilbox(proxies, temporarySceneCilbox);
    }

    private static void RebindSceneProxiesToTemporarySceneCilbox(Scene scene, Cilbox.Cilbox temporarySceneCilbox)
    {
        if (!scene.IsValid() || !scene.isLoaded || temporarySceneCilbox == null)
        {
            return;
        }

        List<CilboxProxy> proxies = new List<CilboxProxy>();
        GameObject[] roots = scene.GetRootGameObjects();
        int rootCount = roots.Length;
        for (int i = 0; i < rootCount; i++)
        {
            GameObject root = roots[i];
            if (root == null)
            {
                continue;
            }

            proxies.AddRange(root.GetComponentsInChildren<CilboxProxy>(true));
        }

        RebindProxiesToTemporarySceneCilbox(proxies.ToArray(), temporarySceneCilbox);
    }

    private static void RebindProxiesToTemporarySceneCilbox(CilboxProxy[] proxies, Cilbox.Cilbox temporarySceneCilbox)
    {
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

    private static void RestoreExternalCilboxAssemblyData(Dictionary<EntityId, string> snapshot, Scene keepScene)
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

            EntityId id = cilbox.GetEntityId();
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

    private static List<CilboxProxyReferenceSnapshot> CaptureProxyReferences(GameObject root)
    {
        List<CilboxProxyReferenceSnapshot> snapshot = new List<CilboxProxyReferenceSnapshot>();
        if (root == null)
        {
            return snapshot;
        }

        CilboxProxy[] proxies = root.GetComponentsInChildren<CilboxProxy>(true);
        int length = proxies.Length;
        for (int i = 0; i < length; i++)
        {
            CilboxProxy proxy = proxies[i];
            if (proxy == null)
            {
                continue;
            }

            snapshot.Add(
                new CilboxProxyReferenceSnapshot
                {
                    Proxy = proxy,
                    Box = proxy.box
                }
            );
        }

        return snapshot;
    }

    private static void RestoreProxyReferences(List<CilboxProxyReferenceSnapshot> snapshot)
    {
        if (snapshot == null)
        {
            return;
        }

        int length = snapshot.Count;
        for (int i = 0; i < length; i++)
        {
            CilboxProxyReferenceSnapshot entry = snapshot[i];
            if (entry?.Proxy == null || entry.Proxy.box == entry.Box)
            {
                continue;
            }

            entry.Proxy.box = entry.Box;
            EditorUtility.SetDirty(entry.Proxy);
        }
    }
}
#endif
