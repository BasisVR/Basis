using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using static BasisSerialization;
using static BundledContentHolder;
public static class BasisLoadHandler
{
    public static bool IsInitialized = false;
    public static ConcurrentDictionary<string, BasisTrackedBundleWrapper> LoadedBundles = new ConcurrentDictionary<string, BasisTrackedBundleWrapper>();
    public static ConcurrentDictionary<string, BasisBEEExtensionMeta> OnDiscData = new ConcurrentDictionary<string, BasisBEEExtensionMeta>();
    public static readonly object _discInfoLock = new object();
    public static SemaphoreSlim _initSemaphore = new SemaphoreSlim(1, 1);
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static async void Initialization()
    {
        BasisDebug.Log("Game has started after scene load.", BasisDebug.LogTag.Event);
        await EnsureInitializationComplete();
        SceneManager.sceneUnloaded += SceneUnloaded;
    }
    private static async void SceneUnloaded(Scene UnloadedScene)
    {
        if (LoadedBundles == null || string.IsNullOrEmpty(UnloadedScene.path))
        {
            return;
        }
        List<string> keysToRemove = new List<string>();
        foreach (KeyValuePair<string, BasisTrackedBundleWrapper> kvp in LoadedBundles)
        {
            var bundle = kvp.Value;

            if (bundle == null || string.IsNullOrEmpty(bundle.MetaLink))
                continue;

            if (bundle.MetaLink == UnloadedScene.path)
            {
                bundle.DeIncrement();

                bool state = false;
                try
                {
                    state = await bundle.UnloadIfReady();
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Error while unloading bundle '{kvp.Key}': {ex}");
                }

                if (state)
                {
                    keysToRemove.Add(kvp.Key);
                }
            }
        }

        foreach (string key in keysToRemove)
        {
            LoadedBundles.TryRemove(key, out var data);
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
        string CombinedURL = loadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation;
        if (LoadedBundles.TryGetValue(CombinedURL, out BasisTrackedBundleWrapper Wrapper))
        {
            Wrapper.DeIncrement();
            bool State = await Wrapper.UnloadIfReady();
            if (State)
            {
                LoadedBundles.Remove(CombinedURL, out var data);
                return;
            }
        }
        else
        {
            if (CombinedURL.ToLower() != BasisBeeConstants.DefaultAvatar.ToLower())
            {
                BasisDebug.LogError($"tried to find Loaded Key {CombinedURL} but could not find it!");
            }
        }
    }
    public static async Task<GameObject> LoadGameObjectBundle(GameObject DisabledGameobject,BasisLoadableBundle loadableBundle, bool useContentRemoval, BasisProgressReport report, CancellationToken cancellationToken, Vector3 Position, Quaternion Rotation, Vector3 Scale, bool ModifyScale, Selector Selector, Transform Parent = null, bool DestroyColliders = false,bool ChangeColidersToCorrectLayer = false, long MaxDownloadSizeInMB = 4L * 1024 * 1024 * 1024)
    {
        await EnsureInitializationComplete();

        if (LoadedBundles.TryGetValue(loadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation, out BasisTrackedBundleWrapper wrapper))
        {
            try
            {
                await wrapper.WaitForBundleLoadAsync();

                // ensure the bundle connector is updated from the wrapper
                loadableBundle.BasisBundleConnector = wrapper.LoadableBundle.BasisBundleConnector;

                return await BasisBundleLoadAsset.LoadFromWrapper(DisabledGameobject,wrapper, useContentRemoval, Position, Rotation, ModifyScale, Scale, Selector, Parent, DestroyColliders, ChangeColidersToCorrectLayer);
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"Failed to load content: {ex}");
                LoadedBundles.Remove(loadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation, out var data);
                return null;
            }
        }

        return await HandleFirstBundleLoad(DisabledGameobject,loadableBundle, useContentRemoval, report, cancellationToken, Position, Rotation, Scale, ModifyScale, Selector, Parent, DestroyColliders, ChangeColidersToCorrectLayer, MaxDownloadSizeInMB);
    }
    public static async Task<Scene> LoadSceneBundle(bool makeActiveScene, BasisLoadableBundle loadableBundle, BasisProgressReport report, CancellationToken cancellationToken, long MaxDownloadSizeInMB = 4L * 1024 * 1024 * 1024)
    {
        await EnsureInitializationComplete();

        if (LoadedBundles.TryGetValue(loadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation, out BasisTrackedBundleWrapper wrapper))
        {
            BasisDebug.Log($"Bundle On Disc Loading", BasisDebug.LogTag.Networking);
            await wrapper.WaitForBundleLoadAsync();
            BasisDebug.Log($"Bundle Loaded, Loading Scene", BasisDebug.LogTag.Networking);
            return await BasisBundleLoadAsset.LoadSceneFromBundleAsync(wrapper, makeActiveScene, report);
        }

        return await HandleFirstSceneLoad(loadableBundle, makeActiveScene, report, cancellationToken, MaxDownloadSizeInMB);
    }

    private static async Task<Scene> HandleFirstSceneLoad(BasisLoadableBundle loadableBundle, bool makeActiveScene, BasisProgressReport report, CancellationToken cancellationToken, long MaxDownloadSizeInMB = 4L * 1024 * 1024 * 1024)
    {
        BasisTrackedBundleWrapper wrapper = new BasisTrackedBundleWrapper { AssetBundle = null, LoadableBundle = loadableBundle };

        if (!LoadedBundles.TryAdd(loadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation, wrapper))
        {
            BasisDebug.LogError("Unable to add bundle wrapper.");
            return new Scene();
        }

        await BasisBeeManagement.HandleBundleAndMetaLoading(wrapper, report, cancellationToken, MaxDownloadSizeInMB);
        return await BasisBundleLoadAsset.LoadSceneFromBundleAsync(wrapper, makeActiveScene, report);
    }

    private static async Task<GameObject> HandleFirstBundleLoad(GameObject DisabledGameobject,BasisLoadableBundle loadableBundle, bool useContentRemoval, BasisProgressReport report, CancellationToken cancellationToken, Vector3 Position, Quaternion Rotation, Vector3 Scale, bool ModifyScale, Selector Selector, Transform Parent = null, bool DestroyColliders = false, bool ChangeColidersToCorrectLayer = false, long MaxDownloadSizeInMB = 4L * 1024 * 1024 * 1024)
    {
        BasisTrackedBundleWrapper wrapper = new BasisTrackedBundleWrapper
        {
            AssetBundle = null,
            LoadableBundle = loadableBundle
        };

        if (!LoadedBundles.TryAdd(loadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation, wrapper))
        {
            BasisDebug.LogError("Unable to add bundle wrapper.");
            return null;
        }

        try
        {
            await BasisBeeManagement.HandleBundleAndMetaLoading(wrapper, report, cancellationToken, MaxDownloadSizeInMB);
            return await BasisBundleLoadAsset.LoadFromWrapper(DisabledGameobject, wrapper, useContentRemoval, Position, Rotation, ModifyScale, Scale, Selector, Parent, DestroyColliders, ChangeColidersToCorrectLayer);
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"{ex.Message} {ex.StackTrace}");
            LoadedBundles.Remove(loadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation, out var data);
            CleanupFiles(loadableBundle.BasisLocalEncryptedBundle);
            OnDiscData.TryRemove(loadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation, out _);
            return null;
        }
    }
    public static bool IsMetaDataOnDisc(string MetaURL, out BasisBEEExtensionMeta info)
    {
        lock (_discInfoLock)
        {
            foreach (var discInfo in OnDiscData.Values)
            {
                if (discInfo.StoredRemote.RemoteBeeFileLocation == MetaURL)
                {
                    info = discInfo;

                    if (string.IsNullOrEmpty(discInfo.StoredLocal.DownloadedBeeFileLocation))
                    {
                        string BEEPath = BasisIOManagement.GenerateFilePath($"{info.UniqueVersion}{BasisBeeConstants.BasisEncryptedExtension}", BasisBeeConstants.AssetBundlesFolder);
                        if (File.Exists(BEEPath))
                        {
                            discInfo.StoredLocal.DownloadedBeeFileLocation = BEEPath;
                            return true;
                        }
                    }
                    else
                    {
                        if (File.Exists(discInfo.StoredLocal.DownloadedBeeFileLocation))
                        {
                            return true;
                        }
                    }
                }
            }

            info = new BasisBEEExtensionMeta();
            return false;
        }
    }

    public static async Task AddDiscInfo(BasisBEEExtensionMeta discInfo)
    {
        if (OnDiscData.TryAdd(discInfo.StoredRemote.RemoteBeeFileLocation, discInfo))
        {
        }
        else
        {
            OnDiscData[discInfo.StoredRemote.RemoteBeeFileLocation] = discInfo;
            BasisDebug.Log("Disc info updated.", BasisDebug.LogTag.Event);
        }
        string filePath = BasisIOManagement.GenerateFilePath($"{discInfo.UniqueVersion}{BasisBeeConstants.BasisMetaExtension}", BasisBeeConstants.AssetBundlesFolder);
        byte[] serializedData = BasisSerialization.SerializeValue(discInfo);

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
        BasisStorageManagement.DeleteStoredFile(metaUrl);
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
        string path = BasisIOManagement.GenerateFolderPath(BasisBeeConstants.AssetBundlesFolder);
        string[] files = Directory.GetFiles(path, $"*{BasisBeeConstants.BasisMetaExtension}");

        List<Task> loadTasks = new List<Task>();

        foreach (string file in files)
        {
            loadTasks.Add(Task.Run(async () =>
            {
               // BasisDebug.Log($"Loading file: {file}");
                try
                {
                    byte[] fileData = await File.ReadAllBytesAsync(file);
                    BasisBEEExtensionMeta discInfo = BasisSerialization.DeserializeValue<BasisBEEExtensionMeta>(fileData);
                    OnDiscData.TryAdd(discInfo.StoredRemote.RemoteBeeFileLocation, discInfo);
                }
                catch (Exception ex)
                {
                    BasisDebug.LogError($"Failed to load disc info from {file}: {ex.Message}", BasisDebug.LogTag.Event);
                    File.Delete(file);
                }
            }));
        }

        await Task.WhenAll(loadTasks);

        BasisDebug.Log("Completed loading all disc data.");
    }

    private static void CleanupFiles(BasisStoredEncryptedBundle bundle)
    {
        if (File.Exists(bundle.DownloadedBeeFileLocation))
        {
            File.Delete(bundle.DownloadedBeeFileLocation);
        }
    }
}
