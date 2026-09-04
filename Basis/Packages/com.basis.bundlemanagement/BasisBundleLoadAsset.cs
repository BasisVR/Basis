using System.Collections.Generic;
using System.Threading.Tasks;
using Basis.Scripts.BasisSdk;
using UnityEngine;
using UnityEngine.SceneManagement;
using static BundledContentHolder;
public static class BasisBundleLoadAsset
{
    public static async Task<GameObject> LoadFromWrapper(GameObject DisabledGameobject,BasisTrackedBundleWrapper BasisLoadableBundle, bool UseContentRemoval, Vector3 Position, Quaternion Rotation, bool ModifyScale, Vector3 Scale, Selector Selector, Transform Parent = null, bool DestroyColliders = false,bool ChangeColidersToCorrectLayer = false, List<BasisHeadChop.HeadChopTarget> HarvestedHeadChop = null)
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
        // Instantiate (phase one) and the component strip/scrub walk (phase two) are
        // each a multi-ms main-thread cost; running both in one frame is the load hitch.
        // BeginContentControl parks the clone inactive after Instantiate; yield a frame
        // before FinishContentControl runs the walk + activate so the two never share a
        // frame. The budget gate still spreads concurrent loads across frames on top of that.
        await BasisLoadFrameBudget.WaitForBudgetAsync();
        double instantiateStart = BasisLoadFrameBudget.BeginStep();
        ContentPoliceControl.ContentControlState scrubState = ContentPoliceControl.BeginContentControl(DisabledGameobject, loadedObject, ChecksRequired, Position, Rotation, ModifyScale, Scale, Selector, Parent, LayerMask.NameToLayer("IgnoredByInteractable"), HarvestedHeadChop, harvest);
        BasisLoadFrameBudget.EndStep(instantiateStart);
        GameObject CreatedCopy;
        if (scrubState.RemovalWalkPending)
        {
            await Task.Yield();
            await BasisLoadFrameBudget.WaitForBudgetAsync();
            double walkStart = BasisLoadFrameBudget.BeginStep();
            CreatedCopy = ContentPoliceControl.FinishContentControl(scrubState);
            BasisLoadFrameBudget.EndStep(walkStart);
        }
        else
        {
            CreatedCopy = ContentPoliceControl.FinishContentControl(scrubState);
        }
        if (CreatedCopy == null)
        {
            BasisDebug.LogError("ContentControl returned null; clone was destroyed during the frame-split load.");
            return null;
        }
        if (harvest != null && CreatedCopy != null && CreatedCopy.TryGetComponent(out Basis.Scripts.BasisSdk.BasisAvatar createdAvatar))
        {
            createdAvatar.Harvest = harvest;
        }
        // The worn-instance reservation is taken by the LOAD entry points (BasisLoadHandler)
        // BEFORE their first await — incrementing here, after the multi-frame budgeted
        // instantiate, left a window where the unload grace re-check saw zero holders and
        // Unload(true) destroyed the assets under this very clone.
        string InstanceID = BasisGenerateUniqueID.GenerateUniqueID();
        CreatedCopy.name = InstanceID;
        return CreatedCopy;
    }
    public static async Task<Scene> LoadSceneFromBundleAsync(BasisTrackedBundleWrapper bundle, bool MakeActiveScene, BasisProgressReport progressCallback)
    {
        string UniqueID = BasisGenerateUniqueID.GenerateUniqueID();
        bool AssignedIncrement = false;
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
            string sceneName = System.IO.Path.GetFileNameWithoutExtension(scenePaths[0]);
            // Load the scene asynchronously
            AsyncOperation asyncLoad = SceneManager.LoadSceneAsync(scenePaths[0], LoadSceneMode.Additive);
            asyncLoad.allowSceneActivation = true;
            while (!asyncLoad.isDone)
            {
                progressCallback.ReportProgress(UniqueID, 50 + asyncLoad.progress * 50, $"Activating scene {sceneName}");
                await Task.Yield();
            }

            BasisDebug.Log("Scene loaded successfully from AssetBundle.");
            Scene loadedScene = SceneManager.GetSceneByPath(scenePaths[0]);
            bundle.MetaLink = loadedScene.path;
            // Set the loaded scene as the active scene
            if (loadedScene.IsValid())
            {
                ChecksRequired ChecksRequired = new ChecksRequired();
                ChecksRequired.UseContentRemoval = true;
                ChecksRequired.ScrubPersistentUnityEvents = true;
                ContentPoliceControl.ContentControl(ChecksRequired, Selector.World, loadedScene, true);
                AssignedIncrement = bundle.Increment();
                if (MakeActiveScene)
                {
                    SceneManager.SetActiveScene(loadedScene);
                    BasisDebug.Log("Scene set as active: " + loadedScene.name);
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

// Cooperative per-frame budget for the synchronous Instantiate + ContentPolice scrub tail of a
// bundle load (that work can't leave the main thread, so concurrent loads otherwise stack their
// tails into one hitch). Loads call WaitForBudgetAsync before the heavy step; once a frame has
// spent FrameBudgetMs on load tails, further loads defer to the next frame. Light frames still
// run many back-to-back, so mass joins aren't throttled. Main-thread only (no locks needed).
public static class BasisLoadFrameBudget
{
    // <= 0 disables the gate (original stack-in-one-frame behavior).
    public static double FrameBudgetMs = 4.0;

    private static int _budgetFrame = -1;
    private static double _spentThisFrameMs;

    public static async Task WaitForBudgetAsync()
    {
        if (FrameBudgetMs <= 0) return;
        ResetIfNewFrame();
        while (_spentThisFrameMs >= FrameBudgetMs)
        {
            await Task.Yield();
            ResetIfNewFrame();
        }
    }

    public static double BeginStep() => Time.realtimeSinceStartupAsDouble * 1000.0;

    public static void EndStep(double startMs)
    {
        if (FrameBudgetMs <= 0) return;
        ResetIfNewFrame();
        _spentThisFrameMs += Time.realtimeSinceStartupAsDouble * 1000.0 - startMs;
    }

    private static void ResetIfNewFrame()
    {
        int frame = Time.frameCount;
        if (frame != _budgetFrame)
        {
            _budgetFrame = frame;
            _spentThisFrameMs = 0;
        }
    }
}
