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
    public static async Task<(BasisBundleGenerated Generated, BasisBundleSection Section, string ErrorMessage)> DownloadLoadBundleConnector(BasisTrackedBundleWrapper bundleWrapper, BasisProgressReport progressCallback, CancellationToken cancellationToken, long MaxDownloadSizeInMB = 4L * 1024 * 1024 * 1024)
    {
        if (!BasisBeeValidator.ValidateWrapperPasswordAndUrl(bundleWrapper, out string url, out string err))
        {
            return (null, default, err);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return (null, default, "Cancelled before starting.");
        }
        BasisDebug.Log("Starting download process for " + url);
        BeeResult<BeeDownloadResult> result = await BasisIOManagement.DownloadBEEEx(url, bundleWrapper.LoadableBundle.UnlockPassword, progressCallback, cancellationToken,MaxDownloadSizeInMB);

        if (!result.IsSuccess || result.Value is null)
        {
            return (null, default, BasisBeeValidator.BuildResultError("DownloadBEEEx failed", string.IsNullOrEmpty(result.Error), result.ResponseCode != -1 && result.ResponseCode != 0, result.Error, result.ResponseCode));
        }

        BasisIOManagement.BeeDownloadResult bee = result.Value;

        if (string.IsNullOrWhiteSpace(bee.LocalPath))
        {
            return (null, default, "Download completed but local file path is empty.");
        }

        if (bee.Connector is null)
        {
            return (null, default, "Connector is null after download.");
        }

        if (bee.SectionData is null || bee.SectionData.Length == 0)
        {
            return (null, default, "Section data is missing after download.");
        }

        // persist references to wrapper
        bundleWrapper.LoadableBundle.BasisBundleConnector = bee.Connector;
        bundleWrapper.LoadableBundle.BasisLocalEncryptedBundle.DownloadedBeeFileLocation = bee.LocalPath;
        // The version the HOST reported for these exact bytes. Recorded against the cache entry so
        // freshness is decided against something the server asserted, not something a peer claimed.
        bundleWrapper.ObservedVersionTag = bee.ObservedVersionTag;

        BasisDebug.Log("Parsing downloaded connector & resolving platform bundle from " + url);
        if (!TryGetPlatform(bundleWrapper.LoadableBundle.BasisBundleConnector, out BasisBundleGenerated generated, out string pfErr))
        {
            return (null, default, "Connector loaded, but " + pfErr + " (platform=" + Application.platform + ").");
        }

        return (generated, BasisBundleSection.FromBytes(bee.SectionData), string.Empty);
    }

    /// <summary>
    /// Reads connector and section LENGTH from an already-downloaded .BEE file, without pulling the
    /// encrypted section into memory. For callers that only need to confirm the cached file is
    /// intact and complete — reading the bytes to length-check them costs a full-bundle managed
    /// allocation per item, which on the LOH is never handed back to the OS.
    /// </summary>
    public static async Task<(BasisBundleGenerated Generated, long SectionLength, string ErrorMessage)> LocalVerifyBundleConnector(BasisTrackedBundleWrapper bundleWrapper, BasisStoredEncryptedBundle storedBundle, BasisProgressReport progressCallback, CancellationToken cancellationToken)
    {
        var (generated, result, error) = await LocalReadBundleConnector(bundleWrapper, storedBundle, progressCallback, cancellationToken);
        if (generated == null || !string.IsNullOrEmpty(error))
        {
            return (null, 0, error);
        }

        return (generated, result.SectionLength, string.Empty);
    }

    /// <summary>
    /// Locates the connector and the platform section of an already-downloaded .BEE file. The
    /// section is returned as a range of that file rather than as bytes: it is decrypted straight
    /// off disk, so a cached load never holds the encrypted bundle in managed memory at all.
    /// </summary>
    public static async Task<(BasisBundleGenerated Generated, BasisBundleSection Section, string ErrorMessage)> LocalLoadBundleConnector(BasisTrackedBundleWrapper bundleWrapper, BasisStoredEncryptedBundle storedBundle, BasisProgressReport progressCallback, CancellationToken cancellationToken)
    {
        var (generated, result, error) = await LocalReadBundleConnector(bundleWrapper, storedBundle, progressCallback, cancellationToken);
        if (generated == null || !string.IsNullOrEmpty(error))
        {
            return (null, default, error);
        }

        return (generated, BasisBundleSection.FromFile(storedBundle.DownloadedBeeFileLocation, result.SectionOffset, result.SectionLength), string.Empty);
    }

    private static async Task<(BasisBundleGenerated Generated, BeeReadResult Result, string ErrorMessage)> LocalReadBundleConnector(BasisTrackedBundleWrapper bundleWrapper, BasisStoredEncryptedBundle storedBundle, BasisProgressReport progressCallback, CancellationToken cancellationToken)
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
        BeeResult<BeeReadResult> result = await BasisIOManagement.ReadBEEFileEx(storedBundle.DownloadedBeeFileLocation, bundleWrapper.LoadableBundle.UnlockPassword!, progressCallback, cancellationToken, includeSection: false).ConfigureAwait(false);

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

        return (generated, data, string.Empty);
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
        BeeResult<BeeReadResult> result = await BasisIOManagement.ReadBEEConnectorFileEx(readPath, bundleWrapper.LoadableBundle.UnlockPassword, progressCallback, cancellationToken).ConfigureAwait(false);

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
        BeeResult<(BasisBundleConnector, string, string)> result = await BasisIOManagement.DownloadConnectorOnlyEx(url, bundleWrapper.LoadableBundle.UnlockPassword!, progressCallback, cancellationToken, MaxDownloadSizeInMB);

        if (!result.IsSuccess || result.Value.Item1 is null)
        {
            return (null, BasisBeeValidator.BuildResultError("DownloadConnectorOnlyEx failed", !string.IsNullOrEmpty(result.Error), result.ResponseCode != -1 && result.ResponseCode != 0, result.Error, result.ResponseCode));
        }

        bundleWrapper.LoadableBundle.BasisBundleConnector = result.Value.Item1;
        bundleWrapper.LoadableBundle.BasisLocalEncryptedBundle.DownloadedConnectorFileLocation = result.Value.Item2;
        bundleWrapper.ObservedVersionTag = result.Value.Item3;

        if (!BasisBeeValidator.IsValidConnector(result.Value.Item1, out string connErr))
        {
            return (null, connErr);
        }

        BasisDebug.Log("Successfully obtained connector (connector-only).");
        return (result.Value.Item1, string.Empty);
    }

    /// <summary>
    /// Reads connector + platform section directly from a local BEE file. Tries the remote-format
    /// layout (8-byte header, all platform sections — the SDK export) first, then falls back to the
    /// full-file cache layout (4-byte header, single section), so either on-disk format loads.
    /// </summary>
    public static async Task<(BasisBundleGenerated Generated, BasisBundleSection Section, string ErrorMessage)> LocalDirectLoadBundleConnector(BasisTrackedBundleWrapper bundleWrapper, string localPath, BasisProgressReport progressCallback, CancellationToken cancellationToken)
    {
        if (!BasisBeeValidator.IsValidBundleWrapper(bundleWrapper, out string wrapperErr))
        {
            return (null, default, wrapperErr);
        }

        if (string.IsNullOrWhiteSpace(localPath))
        {
            return (null, default, "Local bee path is null or empty.");
        }

        if (string.IsNullOrWhiteSpace(bundleWrapper.LoadableBundle.UnlockPassword))
        {
            return (null, default, "Unlock password is null or empty.");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return (null, default, "Cancelled before starting.");
        }

        string password = bundleWrapper.LoadableBundle.UnlockPassword;
        BasisDebug.Log("Reading local bee file from disk: " + localPath);

        BeeResult<BeeReadResult> result = await BasisIOManagement.ReadRemoteBeeFromDiskEx(localPath, password, progressCallback, cancellationToken, includeSection: true).ConfigureAwait(false);
        if (!result.IsSuccess || result.Value is null)
        {
            BasisDebug.Log($"Local remote-format read failed ({result.Error}); trying full-file format.", BasisDebug.LogTag.Event);
            result = await BasisIOManagement.ReadBEEFileEx(localPath, password, progressCallback, cancellationToken).ConfigureAwait(false);
        }

        if (!result.IsSuccess || result.Value is null)
        {
            return (null, default, "Local bee read failed. " + (result.Error ?? "No details."));
        }

        BeeReadResult data = result.Value;
        bundleWrapper.LoadableBundle.BasisBundleConnector = data.Connector;
        bundleWrapper.LoadableBundle.BasisLocalEncryptedBundle.DownloadedBeeFileLocation = localPath;

        if (!BasisBeeValidator.IsValidConnector(data.Connector, out string connErr))
        {
            return (null, default, connErr);
        }

        if (data.SectionData is null || data.SectionData.Length == 0)
        {
            return (null, default, "Section data is missing after local read.");
        }

        if (!TryGetPlatform(bundleWrapper.LoadableBundle.BasisBundleConnector, out BasisBundleGenerated generated, out string pfErr))
        {
            return (null, default, "Local bee loaded connector but " + pfErr + " (platform=" + Application.platform + ").");
        }

        return (generated, BasisBundleSection.FromBytes(data.SectionData), string.Empty);
    }

    /// <summary>
    /// Reads only the connector from a local BEE file, trying the remote-format layout first and
    /// falling back to the full-file cache layout. Used by meta-only loads (library cards).
    /// </summary>
    public static async Task<(BasisBundleConnector Connector, string ErrorMessage)> LocalDirectConnectorFile(BasisTrackedBundleWrapper bundleWrapper, string localPath, BasisProgressReport progressCallback, CancellationToken cancellationToken)
    {
        if (!BasisBeeValidator.IsValidBundleWrapper(bundleWrapper, out string wrapperErr))
        {
            return (null, wrapperErr);
        }

        if (string.IsNullOrWhiteSpace(localPath))
        {
            return (null, "Local bee path is null or empty.");
        }

        if (string.IsNullOrWhiteSpace(bundleWrapper.LoadableBundle.UnlockPassword))
        {
            return (null, "Unlock password is null or empty.");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return (null, "Cancelled before starting.");
        }

        string password = bundleWrapper.LoadableBundle.UnlockPassword;
        BasisDebug.Log("Reading local bee connector from disk: " + localPath);

        BeeResult<BeeReadResult> result = await BasisIOManagement.ReadRemoteBeeFromDiskEx(localPath, password, progressCallback, cancellationToken, includeSection: false).ConfigureAwait(false);
        if (!result.IsSuccess || result.Value is null)
        {
            result = await BasisIOManagement.ReadBEEConnectorFileEx(localPath, password, progressCallback, cancellationToken).ConfigureAwait(false);
        }

        if (!result.IsSuccess || result.Value is null)
        {
            return (null, "Local bee connector read failed. " + (result.Error ?? "No details."));
        }

        bundleWrapper.LoadableBundle.BasisBundleConnector = result.Value.Connector;
        bundleWrapper.LoadableBundle.BasisLocalEncryptedBundle.DownloadedBeeFileLocation = localPath;

        if (!BasisBeeValidator.IsValidConnector(result.Value.Connector, out string connErr))
        {
            return (null, connErr);
        }

        return (result.Value.Connector, string.Empty);
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
