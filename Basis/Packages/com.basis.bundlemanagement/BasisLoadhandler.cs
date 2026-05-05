using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Basis.Scripts.BasisSdk;
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
    public static async Task<GameObject> LoadGameObjectBundle(GameObject DisabledGameobject,BasisLoadableBundle loadableBundle, bool useContentRemoval, BasisProgressReport report, CancellationToken cancellationToken, Vector3 Position, Quaternion Rotation, Vector3 Scale, bool ModifyScale, Selector Selector, Transform Parent = null, bool DestroyColliders = false,bool ChangeColidersToCorrectLayer = false, long MaxDownloadSizeInMB = 4L * 1024 * 1024 * 1024, List<BasisHeadChop.HeadChopTarget> HarvestedHeadChop = null)
    {
        await EnsureInitializationComplete();

        if (LoadedBundles.TryGetValue(loadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation, out BasisTrackedBundleWrapper wrapper))
        {
            try
            {
                await wrapper.WaitForBundleLoadAsync();

                // ensure the bundle connector is updated from the wrapper
                loadableBundle.BasisBundleConnector = wrapper.LoadableBundle.BasisBundleConnector;

                return await BasisBundleLoadAsset.LoadFromWrapper(DisabledGameobject,wrapper, useContentRemoval, Position, Rotation, ModifyScale, Scale, Selector, Parent, DestroyColliders, ChangeColidersToCorrectLayer, HarvestedHeadChop);
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"Failed to load content: {ex}");
                LoadedBundles.Remove(loadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation, out var data);
                return null;
            }
        }

        return await HandleFirstBundleLoad(DisabledGameobject,loadableBundle, useContentRemoval, report, cancellationToken, Position, Rotation, Scale, ModifyScale, Selector, Parent, DestroyColliders, ChangeColidersToCorrectLayer, MaxDownloadSizeInMB, HarvestedHeadChop);
    }
    public static async Task<Scene> LoadSceneBundle(bool makeActiveScene, BasisLoadableBundle loadableBundle, BasisProgressReport report, CancellationToken cancellationToken, long MaxDownloadSizeInMB = 4L * 1024 * 1024 * 1024)
    {
        await EnsureInitializationComplete();

        if (LoadedBundles.TryGetValue(loadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation, out BasisTrackedBundleWrapper wrapper))
        {
            BasisDebug.Log($"Bundle On Disc Loading", BasisDebug.LogTag.Networking);
            if (wrapper.AssetBundle == null)
            {
                await BasisBeeManagement.HandleBundleAndMetaLoading(wrapper, report, cancellationToken, MaxDownloadSizeInMB);
            }
            else
            {
                await wrapper.WaitForBundleLoadAsync();
            }

            if (wrapper.AssetBundle == null)
            {
                BasisDebug.LogError("Scene bundle was not available after load attempt.");
                return new Scene();
            }
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

    private static async Task<GameObject> HandleFirstBundleLoad(GameObject DisabledGameobject,BasisLoadableBundle loadableBundle, bool useContentRemoval, BasisProgressReport report, CancellationToken cancellationToken, Vector3 Position, Quaternion Rotation, Vector3 Scale, bool ModifyScale, Selector Selector, Transform Parent = null, bool DestroyColliders = false, bool ChangeColidersToCorrectLayer = false, long MaxDownloadSizeInMB = 4L * 1024 * 1024 * 1024, List<BasisHeadChop.HeadChopTarget> HarvestedHeadChop = null)
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
            return await BasisBundleLoadAsset.LoadFromWrapper(DisabledGameobject, wrapper, useContentRemoval, Position, Rotation, ModifyScale, Scale, Selector, Parent, DestroyColliders, ChangeColidersToCorrectLayer, HarvestedHeadChop);
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"{ex.Message} {ex.StackTrace}");
            LoadedBundles.Remove(loadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation, out var data);
            CleanupFiles(loadableBundle.BasisLocalEncryptedBundle);
            return null;
        }
    }

    public static string GetDiscInfoKey(string remoteUrl, string downloadedPlatform)
    {
        string safeUrl = remoteUrl ?? string.Empty;
        string safePlatform = string.IsNullOrWhiteSpace(downloadedPlatform) ? "legacy" : downloadedPlatform.Trim();
        return $"{safePlatform}|{safeUrl}";
    }

    private static bool TryResolveStoredBeePath(BasisBEEExtensionMeta discInfo, out string beePath)
    {
        beePath = discInfo.StoredLocal.DownloadedBeeFileLocation;
        if (!string.IsNullOrWhiteSpace(beePath) && File.Exists(beePath))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(discInfo.UniqueVersion))
        {
            string platformAwarePath = BasisIOManagement.GetBeeCacheFilePath(discInfo.UniqueVersion, discInfo.DownloadedPlatform);
            if (File.Exists(platformAwarePath))
            {
                discInfo.StoredLocal.DownloadedBeeFileLocation = platformAwarePath;
                beePath = platformAwarePath;
                return true;
            }

            string legacyPath = BasisIOManagement.GetLegacyBeeCacheFilePath(discInfo.UniqueVersion);
            if (File.Exists(legacyPath))
            {
                discInfo.StoredLocal.DownloadedBeeFileLocation = legacyPath;
                beePath = legacyPath;
                return true;
            }
        }

        beePath = null;
        return false;
    }

    private static bool TryResolveStoredConnectorPath(BasisBEEExtensionMeta discInfo, out string connectorPath)
    {
        connectorPath = discInfo.StoredLocal.DownloadedConnectorFileLocation;
        if (!string.IsNullOrWhiteSpace(connectorPath) && File.Exists(connectorPath))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(discInfo.UniqueVersion))
        {
            string platformAwarePath = BasisIOManagement.GetConnectorCacheFilePath(discInfo.UniqueVersion, discInfo.DownloadedPlatform);
            if (File.Exists(platformAwarePath))
            {
                discInfo.StoredLocal.DownloadedConnectorFileLocation = platformAwarePath;
                connectorPath = platformAwarePath;
                return true;
            }
        }

        connectorPath = null;
        return false;
    }

    private static bool HasAnyCachedPayload(BasisBEEExtensionMeta discInfo)
    {
        return TryResolveStoredBeePath(discInfo, out _) || TryResolveStoredConnectorPath(discInfo, out _);
    }

    private static bool TryLoadDiscInfoFromFile(string filePath, string expectedRemoteUrl, out BasisBEEExtensionMeta info)
    {
        info = null;
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        try
        {
            byte[] fileData = File.ReadAllBytes(filePath);
            BasisBEEExtensionMeta discInfo = BasisSerialization.DeserializeValue<BasisBEEExtensionMeta>(fileData);
            if (discInfo?.StoredRemote?.RemoteBeeFileLocation == null)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(expectedRemoteUrl) &&
                !string.Equals(discInfo.StoredRemote.RemoteBeeFileLocation, expectedRemoteUrl, StringComparison.Ordinal))
            {
                return false;
            }

            OnDiscData[GetDiscInfoKey(discInfo.StoredRemote.RemoteBeeFileLocation, discInfo.DownloadedPlatform)] = discInfo;
            if (!HasAnyCachedPayload(discInfo))
            {
                return false;
            }

            info = discInfo;
            return true;
        }
        catch (Exception ex)
        {
            BasisDebug.LogWarning($"Failed loading disc info from {filePath}: {ex.Message}", BasisDebug.LogTag.Event);
            return false;
        }
    }

    public static bool IsMetaDataOnDisc(string MetaURL, out BasisBEEExtensionMeta info)
    {
        lock (_discInfoLock)
        {
            string currentPlatform = BasisIOManagement.GetCurrentCachePlatform();
            BasisBEEExtensionMeta legacyCandidate = null;

            foreach (var discInfo in OnDiscData.Values)
            {
                if (discInfo.StoredRemote.RemoteBeeFileLocation == MetaURL)
                {
                    if (!string.IsNullOrWhiteSpace(discInfo.DownloadedPlatform) &&
                        !BasisIOManagement.CachePlatformMatchesCurrent(discInfo.DownloadedPlatform))
                    {
                        continue;
                    }

                    if (HasAnyCachedPayload(discInfo))
                    {
                        if (BasisIOManagement.CachePlatformMatchesCurrent(discInfo.DownloadedPlatform))
                        {
                            info = discInfo;
                            return true;
                        }

                        legacyCandidate = discInfo;
                    }
                }
            }

            if (legacyCandidate != null)
            {
                info = legacyCandidate;
                return true;
            }

            info = new BasisBEEExtensionMeta();
            return false;
        }
    }

    public static bool TryLoadMetaDataFromCacheFile(string uniqueVersion, string expectedRemoteUrl, out BasisBEEExtensionMeta info)
    {
        lock (_discInfoLock)
        {
            if (string.IsNullOrWhiteSpace(uniqueVersion))
            {
                info = new BasisBEEExtensionMeta();
                return false;
            }

            string platformAwarePath = BasisIOManagement.GetMetaCacheFilePath(uniqueVersion, BasisIOManagement.GetCurrentCachePlatform());
            if (TryLoadDiscInfoFromFile(platformAwarePath, expectedRemoteUrl, out info))
            {
                return true;
            }

            string legacyPath = BasisIOManagement.GetLegacyMetaCacheFilePath(uniqueVersion);
            return TryLoadDiscInfoFromFile(legacyPath, expectedRemoteUrl, out info);
        }
    }

    public static async Task AddDiscInfo(BasisBEEExtensionMeta discInfo)
    {
        string discKey = GetDiscInfoKey(discInfo.StoredRemote.RemoteBeeFileLocation, discInfo.DownloadedPlatform);
        OnDiscData[discKey] = discInfo;
        string filePath = BasisIOManagement.GetMetaCacheFilePath(discInfo.UniqueVersion, discInfo.DownloadedPlatform);
        byte[] serializedData = BasisSerialization.SerializeValue(discInfo);

        try
        {
            if (!string.IsNullOrWhiteSpace(discInfo.UniqueVersion))
            {
                string legacyMetaPath = BasisIOManagement.GetLegacyMetaCacheFilePath(discInfo.UniqueVersion);
                if (File.Exists(legacyMetaPath))
                {
                    File.Delete(legacyMetaPath);
                }
            }
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

    public static async Task EnsureInitializationComplete()
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
        string rootPath = BasisIOManagement.GenerateFolderPath(BasisBeeConstants.AssetBundlesFolder);
        string currentPlatformPath = BasisIOManagement.GenerateFolderPath(Path.Combine(BasisBeeConstants.AssetBundlesFolder, BasisIOManagement.GetCurrentCachePlatform()));

        List<string> files = new List<string>();
        files.AddRange(Directory.GetFiles(rootPath, $"*{BasisBeeConstants.BasisMetaExtension}", SearchOption.TopDirectoryOnly));

        if (!string.Equals(rootPath, currentPlatformPath, StringComparison.OrdinalIgnoreCase) && Directory.Exists(currentPlatformPath))
        {
            files.AddRange(Directory.GetFiles(currentPlatformPath, $"*{BasisBeeConstants.BasisMetaExtension}", SearchOption.TopDirectoryOnly));
        }

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
                    OnDiscData[GetDiscInfoKey(discInfo.StoredRemote.RemoteBeeFileLocation, discInfo.DownloadedPlatform)] = discInfo;
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
        if (!string.IsNullOrWhiteSpace(bundle?.DownloadedBeeFileLocation) && File.Exists(bundle.DownloadedBeeFileLocation))
        {
            File.Delete(bundle.DownloadedBeeFileLocation);
        }
    }
}
