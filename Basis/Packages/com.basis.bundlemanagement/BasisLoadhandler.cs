using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
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
        string Key = GetBundleKey(loadableBundle);
        if (LoadedBundles.TryGetValue(Key, out BasisTrackedBundleWrapper Wrapper))
        {
            Wrapper.DeIncrement();
            bool State = await Wrapper.UnloadIfReady();
            if (State)
            {
                LoadedBundles.Remove(Key, out var data);
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

        string Key = GetBundleKey(loadableBundle);
        if (LoadedBundles.TryGetValue(Key, out BasisTrackedBundleWrapper wrapper))
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
                LoadedBundles.Remove(Key, out var data);
                return null;
            }
        }

        return await HandleFirstBundleLoad(DisabledGameobject,loadableBundle, useContentRemoval, report, cancellationToken, Position, Rotation, Scale, ModifyScale, Selector, Parent, DestroyColliders, ChangeColidersToCorrectLayer, MaxDownloadSizeInMB, HarvestedHeadChop);
    }
    public static async Task<Scene> LoadSceneBundle(bool makeActiveScene, BasisLoadableBundle loadableBundle, BasisProgressReport report, CancellationToken cancellationToken, long MaxDownloadSizeInMB = 4L * 1024 * 1024 * 1024)
    {
        await EnsureInitializationComplete();

        string Key = GetBundleKey(loadableBundle);
        if (LoadedBundles.TryGetValue(Key, out BasisTrackedBundleWrapper wrapper))
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
        string Key = GetBundleKey(loadableBundle);
        BasisTrackedBundleWrapper wrapper = new BasisTrackedBundleWrapper { AssetBundle = null, LoadableBundle = loadableBundle };

        if (!LoadedBundles.TryAdd(Key, wrapper))
        {
            BasisDebug.LogError("Unable to add bundle wrapper.");
            return new Scene();
        }

        try
        {
            await BasisBeeManagement.HandleBundleAndMetaLoading(wrapper, report, cancellationToken, MaxDownloadSizeInMB);
            return await BasisBundleLoadAsset.LoadSceneFromBundleAsync(wrapper, makeActiveScene, report);
        }
        catch
        {
            wrapper.DidErrorOccur = true;
            if (wrapper.AssetBundle != null)
            {
                wrapper.AssetBundle.Unload(true);
                wrapper.AssetBundle = null;
            }
            LoadedBundles.Remove(Key, out var data);
            throw;
        }
    }

    private static async Task<GameObject> HandleFirstBundleLoad(GameObject DisabledGameobject,BasisLoadableBundle loadableBundle, bool useContentRemoval, BasisProgressReport report, CancellationToken cancellationToken, Vector3 Position, Quaternion Rotation, Vector3 Scale, bool ModifyScale, Selector Selector, Transform Parent = null, bool DestroyColliders = false, bool ChangeColidersToCorrectLayer = false, long MaxDownloadSizeInMB = 4L * 1024 * 1024 * 1024, List<BasisHeadChop.HeadChopTarget> HarvestedHeadChop = null)
    {
        string Key = GetBundleKey(loadableBundle);
        BasisTrackedBundleWrapper wrapper = new BasisTrackedBundleWrapper
        {
            AssetBundle = null,
            LoadableBundle = loadableBundle
        };

        if (!LoadedBundles.TryAdd(Key, wrapper))
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
            wrapper.DidErrorOccur = true;
            if (wrapper.AssetBundle != null)
            {
                wrapper.AssetBundle.Unload(true);
                wrapper.AssetBundle = null;
            }
            LoadedBundles.Remove(Key, out var data);
            CleanupFiles(loadableBundle.BasisLocalEncryptedBundle);
            return null;
        }
    }

    private static string GetBundleKey(BasisLoadableBundle loadableBundle)
    {
        string url = loadableBundle?.BasisRemoteBundleEncrypted?.RemoteBeeFileLocation ?? string.Empty;
        return $"{url}|{HashUnlockPassword(loadableBundle?.UnlockPassword)}";
    }

    // Hashing the unlock password (SHA256.Create + ComputeHash) is the bulk of GetBundleKey's
    // cost and the key is recomputed several times per load for the same (usually empty) password.
    // Memoize per distinct password so it is paid once.
    private static readonly ConcurrentDictionary<string, string> _passwordHashCache = new ConcurrentDictionary<string, string>();

    private static string HashUnlockPassword(string unlockPassword)
    {
        return _passwordHashCache.GetOrAdd(unlockPassword ?? string.Empty, password =>
        {
            using SHA256 sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(password));
            return BitConverter.ToString(hash).Replace("-", string.Empty);
        });
    }

    public static bool IsUrlLoadedInMemory(string remoteUrl)
    {
        if (string.IsNullOrEmpty(remoteUrl))
        {
            return false;
        }
        foreach (BasisTrackedBundleWrapper wrapper in LoadedBundles.Values)
        {
            if (wrapper?.LoadableBundle?.BasisRemoteBundleEncrypted?.RemoteBeeFileLocation == remoteUrl)
            {
                return true;
            }
        }
        return false;
    }

    public static void UnloadAllForUrl(string remoteUrl)
    {
        if (string.IsNullOrEmpty(remoteUrl))
        {
            return;
        }
        List<string> keysToRemove = new List<string>();
        foreach (KeyValuePair<string, BasisTrackedBundleWrapper> kvp in LoadedBundles)
        {
            if (kvp.Value?.LoadableBundle?.BasisRemoteBundleEncrypted?.RemoteBeeFileLocation == remoteUrl)
            {
                keysToRemove.Add(kvp.Key);
            }
        }
        foreach (string key in keysToRemove)
        {
            if (LoadedBundles.TryGetValue(key, out BasisTrackedBundleWrapper inUseCheck) && inUseCheck != null && inUseCheck.IsInUse)
            {
                BasisDebug.LogError($"Skipping in-memory unload for: {remoteUrl}; bundle is still in use.");
                continue;
            }
            if (LoadedBundles.TryRemove(key, out BasisTrackedBundleWrapper removed) && removed?.AssetBundle != null)
            {
                try
                {
                    BasisDebug.Log($"Unloading in-memory AssetBundle for: {remoteUrl}", BasisDebug.LogTag.Event);
                    removed.AssetBundle.Unload(true);
                }
                catch (Exception ex)
                {
                    BasisDebug.LogError($"Error unloading AssetBundle: {ex.Message}");
                }
            }
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

    private static bool TryLazyLoadDiscInfo(string metaUrl, string currentPlatform, out BasisBEEExtensionMeta info)
    {
        info = null;

        string path = BasisIOManagement.GenerateFolderPath(BasisBeeConstants.AssetBundlesFolder);
        if (!Directory.Exists(path))
        {
            return false;
        }

        BasisBEEExtensionMeta legacyCandidate = null;

        foreach (string file in Directory.GetFiles(path, $"*{BasisBeeConstants.BasisMetaExtension}"))
        {
            try
            {
                byte[] fileData = File.ReadAllBytes(file);
                BasisBEEExtensionMeta discInfo = BasisSerialization.DeserializeValue<BasisBEEExtensionMeta>(fileData);
                if (discInfo?.StoredRemote?.RemoteBeeFileLocation != metaUrl)
                {
                    continue;
                }

                OnDiscData[GetDiscInfoKey(discInfo.StoredRemote.RemoteBeeFileLocation, discInfo.DownloadedPlatform)] = discInfo;

                if (!HasAnyCachedPayload(discInfo))
                {
                    continue;
                }

                if (BasisIOManagement.CachePlatformMatchesCurrent(discInfo.DownloadedPlatform))
                {
                    info = discInfo;
                    return true;
                }

                if (string.IsNullOrWhiteSpace(discInfo.DownloadedPlatform) && legacyCandidate == null)
                {
                    legacyCandidate = discInfo;
                }
            }
            catch (Exception ex)
            {
                BasisDebug.LogWarning($"Failed lazy-loading disc info from {file}: {ex.Message}", BasisDebug.LogTag.Event);
            }
        }

        if (legacyCandidate != null)
        {
            info = legacyCandidate;
            return true;
        }

        return false;
    }

    private static bool TryGetInMemoryDiscInfo(string MetaURL, out BasisBEEExtensionMeta info)
    {
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

        info = null;
        return false;
    }

    public static bool IsMetaDataOnDisc(string MetaURL, out BasisBEEExtensionMeta info)
    {
        if (TryGetInMemoryDiscInfo(MetaURL, out info))
        {
            return true;
        }

        string currentPlatform = BasisIOManagement.GetCurrentCachePlatform();
        if (TryLazyLoadDiscInfo(MetaURL, currentPlatform, out BasisBEEExtensionMeta lazyLoadedInfo))
        {
            info = lazyLoadedInfo;
            return true;
        }

        info = new BasisBEEExtensionMeta();
        return false;
    }

    public static Task<(bool found, BasisBEEExtensionMeta info)> IsMetaDataOnDiscAsync(string MetaURL)
    {
        if (TryGetInMemoryDiscInfo(MetaURL, out BasisBEEExtensionMeta inMemory))
        {
            return Task.FromResult((true, inMemory));
        }

        return Task.Run<(bool, BasisBEEExtensionMeta)>(() =>
        {
            string currentPlatform = BasisIOManagement.GetCurrentCachePlatform();
            if (TryLazyLoadDiscInfo(MetaURL, currentPlatform, out BasisBEEExtensionMeta lazyInfo))
            {
                return (true, lazyInfo);
            }
            return (false, new BasisBEEExtensionMeta());
        });
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
            string tempPath = filePath + ".tmp";
            await File.WriteAllBytesAsync(tempPath, serializedData);
            if (File.Exists(filePath))
            {
                File.Replace(tempPath, filePath, null);
            }
            else
            {
                File.Move(tempPath, filePath);
            }
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
