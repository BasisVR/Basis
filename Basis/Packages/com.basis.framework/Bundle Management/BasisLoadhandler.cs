using Basis.Scripts.Avatar;
using BasisSerializer.OdinSerializer;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using static BundledContentHolder;

public static class BasisLoadHandler
{
    public static Dictionary<string, BasisTrackedBundleWrapper> LoadedBundles = new Dictionary<string, BasisTrackedBundleWrapper>();
    public static ConcurrentDictionary<string, BasisOnDiscInformation> OnDiscData = new ConcurrentDictionary<string, BasisOnDiscInformation>();
    public static bool IsInitialized = false;

    private static readonly object _discInfoLock = new object();
    private static SemaphoreSlim _initSemaphore = new SemaphoreSlim(1, 1);
    public static int TimeUntilMemoryRemoval = 30;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static async Task OnGameStart()
    {
        BasisDebug.Log("Game has started after scene load.", BasisDebug.LogTag.Event);
        await EnsureInitializationComplete();
        SceneManager.sceneUnloaded += sceneUnloaded;
    }

    private static async void sceneUnloaded(Scene UnloadedScene)
    {
        foreach (KeyValuePair<string, BasisTrackedBundleWrapper> kvp in LoadedBundles)
        {
            if (kvp.Value != null)
            {
                if (kvp.Value.MetaLink == UnloadedScene.path)
                {
                    kvp.Value.DeIncrement();
                    bool State = await kvp.Value.UnloadIfReady();
                    if (State)
                    {
                        LoadedBundles.Remove(kvp.Key);
                        return;
                    }
                }
            }
        }
    }
    /// <summary>
    /// this will take 30 seconds to execute
    /// after that we wait for 30 seconds to see if we can also remove the bundle!
    /// </summary>
    /// <param name="LoadedKey"></param>
    /// <returns></returns>
    public static async Task RequestDeIncrementOfBundle(BasisLoadableBundle loadableBundle)
    {
        string CombinedURL = loadableBundle.BasisRemoteBundleEncrypted.CombinedURL;
        if (LoadedBundles.TryGetValue(CombinedURL, out BasisTrackedBundleWrapper Wrapper))
        {
            Wrapper.DeIncrement();
            bool State = await Wrapper.UnloadIfReady();
            if (State)
            {
                LoadedBundles.Remove(CombinedURL);
                return;
            }
        }
        else
        {
            if (CombinedURL.ToLower() != BasisAvatarFactory.LoadingAvatar.BasisRemoteBundleEncrypted.CombinedURL.ToLower())
            {
                BasisDebug.LogError($"tried to find Loaded Key {CombinedURL} but could not find it!");
            }
        }
    }
    public static async Task<GameObject> LoadGameObjectBundle(BasisLoadableBundle loadableBundle, bool useContentRemoval, BasisProgressReport report, CancellationToken cancellationToken, Vector3 Position, Quaternion Rotation, Vector3 Scale, bool ModifyScale, Selector Selector, Transform Parent = null, bool DestroyColliders = false)
    {
        await EnsureInitializationComplete();

        if (LoadedBundles.TryGetValue(loadableBundle.BasisRemoteBundleEncrypted.CombinedURL, out BasisTrackedBundleWrapper wrapper))
        {
            try
            {
                await wrapper.WaitForBundleLoadAsync();
                return await BasisBundleLoadAsset.LoadFromWrapper(wrapper, useContentRemoval, Position, Rotation, ModifyScale, Scale, Selector, Parent, DestroyColliders);
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"Failed to load content: {ex}");
                LoadedBundles.Remove(loadableBundle.BasisRemoteBundleEncrypted.CombinedURL);
                return null;
            }
        }

        return await HandleFirstBundleLoad(loadableBundle, useContentRemoval, report, cancellationToken, Position, Rotation, Scale, ModifyScale, Selector, Parent, DestroyColliders);
    }

    public static async Task<Scene> LoadSceneBundle(bool makeActiveScene, BasisLoadableBundle loadableBundle, BasisProgressReport report, CancellationToken cancellationToken)
    {
        await EnsureInitializationComplete();

        if (LoadedBundles.TryGetValue(loadableBundle.BasisRemoteBundleEncrypted.CombinedURL, out BasisTrackedBundleWrapper wrapper))
        {
            BasisDebug.Log($"Bundle On Disc Loading", BasisDebug.LogTag.Networking);
            await wrapper.WaitForBundleLoadAsync();
            BasisDebug.Log($"Bundle Loaded, Loading Scene", BasisDebug.LogTag.Networking);
            return await BasisBundleLoadAsset.LoadSceneFromBundleAsync(wrapper, makeActiveScene, report);
        }

        return await HandleFirstSceneLoad(loadableBundle, makeActiveScene, report, cancellationToken);
    }

    private static async Task<Scene> HandleFirstSceneLoad(BasisLoadableBundle loadableBundle, bool makeActiveScene, BasisProgressReport report, CancellationToken cancellationToken)
    {
        BasisTrackedBundleWrapper wrapper = new BasisTrackedBundleWrapper { AssetBundle = null, LoadableBundle = loadableBundle };

        if (!LoadedBundles.TryAdd(loadableBundle.BasisRemoteBundleEncrypted.CombinedURL, wrapper))
        {
            BasisDebug.LogError("Unable to add bundle wrapper.");
            return new Scene();
        }

        await HandleBundleAndMetaLoading(wrapper, report, cancellationToken);
        return await BasisBundleLoadAsset.LoadSceneFromBundleAsync(wrapper, makeActiveScene, report);
    }

    private static async Task<GameObject> HandleFirstBundleLoad(BasisLoadableBundle loadableBundle, bool useContentRemoval, BasisProgressReport report, CancellationToken cancellationToken, Vector3 Position, Quaternion Rotation, Vector3 Scale, bool ModifyScale,Selector Selector, Transform Parent = null, bool DestroyColliders = false)
    {
        BasisTrackedBundleWrapper wrapper = new BasisTrackedBundleWrapper
        {
            AssetBundle = null,
            LoadableBundle = loadableBundle
        };

        if (!LoadedBundles.TryAdd(loadableBundle.BasisRemoteBundleEncrypted.CombinedURL, wrapper))
        {
            BasisDebug.LogError("Unable to add bundle wrapper.");
            return null;
        }

        try
        {
            await HandleBundleAndMetaLoading(wrapper, report, cancellationToken);
            return await BasisBundleLoadAsset.LoadFromWrapper(wrapper, useContentRemoval, Position, Rotation, ModifyScale, Scale, Selector, Parent, DestroyColliders);
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"{ex.Message} {ex.StackTrace}");
            LoadedBundles.Remove(loadableBundle.BasisRemoteBundleEncrypted.CombinedURL);
            CleanupFiles(loadableBundle.BasisLocalEncryptedBundle);
            OnDiscData.TryRemove(loadableBundle.BasisRemoteBundleEncrypted.CombinedURL, out _);
            return null;
        }
    }

    public static async Task HandleBundleAndMetaLoading(BasisTrackedBundleWrapper wrapper, BasisProgressReport report, CancellationToken cancellationToken)
    {
        bool IsMetaOnDisc = IsMetaDataOnDisc(wrapper.LoadableBundle.BasisRemoteBundleEncrypted.CombinedURL, out BasisOnDiscInformation MetaInfo);

        (BasisBundleGenerated, byte[],string) output = new(null, null,string.Empty);
        if (IsMetaOnDisc)
        {
            BasisDebug.Log("Process On Disc Meta Data Async", BasisDebug.LogTag.Event);
            output = await BasisBundleManagement.ProcessOnDiscMetaDataAsync(wrapper, MetaInfo.StoredLocal, report, cancellationToken);
        }
        else
        {
            BasisDebug.Log("Download Store Meta And Bundle", BasisDebug.LogTag.Event);
            output = await BasisBundleManagement.DownloadStoreMetaAndBundle(wrapper, report, cancellationToken);
        }
        if(output.Item1 == null || output.Item3 != string.Empty)
        {
            new Exception("missing Bundle Bytes Array Error Message " + output.Item3);
        }
        IEnumerable<AssetBundle> AssetBundles = AssetBundle.GetAllLoadedAssetBundles();
        foreach (AssetBundle assetBundle in AssetBundles)
        {
           string AssetToLoadName = output.Item1.AssetToLoadName;
            if (assetBundle != null && assetBundle.Contains(AssetToLoadName))
            {
                wrapper.AssetBundle = assetBundle;
                BasisDebug.Log($"we already have this AssetToLoadName in our loaded bundles using that instead! {AssetToLoadName}");
                if (IsMetaOnDisc == false)
                {
                    BasisOnDiscInformation newDiscInfo = new BasisOnDiscInformation
                    {
                        StoredRemote = wrapper.LoadableBundle.BasisRemoteBundleEncrypted,
                        StoredLocal = wrapper.LoadableBundle.BasisLocalEncryptedBundle,
                        UniqueVersion = wrapper.LoadableBundle.BasisBundleConnector.UniqueVersion,
                    };

                    await AddDiscInfo(newDiscInfo);
                }
                return;
            }
        }
        if(output.Item2 == null)
        {
            BasisDebug.LogError("Missing BundleArray");
            return;
        }
        AssetBundleCreateRequest bundleRequest = await BasisEncryptionToData.GenerateBundleFromFile( wrapper.LoadableBundle.UnlockPassword, output.Item2, output.Item1.AssetBundleCRC,report
        );

        wrapper.AssetBundle = bundleRequest.assetBundle;

        if (IsMetaOnDisc == false)
        {
            BasisOnDiscInformation newDiscInfo = new BasisOnDiscInformation
            {
                StoredRemote = wrapper.LoadableBundle.BasisRemoteBundleEncrypted,
                StoredLocal = wrapper.LoadableBundle.BasisLocalEncryptedBundle,
                UniqueVersion = wrapper.LoadableBundle.BasisBundleConnector.UniqueVersion,
            };

            await AddDiscInfo(newDiscInfo);
        }
    }
    public static bool IsMetaDataOnDisc(string MetaURL, out BasisOnDiscInformation info)
    {
        lock (_discInfoLock)
        {
            foreach (var discInfo in OnDiscData.Values)
            {
                if (discInfo.StoredRemote.CombinedURL == MetaURL)
                {
                    info = discInfo;
                    if (File.Exists(discInfo.StoredLocal.LocalConnectorPath))
                    {
                        return true;
                    }
                }
            }

            info = new BasisOnDiscInformation();
            return false;
        }
    }
    public static bool IsBundleDataOnDisc(string BundleURL, out BasisOnDiscInformation info)
    {
        lock (_discInfoLock)
        {
            foreach (var discInfo in OnDiscData.Values)
            {
                if (discInfo.StoredRemote.CombinedURL == BundleURL)
                {
                    info = discInfo;
                    if (File.Exists(discInfo.StoredLocal.LocalConnectorPath))
                    {
                        return true;
                    }
                }
            }

            info = new BasisOnDiscInformation();
            return false;
        }
    }

    public static async Task AddDiscInfo(BasisOnDiscInformation discInfo)
    {
        if (OnDiscData.TryAdd(discInfo.StoredRemote.CombinedURL, discInfo))
        {
        }
        else
        {
            OnDiscData[discInfo.StoredRemote.CombinedURL] = discInfo;
            BasisDebug.Log("Disc info updated.", BasisDebug.LogTag.Event);
        }
        string filePath = BasisIOManagement.GenerateFilePath($"{discInfo.UniqueVersion}{BasisBundleManagement.BasisMetaExtension}", BasisBundleManagement.AssetBundlesFolder);
        byte[] serializedData = SerializationUtility.SerializeValue(discInfo, DataFormat.Binary);

        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
            await File.WriteAllBytesAsync(filePath, serializedData);
            BasisDebug.Log($"Disc info saved to {filePath}", BasisDebug.LogTag.Event);
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"Failed to save disc info: {ex.Message}", BasisDebug.LogTag.Event);
        }
    }

    public static void RemoveDiscInfo(string metaUrl)
    {
        if (OnDiscData.TryRemove(metaUrl, out _))
        {
            string filePath = BasisIOManagement.GenerateFilePath($"{metaUrl}{BasisBundleManagement.BasisEncryptedExtension}", BasisBundleManagement.AssetBundlesFolder);

            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                BasisDebug.Log($"Deleted disc info from {filePath}", BasisDebug.LogTag.Event);
            }
            else
            {
                BasisDebug.LogWarning($"File not found at {filePath}", BasisDebug.LogTag.Event);
            }
        }
        else
        {
            BasisDebug.LogError("Disc info not found or already removed.", BasisDebug.LogTag.Event);
        }
    }

    private static async Task EnsureInitializationComplete()
    {
        if (!IsInitialized)
        {
            await _initSemaphore.WaitAsync();
            try
            {
                if (!IsInitialized)
                {
                    await LoadAllDiscData();
                    IsInitialized = true;
                }
            }
            finally
            {
                _initSemaphore.Release();
            }
        }
    }

    private static async Task LoadAllDiscData()
    {
        BasisDebug.Log("Loading all disc data...", BasisDebug.LogTag.Event);
        string path = BasisIOManagement.GenerateFolderPath(BasisBundleManagement.AssetBundlesFolder);
        string[] files = Directory.GetFiles(path, $"*{BasisBundleManagement.BasisMetaExtension}");

        List<Task> loadTasks = new List<Task>();

        foreach (string file in files)
        {
            loadTasks.Add(Task.Run(async () =>
            {
                BasisDebug.Log($"Loading file: {file}");
                try
                {
                    byte[] fileData = await File.ReadAllBytesAsync(file);
                    BasisOnDiscInformation discInfo = SerializationUtility.DeserializeValue<BasisOnDiscInformation>(fileData, DataFormat.Binary);
                    OnDiscData.TryAdd(discInfo.StoredRemote.CombinedURL, discInfo);
                }
                catch (Exception ex)
                {
                    BasisDebug.LogError($"Failed to load disc info from {file}: {ex.Message}", BasisDebug.LogTag.Event);
                }
            }));
        }

        await Task.WhenAll(loadTasks);

        BasisDebug.Log("Completed loading all disc data.");
    }

    private static void CleanupFiles(BasisStoredEncryptedBundle bundle)
    {
        if (File.Exists(bundle.LocalConnectorPath))
        {
            File.Delete(bundle.LocalConnectorPath);
        }
    }
}
