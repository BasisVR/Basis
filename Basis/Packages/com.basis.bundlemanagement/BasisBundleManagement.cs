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
        // The version the HOST reported for these exact bytes. Recorded against the cache entry so
        // freshness is decided against something the server asserted, not something a peer claimed.
        bundleWrapper.ObservedVersionTag = bee.ObservedVersionTag;

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
        BeeResult<BeeReadResult> result = await BasisIOManagement.ReadBEEFileEx(storedBundle.DownloadedBeeFileLocation, bundleWrapper.LoadableBundle.UnlockPassword!, progressCallback, cancellationToken).ConfigureAwait(false);

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
    /// Reads only the connector from an already-downloaded .BEE file. The third tuple element
    /// carries the reader's verdict; only <see cref="BasisLoadFailureKind.Corrupt"/> may be treated
    /// by a caller as grounds for deleting the user's content.
    /// </summary>
    public static async Task<(BasisBundleConnector Connector, string ErrorMessage, BasisLoadFailureKind FailureKind)> ReadConnectorFile(BasisTrackedBundleWrapper bundleWrapper, BasisStoredEncryptedBundle storedBundle, BasisProgressReport progressCallback, CancellationToken cancellationToken)
    {
        if (!BasisBeeValidator.IsValidBundleWrapper(bundleWrapper, out string wrapperErr) || storedBundle is null)
        {
            return (null, wrapperErr ?? "Stored bundle is null.", BasisLoadFailureKind.Unspecified);
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
            return (null, "Stored bundle path is null or empty.", BasisLoadFailureKind.Unspecified);
        }

        if (string.IsNullOrWhiteSpace(bundleWrapper.LoadableBundle.UnlockPassword))
        {
            return (null, "Unlock password is null or empty.", BasisLoadFailureKind.Unspecified);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return (null, "Cancelled before starting.", BasisLoadFailureKind.Unspecified);
        }
        BeeResult<BeeReadResult> result = await BasisIOManagement.ReadBEEConnectorFileEx(readPath, bundleWrapper.LoadableBundle.UnlockPassword, progressCallback, cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess || result.Value is null)
        {
            return (null, "ReadBEEFileEx failed. " + (result.Error ?? "No details."), result.FailureKind);
        }

        BasisIOManagement.BeeReadResult data = result.Value;
        bundleWrapper.LoadableBundle.BasisBundleConnector = data.Connector;

        if (!BasisBeeValidator.IsValidConnector(data.Connector, out string connErr))
        {
            // Unspecified, not Corrupt: IsValidConnector only null-checks, and every caller above
            // already returned on a null connector, so reaching this is an internal invariant
            // violation rather than a statement about the user's bytes. If this check ever grows
            // real field validation, classify each new rejection before letting it evict.
            return (null, connErr, BasisLoadFailureKind.Unspecified);
        }

        return (data.Connector, string.Empty, BasisLoadFailureKind.Unspecified);
    }

    /// <summary>
    /// Downloads connector only and returns it.
    /// </summary>
    public static async Task<(BasisBundleConnector Connector, string ErrorMessage, BasisLoadFailureKind FailureKind)> DownloadConnectorFile(BasisTrackedBundleWrapper bundleWrapper, BasisProgressReport progressCallback, CancellationToken cancellationToken, long MaxDownloadSizeInMB = 4L * 1024 * 1024 * 1024)
    {
        // Refusing to start is not the same as the content being bad: an unsupported scheme, a
        // blocked host or a missing password all land here and must never evict.
        if (!BasisBeeValidator.ValidateWrapperPasswordAndUrl(bundleWrapper, out string url, out string err))
        {
            return (null, err, BasisLoadFailureKind.Unspecified);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return (null, "Cancelled before starting.", BasisLoadFailureKind.Unspecified);
        }
        BasisDebug.Log("Downloading BEE (connector-only) from " + url);
        BeeResult<(BasisBundleConnector, string, string)> result = await BasisIOManagement.DownloadConnectorOnlyEx(url, bundleWrapper.LoadableBundle.UnlockPassword!, progressCallback, cancellationToken, MaxDownloadSizeInMB);

        if (!result.IsSuccess || result.Value.Item1 is null)
        {
            return (null, BasisBeeValidator.BuildResultError("DownloadConnectorOnlyEx failed", !string.IsNullOrEmpty(result.Error), result.ResponseCode != -1 && result.ResponseCode != 0, result.Error, result.ResponseCode), result.FailureKind);
        }

        bundleWrapper.LoadableBundle.BasisBundleConnector = result.Value.Item1;
        bundleWrapper.LoadableBundle.BasisLocalEncryptedBundle.DownloadedConnectorFileLocation = result.Value.Item2;
        bundleWrapper.ObservedVersionTag = result.Value.Item3;

        if (!BasisBeeValidator.IsValidConnector(result.Value.Item1, out string connErr))
        {
            // Unspecified, not Corrupt: IsValidConnector only null-checks, and every caller above
            // already returned on a null connector, so reaching this is an internal invariant
            // violation rather than a statement about the user's bytes. If this check ever grows
            // real field validation, classify each new rejection before letting it evict.
            return (null, connErr, BasisLoadFailureKind.Unspecified);
        }

        BasisDebug.Log("Successfully obtained connector (connector-only).");
        return (result.Value.Item1, string.Empty, BasisLoadFailureKind.Unspecified);
    }

    /// <summary>
    /// Reads connector + platform section directly from a local BEE file. Tries the remote-format
    /// layout (8-byte header, all platform sections — the SDK export) first, then falls back to the
    /// full-file cache layout (4-byte header, single section), so either on-disk format loads.
    /// </summary>
    public static async Task<(BasisBundleGenerated Generated, byte[] BundleBytes, string ErrorMessage)> LocalDirectLoadBundleConnector(BasisTrackedBundleWrapper bundleWrapper, string localPath, BasisProgressReport progressCallback, CancellationToken cancellationToken)
    {
        if (!BasisBeeValidator.IsValidBundleWrapper(bundleWrapper, out string wrapperErr))
        {
            return (null, null, wrapperErr);
        }

        if (string.IsNullOrWhiteSpace(localPath))
        {
            return (null, null, "Local bee path is null or empty.");
        }

        if (string.IsNullOrWhiteSpace(bundleWrapper.LoadableBundle.UnlockPassword))
        {
            return (null, null, "Unlock password is null or empty.");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return (null, null, "Cancelled before starting.");
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
            return (null, null, "Local bee read failed. " + (result.Error ?? "No details."));
        }

        BeeReadResult data = result.Value;
        bundleWrapper.LoadableBundle.BasisBundleConnector = data.Connector;
        bundleWrapper.LoadableBundle.BasisLocalEncryptedBundle.DownloadedBeeFileLocation = localPath;

        if (!BasisBeeValidator.IsValidConnector(data.Connector, out string connErr))
        {
            return (null, null, connErr);
        }

        if (data.SectionData is null || data.SectionData.Length == 0)
        {
            return (null, null, "Section data is missing after local read.");
        }

        if (!TryGetPlatform(bundleWrapper.LoadableBundle.BasisBundleConnector, out BasisBundleGenerated generated, out string pfErr))
        {
            return (null, null, "Local bee loaded connector but " + pfErr + " (platform=" + Application.platform + ").");
        }

        return (generated, data.SectionData, string.Empty);
    }

    /// <summary>
    /// Reads only the connector from a local BEE file, trying the remote-format layout first and
    /// falling back to the full-file cache layout. Used by meta-only loads (library cards).
    /// </summary>
    public static async Task<(BasisBundleConnector Connector, string ErrorMessage, BasisLoadFailureKind FailureKind)> LocalDirectConnectorFile(BasisTrackedBundleWrapper bundleWrapper, string localPath, BasisProgressReport progressCallback, CancellationToken cancellationToken)
    {
        if (!BasisBeeValidator.IsValidBundleWrapper(bundleWrapper, out string wrapperErr))
        {
            return (null, wrapperErr, BasisLoadFailureKind.Unspecified);
        }

        if (string.IsNullOrWhiteSpace(localPath))
        {
            return (null, "Local bee path is null or empty.", BasisLoadFailureKind.Unspecified);
        }

        if (string.IsNullOrWhiteSpace(bundleWrapper.LoadableBundle.UnlockPassword))
        {
            return (null, "Unlock password is null or empty.", BasisLoadFailureKind.Unspecified);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return (null, "Cancelled before starting.", BasisLoadFailureKind.Unspecified);
        }

        string password = bundleWrapper.LoadableBundle.UnlockPassword;
        BasisDebug.Log("Reading local bee connector from disk: " + localPath);

        BeeResult<BeeReadResult> result = await BasisIOManagement.ReadRemoteBeeFromDiskEx(localPath, password, progressCallback, cancellationToken, includeSection: false).ConfigureAwait(false);
        BasisLoadFailureKind remoteFormatKind = result.IsSuccess ? BasisLoadFailureKind.Unspecified : result.FailureKind;
        if (!result.IsSuccess || result.Value is null)
        {
            result = await BasisIOManagement.ReadBEEConnectorFileEx(localPath, password, progressCallback, cancellationToken).ConfigureAwait(false);
        }

        if (!result.IsSuccess || result.Value is null)
        {
            // Reading this file with the wrong layout is EXPECTED — the two readers are a
            // format probe, and whichever one runs second is parsing a header it was never
            // meant to see, so its verdict alone means nothing. Only call the file unusable
            // when both layouts positively rejected it; otherwise the fallback's confusion
            // would delete a local bee that is merely in the other format, or one this
            // reader declined for a reason the first reader deliberately left unclassified.
            BasisLoadFailureKind kind = remoteFormatKind == BasisLoadFailureKind.Corrupt && result.FailureKind == BasisLoadFailureKind.Corrupt
                ? BasisLoadFailureKind.Corrupt
                : BasisLoadFailureKind.Unspecified;
            return (null, "Local bee connector read failed. " + (result.Error ?? "No details."), kind);
        }

        bundleWrapper.LoadableBundle.BasisBundleConnector = result.Value.Connector;
        bundleWrapper.LoadableBundle.BasisLocalEncryptedBundle.DownloadedBeeFileLocation = localPath;

        if (!BasisBeeValidator.IsValidConnector(result.Value.Connector, out string connErr))
        {
            // Unspecified, not Corrupt: IsValidConnector only null-checks, and every caller above
            // already returned on a null connector, so reaching this is an internal invariant
            // violation rather than a statement about the user's bytes. If this check ever grows
            // real field validation, classify each new rejection before letting it evict.
            return (null, connErr, BasisLoadFailureKind.Unspecified);
        }

        return (result.Value.Connector, string.Empty, BasisLoadFailureKind.Unspecified);
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
