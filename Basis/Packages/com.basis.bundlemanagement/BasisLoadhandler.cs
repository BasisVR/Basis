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
    /// Destroys a wrapper's AssetBundle assets, but only when no other registered wrapper pointing
    /// at that same AssetBundle still holds reservations. Wrappers alias one AssetBundle whenever
    /// the registry key changes for content already in memory, and each counts its worn instances
    /// separately, so an unload driven by one count reaching zero must not destroy the meshes and
    /// rigs another wrapper's live avatars are still driven with.
    /// </summary>
    public static bool TryUnloadBundleAssets(BasisTrackedBundleWrapper wrapper)
    {
        AssetBundle bundle = wrapper?.AssetBundle;
        if (bundle == null)
        {
            return false;
        }
        // Claim, don't just read. An IsInUse check here and an Unload(true) below is a gap another
        // thread can Increment through, handing out a reservation on a bundle that is one statement
        // from having every asset destroyed under it.
        List<BasisTrackedBundleWrapper> claimed = null;
        foreach (BasisTrackedBundleWrapper other in LoadedBundles.Values)
        {
            if (other == null || ReferenceEquals(other, wrapper) || !ReferenceEquals(other.AssetBundle, bundle))
            {
                continue;
            }
            if (!other.TryClaimUnload())
            {
                BasisDebug.Log($"Skipping unload of {bundle.name}; another loaded wrapper still has instances of it.", BasisDebug.LogTag.Event);
                if (claimed != null)
                {
                    foreach (BasisTrackedBundleWrapper release in claimed)
                    {
                        release.ReleaseUnloadClaim();
                    }
                }
                return false;
            }
            claimed ??= new List<BasisTrackedBundleWrapper>();
            claimed.Add(other);
        }
        wrapper.IsUnloaded = true;
        foreach (KeyValuePair<string, BasisTrackedBundleWrapper> pair in LoadedBundles)
        {
            BasisTrackedBundleWrapper other = pair.Value;
            if (other == null || ReferenceEquals(other, wrapper) || !ReferenceEquals(other.AssetBundle, bundle))
            {
                continue;
            }
            other.IsUnloaded = true;
            if (LoadedBundles.TryGetValue(pair.Key, out BasisTrackedBundleWrapper current) && ReferenceEquals(current, other))
            {
                LoadedBundles.Remove(pair.Key, out var husk);
            }
        }
        bundle.Unload(true);
        return true;
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
        // Release against the wrapper the reservation was actually taken on, and consume the
        // ticket so the same reservation can never be paid twice. Recomputing the key here reads
        // a version tag that other systems write onto this record after the load reserved, and a
        // drifted key silently decrements whichever wrapper it lands on instead — a count that
        // belongs to somebody else's live avatar.
        string Key = loadableBundle.ReservedWrapperKey;
        loadableBundle.ReservedWrapperKey = null;
        bool IsDefaultAvatar = string.Equals(CombinedURL, BasisBeeConstants.DefaultAvatar, StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(Key))
        {
            Key = GetBundleKey(loadableBundle);
            if (!IsDefaultAvatar)
            {
                BasisDebug.LogWarning($"No load reservation recorded for {CombinedURL}; releasing against recomputed key '{Key}'. Either this is a double release, or the content was loaded by a path that never reserved.", BasisDebug.LogTag.Event);
            }
        }
        if (LoadedBundles.TryGetValue(Key, out BasisTrackedBundleWrapper Wrapper))
        {
            Wrapper.DeIncrement();
            bool State = await Wrapper.UnloadIfReady();
            if (State)
            {
                // Only remove OUR wrapper: a lookup during the unload continuation may have
                // seen IsUnloaded, dropped the husk, and registered a fresh wrapper under the
                // same key — removing blindly here would tear that replacement out.
                string registeredKey = Wrapper.RegisteredKey ?? Key;
                if (LoadedBundles.TryGetValue(registeredKey, out BasisTrackedBundleWrapper current) && ReferenceEquals(current, Wrapper))
                {
                    LoadedBundles.Remove(registeredKey, out var data);
                }
                return;
            }
        }
        else
        {
            if (!IsDefaultAvatar)
            {
                // The key is logged because a miss here is almost always key drift rather than a
                // genuinely absent bundle: the reservation stays held, so the wrapper never
                // unloads, and whatever DID get found under the drifted key was decremented in
                // its place.
                BasisDebug.LogError($"tried to find Loaded Key {CombinedURL} (key '{Key}') but could not find it!");
            }
        }
    }
    public static async Task<GameObject> LoadGameObjectBundle(GameObject DisabledGameobject,BasisLoadableBundle loadableBundle, bool useContentRemoval, BasisProgressReport report, CancellationToken cancellationToken, Vector3 Position, Quaternion Rotation, Vector3 Scale, bool ModifyScale, Selector Selector, Transform Parent = null, bool DestroyColliders = false,bool ChangeColidersToCorrectLayer = false, long MaxDownloadSizeInMB = 4L * 1024 * 1024 * 1024, List<BasisHeadChop.HeadChopTarget> HarvestedHeadChop = null)
    {
        await EnsureInitializationComplete();

        string Key = GetBundleKey(loadableBundle);
        if (LoadedBundles.TryGetValue(Key, out BasisTrackedBundleWrapper wrapper))
        {
            // Reserve BEFORE any await: the unload grace period re-checks the count, and
            // the budgeted instantiate spans frames. Incrementing only at instantiate-end
            // (the old LoadFromWrapper behavior) let the grace expire mid-load on a
            // range-boundary re-entry and Unload(true) killed the clone's assets.
            // A refused Increment means an unload is already claimed, which is the same MISS
            // as an IsUnloaded husk.
            if (wrapper.IsUnloaded || !wrapper.Increment())
            {
                // Unload(true) already destroyed this wrapper's assets — instantiating from
                // it produces an avatar whose Animator.avatar and meshes are dead. Drop the
                // husk and load fresh.
                if (LoadedBundles.TryGetValue(Key, out BasisTrackedBundleWrapper stale) && ReferenceEquals(stale, wrapper))
                {
                    LoadedBundles.Remove(Key, out var husk);
                }
            }
            else
            {
                // Ticket the reservation with the wrapper it was taken on, so the release finds
                // this exact wrapper rather than re-deriving a key that may have drifted.
                loadableBundle.ReservedWrapperKey = wrapper.RegisteredKey ?? Key;
                try
                {
                    await wrapper.WaitForBundleLoadAsync();

                    // ensure the bundle connector is updated from the wrapper
                    loadableBundle.BasisBundleConnector = wrapper.LoadableBundle.BasisBundleConnector;

                    GameObject result = await BasisBundleLoadAsset.LoadFromWrapper(DisabledGameobject,wrapper, useContentRemoval, Position, Rotation, ModifyScale, Scale, Selector, Parent, DestroyColliders, ChangeColidersToCorrectLayer, HarvestedHeadChop);
                    if (result == null)
                    {
                        wrapper.DeIncrement();
                        loadableBundle.ReservedWrapperKey = null;
                    }
                    return result;
                }
                catch (Exception ex)
                {
                    wrapper.DeIncrement();
                    loadableBundle.ReservedWrapperKey = null;
                    BasisDebug.LogError($"Failed to load content: {ex}");
                    LoadedBundles.Remove(Key, out var data);
                    return null;
                }
            }
        }

        return await HandleFirstBundleLoad(DisabledGameobject,loadableBundle, useContentRemoval, report, cancellationToken, Position, Rotation, Scale, ModifyScale, Selector, Parent, DestroyColliders, ChangeColidersToCorrectLayer, MaxDownloadSizeInMB, HarvestedHeadChop);
    }
    public static async Task<Scene> LoadSceneBundle(bool makeActiveScene, BasisLoadableBundle loadableBundle, BasisProgressReport report, CancellationToken cancellationToken, long MaxDownloadSizeInMB = 4L * 1024 * 1024 * 1024)
    {
        await EnsureInitializationComplete();

        string Key = GetBundleKey(loadableBundle);
        if (LoadedBundles.TryGetValue(Key, out BasisTrackedBundleWrapper wrapper) && wrapper.IsUnloaded)
        {
            // Dead husk from a completed unload — drop it and take the first-load path.
            if (LoadedBundles.TryGetValue(Key, out BasisTrackedBundleWrapper stale) && ReferenceEquals(stale, wrapper))
            {
                LoadedBundles.Remove(Key, out var husk);
            }
            wrapper = null;
        }
        // Reserve BEFORE any await, exactly as the GameObject path does. A scene wrapper sits
        // at zero between unloads (its durable count is taken by LoadSceneFromBundleAsync only
        // once the scene is live), so without this the awaits below span a window where the
        // grace period can expire — or an aliasing wrapper reaching zero can Unload(true) the
        // bundle we are about to load the scene out of. A refused Increment is an unload already
        // claimed: same MISS as a husk.
        if (wrapper != null && !wrapper.Increment())
        {
            if (LoadedBundles.TryGetValue(Key, out BasisTrackedBundleWrapper claimedStale) && ReferenceEquals(claimedStale, wrapper))
            {
                LoadedBundles.Remove(Key, out var husk);
            }
            wrapper = null;
        }
        if (wrapper != null)
        {
            BasisDebug.Log($"Bundle On Disc Loading", BasisDebug.LogTag.Networking);
            try
            {
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
            finally
            {
                // Hand-off, not a release: a scene that came up holds its own reservation, paired
                // with SceneUnloaded. This only gives back the one held across the load.
                wrapper.DeIncrement();
            }
        }

        return await HandleFirstSceneLoad(loadableBundle, makeActiveScene, report, cancellationToken, MaxDownloadSizeInMB);
    }

    private static async Task<Scene> HandleFirstSceneLoad(BasisLoadableBundle loadableBundle, bool makeActiveScene, BasisProgressReport report, CancellationToken cancellationToken, long MaxDownloadSizeInMB = 4L * 1024 * 1024 * 1024)
    {
        string Key = GetBundleKey(loadableBundle);
        BasisTrackedBundleWrapper wrapper = new BasisTrackedBundleWrapper { AssetBundle = null, LoadableBundle = loadableBundle, RegisteredKey = Key };

        // Held across the load for the same reason as the GameObject path: until the scene is
        // live nothing counts this wrapper as in use, and an aliasing wrapper reaching zero
        // would Unload(true) the bundle out from under the load in flight. Taken before the
        // wrapper is published so no other thread can claim an unload on it at zero first.
        wrapper.Increment();

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
            if (TryUnloadBundleAssets(wrapper))
            {
                wrapper.AssetBundle = null;
            }
            LoadedBundles.Remove(Key, out var data);
            throw;
        }
        finally
        {
            // Hand-off, not a release: a scene that came up holds its own reservation, paired
            // with SceneUnloaded. This only gives back the one held across the load.
            wrapper.DeIncrement();
        }
    }

    private static async Task<GameObject> HandleFirstBundleLoad(GameObject DisabledGameobject,BasisLoadableBundle loadableBundle, bool useContentRemoval, BasisProgressReport report, CancellationToken cancellationToken, Vector3 Position, Quaternion Rotation, Vector3 Scale, bool ModifyScale, Selector Selector, Transform Parent = null, bool DestroyColliders = false, bool ChangeColidersToCorrectLayer = false, long MaxDownloadSizeInMB = 4L * 1024 * 1024 * 1024, List<BasisHeadChop.HeadChopTarget> HarvestedHeadChop = null)
    {
        string Key = GetBundleKey(loadableBundle);
        BasisTrackedBundleWrapper wrapper = new BasisTrackedBundleWrapper
        {
            AssetBundle = null,
            LoadableBundle = loadableBundle,
            RegisteredKey = Key
        };

        // The instantiate reservation, held from registration so the unload grace can never
        // fire between the download completing and the budgeted instantiate finishing. Taken
        // before the wrapper is published so no other thread can claim an unload on it at zero.
        wrapper.Increment();

        if (!LoadedBundles.TryAdd(Key, wrapper))
        {
            BasisDebug.LogError("Unable to add bundle wrapper.");
            return null;
        }

        // Ticket the reservation with the wrapper it was taken on, so the release finds this
        // exact wrapper rather than re-deriving a key that may have drifted.
        loadableBundle.ReservedWrapperKey = wrapper.RegisteredKey ?? Key;
        try
        {
            await BasisBeeManagement.HandleBundleAndMetaLoading(wrapper, report, cancellationToken, MaxDownloadSizeInMB);
            GameObject result = await BasisBundleLoadAsset.LoadFromWrapper(DisabledGameobject, wrapper, useContentRemoval, Position, Rotation, ModifyScale, Scale, Selector, Parent, DestroyColliders, ChangeColidersToCorrectLayer, HarvestedHeadChop);
            if (result == null)
            {
                wrapper.DeIncrement();
                loadableBundle.ReservedWrapperKey = null;
            }
            return result;
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"{ex.Message} {ex.StackTrace}");
            wrapper.DeIncrement();
            loadableBundle.ReservedWrapperKey = null;
            wrapper.DidErrorOccur = true;
            if (TryUnloadBundleAssets(wrapper))
            {
                wrapper.AssetBundle = null;
            }
            wrapper.UnloadGltfTemplate();
            LoadedBundles.Remove(Key, out var data);
            CleanupFiles(loadableBundle.BasisLocalEncryptedBundle);
            return null;
        }
    }

    public static string GetBundleKey(BasisLoadableBundle loadableBundle)
    {
        string url = BasisIOManagement.CanonicalizeRemoteUrl(loadableBundle?.BasisRemoteBundleEncrypted?.RemoteBeeFileLocation);
        string key = $"{url}|{HashUnlockPassword(loadableBundle?.UnlockPassword)}";

        // Version-aware only when a version is actually declared, so unversioned content produces
        // the exact key it did before and its load/DeIncrement pairing is untouched. When a url is
        // republished, this is what stops two players wearing the same url at different versions
        // from collapsing onto one wrapper — the in-memory cache is keyed by url alone otherwise,
        // and would hand the stale bundle to whoever asked for the new one.
        string versionTag = BasisContentVersion.Normalize(loadableBundle?.BasisRemoteBundleEncrypted?.RemoteVersionTag);
        return versionTag.Length == 0 ? key : $"{key}|{versionTag}";
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
        string canonicalUrl = BasisIOManagement.CanonicalizeRemoteUrl(remoteUrl);
        foreach (KeyValuePair<string, BasisTrackedBundleWrapper> kvp in LoadedBundles)
        {
            if (BasisIOManagement.CanonicalizeRemoteUrl(kvp.Value?.LoadableBundle?.BasisRemoteBundleEncrypted?.RemoteBeeFileLocation) == canonicalUrl)
            {
                keysToRemove.Add(kvp.Key);
            }
        }
        foreach (string key in keysToRemove)
        {
            if (LoadedBundles.TryGetValue(key, out BasisTrackedBundleWrapper inUseCheck) && inUseCheck != null && inUseCheck.IsInUse)
            {
                BasisDebug.LogWarning($"Skipping in-memory unload for: {remoteUrl}; bundle is still in use.", BasisDebug.LogTag.Event);
                continue;
            }
            if (LoadedBundles.TryRemove(key, out BasisTrackedBundleWrapper removed) && removed != null)
            {
                try
                {
                    if (removed.AssetBundle != null)
                    {
                        BasisDebug.Log($"Unloading in-memory AssetBundle for: {remoteUrl}", BasisDebug.LogTag.Event);
                        TryUnloadBundleAssets(removed);
                    }
                    else
                    {
                        removed.IsUnloaded = true;
                    }
                    removed.UnloadGltfTemplate();
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
        string safeUrl = BasisIOManagement.CanonicalizeRemoteUrl(remoteUrl);
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
        string canonicalUrl = BasisIOManagement.CanonicalizeRemoteUrl(metaUrl);

        foreach (string file in Directory.GetFiles(path, $"*{BasisBeeConstants.BasisMetaExtension}"))
        {
            try
            {
                byte[] fileData = File.ReadAllBytes(file);
                BasisBEEExtensionMeta discInfo = BasisSerialization.DeserializeValue<BasisBEEExtensionMeta>(fileData);
                if (BasisIOManagement.CanonicalizeRemoteUrl(discInfo?.StoredRemote?.RemoteBeeFileLocation) != canonicalUrl)
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
        string canonicalUrl = BasisIOManagement.CanonicalizeRemoteUrl(MetaURL);

        foreach (var discInfo in OnDiscData.Values)
        {
            if (BasisIOManagement.CanonicalizeRemoteUrl(discInfo.StoredRemote.RemoteBeeFileLocation) == canonicalUrl)
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
