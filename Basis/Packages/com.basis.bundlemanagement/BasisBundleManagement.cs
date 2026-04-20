using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using static BasisIOManagement;
public static class BasisBundleManagement
{
    /// <summary>
    /// Downloads remote BEE, stores it, and returns the platform-matching generated metadata + bundle bytes.
    /// </summary>
    public static async Task<(BasisBundleGenerated Generated, byte[] BundleBytes, string ErrorMessage)> DownloadLoadBundleConnector(BasisTrackedBundleWrapper bundleWrapper, BasisProgressReport progressCallback, CancellationToken cancellationToken, long MaxDownloadSizeInMB = 4L * 1024 * 1024 * 1024)
    {
        if (!BasisBeeValidator.ValidateWrapperPasswordAndUrl(bundleWrapper, out string url, out string err))
        {
            return (null, null, err);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return (null, null, "Cancelled before starting.");
        }
        BasisDebug.Log("Starting download process for " + url);
        BeeResult<BeeDownloadResult> result = await BasisIOManagement.DownloadBEEEx(url, bundleWrapper.LoadableBundle.UnlockPassword, progressCallback, cancellationToken,MaxDownloadSizeInMB);

        if (!result.IsSuccess || result.Value is null)
        {
            return (null, null, BasisBeeValidator.BuildResultError("DownloadBEEEx failed", string.IsNullOrEmpty(result.Error), result.ResponseCode != -1 && result.ResponseCode != 0, result.Error, result.ResponseCode));
        }

        BasisIOManagement.BeeDownloadResult bee = result.Value;

        if (string.IsNullOrWhiteSpace(bee.LocalPath))
        {
            return (null, null, "Download completed but local file path is empty.");
        }

        if (bee.Connector is null)
        {
            return (null, null, "Connector is null after download.");
        }

        if (bee.SectionData is null || bee.SectionData.Length == 0)
        {
            return (null, null, "Section data is missing after download.");
        }

        // persist references to wrapper
        bundleWrapper.LoadableBundle.BasisBundleConnector = bee.Connector;
        bundleWrapper.LoadableBundle.BasisLocalEncryptedBundle.DownloadedBeeFileLocation = bee.LocalPath;

        BasisDebug.Log("Parsing downloaded connector & resolving platform bundle from " + url);
        if (!TryGetPlatform(bundleWrapper.LoadableBundle.BasisBundleConnector, out BasisBundleGenerated generated, out string pfErr))
        {
            return (null, null, "Connector loaded, but " + pfErr + " (platform=" + Application.platform + ").");
        }

        return (generated, bee.SectionData, string.Empty);
    }

    /// <summary>
    /// Reads connector and section bytes from an already-downloaded .BEE file.
    /// </summary>
    public static async Task<(BasisBundleGenerated Generated, byte[] BundleBytes, string ErrorMessage)> LocalLoadBundleConnector(BasisTrackedBundleWrapper bundleWrapper, BasisStoredEncryptedBundle storedBundle, BasisProgressReport progressCallback, CancellationToken cancellationToken)
    {
        if (!BasisBeeValidator.IsValidBundleWrapper(bundleWrapper, out string wrapperErr) || storedBundle is null)
        {
            string msg = wrapperErr ?? "Stored bundle is null.";
            BasisDebug.LogError("Invalid bundle data. " + msg);
            return (null, null, "Invalid Bundle Wrapper or stored bundle.");
        }

        if (string.IsNullOrWhiteSpace(storedBundle.DownloadedBeeFileLocation))
        {
            return (null, null, "Stored bundle path is null or empty.");
        }

        if (string.IsNullOrWhiteSpace(bundleWrapper.LoadableBundle.UnlockPassword))
        {
            return (null, null, "Unlock password is null or empty.");
        }

        if (!BasisBeeValidator.IsValidUrl(bundleWrapper.LoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation, out string urlErr))
        {
            return (null, null, urlErr);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return (null, null, "Cancelled before starting.");
        }
        BasisDebug.Log("Processing on-disk meta at " + storedBundle.DownloadedBeeFileLocation);
        BeeResult<BeeReadResult> result = await BasisIOManagement.ReadBEEFileEx(storedBundle.DownloadedBeeFileLocation, bundleWrapper.LoadableBundle.UnlockPassword!, progressCallback, cancellationToken);

        if (!result.IsSuccess || result.Value is null)
        {
            return (null, null, "ReadBEEFileEx failed. " + (result.Error ?? "No details."));
        }

        BeeReadResult data = result.Value;
        bundleWrapper.LoadableBundle.BasisBundleConnector = data.Connector;

        if (!BasisBeeValidator.IsValidConnector(data.Connector, out string connErr))
        {
            return (null!, null!, connErr);
        }

        BasisDebug.Log("Successfully processed the Connector and related files.");
        if (!TryGetPlatform(bundleWrapper.LoadableBundle.BasisBundleConnector, out BasisBundleGenerated generated, out string pfErr))
        {
            return (null!, null!, "Was able to load connector but " + pfErr + " (platform=" + Application.platform + ").");
        }

        return (generated, data.SectionData, string.Empty);
    }

    /// <summary>
    /// Reads only the connector from an already-downloaded .BEE file.
    /// </summary>
    public static async Task<(BasisBundleConnector Connector, string ErrorMessage)> ReadConnectorFile(BasisTrackedBundleWrapper bundleWrapper, BasisStoredEncryptedBundle storedBundle, BasisProgressReport progressCallback, CancellationToken cancellationToken)
    {
        if (!BasisBeeValidator.IsValidBundleWrapper(bundleWrapper, out string wrapperErr) || storedBundle is null)
        {
            return (null, wrapperErr ?? "Stored bundle is null.");
        }

        string readPath = null;
        if (!string.IsNullOrWhiteSpace(storedBundle.DownloadedConnectorFileLocation) && File.Exists(storedBundle.DownloadedConnectorFileLocation))
        {
            readPath = storedBundle.DownloadedConnectorFileLocation;
        }
        else if (!string.IsNullOrWhiteSpace(storedBundle.DownloadedBeeFileLocation) && File.Exists(storedBundle.DownloadedBeeFileLocation))
        {
            readPath = storedBundle.DownloadedBeeFileLocation;
        }

        if (string.IsNullOrWhiteSpace(readPath))
        {
            return (null, "Stored bundle path is null or empty.");
        }

        if (string.IsNullOrWhiteSpace(bundleWrapper.LoadableBundle.UnlockPassword))
        {
            return (null, "Unlock password is null or empty.");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return (null, "Cancelled before starting.");
        }
        BasisDebug.Log("Reading connector from disk: " + readPath);
        BeeResult<BeeReadResult> result = await BasisIOManagement.ReadBEEConnectorFileEx(readPath, bundleWrapper.LoadableBundle.UnlockPassword, progressCallback, cancellationToken);

        if (!result.IsSuccess || result.Value is null)
        {
            return (null, "ReadBEEFileEx failed. " + (result.Error ?? "No details."));
        }

        BasisIOManagement.BeeReadResult data = result.Value;
        bundleWrapper.LoadableBundle.BasisBundleConnector = data.Connector;

        if (!BasisBeeValidator.IsValidConnector(data.Connector, out string connErr))
        {
            return (null, connErr);
        }


        BasisDebug.Log("Successfully recovered connector from disk (connector-only).");
        return (data.Connector, string.Empty);
    }

    /// <summary>
    /// Downloads connector only and returns it.
    /// </summary>
    public static async Task<(BasisBundleConnector Connector, string ErrorMessage)> DownloadConnectorFile(BasisTrackedBundleWrapper bundleWrapper, BasisProgressReport progressCallback, CancellationToken cancellationToken, long MaxDownloadSizeInMB = 4L * 1024 * 1024 * 1024)
    {
        if (!BasisBeeValidator.ValidateWrapperPasswordAndUrl(bundleWrapper, out string url, out string err))
        {
            return (null, err);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return (null, "Cancelled before starting.");
        }
        BasisDebug.Log("Downloading BEE (connector-only) from " + url);
        BeeResult<(BasisBundleConnector, string)> result = await BasisIOManagement.DownloadConnectorOnlyEx(url, bundleWrapper.LoadableBundle.UnlockPassword!, progressCallback, cancellationToken, MaxDownloadSizeInMB);

        if (!result.IsSuccess || result.Value.Item1 is null)
        {
            return (null, BasisBeeValidator.BuildResultError("DownloadConnectorOnlyEx failed", !string.IsNullOrEmpty(result.Error), result.ResponseCode != -1 && result.ResponseCode != 0, result.Error, result.ResponseCode));
        }

        bundleWrapper.LoadableBundle.BasisBundleConnector = result.Value.Item1;
        bundleWrapper.LoadableBundle.BasisLocalEncryptedBundle.DownloadedConnectorFileLocation = result.Value.Item2;

        if (!BasisBeeValidator.IsValidConnector(result.Value.Item1, out string connErr))
        {
            return (null, connErr);
        }

        BasisDebug.Log("Successfully obtained connector (connector-only).");
        return (result.Value.Item1, string.Empty);
    }

    private static bool TryGetPlatform(BasisBundleConnector connector, out BasisBundleGenerated generated, out string error)
    {
        generated = null;
        error = string.Empty;

        try
        {
            if (connector is null)
            {
                error = "Connector is null.";
                return false;
            }

            if (connector.GetPlatform(out generated))
            {
                if (generated is null)
                {
                    error = "GetPlatform returned true but provided generated == null.";
                    return false;
                }

                return true;
            }

            error = "missing bundle for current platform";
            return false;
        }
        catch (Exception ex)
        {
            error = "Exception from GetPlatform: " + ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }
}
