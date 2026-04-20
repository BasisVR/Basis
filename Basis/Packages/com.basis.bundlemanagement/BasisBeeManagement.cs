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
    /// this allows obtaining the entire bee file
    /// </summary>
    /// <param name="wrapper"></param>
    /// <param name="report"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static async Task HandleBundleAndMetaLoading(BasisTrackedBundleWrapper wrapper, BasisProgressReport report, CancellationToken cancellationToken, long MaxDownloadSizeInBytes = 4L * 1024 * 1024 * 1024)
    {
        bool IsMetaOnDisc = BasisLoadHandler.IsMetaDataOnDisc(wrapper.LoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation, out BasisBEEExtensionMeta MetaInfo);
        bool didForceRedownload = false;
        bool shouldUseOnDiskMeta = IsMetaOnDisc;

        if (shouldUseOnDiskMeta && !HasCompatibleDownloadedPlatform(MetaInfo) && !string.IsNullOrEmpty(MetaInfo.DownloadedPlatform))
        {
            BasisDebug.Log($"Cached bundle platform {MetaInfo.DownloadedPlatform} does not match {Application.platform}. Forcing re-download.", BasisDebug.LogTag.Event);
            shouldUseOnDiskMeta = false;
        }

        (BasisBundleGenerated, byte[], string) output;
        if (shouldUseOnDiskMeta)
        {
            BasisDebug.Log("Process On Disc Meta Data Async", BasisDebug.LogTag.Event);
            output = await BasisBundleManagement.LocalLoadBundleConnector(wrapper, MetaInfo.StoredLocal, report, cancellationToken);
        }
        else
        {
            BasisDebug.Log("Download Store Meta And Bundle", BasisDebug.LogTag.Event);
            output = await BasisBundleManagement.DownloadLoadBundleConnector(wrapper, report, cancellationToken, MaxDownloadSizeInBytes);
        }
        if(output.Item2 == null || output.Item2.Length == 0)
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

                    if (output.Item1 == null || output.Item2 == null || output.Item2.Length == 0 || !string.IsNullOrEmpty(output.Item3))
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
    /// Saves or updates on-disc metadata when it is missing or was refreshed by a forced re-download.
    /// </summary>
    private static async Task SaveMetaIfNeeded(BasisTrackedBundleWrapper wrapper, bool wasMetaOnDisc, bool didForceRedownload, string downloadedPlatform)
    {
        if (!wasMetaOnDisc || didForceRedownload)
        {
            BasisBEEExtensionMeta newDiscInfo = new BasisBEEExtensionMeta
            {
                StoredRemote = wrapper.LoadableBundle.BasisRemoteBundleEncrypted,
                StoredLocal = wrapper.LoadableBundle.BasisLocalEncryptedBundle,
                UniqueVersion = wrapper.LoadableBundle.BasisBundleConnector.UniqueVersion,
                DownloadedPlatform = downloadedPlatform,
            };

            await BasisLoadHandler.AddDiscInfo(newDiscInfo);
            BasisStorageManagement.EnforceCacheSizeLimit();
        }
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
        bool IsMetaOnDisc = BasisLoadHandler.IsMetaDataOnDisc(wrapper.LoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation, out BasisBEEExtensionMeta MetaInfo);
        (BasisBundleConnector Connector, string ErrorMessage) output;
        if (IsMetaOnDisc)
        {
            BasisDebug.Log("Process On Disc Meta Data Async", BasisDebug.LogTag.Event);
            output = await BasisBundleManagement.ReadConnectorFile(wrapper, MetaInfo.StoredLocal, report, cancellationToken);
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
        if (IsMetaOnDisc == false)
        {
            BasisBEEExtensionMeta newDiscInfo = new BasisBEEExtensionMeta
            {
                StoredRemote = wrapper.LoadableBundle.BasisRemoteBundleEncrypted,
                StoredLocal = wrapper.LoadableBundle.BasisLocalEncryptedBundle,
                UniqueVersion = wrapper.LoadableBundle.BasisBundleConnector.UniqueVersion,
                DownloadedPlatform = BasisIOManagement.GetCurrentCachePlatform(),
            };

            await BasisLoadHandler.AddDiscInfo(newDiscInfo);
            BasisStorageManagement.EnforceCacheSizeLimit();
        }

        return BasisMetaLoadResult.Success;
    }
}
