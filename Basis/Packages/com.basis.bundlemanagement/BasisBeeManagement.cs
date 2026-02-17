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
    public static async Task HandleBundleAndMetaLoading(BasisTrackedBundleWrapper wrapper, BasisProgressReport report, CancellationToken cancellationToken, long MaxDownloadSizeInMB = 4L * 1024 * 1024 * 1024)
    {
        bool IsMetaOnDisc = BasisLoadHandler.IsMetaDataOnDisc(wrapper.LoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation, out BasisBEEExtensionMeta MetaInfo);

        (BasisBundleGenerated, byte[], string) output;
        if (IsMetaOnDisc)
        {
            BasisDebug.Log("Process On Disc Meta Data Async", BasisDebug.LogTag.Event);
            output = await BasisBundleManagement.LocalLoadBundleConnector(wrapper, MetaInfo.StoredLocal, report, cancellationToken);
        }
        else
        {
            BasisDebug.Log("Download Store Meta And Bundle", BasisDebug.LogTag.Event);
            output = await BasisBundleManagement.DownloadLoadBundleConnector(wrapper, report, cancellationToken, MaxDownloadSizeInMB);
        }
        if(output.Item2 == null)
        {
            //lets force download it again. this guards against partial file, corrupt file or reattempt at downloading if it fails.
            output = await BasisBundleManagement.DownloadLoadBundleConnector(wrapper, report, cancellationToken, MaxDownloadSizeInMB);
        }

        if (output.Item1 == null || output.Item3 != string.Empty)
        {
            new Exception($"missing Bundle Bytes Array Error Message {output.Item3}");
        }
        IEnumerable<AssetBundle> AssetBundles = AssetBundle.GetAllLoadedAssetBundles();
        foreach (AssetBundle assetBundle in AssetBundles)
        {
            if (output.Item1 == null || output.Item1.AssetToLoadName == null)
            {
                new Exception($"Missing AssetToName! in obtained file! corrupted?");
            }
            else
            {
                string AssetToLoadName = output.Item1.AssetToLoadName;
                if (assetBundle != null && assetBundle.Contains(AssetToLoadName))
                {
                    wrapper.AssetBundle = assetBundle;
                    BasisDebug.Log($"we already have this AssetToLoadName in our loaded bundles using that instead! {AssetToLoadName}");
                    if (IsMetaOnDisc == false)
                    {
                        BasisBEEExtensionMeta newDiscInfo = new BasisBEEExtensionMeta
                        {
                            StoredRemote = wrapper.LoadableBundle.BasisRemoteBundleEncrypted,
                            StoredLocal = wrapper.LoadableBundle.BasisLocalEncryptedBundle,
                            UniqueVersion = wrapper.LoadableBundle.BasisBundleConnector.UniqueVersion,
                        };

                        await BasisLoadHandler.AddDiscInfo(newDiscInfo);
                    }
                    return;
                }
            }
        }
        BasisDebug.Log("Calling Load Request", BasisDebug.LogTag.System);
        try
        {
            AssetBundleCreateRequest bundleRequest = await BasisEncryptionToData.GenerateBundleFromFile(wrapper.LoadableBundle.UnlockPassword, output.Item2, output.Item1.AssetBundleCRC, report);

            wrapper.AssetBundle = bundleRequest.assetBundle;

            if (IsMetaOnDisc == false)
            {
                BasisBEEExtensionMeta newDiscInfo = new BasisBEEExtensionMeta
                {
                    StoredRemote = wrapper.LoadableBundle.BasisRemoteBundleEncrypted,
                    StoredLocal = wrapper.LoadableBundle.BasisLocalEncryptedBundle,
                    UniqueVersion = wrapper.LoadableBundle.BasisBundleConnector.UniqueVersion,
                };

                await BasisLoadHandler.AddDiscInfo(newDiscInfo);
            }
        }
        catch (Exception ex)
        {
            BasisDebug.LogError(ex);
        }
    }
    /// <summary>
    /// this allows us to obtain just the meta data.
    /// </summary>
    /// <param name="wrapper"></param>
    /// <param name="report"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static async Task HandleMetaOnlyLoad(BasisTrackedBundleWrapper wrapper, BasisProgressReport report, CancellationToken cancellationToken)
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
            return;
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
        }
    }
}
