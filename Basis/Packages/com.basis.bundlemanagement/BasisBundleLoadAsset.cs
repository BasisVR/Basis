using Basis.Scripts.BasisSdk;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using static BundledContentHolder;
public static class BasisBundleLoadAsset
{
    public static async Task<GameObject> LoadFromWrapper(BasisTrackedBundleWrapper BasisLoadableBundle, bool UseContentRemoval, Vector3 Position, Quaternion Rotation,bool ModifyScale,Vector3 Scale, Selector Selector, Transform Parent = null, bool DestroyColliders = false)
    {
        bool Incremented = false;
        if (BasisLoadableBundle.AssetBundle != null)
        {
            BasisLoadableBundle output = BasisLoadableBundle.LoadableBundle;
            if (output.BasisBundleConnector.GetPlatform(out BasisBundleGenerated Generated))
            {
                switch (Generated.AssetMode)
                {
                    case "GameObject":
                        {
                            string ReplacedName = Generated.AssetToLoadName.Replace(".bundle", ".prefab");

                            AssetBundleRequest Request = BasisLoadableBundle.AssetBundle.LoadAssetAsync<GameObject>(ReplacedName);//assets/temporarystorage/19a260b00e5f474f8472fa9dea4ca2a920250227.prefab
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
                            ChecksRequired ChecksRequired = new ChecksRequired();
                            if (loadedObject.TryGetComponent<BasisAvatar>(out BasisAvatar BasisAvatar))
                            {
                                ChecksRequired.DisableAnimatorEvents = true;
                            }
                            ChecksRequired.UseContentRemoval = UseContentRemoval;
                            ChecksRequired.RemoveColliders = DestroyColliders;
                            GameObject CreatedCopy = ContentPoliceControl.ContentControl(loadedObject, ChecksRequired, Position, Rotation, ModifyScale, Scale, Selector, Parent);
                            Incremented = BasisLoadableBundle.Increment();
                            return CreatedCopy;
                        }
                    default:
                        BasisDebug.LogError("Requested type " + Generated.AssetMode + " has no handler");
                        return null;
                }
            }
            else
            {
                BasisDebug.LogError("Missing Platform Bundle! cant find : "+ Application.platform);
            }
        }
        else
        {
            BasisDebug.LogError("Missing Bundle!");
        }
        BasisDebug.LogError("Returning unable to load gameobject!");
        return null;
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
            // Load the scene asynchronously
            AsyncOperation asyncLoad = SceneManager.LoadSceneAsync(scenePaths[0], LoadSceneMode.Additive);
            // Track scene loading progress
            while (!asyncLoad.isDone)
            {
                progressCallback.ReportProgress(UniqueID,50 + asyncLoad.progress * 50, "loading scene"); // Progress from 50 to 100 during scene load
                await Task.Yield();
            }

            BasisDebug.Log("Scene loaded successfully from AssetBundle.");
            Scene loadedScene = SceneManager.GetSceneByPath(scenePaths[0]);
            bundle.MetaLink = loadedScene.path;
            // Set the loaded scene as the active scene
            if (loadedScene.IsValid())
            {
                if (MakeActiveScene)
                {
                    SceneManager.SetActiveScene(loadedScene);
                    AssignedIncrement = bundle.Increment();
                }
                BasisDebug.Log("Scene set as active: " + loadedScene.name);
                progressCallback.ReportProgress(UniqueID, 100, "loading scene"); // Set progress to 100 when done
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
