using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
public static class BasisBeeManagement
{
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

        (BasisBundleGenerated, byte[], string) output;
        if (IsMetaOnDisc)
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
            throw new Exception($"missing Bundle Bytes Array Error Message {output.Item3}");
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
                    BasisDebug.Log($"we already have this AssetToLoadName in our loaded bundles using that instead! {AssetToLoadName}");
                    await SaveMetaIfNeeded(wrapper, IsMetaOnDisc, didForceRedownload);
                    return;
                }
            }
        }
        BasisDebug.Log("Calling Load Request", BasisDebug.LogTag.System);
        try
        {
            AssetBundleCreateRequest bundleRequest = await BasisEncryptionToData.GenerateBundleFromFile(wrapper.LoadableBundle.UnlockPassword, output.Item2, output.Item1.AssetBundleCRC, report);

            wrapper.AssetBundle = bundleRequest.assetBundle;

            await SaveMetaIfNeeded(wrapper, IsMetaOnDisc, didForceRedownload);
        }
        catch (Exception ex)
        {
            BasisDebug.LogError(ex);
        }
    }
    /// <summary>
    /// Saves or updates on-disc metadata when it is missing or was refreshed by a forced re-download.
    /// </summary>
    private static async Task SaveMetaIfNeeded(BasisTrackedBundleWrapper wrapper, bool wasMetaOnDisc, bool didForceRedownload)
    {
        if (!wasMetaOnDisc || didForceRedownload)
        {
            BasisBEEExtensionMeta newDiscInfo = new BasisBEEExtensionMeta
            {
                StoredRemote = wrapper.LoadableBundle.BasisRemoteBundleEncrypted,
                StoredLocal = wrapper.LoadableBundle.BasisLocalEncryptedBundle,
                UniqueVersion = wrapper.LoadableBundle.BasisBundleConnector.UniqueVersion,
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
    /// <returns>Task bool, this bool is true if we read the BundleArray, false if we received an output message</returns>
    public static async Task<bool> HandleMetaOnlyLoad(BasisTrackedBundleWrapper wrapper, BasisProgressReport report, CancellationToken cancellationToken)
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
            BasisDebug.LogError($"Missing BundleArray {output.ErrorMessage}");
            return false;
        }
        if (IsMetaOnDisc == false)
        {
            BasisBEEExtensionMeta newDiscInfo = new BasisBEEExtensionMeta
            {
                StoredRemote = wrapper.LoadableBundle.BasisRemoteBundleEncrypted,
                StoredLocal = wrapper.LoadableBundle.BasisLocalEncryptedBundle,
                UniqueVersion = wrapper.LoadableBundle.BasisBundleConnector.UniqueVersion,
            };

            await BasisLoadHandler.AddDiscInfo(newDiscInfo);
            BasisStorageManagement.EnforceCacheSizeLimit();
        }

        return true;
    }
}
