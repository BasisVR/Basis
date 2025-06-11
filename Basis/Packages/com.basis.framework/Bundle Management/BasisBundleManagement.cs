using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public static class BasisBundleManagement
{
    public static string BasisMetaExtension = ".BME";
    public static string BasisEncryptedExtension = ".BEE";
    public static string AssetBundlesFolder = "BEEData";
    public static async Task<(BasisBundleGenerated, byte[],string ErrorMessage)> DownloadStoreMetaAndBundle(BasisTrackedBundleWrapper bundleWrapper, BasisProgressReport progressCallback, CancellationToken cancellationToken)
    {
        if (!IsValidBundleWrapper(bundleWrapper)) return new(null, null,"Invalid Bundle Wrapper!");

        string metaUrl = bundleWrapper.LoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation;
        if (!IsValidUrl(metaUrl)) return new(null, null, "Combined URL is missing!");

        try
        {
            BasisDebug.Log($"Starting download process for {metaUrl}");

            (BasisBundleConnector connector, string localPath, byte[] Bytes) = await BasisIOManagement.DownloadBEE(metaUrl, bundleWrapper.LoadableBundle.UnlockPassword, progressCallback, cancellationToken);
            bundleWrapper.LoadableBundle.BasisBundleConnector = connector;
            bundleWrapper.LoadableBundle.BasisLocalEncryptedBundle.DownloadedBeeFileLocation = localPath;

            if (!IsValidConnector(bundleWrapper.LoadableBundle.BasisBundleConnector)) return new (null, null, "");

            BasisDebug.Log($"Downloading bundle file from {metaUrl}");
            if (bundleWrapper.LoadableBundle.BasisBundleConnector.GetPlatform(out BasisBundleGenerated Generated))
            {
                return (Generated,Bytes, string.Empty);
            }
            else
            {
                return (null,null,"Was Able to load connector but is missing bundle for platform " + Application.platform);
            }
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"Error during download and processing of meta: {ex.Message} {ex.StackTrace} {ex?.InnerException?.StackTrace}");
            return new(null, null, $"Error during download and processing of meta: {ex.Message} {ex.StackTrace} {ex?.InnerException?.StackTrace}");
        }
    }

    public static async Task<(BasisBundleGenerated, byte[], string Error)> ProcessOnDiscMetaDataAsync(BasisTrackedBundleWrapper bundleWrapper, BasisStoredEncryptedBundle storedBundle, BasisProgressReport progressCallback, CancellationToken cancellationToken)
    {
        if (!IsValidBundleWrapper(bundleWrapper) || storedBundle == null)
        {
            BasisDebug.LogError("Invalid bundle data. Exiting method.");
            return new(null, null, "Invalid Bundle Wrapper!");
        }

        bundleWrapper.LoadableBundle.BasisLocalEncryptedBundle = storedBundle;
        string metaUrl = bundleWrapper.LoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation;

        if (!IsValidUrl(metaUrl))
        {
            return new(null, null, "Invalid CombinedURL!");
        }
        try
        {
            BasisDebug.Log($"Processing on-disk meta at {storedBundle.DownloadedBeeFileLocation}");

            (BasisBundleConnector, byte[]) value = await BasisIOManagement.ReadBEEFile(storedBundle.DownloadedBeeFileLocation, bundleWrapper.LoadableBundle.UnlockPassword, progressCallback, cancellationToken);
            bundleWrapper.LoadableBundle.BasisBundleConnector = value.Item1;

            if (bundleWrapper.LoadableBundle == null)
            {
                BasisDebug.LogError("Failed to process the Connector and related files.");
            }
            else
            {
                BasisDebug.Log("Successfully processed the Connector and related files.");
            }
            if (bundleWrapper.LoadableBundle.BasisBundleConnector.GetPlatform(out BasisBundleGenerated Generated))
            {
                return (Generated, value.Item2, string.Empty);
            }
            else
            {
                return (null, null, "Was Able to load connector but is missing bundle for platform " + Application.platform);
            }
        }
        catch (Exception ex)
        {
            string Parse = $"{ex.Message} {ex.StackTrace} {ex?.InnerException?.StackTrace}";
            BasisDebug.LogError($"Error during download and processing of meta: {Parse}");
            return new(null, null, $"Error during download and processing of meta: {Parse}");
        }
    }

    private static bool IsValidBundleWrapper(BasisTrackedBundleWrapper bundleWrapper)
    {
        if (bundleWrapper?.LoadableBundle == null || bundleWrapper.LoadableBundle.BasisRemoteBundleEncrypted == null)
        {
            BasisDebug.LogError("Invalid BasisTrackedBundleWrapper.");
            return false;
        }
        return true;
    }

    private static bool IsValidUrl(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            BasisDebug.LogError("MetaURL is null or empty.");
            return false;
        }
        return true;
    }

    private static bool IsValidConnector(BasisBundleConnector connector)
    {
        if (connector == null)
        {
            BasisDebug.LogError("Failed to decrypt meta file, Basis Bundle Connector is null.");
            return false;
        }
        return true;
    }
}
