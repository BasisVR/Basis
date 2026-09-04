using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Result of a meta-only load attempt. Distinguishes transient network failures
/// (caller should keep cached state intact and retry later) from genuine corruption
/// or missing data (caller may safely evict the item).
/// </summary>
public readonly struct BasisMetaLoadResult
{
    public readonly bool Loaded;
    public readonly bool IsTransient;
    public readonly string Error;

    private BasisMetaLoadResult(bool loaded, bool isTransient, string error)
    {
        Loaded = loaded;
        IsTransient = isTransient;
        Error = error;
    }

    public static BasisMetaLoadResult Success => new BasisMetaLoadResult(true, false, null);
    public static BasisMetaLoadResult Transient(string error) => new BasisMetaLoadResult(false, true, error);
    public static BasisMetaLoadResult Corrupt(string error) => new BasisMetaLoadResult(false, false, error);

    // Implicit bool conversion preserves legacy `bool x = await HandleMetaOnlyLoad(...)` call sites.
    public static implicit operator bool(BasisMetaLoadResult r) => r.Loaded;

    public static bool LooksLikeTransientError(string error)
    {
        if (string.IsNullOrEmpty(error)) return false;
        return error.IndexOf("Network error:", StringComparison.OrdinalIgnoreCase) >= 0
            || error.IndexOf("Cancelled", StringComparison.OrdinalIgnoreCase) >= 0
            || error.IndexOf("Timeout", StringComparison.OrdinalIgnoreCase) >= 0
            || error.IndexOf("SSL", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}

public static class BasisBeeManagement
{
    private static bool HasCompatibleDownloadedPlatform(BasisBEEExtensionMeta metaInfo)
    {
        return metaInfo != null &&
               !string.IsNullOrEmpty(metaInfo.DownloadedPlatform) &&
               BasisIOManagement.CachePlatformMatchesCurrent(metaInfo.DownloadedPlatform);
    }

    /// <summary>
    /// The single decision that lets content published to a STATIC url ever be updated: whether a
    /// cache entry found by url is also current for the version the requester asked for.
    ///
    /// <para>Returns true (keep using the cache) whenever no version was declared, which is the
    /// branch taken by every bundle and client that predates versioning — so this is a no-op for
    /// existing content.</para>
    ///
    /// <para>A mismatched requested tag alone is NOT proof the cache is stale — the claim can be
    /// the stale side (see <see cref="HostConfirmsCachedCopyCurrentAsync"/>), so a mismatch asks
    /// the host before anything is evicted.</para>
    /// </summary>
    /// <param name="evictStaleCache">
    /// Whether a stale entry should be deleted outright rather than merely bypassed. True for the
    /// full load, where eviction reclaims the previous UniqueVersion's files instead of leaving
    /// them for the LRU sweep. False for the connector-only load: its caller treats "no meta on
    /// disc" as a corrupt item and REMOVES the user's library key, so deleting the entry there would turn
    /// a failed re-download into silent loss of the user's saved item. Bypassing still refreshes —
    /// the re-download rewrites the entry — it just leaves the old payload for the LRU sweep.
    /// </param>
    private static async Task<bool> CacheIsCurrentForRequestedVersionAsync(BasisTrackedBundleWrapper wrapper, BasisBEEExtensionMeta metaInfo, string beeLocation, bool evictStaleCache, CancellationToken cancellationToken)
    {
        string requestedVersionTag = wrapper?.LoadableBundle?.BasisRemoteBundleEncrypted?.RemoteVersionTag;
        if (BasisContentVersion.ShouldUseCache(metaInfo, requestedVersionTag, beeLocation))
        {
            return true;
        }

        if (await HostConfirmsCachedCopyCurrentAsync(metaInfo, beeLocation, cancellationToken))
        {
            return true;
        }

        if (evictStaleCache)
        {
            // Safe mid-load: the caller registered and incremented this wrapper before starting,
            // and UnloadAllForUrl skips bundles in use, so only idle copies of the old version go.
            BasisContentVersion.Invalidate(beeLocation);
        }

        return false;
    }

    /// <summary>
    /// A requested tag that fails to match the cache is a CLAIM, not a fact — usually a peer
    /// echoing whatever validator their own download observed, however long ago. The claim itself
    /// goes stale whenever the host's validator changes without the bytes changing (nginx ETags
    /// embed file mtime, so re-uploading or syncing identical bytes mints a new tag), and the
    /// wearer never notices because their warm loads never touch the network. Believing the claim
    /// outright would evict a perfectly current cache and re-download the full bee on every load,
    /// forever.
    ///
    /// <para>So before evicting, ask the HOST: one conditional request against the tag the cached
    /// bytes were actually validated with. 304 (or a matching validator) proves the cache is
    /// current and the claim merely outdated; only a host reporting different content costs a
    /// re-download. An unreachable host also serves the cache — the download a mismatch would
    /// trigger cannot succeed either, and a bare claim is not evidence enough to strand the user
    /// content-less. Throttled upstream by TryBeginVersionRefresh, so a claim-flipping peer costs
    /// one small request per url per window instead of a full download.</para>
    /// </summary>
    private static async Task<bool> HostConfirmsCachedCopyCurrentAsync(BasisBEEExtensionMeta metaInfo, string beeLocation, CancellationToken cancellationToken)
    {
        string cachedTag = metaInfo?.CachedVersionTag;
        if (string.IsNullOrWhiteSpace(cachedTag))
        {
            // No baseline to verify against: the entry predates versioning, and an actively
            // claimed version IS evidence of change there. One download settles it.
            return false;
        }

        BeeResult<BasisIOManagement.BasisRemoteValidator> result;
        try
        {
            result = await BasisIOManagement.FetchRemoteValidatorAsync(beeLocation, cachedTag.Trim(), cancellationToken);
        }
        catch (Exception ex)
        {
            BasisDebug.LogWarning($"Version-claim check for {beeLocation} threw ({ex.Message}); serving the cached copy.", BasisDebug.LogTag.Event);
            return true;
        }

        if (!result.IsSuccess)
        {
            BasisDebug.LogWarning($"Could not verify version claim for {beeLocation} ({result.Error}); serving the cached copy.", BasisDebug.LogTag.Event);
            return true;
        }

        if (BasisContentVersion.HostConfirmsCache(cachedTag, result.Value))
        {
            BasisDebug.Log($"Host confirms the cached copy of {beeLocation} is current; the requested tag is an outdated claim. Serving cache.", BasisDebug.LogTag.Event);
            await BasisContentVersion.MarkValidatedAsync(beeLocation, result.Value.NotModified ? cachedTag : result.Value.Tag);
            return true;
        }

        return false;
    }

    /// <summary>
    /// this allows obtaining the entire bee file
    /// </summary>
    /// <param name="wrapper"></param>
    /// <param name="report"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static async Task HandleBundleAndMetaLoading(BasisTrackedBundleWrapper wrapper, BasisProgressReport report, CancellationToken cancellationToken, long MaxDownloadSizeInBytes = 4L * 1024 * 1024 * 1024)
    {
        string beeLocation = wrapper.LoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation;
        bool networkSourced = wrapper.LoadableBundle.BasisRemoteBundleEncrypted.IsNetworkSourced;
        if (networkSourced && !Basis.Scripts.Common.BasisUrlSecurity.IsHttpUrlAllowed(beeLocation, out string locationError))
        {
            throw new Exception($"Refusing networked content location: {locationError}");
        }
        if (!networkSourced && BasisIOManagement.TryResolveLocalBeePath(beeLocation, out string localBeePath))
        {
            await HandleLocalBeeBundle(wrapper, localBeePath, report, cancellationToken);
            return;
        }

        var (IsMetaOnDisc, MetaInfo) = await BasisLoadHandler.IsMetaDataOnDiscAsync(beeLocation);
        bool didForceRedownload = false;
        bool shouldUseOnDiskMeta = IsMetaOnDisc;

        if (shouldUseOnDiskMeta && !HasCompatibleDownloadedPlatform(MetaInfo) && !string.IsNullOrEmpty(MetaInfo.DownloadedPlatform))
        {
            BasisDebug.Log($"Cached bundle platform {MetaInfo.DownloadedPlatform} does not match {Application.platform}. Forcing re-download.", BasisDebug.LogTag.Event);
            shouldUseOnDiskMeta = false;
        }

        if (shouldUseOnDiskMeta && !await CacheIsCurrentForRequestedVersionAsync(wrapper, MetaInfo, beeLocation, evictStaleCache: true, cancellationToken))
        {
            shouldUseOnDiskMeta = false;
        }

        (BasisBundleGenerated, BasisBundleSection, string) output;
        if (shouldUseOnDiskMeta)
        {
            output = await BasisBundleManagement.LocalLoadBundleConnector(wrapper, MetaInfo.StoredLocal, report, cancellationToken);
        }
        else
        {
            BasisDebug.Log("Download Store Meta And Bundle", BasisDebug.LogTag.Event);
            // First-time download: fetch the connector alone first (two small ranged
            // requests). It carries the far avatar payload, so a player can appear as their
            // own silhouette within moments while the full bundle downloads behind it.
            // UniqueVersion check, not null: network-converted bundles carry an EMPTY
            // connector husk that would otherwise read as "already have one".
            if (string.IsNullOrEmpty(wrapper.LoadableBundle.BasisBundleConnector?.UniqueVersion))
            {
                try
                {
                    var (connector, connectorError) = await BasisBundleManagement.DownloadConnectorFile(wrapper, new BasisProgressReport(), cancellationToken, MaxDownloadSizeInBytes);
                    if (connector == null)
                    {
                        BasisDebug.Log($"Connector prefetch unavailable ({connectorError}) — continuing with the full download.", BasisDebug.LogTag.Event);
                    }
                }
                catch (Exception prefetchException)
                {
                    BasisDebug.Log($"Connector prefetch failed ({prefetchException.Message}) — continuing with the full download.", BasisDebug.LogTag.Event);
                }
            }
            output = await BasisBundleManagement.DownloadLoadBundleConnector(wrapper, report, cancellationToken, MaxDownloadSizeInBytes);
        }
        if(!output.Item2.HasPayload)
        {
            //lets force download it again. this guards against partial file, corrupt file or reattempt at downloading if it fails.
            BasisDebug.Log("Local load returned null section data, forcing re-download", BasisDebug.LogTag.Event);
            output = await BasisBundleManagement.DownloadLoadBundleConnector(wrapper, report, cancellationToken, MaxDownloadSizeInBytes);
            didForceRedownload = true;
        }

        if (output.Item1 == null || output.Item3 != string.Empty)
        {
            throw new Exception($"Bundle load failed for {wrapper?.LoadableBundle?.BasisRemoteBundleEncrypted?.RemoteBeeFileLocation ?? "unknown"}: {output.Item3}");
        }
        // Generic (glTF) fallback section: no AssetBundle exists for this platform, the bytes
        // are an encrypted glb. Build the template instead of an AssetBundle, with the same
        // cache-refresh retry the bundle path gets for stale cached bytes.
        if (BasisBundleConnector.IsGltfMode(output.Item1))
        {
            if (wrapper.HasGltfTemplate)
            {
                return;
            }
            bool gltfLoaded = await BasisGltfAvatarLoader.LoadTemplate(wrapper, output.Item1, output.Item2, report);
            if (!gltfLoaded && shouldUseOnDiskMeta && !didForceRedownload)
            {
                BasisDebug.Log("Cached generic (glTF) bytes failed to load; forcing re-download.", BasisDebug.LogTag.Event);
                output = await BasisBundleManagement.DownloadLoadBundleConnector(wrapper, report, cancellationToken, MaxDownloadSizeInBytes);
                didForceRedownload = true;

                if (output.Item1 == null || !output.Item2.HasPayload || !string.IsNullOrEmpty(output.Item3))
                {
                    throw new Exception($"Unable to reload generic (glTF) section after cache mismatch. {output.Item3}");
                }

                gltfLoaded = await BasisGltfAvatarLoader.LoadTemplate(wrapper, output.Item1, output.Item2, report);
            }

            if (!gltfLoaded)
            {
                throw new Exception($"Generic (glTF) avatar template creation failed for {wrapper?.LoadableBundle?.BasisRemoteBundleEncrypted?.RemoteBeeFileLocation ?? "unknown"}.");
            }

            await SaveMetaIfNeeded(wrapper, shouldUseOnDiskMeta, didForceRedownload, output.Item1.Platform);
            return;
        }
        IEnumerable<AssetBundle> AssetBundles = AssetBundle.GetAllLoadedAssetBundles();
        foreach (AssetBundle assetBundle in AssetBundles)
        {
            if (output.Item1 == null || output.Item1.AssetToLoadName == null)
            {
                throw new Exception($"Missing AssetToName! in obtained file! corrupted?");
            }
            else
            {
                string AssetToLoadName = output.Item1.AssetToLoadName;
                if (assetBundle != null && assetBundle.Contains(AssetToLoadName))
                {
                    wrapper.AssetBundle = assetBundle;
                    #if UNITY_BUNDLEUNLOAD
                    wrapper.IsBundleBackingStoreReleased = false;
                    #endif
                    BasisDebug.Log($"we already have this AssetToLoadName in our loaded bundles using that instead! {AssetToLoadName}");
                    await SaveMetaIfNeeded(wrapper, shouldUseOnDiskMeta, didForceRedownload, output.Item1.Platform);
                    return;
                }
            }
        }
        BasisDebug.Log("Calling Load Request", BasisDebug.LogTag.System);
        try
        {
            AssetBundleCreateRequest bundleRequest = await BasisEncryptionToData.GenerateBundleFromFile(wrapper.LoadableBundle.UnlockPassword, output.Item2, output.Item1.AssetBundleCRC, report);
            if (bundleRequest == null || bundleRequest.assetBundle == null)
            {
                if (shouldUseOnDiskMeta && !didForceRedownload)
                {
                    BasisDebug.Log("Cached bundle bytes failed to load; forcing re-download.", BasisDebug.LogTag.Event);
                    output = await BasisBundleManagement.DownloadLoadBundleConnector(wrapper, report, cancellationToken, MaxDownloadSizeInBytes);
                    didForceRedownload = true;

                    if (output.Item1 == null || !output.Item2.HasPayload || !string.IsNullOrEmpty(output.Item3))
                    {
                        throw new Exception($"Unable to reload bundle after cache mismatch. {output.Item3}");
                    }

                    bundleRequest = await BasisEncryptionToData.GenerateBundleFromFile(wrapper.LoadableBundle.UnlockPassword, output.Item2, output.Item1.AssetBundleCRC, report);
                }

                if (bundleRequest == null || bundleRequest.assetBundle == null)
                {
                    throw new Exception("AssetBundle creation failed after attempting to refresh the cached bundle.");
                }
            }

            wrapper.AssetBundle = bundleRequest.assetBundle;
            #if UNITY_BUNDLEUNLOAD
            wrapper.IsBundleBackingStoreReleased = false;
            #endif

            await SaveMetaIfNeeded(wrapper, shouldUseOnDiskMeta, didForceRedownload, output.Item1.Platform);
        }
        catch (Exception ex)
        {
            BasisDebug.LogError(ex);
            throw;
        }
    }
    /// <summary>
    /// Loads a BEE that lives on the local filesystem (no download, no on-disc cache copy).
    /// Reads connector + platform section directly and generates the asset bundle.
    /// </summary>
    private static async Task HandleLocalBeeBundle(BasisTrackedBundleWrapper wrapper, string localBeePath, BasisProgressReport report, CancellationToken cancellationToken)
    {
        var output = await BasisBundleManagement.LocalDirectLoadBundleConnector(wrapper, localBeePath, report, cancellationToken);

        if (output.Item1 == null || !string.IsNullOrEmpty(output.Item3))
        {
            throw new Exception($"Local bundle load failed for {localBeePath}: {output.Item3}");
        }

        if (!output.Item2.HasPayload)
        {
            throw new Exception($"Local bundle load returned no section data for {localBeePath}.");
        }

        // Generic (glTF) fallback section from a local bee — same template path as remote.
        if (BasisBundleConnector.IsGltfMode(output.Item1))
        {
            bool gltfLoaded = await BasisGltfAvatarLoader.LoadTemplate(wrapper, output.Item1, output.Item2, report);
            if (!gltfLoaded)
            {
                throw new Exception($"Generic (glTF) avatar template creation failed for local bee file {localBeePath}.");
            }
            return;
        }

        string assetToLoadName = output.Item1.AssetToLoadName;
        if (string.IsNullOrEmpty(assetToLoadName))
        {
            throw new Exception("Missing AssetToLoadName in local bee file! corrupted?");
        }

        foreach (AssetBundle assetBundle in AssetBundle.GetAllLoadedAssetBundles())
        {
            if (assetBundle != null && assetBundle.Contains(assetToLoadName))
            {
                wrapper.AssetBundle = assetBundle;
                #if UNITY_BUNDLEUNLOAD
                wrapper.IsBundleBackingStoreReleased = false;
                #endif
                BasisDebug.Log($"Reusing already-loaded AssetBundle for {assetToLoadName}");
                return;
            }
        }

        AssetBundleCreateRequest bundleRequest = await BasisEncryptionToData.GenerateBundleFromFile(wrapper.LoadableBundle.UnlockPassword, output.Item2, output.Item1.AssetBundleCRC, report);
        if (bundleRequest == null || bundleRequest.assetBundle == null)
        {
            throw new Exception($"AssetBundle creation failed for local bee file {localBeePath}.");
        }

        wrapper.AssetBundle = bundleRequest.assetBundle;
        #if UNITY_BUNDLEUNLOAD
        wrapper.IsBundleBackingStoreReleased = false;
        #endif
    }
    /// <summary>
    /// Saves or updates on-disc metadata when it is missing or was refreshed by a forced re-download.
    /// </summary>
    private static async Task SaveMetaIfNeeded(BasisTrackedBundleWrapper wrapper, bool wasMetaOnDisc, bool didForceRedownload, string downloadedPlatform)
    {
        if (!wasMetaOnDisc || didForceRedownload)
        {
            BasisBEEExtensionMeta newDiscInfo = new BasisBEEExtensionMeta
            {
                // Cloned: the meta cache outlives this load and is handed to the library UI, which
                // writes CachedVersionTag into whatever record it is given. Sharing the wrapper's
                // own instance lets that write re-key a bundle that is still loaded and in use.
                StoredRemote = wrapper.LoadableBundle.BasisRemoteBundleEncrypted.Clone(),
                StoredLocal = wrapper.LoadableBundle.BasisLocalEncryptedBundle,
                UniqueVersion = wrapper.LoadableBundle.BasisBundleConnector.UniqueVersion,
                DownloadedPlatform = downloadedPlatform,
                CachedVersionTag = ResolveCachedVersionTag(wrapper),
                LastValidatedUnixUtc = BasisContentVersion.NowUnixUtc(),
            };

            await BasisLoadHandler.AddDiscInfo(newDiscInfo);
            BasisStorageManagement.EnforceCacheSizeLimit();
        }
    }

    /// <summary>
    /// The version to record against bytes that were just fetched. Prefers the validator the SERVER
    /// reported (<see cref="BasisTrackedBundleWrapper.ObservedVersionTag"/>) over the tag the
    /// requester asked for, so a value a peer merely claimed never gets written into the cache as
    /// though it had been verified. Falls back to the requested tag for hosts that publish no
    /// validator at all — there the tag is a creator-stamped nonce and echoing it is the whole
    /// mechanism, and the worst a bogus one can do is cost a single throttled refresh.
    /// </summary>
    private static string ResolveCachedVersionTag(BasisTrackedBundleWrapper wrapper)
    {
        // Stored verbatim, never normalized: a later conditional request has to echo the server's
        // exact ETag spelling. Normalization is applied when comparing, not when recording.
        string observed = wrapper?.ObservedVersionTag;
        if (!string.IsNullOrWhiteSpace(observed))
        {
            return observed.Trim();
        }

        string requested = wrapper?.LoadableBundle?.BasisRemoteBundleEncrypted?.RemoteVersionTag;
        return string.IsNullOrWhiteSpace(requested) ? string.Empty : requested.Trim();
    }
    /// <summary>
    /// this allows us to obtain just the meta data.
    /// </summary>
    /// <param name="wrapper"></param>
    /// <param name="report"></param>
    /// <param name="cancellationToken"></param>
    /// <returns>A <see cref="BasisMetaLoadResult"/> describing whether the connector was obtained,
    /// and if not, whether the failure was transient (network/cancel) or fatal (missing/corrupt).</returns>
    public static async Task<BasisMetaLoadResult> HandleMetaOnlyLoad(BasisTrackedBundleWrapper wrapper, BasisProgressReport report, CancellationToken cancellationToken)
    {
        string beeLocation = wrapper.LoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation;
        bool networkSourced = wrapper.LoadableBundle.BasisRemoteBundleEncrypted.IsNetworkSourced;
        if (networkSourced && !Basis.Scripts.Common.BasisUrlSecurity.IsHttpUrlAllowed(beeLocation, out string locationError))
        {
            return BasisMetaLoadResult.Corrupt($"Refusing networked content location: {locationError}");
        }
        if (!networkSourced && BasisIOManagement.TryResolveLocalBeePath(beeLocation, out string localBeePath))
        {
            var (localConnector, localErr) = await BasisBundleManagement.LocalDirectConnectorFile(wrapper, localBeePath, report, cancellationToken);
            if (localConnector == null || !string.IsNullOrEmpty(localErr))
            {
                return BasisMetaLoadResult.Corrupt(localErr ?? "Local connector read failed.");
            }
            return BasisMetaLoadResult.Success;
        }

        var (IsMetaOnDisc, MetaInfo) = await BasisLoadHandler.IsMetaDataOnDiscAsync(beeLocation);
        // Same static-url freshness gate as the full load. Library cards read the connector through
        // here, so without it a card would keep showing the previous name/thumbnail/date after the
        // bee behind its url was replaced.
        bool useCachedConnector = IsMetaOnDisc && await CacheIsCurrentForRequestedVersionAsync(wrapper, MetaInfo, beeLocation, evictStaleCache: false, cancellationToken);
        (BasisBundleConnector Connector, string ErrorMessage) output;
        if (useCachedConnector)
        {
            output = await BasisBundleManagement.ReadConnectorFile(wrapper, MetaInfo.StoredLocal, report, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            BasisDebug.Log("Download Store Meta And Bundle", BasisDebug.LogTag.Event);
            output = await BasisBundleManagement.DownloadConnectorFile(wrapper, report, cancellationToken);
        }
        if (!string.IsNullOrEmpty(output.ErrorMessage))
        {
            // A transient failure (SSL/DNS/timeout/cancel) must not be treated as corruption by the caller.
            // The on-disc cache (if any) is still intact; only the download attempt failed.
            if (BasisMetaLoadResult.LooksLikeTransientError(output.ErrorMessage))
            {
                BasisDebug.LogWarning($"Meta-only load deferred (transient): {output.ErrorMessage}");
                return BasisMetaLoadResult.Transient(output.ErrorMessage);
            }

            BasisDebug.LogError($"Missing BundleArray {output.ErrorMessage}");
            return BasisMetaLoadResult.Corrupt(output.ErrorMessage);
        }
        if (useCachedConnector == false)
        {
            BasisBEEExtensionMeta newDiscInfo = new BasisBEEExtensionMeta
            {
                // Cloned for the same reason as the full-load path: a shared record lets the
                // library UI's CachedVersionTag write re-key a live wrapper.
                StoredRemote = wrapper.LoadableBundle.BasisRemoteBundleEncrypted.Clone(),
                // Connector-only load: no platform section was written to disk. Snapshot only
                // what actually exists — carrying the wrapper's pre-generated bee path here
                // made the full-load path read a file that was never downloaded.
                StoredLocal = new BasisStoredEncryptedBundle
                {
                    DownloadedConnectorFileLocation = wrapper.LoadableBundle.BasisLocalEncryptedBundle?.DownloadedConnectorFileLocation,
                    DownloadedBeeFileLocation = string.Empty,
                },
                UniqueVersion = wrapper.LoadableBundle.BasisBundleConnector.UniqueVersion,
                DownloadedPlatform = BasisIOManagement.GetCurrentCachePlatform(),
                CachedVersionTag = ResolveCachedVersionTag(wrapper),
                LastValidatedUnixUtc = BasisContentVersion.NowUnixUtc(),
            };

            await BasisLoadHandler.AddDiscInfo(newDiscInfo);
            BasisStorageManagement.EnforceCacheSizeLimit();
        }

        return BasisMetaLoadResult.Success;
    }
}
