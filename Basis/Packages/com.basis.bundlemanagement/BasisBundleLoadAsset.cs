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
    public static async Task<GameObject> LoadFromWrapper(GameObject DisabledGameobject, BasisTrackedBundleWrapper BasisLoadableBundle, bool UseContentRemoval, Vector3 Position, Quaternion Rotation, bool ModifyScale, Vector3 Scale, Selector Selector, Transform Parent = null, bool DestroyColliders = false, bool ChangeColidersToCorrectLayer = false, List<BasisHeadChop.HeadChopTarget> HarvestedHeadChop = null)
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
                            return await InstantiateContentControlled(DisabledGameobject, BasisLoadableBundle, loadedObject, UseContentRemoval, Position, Rotation, ModifyScale, Scale, Selector, Parent, DestroyColliders, ChangeColidersToCorrectLayer, HarvestedHeadChop);
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
                            return await InstantiateContentControlled(DisabledGameobject, BasisLoadableBundle, template, UseContentRemoval, Position, Rotation, ModifyScale, Scale, Selector, Parent, DestroyColliders, ChangeColidersToCorrectLayer, HarvestedHeadChop);
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

    private static async Task<GameObject> InstantiateContentControlled(GameObject DisabledGameobject, BasisTrackedBundleWrapper BasisLoadableBundle, GameObject loadedObject, bool UseContentRemoval, Vector3 Position, Quaternion Rotation, bool ModifyScale, Vector3 Scale, Selector Selector, Transform Parent, bool DestroyColliders, bool ChangeColidersToCorrectLayer, List<BasisHeadChop.HeadChopTarget> HarvestedHeadChop)
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
        ContentPoliceControl.ContentControlState scrubState = ContentPoliceControl.BeginContentControl(DisabledGameobject, loadedObject, ChecksRequired, Position, Rotation, ModifyScale, Scale, Selector, Parent, LayerMask.NameToLayer("IgnoredByInteractable"), HarvestedHeadChop, harvest, BasisLoadableBundle.EmbeddedGraphicsStates, emissionPostprocessor);
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
            List<GameObject> roots = null;
            List<Renderer> gatedRenderers = null;
            bool[] previousForceRenderingOff = null;

            // sceneLoaded runs before Start and before the first rendered frame. Gate only the
            // renderers: disabling whole roots also disables terrain/ground colliders, allowing
            // the player to fall below the world while PSOs warm.
            void GateSceneRenderers(Scene scene)
            {
                roots = new List<GameObject>();
                scene.GetRootGameObjects(roots);
                gatedRenderers = new List<Renderer>();
                int RootCounts = roots.Count;
                for (int Index = 0; Index < RootCounts; Index++)
                {
                    roots[Index].GetComponentsInChildren(true, gatedRenderers);
                }

                previousForceRenderingOff = new bool[gatedRenderers.Count];
                for (int i = 0; i < gatedRenderers.Count; i++)
                {
                    Renderer renderer = gatedRenderers[i];
                    if (renderer == null)
                    {
                        continue;
                    }

                    previousForceRenderingOff[i] = renderer.forceRenderingOff;
                    renderer.forceRenderingOff = true;
                }
            }

            void OnSceneLoaded(Scene scene, LoadSceneMode mode)
            {
                if (string.Equals(scene.path, scenePath, StringComparison.OrdinalIgnoreCase))
                {
                    GateSceneRenderers(scene);
                }
            }

            void RestoreSceneRenderers()
            {
                if (gatedRenderers == null || previousForceRenderingOff == null)
                {
                    return;
                }
                int GatedRendersCount = gatedRenderers.Count;
                for (int Index = 0; Index < GatedRendersCount; Index++)
                {
                    Renderer renderer = gatedRenderers[Index];
                    if (renderer != null) renderer.forceRenderingOff = previousForceRenderingOff[Index];
                }
            }

            SceneManager.sceneLoaded += OnSceneLoaded;
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
                SceneManager.sceneLoaded -= OnSceneLoaded;
            }

            BasisDebug.Log("Scene loaded successfully from AssetBundle.");
            Scene loadedScene = SceneManager.GetSceneByPath(scenePath);
            bundle.MetaLink = loadedScene.path;
            if (loadedScene.IsValid())
            {
                // Defensive fallback if Unity did not deliver sceneLoaded for this path.
                if (roots == null)
                {
                    GateSceneRenderers(loadedScene);
                }

                ChecksRequired ChecksRequired = new ChecksRequired
                {
                    UseContentRemoval = true,
                    ScrubPersistentUnityEvents = true
                };
                // Scene shaders become resident as part of additive scene load. Loading the
                // collection here lets Unity resolve them before we select and warm its PSOs.
                try
                {
                    await bundle.EnsureEmbeddedGraphicsStatesLoaded(progressCallback);
                    BasisGraphicsStatePrewarm.WarmupRequest warmup = ContentPoliceControl.ContentControl(
                        ChecksRequired,
                        Selector.World,
                        loadedScene,
                        true,
                        bundle.EmbeddedGraphicsStates,
                        requiresLegacyEmissionUpgrade
                            ? renderers => BasisLegacyMaterialCompatibility.UpgradeEmission(renderers, bundle.BuiltWithUnityVersion)
                            : null);
                    while (!warmup.IsCompleted)
                    {
                        await Task.Yield();
                    }
                    warmup.CompleteAndDispose();

                }
                finally
                {
                    RestoreSceneRenderers();
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
                RestoreSceneRenderers();
                BasisDebug.LogError("Failed to get loaded scene.");
            }
        }
        else
        {
            BasisDebug.LogError("Path was null or empty! this should not be happening!");
        }
        return new Scene();
    }
}
