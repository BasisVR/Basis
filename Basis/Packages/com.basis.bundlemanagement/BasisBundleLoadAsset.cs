using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Basis.Scripts.BasisSdk;
using UnityEngine;
using UnityEngine.SceneManagement;
using static BundledContentHolder;
public static class BasisBundleLoadAsset
{
    public static async Task<GameObject> LoadFromWrapper(GameObject DisabledGameobject, BasisTrackedBundleWrapper BasisLoadableBundle, bool UseContentRemoval, Vector3 Position, Quaternion Rotation, bool ModifyScale, Vector3 Scale, Selector Selector, Transform Parent = null, bool DestroyColliders = false, bool ChangeColidersToCorrectLayer = false, List<BasisHeadChop.HeadChopTarget> HarvestedHeadChop = null, int RendererLayer = -1)
    {
        if (BasisLoadableBundle.AssetBundle != null || BasisLoadableBundle.HasGltfTemplate)
        {
            BasisLoadableBundle output = BasisLoadableBundle.LoadableBundle;
            if (output.BasisBundleConnector.GetPlatform(out BasisBundleGenerated Generated))
            {
                switch (Generated.AssetMode)
                {
                    case BasisBundleConnector.GameObjectAssetMode:
                        {
                            string ReplacedName = Generated.AssetToLoadName.Replace(".bundle", ".prefab");

                            AssetBundleRequest Request = BasisLoadableBundle.AssetBundle.LoadAssetAsync<GameObject>(ReplacedName);
                            await Request;
                            GameObject loadedObject = Request.asset as GameObject;
                            if (loadedObject == null)
                            {
                                BasisDebug.LogError("Unable to proceed, null Gameobject for request " + Generated.AssetToLoadName);

                                string[] assetNames = BasisLoadableBundle.AssetBundle.GetAllAssetNames();
                                BasisDebug.LogError("All assets in bundle: \n" + string.Join("\n", assetNames));

                                BasisLoadableBundle.DidErrorOccur = true;
                                await BasisLoadableBundle.AssetBundle.UnloadAsync(true);
                                return null;
                            }
                            // GraphicsStateCollection resolves Shader references at load time, so
                            // parse the sidecar only after this prefab and its shaders are resident.
                            await BasisLoadableBundle.EnsureEmbeddedGraphicsStatesLoaded();
                            return await InstantiateContentControlled(DisabledGameobject, BasisLoadableBundle, loadedObject, UseContentRemoval, Position, Rotation, ModifyScale, Scale, Selector, Parent, DestroyColliders, ChangeColidersToCorrectLayer, HarvestedHeadChop, RendererLayer);
                        }
                    case BasisBundleConnector.GltfAssetMode:
                        {
                            // Generic (glTF) fallback: the wrapper holds an inactive template
                            // instead of an AssetBundle; clone it like a prefab.
                            GameObject template = BasisLoadableBundle.GltfTemplateAvatarRoot;
                            if (template == null)
                            {
                                BasisDebug.LogError("Generic (glTF) template missing on wrapper for " + Generated.AssetToLoadName);
                                BasisLoadableBundle.DidErrorOccur = true;
                                return null;
                            }
                            return await InstantiateContentControlled(DisabledGameobject, BasisLoadableBundle, template, UseContentRemoval, Position, Rotation, ModifyScale, Scale, Selector, Parent, DestroyColliders, ChangeColidersToCorrectLayer, HarvestedHeadChop, RendererLayer);
                        }
                    default:
                        BasisDebug.LogError("Requested type " + Generated.AssetMode + " has no handler");
                        return null;
                }
            }
            else
            {
                BasisDebug.LogError("Missing Platform Bundle! can't find : " + Application.platform);
            }
        }
        else
        {
            BasisDebug.LogError("Missing Bundle!");
        }
        BasisDebug.LogError("Returning unable to load gameobject!");
        return null;
    }

    private static async Task<GameObject> InstantiateContentControlled(GameObject DisabledGameobject, BasisTrackedBundleWrapper BasisLoadableBundle, GameObject loadedObject, bool UseContentRemoval, Vector3 Position, Quaternion Rotation, bool ModifyScale, Vector3 Scale, Selector Selector, Transform Parent, bool DestroyColliders, bool ChangeColidersToCorrectLayer, List<BasisHeadChop.HeadChopTarget> HarvestedHeadChop, int RendererLayer)
    {
        ChecksRequired ChecksRequired = new ChecksRequired();
        if (loadedObject.TryGetComponent<BasisAvatar>(out BasisAvatar BasisAvatar))
        {
            ChecksRequired.DisableAnimatorEvents = true;
        }
        ChecksRequired.UseContentRemoval = UseContentRemoval;
        ChecksRequired.RemoveColliders = DestroyColliders;
        ChecksRequired.ChangeCollidersToCorrectLayer = ChangeColidersToCorrectLayer;
        ChecksRequired.ScrubPersistentUnityEvents = true;
        BasisContentHarvest harvest = BasisAvatar != null ? new BasisContentHarvest() : null;
        // Match the DX11 load path: instantiate and scrub immediately. DX12/Vulkan differ only
        // by keeping rendering gated until their embedded graphics states finish warming.
        Action<IList<Renderer>> emissionPostprocessor =
            BasisLegacyMaterialCompatibility.RequiresLegacyEmissionUpgrade(BasisLoadableBundle.BuiltWithUnityVersion)
                ? renderers => BasisLegacyMaterialCompatibility.UpgradeEmission(renderers, BasisLoadableBundle.BuiltWithUnityVersion)
                : null;
        ContentPoliceControl.ContentControlState scrubState = ContentPoliceControl.BeginContentControl(DisabledGameobject, loadedObject, ChecksRequired, Position, Rotation, ModifyScale, Scale, Selector, Parent, LayerMask.NameToLayer("IgnoredByInteractable"), HarvestedHeadChop, harvest, BasisLoadableBundle.EmbeddedGraphicsStates, emissionPostprocessor, RendererLayer);
        GameObject CreatedCopy = ContentPoliceControl.PrepareContentControl(scrubState, out BasisGraphicsStatePrewarm.WarmupRequest warmup);
        while (!warmup.IsCompleted)
        {
            await Task.Yield();
        }
        warmup.CompleteAndDispose();
        ContentPoliceControl.ActivateContentControl(scrubState, CreatedCopy);
        if (CreatedCopy == null)
        {
            BasisDebug.LogError("ContentControl returned null; clone was destroyed during loading.");
            return null;
        }
        if (harvest != null && CreatedCopy != null && CreatedCopy.TryGetComponent(out Basis.Scripts.BasisSdk.BasisAvatar createdAvatar))
        {
            createdAvatar.Harvest = harvest;
        }
        // The worn-instance reservation is taken by the LOAD entry points (BasisLoadHandler)
        // before their first await so an unload grace re-check cannot destroy the assets under
        // this clone while its asynchronous asset/PSO work is still running.
        string InstanceID = BasisGenerateUniqueID.GenerateUniqueID();
        CreatedCopy.name = InstanceID;
        return CreatedCopy;
    }
    public static async Task<Scene> LoadSceneFromBundleAsync(BasisTrackedBundleWrapper bundle, bool MakeActiveScene, BasisProgressReport progressCallback, CancellationToken cancellationToken = default)
    {
        string UniqueID = BasisGenerateUniqueID.GenerateUniqueID();
        bool AssignedIncrement = false;
        bool requiresLegacyEmissionUpgrade =
            BasisLegacyMaterialCompatibility.RequiresLegacyEmissionUpgrade(bundle.BuiltWithUnityVersion);
        HashSet<EntityId> renderersPresentBeforeLoad = null;
        if (requiresLegacyEmissionUpgrade)
        {
            renderersPresentBeforeLoad = new HashSet<EntityId>();
            Renderer[] existingRenderers = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include);
            for (int i = 0; i < existingRenderers.Length; i++)
                renderersPresentBeforeLoad.Add(existingRenderers[i].GetEntityId());
        }

        string[] scenePaths = bundle.AssetBundle.GetAllScenePaths();
        if (scenePaths.Length == 0)
        {
            BasisDebug.LogError("No scenes found in AssetBundle.");
            return new Scene();
        }
        if (scenePaths.Length > 1)
        {
            BasisDebug.LogError("More then one scene was found in The Asset Bundle, Please Correct!");
            return new Scene();
        }

        if (!string.IsNullOrEmpty(scenePaths[0]))
        {
            string scenePath = scenePaths[0];
            string sceneName = System.IO.Path.GetFileNameWithoutExtension(scenePath);
            ChecksRequired ChecksRequired = new ChecksRequired
            {
                UseContentRemoval = true,
                ScrubPersistentUnityEvents = true
            };
            HashSet<Scene> scenesPresentBeforeLoad = new HashSet<Scene>();
            for (int Index = 0; Index < SceneManager.sceneCount; Index++)
            {
                Scene existing = SceneManager.GetSceneAt(Index);
                if (string.Equals(existing.path, scenePath, StringComparison.OrdinalIgnoreCase)) scenesPresentBeforeLoad.Add(existing);
            }
            BasisWorldSceneGate.Ticket ticket = BasisWorldSceneGate.Expect(scenePath, ChecksRequired, Selector.World, requiresLegacyEmissionUpgrade ? renderers => BasisLegacyMaterialCompatibility.UpgradeEmission(renderers, bundle.BuiltWithUnityVersion) : null);
            try
            {
                AsyncOperation asyncLoad = SceneManager.LoadSceneAsync(scenePath, LoadSceneMode.Additive);
                if (asyncLoad == null)
                {
                    BasisDebug.LogError("Failed to start loading scene " + scenePath);
                    return new Scene();
                }
                while (!asyncLoad.isDone)
                {
                    progressCallback.ReportProgress(UniqueID, Mathf.Min(asyncLoad.progress, 0.99f) * 100, $"Activating scene {sceneName}");
                    await Task.Yield();
                }
            }
            finally
            {
                BasisWorldSceneGate.Release(ticket);
            }

            if (!ticket.Claimed || !ticket.Scanned)
            {
                Scene rejected = ticket.Claimed ? ticket.Scene : FindUnclaimedScene(scenePath, scenesPresentBeforeLoad);
                BasisDebug.LogError($"World scene {sceneName} was refused because Content Police could not scan it before it ran.", BasisDebug.LogTag.Event);
                ticket.RestoreRenderers();
                if (rejected.IsValid() && !string.IsNullOrEmpty(bundle.MetaLink) && bundle.MetaLink == rejected.path) bundle.Increment();
                await UnloadRejectedScene(rejected);
                return new Scene();
            }

            BasisDebug.Log("Scene loaded successfully from AssetBundle.");
            Scene loadedScene = ticket.Scene;
            bundle.MetaLink = loadedScene.path;
            if (loadedScene.IsValid())
            {
                // Scene shaders become resident as part of additive scene load. Loading the
                // collection here lets Unity resolve them before we select and warm its PSOs.
                try
                {
                    await bundle.EnsureEmbeddedGraphicsStatesLoaded(progressCallback);
                    BasisGraphicsStatePrewarm.WarmupRequest warmup = ContentPoliceControl.ScheduleScenePrewarm(ticket.Renderers, loadedScene, bundle.EmbeddedGraphicsStates);
                    while (!warmup.IsCompleted)
                    {
                        await Task.Yield();
                    }
                    warmup.CompleteAndDispose();

                }
                finally
                {
                    ticket.RestoreRenderers();
                }
                AssignedIncrement = bundle.Increment();
                if (MakeActiveScene && !cancellationToken.IsCancellationRequested)
                {
                    SceneManager.SetActiveScene(loadedScene);
                    BasisDebug.Log("Scene set as active: " + loadedScene.name);
                }

                if (requiresLegacyEmissionUpgrade)
                {
                    // One late legacy-only pass catches visuals created or reparented during
                    // activation without charging modern bundles for a global renderer search.
                    await Task.Yield();
                    Renderer[] liveRenderers = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include);
                    List<Renderer> renderersCreatedByLoad = new List<Renderer>();
                    for (int i = 0; i < liveRenderers.Length; i++)
                    {
                        Renderer renderer = liveRenderers[i];
                        if (renderer != null && !renderersPresentBeforeLoad.Contains(renderer.GetEntityId()))
                            renderersCreatedByLoad.Add(renderer);
                    }
                    BasisLegacyMaterialCompatibility.UpgradeEmission(renderersCreatedByLoad, bundle.BuiltWithUnityVersion);
                }

                BasisDebug.Log("Scene loaded: " + loadedScene.name + " (MakeActive=" + MakeActiveScene + ", Incremented=" + AssignedIncrement + ")");
#if UNITY_BUNDLEUNLOAD
                    bundle.ReleaseBundleBackingStore();
#endif
                progressCallback.ReportProgress(UniqueID, 100, $"Loaded scene {sceneName}");
                return loadedScene;
            }
            else
            {
                ticket.RestoreRenderers();
                BasisDebug.LogError("Failed to get loaded scene.");
            }
        }
        else
        {
            BasisDebug.LogError("Path was null or empty! this should not be happening!");
        }
        return new Scene();
    }
    public static Scene FindUnclaimedScene(string scenePath, HashSet<Scene> scenesPresentBeforeLoad)
    {
        for (int Index = SceneManager.sceneCount - 1; Index >= 0; Index--)
        {
            Scene scene = SceneManager.GetSceneAt(Index);
            if (scene.isLoaded && string.Equals(scene.path, scenePath, StringComparison.OrdinalIgnoreCase) && !scenesPresentBeforeLoad.Contains(scene) && !BasisWorldSceneGate.IsClaimed(scene)) return scene;
        }
        return new Scene();
    }
    private static async Task UnloadRejectedScene(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded) return;
        BasisWorldSceneGate.Lock(scene);
        AsyncOperation unload = SceneManager.UnloadSceneAsync(scene);
        if (unload == null) return;
        while (!unload.isDone)
        {
            await Task.Yield();
        }
    }
}
