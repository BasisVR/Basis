using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

public static class BasisIOManagement
{
    public static string PersistentDataPath { get; private set; }
    public static RuntimePlatform CachedPlatform { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void CacheUnityMainThreadValues()
    {
        PersistentDataPath = Application.persistentDataPath;
        CachedPlatform = Application.platform;
    }

    public static string GetCurrentCachePlatform()
    {
        return NormalizeCachePlatformName(CachedPlatform.ToString());
    }

    /// <summary>
    /// Canonical form of a remote bee URL for cache-identity purposes (disc-meta keys and
    /// in-memory bundle keys). The same location arrives in different spellings — avatar
    /// records carry escaped paths ("Dooly%20Sailor3") while other sources carry raw ones
    /// ("Dooly Sailor3"), with host casing differing too. Raw string equality treats those
    /// as different bees, so each flow redownloads and overwrites the other's cache meta
    /// every session. Absolute http(s) URLs normalize scheme/host casing and path escaping;
    /// anything else (local paths, empty) returns trimmed input.
    /// </summary>
    public static string CanonicalizeRemoteUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }
        string trimmed = url.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out Uri uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return uri.AbsoluteUri;
        }
        return trimmed;
    }

    public static bool CachePlatformMatchesCurrent(string downloadedPlatform)
    {
        string normalized = NormalizeCachePlatformName(downloadedPlatform);
        // A cached Generic (glTF) section is platform-agnostic, so it is valid on any device.
        if (string.Equals(normalized, BasisBundleConnector.GenericPlatform, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return string.Equals(normalized, GetCurrentCachePlatform(), StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeCachePlatformName(string platformName)
    {
        if (string.IsNullOrWhiteSpace(platformName))
        {
            return string.Empty;
        }

        string normalized = platformName.Trim();
        return normalized switch
        {
            nameof(RuntimePlatform.WindowsEditor) => "StandaloneWindows64",
            nameof(RuntimePlatform.WindowsPlayer) => "StandaloneWindows64",
            nameof(RuntimePlatform.WindowsServer) => "StandaloneWindows64",
            nameof(RuntimePlatform.LinuxEditor) => "StandaloneLinux64",
            nameof(RuntimePlatform.LinuxPlayer) => "StandaloneLinux64",
            nameof(RuntimePlatform.LinuxServer) => "StandaloneLinux64",
            nameof(RuntimePlatform.OSXEditor) => "StandaloneOSX",
            nameof(RuntimePlatform.OSXPlayer) => "StandaloneOSX",
            nameof(RuntimePlatform.Android) => "Android",
            nameof(RuntimePlatform.IPhonePlayer) => "iOS",
            _ => normalized,
        };
    }

    public static string GetBeeCacheFilePath(string uniqueVersion, string downloadedPlatform = null)
    {
        return GenerateFilePath(BuildPlatformAwareCacheFileName(uniqueVersion, BasisBeeConstants.BasisEncryptedExtension, downloadedPlatform), BasisBeeConstants.AssetBundlesFolder);
    }

    public static string GetConnectorCacheFilePath(string uniqueVersion, string downloadedPlatform = null)
    {
        return GenerateFilePath(BuildPlatformAwareCacheFileName(uniqueVersion, BasisBeeConstants.BasisConnectorExtension, downloadedPlatform), BasisBeeConstants.AssetBundlesFolder);
    }

    public static string GetMetaCacheFilePath(string uniqueVersion, string downloadedPlatform = null)
    {
        return GenerateFilePath(BuildPlatformAwareCacheFileName(uniqueVersion, BasisBeeConstants.BasisMetaExtension, downloadedPlatform), BasisBeeConstants.AssetBundlesFolder);
    }

    public static string GetLegacyBeeCacheFilePath(string uniqueVersion)
    {
        return GenerateFilePath($"{uniqueVersion}{BasisBeeConstants.BasisEncryptedExtension}", BasisBeeConstants.AssetBundlesFolder);
    }

    public static string GetLegacyMetaCacheFilePath(string uniqueVersion)
    {
        return GenerateFilePath($"{uniqueVersion}{BasisBeeConstants.BasisMetaExtension}", BasisBeeConstants.AssetBundlesFolder);
    }

    private static string BuildPlatformAwareCacheFileName(string uniqueVersion, string extension, string downloadedPlatform)
    {
        if (string.IsNullOrWhiteSpace(uniqueVersion))
            throw new ArgumentException("Unique version is null or empty.", nameof(uniqueVersion));

        string normalizedPlatform = string.IsNullOrWhiteSpace(downloadedPlatform)
            ? GetCurrentCachePlatform()
            : NormalizeCachePlatformName(downloadedPlatform);

        foreach (char invalidChar in Path.GetInvalidFileNameChars())
        {
            normalizedPlatform = normalizedPlatform.Replace(invalidChar, '_');
            uniqueVersion = uniqueVersion.Replace(invalidChar, '_');
        }

        return $"{uniqueVersion}.{normalizedPlatform}{extension}";
    }

    /// <summary>
    /// HTTP cache validators for a remote bee. These are what let a client ask "is the file at this
    /// url still the one I cached?" without downloading it, which is the cheap half of supporting
    /// content published to a static url.
    /// </summary>
    public readonly struct BasisRemoteValidator
    {
        public readonly string ETag;
        public readonly string LastModified;
        /// <summary>
        /// The host answered a conditional request with 304, i.e. it confirmed the cached copy is
        /// current. Stronger than comparing tags ourselves, and costs no body.
        /// </summary>
        public readonly bool NotModified;

        public BasisRemoteValidator(string eTag, string lastModified, bool notModified = false)
        {
            ETag = eTag;
            LastModified = lastModified;
            NotModified = notModified;
        }

        public bool HasValue => !string.IsNullOrWhiteSpace(ETag) || !string.IsNullOrWhiteSpace(LastModified);

        /// <summary>
        /// The single opaque tag stored against cached bytes. ETag wins because it tracks content
        /// exactly; Last-Modified only has one-second resolution and would miss an edit republished
        /// within the same second, so it is the fallback. Prefixed so the two are never confused
        /// and a stored tag can be turned back into the right conditional header.
        /// </summary>
        public string Tag
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(ETag))
                {
                    return ETag.Trim();
                }
                if (!string.IsNullOrWhiteSpace(LastModified))
                {
                    return BasisContentVersion.LastModifiedPrefix + LastModified.Trim();
                }
                return string.Empty;
            }
        }
    }

    private static BasisRemoteValidator ReadValidator(UnityWebRequest req)
    {
        if (req == null)
        {
            return default;
        }
        return new BasisRemoteValidator(req.GetResponseHeader("ETag"), req.GetResponseHeader("Last-Modified"));
    }

    public sealed class BeeDownloadResult
    {
        public BasisBundleConnector Connector { get; }
        public string LocalPath { get; }
        public byte[] SectionData { get; }
        /// <summary>
        /// Validator the host reported for the bytes just fetched, or empty when it publishes none.
        /// Recorded against the cache entry so a later load can tell "same url, new bytes" apart
        /// from "same url, same bytes" — which url identity alone cannot express.
        /// </summary>
        public string ObservedVersionTag { get; }

        public BeeDownloadResult(BasisBundleConnector connector, string localPath, byte[] sectionData, string observedVersionTag = null)
        {
            Connector = connector ?? throw new ArgumentNullException(nameof(connector));
            LocalPath = localPath ?? throw new ArgumentNullException(nameof(localPath));
            SectionData = sectionData ?? throw new ArgumentNullException(nameof(sectionData));
            ObservedVersionTag = observedVersionTag ?? string.Empty;
        }
    }

    public sealed class BeeReadResult
    {
        public BasisBundleConnector Connector { get; }
        public byte[] SectionData { get; }

        public BeeReadResult(BasisBundleConnector connector, byte[] sectionData)
        {
            Connector = connector;
            SectionData = sectionData;
        }
    }

    private sealed class DownloadPayload
    {
        public byte[] Data; // present when downloaded to memory
        public string Path; // present when downloaded to file
        public BasisRemoteValidator Validator; // response cache validators, when the host sends any
    }

    /// <summary>
    /// Downloads a remote BEE blob (with 8-byte Int64 header), decrypts/parses the connector,
    /// downloads the platform-matching section, writes a local .bee file (4-byte Int32 header),
    /// and returns all artifacts.
    /// </summary>
    public static async Task<BeeResult<BeeDownloadResult>> DownloadBEEEx(string url, string vp, BasisProgressReport progressCallback, CancellationToken cancellationToken = default, long MaxDownloadSizeInMB = 4L * 1024 * 1024 * 1024)
    {
        // Validate inputs with actionable messages
        if (!ValidateUrl(url, out url, out var urlErr))
            return BeeResult<BeeDownloadResult>.Fail($"DownloadBEEEx: {urlErr}");

        if (string.IsNullOrWhiteSpace(vp))
            return BeeResult<BeeDownloadResult>.Fail("DownloadBEEEx: VP is null or empty.");

        // 1) Read 8-byte remote header (Int64)
        var headerRes = await DownloadRangeInternal(url, startByte: 0, endByteInclusive: BasisBeeConstants.RemoteHeaderSize - 1, toFilePath: null, progressCallback, cancellationToken, MaxDownloadSizeInMB);

        if (!headerRes.IsSuccess || headerRes.Value?.Data == null)
            return BeeResult<BeeDownloadResult>.Fail($"DownloadBEEEx: Failed to read remote header. {headerRes.Error ?? "No data"}", headerRes.ResponseCode);

        if (headerRes.Value.Data.Length != BasisBeeConstants.RemoteHeaderSize)
            return BeeResult<BeeDownloadResult>.Fail($"DownloadBEEEx: Remote header size mismatch. Expected {BasisBeeConstants.RemoteHeaderSize} bytes, got {headerRes.Value.Data.Length}.", headerRes.ResponseCode);

        // Captured from the header request the loader already makes, so establishing the content
        // version for this download costs no extra round trip.
        string observedVersionTag = headerRes.Value.Validator.Tag;

        long connectorLength = ReadInt64LittleEndian(headerRes.Value.Data);
        if (connectorLength <= 0)
            return BeeResult<BeeDownloadResult>.Fail($"DownloadBEEEx: Invalid connector length {connectorLength}. Remote file may be corrupt or not a BEE.");

        if (connectorLength > BasisBeeConstants.MaxConnectorBytes)
            return BeeResult<BeeDownloadResult>.Fail($"DownloadBEEEx: Connector length {connectorLength} exceeds max allowed {BasisBeeConstants.MaxConnectorBytes}.");

        // 2) Download connector bytes (immediately after header)
        long connectorStart = BasisBeeConstants.RemoteHeaderSize;
        long connectorEndInclusive = BasisBeeConstants.RemoteHeaderSize + connectorLength - 1;

        var connectorRes = await DownloadRangeInternal(url, connectorStart, connectorEndInclusive, toFilePath: null, progressCallback, cancellationToken, MaxDownloadSizeInMB);

        if (!connectorRes.IsSuccess || connectorRes.Value.Data == null)
            return BeeResult<BeeDownloadResult>.Fail($"DownloadBEEEx: Failed to download connector block. {connectorRes.Error ?? "No data"}", connectorRes.ResponseCode);

        if (connectorRes.Value.Data.LongLength != connectorLength)
            return BeeResult<BeeDownloadResult>.Fail($"DownloadBEEEx: Expected {connectorLength} connector bytes, got {connectorRes.Value.Data.LongLength}.", connectorRes.ResponseCode);

        var connectorBytes = connectorRes.Value.Data;
        BasisDebug.Log("Downloaded Connector block size: " + connectorBytes.Length);

        // 3) Parse connector
        BasisBundleConnector connector = await BasisEncryptionToData.GenerateMetaFromBytes(vp, connectorBytes, progressCallback);

        if (connector == null)
            return BeeResult<BeeDownloadResult>.Fail("DownloadBEEEx: Failed to parse connector metadata (null).");

        if (connector.BasisBundleGenerated == null || connector.BasisBundleGenerated.Length == 0)
            return BeeResult<BeeDownloadResult>.Fail("DownloadBEEEx: Connector contains no sections.");

        // 4) Walk sections, compute ranges, download only the platform-matching section
        long previousEnd = connectorEndInclusive; // End of connector region in the remote file
        byte[] platformSectionData = null;
        bool downloadedGeneric = false;
        // Generic (glTF) fallback candidate, remembered while walking so it can be range-
        // downloaded only when the walk finds no section for this platform.
        long genericStart = -1;
        long genericLength = 0;
        int genericIndex = -1;

        for (int index = 0; index < connector.BasisBundleGenerated.Length; index++)
        {
            var entry = connector.BasisBundleGenerated[index];
            if (entry == null)
            {
                BasisDebug.LogError($"DownloadBEEEx: Null section entry at index {index}.");
                return BeeResult<BeeDownloadResult>.Fail($"DownloadBEEEx: Null section entry at index {index}.");
            }

            long start = previousEnd + 1;

            long sectionLength = entry.EndByte;
            if (sectionLength <= 0)
            {
                BasisDebug.LogError($"DownloadBEEEx: Invalid section length at index {index}: {sectionLength}.");
                return BeeResult<BeeDownloadResult>.Fail($"DownloadBEEEx: Invalid section length at index {index}: {sectionLength}.");
            }

            if (sectionLength > BasisBeeConstants.MaxSectionBytes)
                return BeeResult<BeeDownloadResult>.Fail($"DownloadBEEEx: Section length {sectionLength} at index {index} exceeds max allowed {BasisBeeConstants.MaxSectionBytes}.");

            long end = start + sectionLength - 1;

            bool isPlatform = false;
            try
            {
                isPlatform = BasisBundleConnector.IsPlatform(entry);
            }
            catch (Exception ex)
            {
                return BeeResult<BeeDownloadResult>.Fail($"DownloadBEEEx: Exception while checking platform for section {index}: {ex.Message}");
            }

            if (isPlatform)
            {
                BasisDebug.Log($"Downloading platform section range {start}-{end}");
                var sectRes = await DownloadRangeInternal(url, start, end, toFilePath: null, progressCallback, cancellationToken, MaxDownloadSizeInMB);

                if (!sectRes.IsSuccess || sectRes.Value?.Data == null)
                    return BeeResult<BeeDownloadResult>.Fail($"DownloadBEEEx: Failed to download platform section at index {index}. {sectRes.Error ?? "No data"}", sectRes.ResponseCode);

                if (sectRes.Value.Data.LongLength != sectionLength)
                    return BeeResult<BeeDownloadResult>.Fail($"DownloadBEEEx: Expected section length {sectionLength}, got {sectRes.Value.Data.LongLength}.", sectRes.ResponseCode);

                platformSectionData = sectRes.Value.Data;
                BasisDebug.Log("Platform section length: " + platformSectionData.LongLength);
                // Do not break; keep walking to ensure previousEnd is advanced correctly regardless of multiple matches
            }
            else if (genericStart < 0 && BasisBundleConnector.IsGenericBundle(entry))
            {
                genericStart = start;
                genericLength = sectionLength;
                genericIndex = index;
            }

            previousEnd = end;
        }

        if ((platformSectionData == null || platformSectionData.Length == 0) && genericStart >= 0)
        {
            BasisDebug.Log($"No section for {Application.platform}; falling back to Generic (glTF) section range {genericStart}-{genericStart + genericLength - 1}");
            var genericRes = await DownloadRangeInternal(url, genericStart, genericStart + genericLength - 1, toFilePath: null, progressCallback, cancellationToken, MaxDownloadSizeInMB);

            if (!genericRes.IsSuccess || genericRes.Value?.Data == null)
                return BeeResult<BeeDownloadResult>.Fail($"DownloadBEEEx: Failed to download generic section at index {genericIndex}. {genericRes.Error ?? "No data"}", genericRes.ResponseCode);

            if (genericRes.Value.Data.LongLength != genericLength)
                return BeeResult<BeeDownloadResult>.Fail($"DownloadBEEEx: Expected generic section length {genericLength}, got {genericRes.Value.Data.LongLength}.", genericRes.ResponseCode);

            platformSectionData = genericRes.Value.Data;
            downloadedGeneric = true;
        }

        if (platformSectionData == null || platformSectionData.Length == 0)
        {
            return BeeResult<BeeDownloadResult>.Fail($"DownloadBEEEx: No platform-matching section found in connector. Platform Request was {Application.platform}. {BasisBundleConnector.DebugOfPlatforms(connector)}");
        }

        // 5) Write local .bee (Int32 header + connector + section)
        // Generic downloads are cached under the Generic platform name so the cache meta,
        // the .bee filename, and the section they describe stay in agreement.
        string cachePlatform = downloadedGeneric ? BasisBundleConnector.GenericPlatform : null;
        string fileName = Path.GetFileName(GetBeeCacheFilePath(connector.UniqueVersion, cachePlatform));
        if (string.IsNullOrWhiteSpace(fileName))
            return BeeResult<BeeDownloadResult>.Fail("DownloadBEEEx: Connector has no UniqueVersion / file extension.");

        string localPath;
        try
        {
            localPath = GetBeeCacheFilePath(connector.UniqueVersion, cachePlatform);
        }
        catch (Exception ex)
        {
            return BeeResult<BeeDownloadResult>.Fail($"DownloadBEEEx: Failed to generate local file path: {ex.Message}");
        }

        var writeRes = await WriteBeeFileAsync(localPath, connectorBytes, platformSectionData, false);
        if (!writeRes.IsSuccess)
            return BeeResult<BeeDownloadResult>.Fail($"DownloadBEEEx: {writeRes.Error}");

        return BeeResult<BeeDownloadResult>.Ok(new BeeDownloadResult(connector, localPath, platformSectionData, observedVersionTag));
    }
    /// <summary>
    /// Downloads only the connector bytes from the remote BEE (8-byte Int64 header) and parses them.
    /// </summary>
    public static async Task<BeeResult<(BasisBundleConnector, string, string)>> DownloadConnectorOnlyEx(string url, string vp, BasisProgressReport progressCallback, CancellationToken cancellationToken = default, long MaxDownloadSizeInMB = 4L * 1024 * 1024 * 1024)
    {
        if (!ValidateUrl(url, out url, out var urlErr))
            return BeeResult<(BasisBundleConnector, string, string)>.Fail($"DownloadConnectorOnlyEx: {urlErr}");

        if (string.IsNullOrWhiteSpace(vp))
            return BeeResult<(BasisBundleConnector, string, string)>.Fail("DownloadConnectorOnlyEx: VP is null or empty.");

        // Header
        var headerRes = await DownloadRangeInternal(url, 0, BasisBeeConstants.RemoteHeaderSize - 1, null, progressCallback, cancellationToken, MaxDownloadSizeInMB);
        if (!headerRes.IsSuccess || headerRes.Value?.Data == null)
            return BeeResult<(BasisBundleConnector, string, string)>.Fail($"DownloadConnectorOnlyEx: Failed to read header. {headerRes.Error ?? "No data"}", headerRes.ResponseCode);

        if (headerRes.Value.Data.Length != BasisBeeConstants.RemoteHeaderSize)
            return BeeResult<(BasisBundleConnector, string, string)>.Fail($"DownloadConnectorOnlyEx: Header size mismatch. Expected {BasisBeeConstants.RemoteHeaderSize}, got {headerRes.Value.Data.Length}.", headerRes.ResponseCode);

        string observedVersionTag = headerRes.Value.Validator.Tag;

        long connectorLength = ReadInt64LittleEndian(headerRes.Value.Data);
        if (connectorLength <= 0)
            return BeeResult<(BasisBundleConnector, string, string)>.Fail($"DownloadConnectorOnlyEx: Invalid connector length {connectorLength}.");

        if (connectorLength > BasisBeeConstants.MaxConnectorBytes)
            return BeeResult<(BasisBundleConnector, string, string)>.Fail($"DownloadConnectorOnlyEx: Connector length {connectorLength} exceeds max allowed {BasisBeeConstants.MaxConnectorBytes}.");

        // Connector bytes
        long start = BasisBeeConstants.RemoteHeaderSize;
        long end = BasisBeeConstants.RemoteHeaderSize + connectorLength - 1;

        var connectorRes = await DownloadRangeInternal(url, start, end, null, progressCallback, cancellationToken, MaxDownloadSizeInMB);

        if (connectorRes.IsSuccess == false && connectorRes.Error != string.Empty)
        {
            return BeeResult<(BasisBundleConnector, string, string)>.Fail(connectorRes.Error, connectorRes.ResponseCode);
        }

        if (!connectorRes.IsSuccess || connectorRes.Value?.Data == null)
            return BeeResult<(BasisBundleConnector, string, string)>.Fail($"DownloadConnectorOnlyEx: Failed to read connector bytes. {connectorRes.Error ?? "No data"}", connectorRes.ResponseCode);

        if (connectorRes.Value.Data.LongLength != connectorLength)
            return BeeResult<(BasisBundleConnector, string, string)>.Fail($"DownloadConnectorOnlyEx: Expected {connectorLength} bytes, got {connectorRes.Value.Data.LongLength}.", connectorRes.ResponseCode);

        var connector = await BasisEncryptionToData.GenerateMetaFromBytes(vp, connectorRes.Value.Data, progressCallback);
        if (connector == null)
        {
            return BeeResult<(BasisBundleConnector, string, string)>.Fail("DownloadConnectorOnlyEx: Failed to parse connector metadata (null).");
        }

        // 5) Write local .bec (Int32 header + connector only, no section)
        string fileName = Path.GetFileName(GetConnectorCacheFilePath(connector.UniqueVersion));
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return BeeResult<(BasisBundleConnector, string, string)>.Fail("DownloadConnectorOnlyEx: Connector has no UniqueVersion / file extension.");

        }
        string localPath = GetConnectorCacheFilePath(connector.UniqueVersion);
        var connectorBytes = connectorRes.Value.Data;

        var writeRes = await WriteBeeFileAsync(localPath, connectorBytes, null, true);

        if (!writeRes.IsSuccess)
        {
            return BeeResult<(BasisBundleConnector, string, string)>.Fail($"DownloadBEEEx: {writeRes.Error}");
        }
        (BasisBundleConnector, string, string) Data = new(connector, localPath, observedVersionTag);

        return BeeResult<(BasisBundleConnector, string, string)>.Ok(Data);
    }

    /// <summary>
    /// Reads a local .bee file (4-byte Int32 header), regenerates the connector, and returns the remaining section data.
    /// </summary>
    public static async Task<BeeResult<BeeReadResult>> ReadBEEFileEx(string filePath, string vp, BasisProgressReport progressCallback, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return BeeResult<BeeReadResult>.Fail("ReadBEEFileEx: File path is null or empty.");
        }

        if (!File.Exists(filePath))
        {
            return BeeResult<BeeReadResult>.Fail($"ReadBEEFileEx: File not found: {filePath}");
        }

        if (string.IsNullOrWhiteSpace(vp))
        {
            return BeeResult<BeeReadResult>.Fail("ReadBEEFileEx: VP is null or empty.");
        }

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 96 * 1024, useAsync: true);

        if (fs.Length < BasisBeeConstants.DiskHeaderSize)
        {
            return BeeResult<BeeReadResult>.Fail($"ReadBEEFileEx: File too small to contain header. Size={fs.Length} bytes.");
        }

        // Read Int32 connector size (little-endian)
        byte[] sizeBytes = await ReadExactAsync(fs, BasisBeeConstants.DiskHeaderSize, cancellationToken).ConfigureAwait(false);
        if (sizeBytes.Length != BasisBeeConstants.DiskHeaderSize)
        {
            return BeeResult<BeeReadResult>.Fail($"ReadBEEFileEx: Failed to read connector size (header). Got {sizeBytes.Length} bytes.");
        }

        int connectorSize = ReadInt32LittleEndian(sizeBytes);
        long remainingPossible = fs.Length - fs.Position;
        if (connectorSize <= 0 || connectorSize > remainingPossible)
        {
            return BeeResult<BeeReadResult>.Fail($"ReadBEEFileEx: Invalid connector size {connectorSize}. Remaining file bytes: {remainingPossible}. File may be corrupt.");
        }

        // Read connector bytes
        byte[] connectorBytes = await ReadExactAsync(fs, connectorSize, cancellationToken).ConfigureAwait(false);
        if (connectorBytes.Length != connectorSize)
        {
            return BeeResult<BeeReadResult>.Fail($"ReadBEEFileEx: Failed to read full connector block. Expected {connectorSize}, got {connectorBytes.Length}.");
        }

        BasisBundleConnector connector = await BasisEncryptionToData.GenerateMetaFromBytes(vp, connectorBytes, progressCallback).ConfigureAwait(false);

        if (connector == null)
            return BeeResult<BeeReadResult>.Fail("ReadBEEFileEx: Failed to regenerate connector metadata (null).");

        // Remaining is section data
        long remaining = fs.Length - fs.Position;
        if (remaining < 0) remaining = 0;

        byte[] sectionData;
        if (remaining == 0)
        {
            sectionData = Array.Empty<byte>();
        }
        else
        {
            sectionData = await ReadExactAsync(fs, checked((int)remaining), cancellationToken).ConfigureAwait(false);
            if (sectionData == null || sectionData.LongLength != remaining)
            {
                return BeeResult<BeeReadResult>.Fail($"ReadBEEFileEx: Failed to read full section data. Expected {remaining}, got {sectionData?.LongLength ?? 0}.");
            }
        }

        return BeeResult<BeeReadResult>.Ok(new BeeReadResult(connector, sectionData));
    }
    /// <summary>
    /// Reads a local .bee file (4-byte Int32 header), regenerates the connector, and returns the remaining section data.
    /// </summary>
    public static async Task<BeeResult<BeeReadResult>> ReadBEEConnectorFileEx(string filePath, string vp, BasisProgressReport progressCallback, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return BeeResult<BeeReadResult>.Fail("ReadBEEFileEx: File path is null or empty.");

        if (!File.Exists(filePath))
            return BeeResult<BeeReadResult>.Fail($"ReadBEEFileEx: File not found: {filePath}");

        if (string.IsNullOrWhiteSpace(vp))
            return BeeResult<BeeReadResult>.Fail("ReadBEEFileEx: VP is null or empty.");

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 96 * 1024, useAsync: true);

        if (fs.Length < BasisBeeConstants.DiskHeaderSize)
            return BeeResult<BeeReadResult>.Fail($"ReadBEEFileEx: File too small to contain header. Size={fs.Length} bytes.");

        // Read Int32 connector size (little-endian)
        byte[] sizeBytes = await ReadExactAsync(fs, BasisBeeConstants.DiskHeaderSize, cancellationToken).ConfigureAwait(false);
        if (sizeBytes.Length != BasisBeeConstants.DiskHeaderSize)
            return BeeResult<BeeReadResult>.Fail($"ReadBEEFileEx: Failed to read connector size (header). Got {sizeBytes.Length} bytes.");

        int connectorSize = ReadInt32LittleEndian(sizeBytes);
        long remainingPossible = fs.Length - fs.Position;
        if (connectorSize <= 0 || connectorSize > remainingPossible)
            return BeeResult<BeeReadResult>.Fail($"ReadBEEFileEx: Invalid connector size {connectorSize}. Remaining file bytes: {remainingPossible}. File may be corrupt.");

        // Read connector bytes
        byte[] connectorBytes = await ReadExactAsync(fs, connectorSize, cancellationToken).ConfigureAwait(false);
        if (connectorBytes.Length != connectorSize)
            return BeeResult<BeeReadResult>.Fail($"ReadBEEFileEx: Failed to read full connector block. Expected {connectorSize}, got {connectorBytes.Length}.");

        BasisBundleConnector connector = await BasisEncryptionToData.GenerateMetaFromBytes(vp, connectorBytes, progressCallback).ConfigureAwait(false);

        if (connector == null)
            return BeeResult<BeeReadResult>.Fail("ReadBEEFileEx: Failed to regenerate connector metadata (null).");

        return BeeResult<BeeReadResult>.Ok(new BeeReadResult(connector, null));
    }

    /// <summary>
    /// Reads a REMOTE-format BEE blob (8-byte Int64 header) from a local file, parses the connector,
    /// and (when <paramref name="includeSection"/> is true) returns the platform-matching section.
    /// This is the on-disk equivalent of <see cref="DownloadBEEEx"/> for a BEE the SDK exported
    /// straight to disk rather than to a HTTP host.
    /// </summary>
    public static async Task<BeeResult<BeeReadResult>> ReadRemoteBeeFromDiskEx(string filePath, string vp, BasisProgressReport progressCallback, CancellationToken cancellationToken = default, bool includeSection = true)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return BeeResult<BeeReadResult>.Fail("ReadRemoteBeeFromDiskEx: File path is null or empty.");

        if (!File.Exists(filePath))
            return BeeResult<BeeReadResult>.Fail($"ReadRemoteBeeFromDiskEx: File not found: {filePath}");

        if (string.IsNullOrWhiteSpace(vp))
            return BeeResult<BeeReadResult>.Fail("ReadRemoteBeeFromDiskEx: VP is null or empty.");

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 96 * 1024, useAsync: true);

        if (fs.Length < BasisBeeConstants.RemoteHeaderSize)
            return BeeResult<BeeReadResult>.Fail($"ReadRemoteBeeFromDiskEx: File too small to contain remote header. Size={fs.Length} bytes.");

        byte[] headerBytes = await ReadExactAsync(fs, BasisBeeConstants.RemoteHeaderSize, cancellationToken).ConfigureAwait(false);
        if (headerBytes.Length != BasisBeeConstants.RemoteHeaderSize)
            return BeeResult<BeeReadResult>.Fail($"ReadRemoteBeeFromDiskEx: Failed to read remote header. Got {headerBytes.Length} bytes.");

        long connectorLength = ReadInt64LittleEndian(headerBytes);
        if (connectorLength <= 0)
            return BeeResult<BeeReadResult>.Fail($"ReadRemoteBeeFromDiskEx: Invalid connector length {connectorLength}. File may not be a remote-format BEE.");

        if (connectorLength > BasisBeeConstants.MaxConnectorBytes)
            return BeeResult<BeeReadResult>.Fail($"ReadRemoteBeeFromDiskEx: Connector length {connectorLength} exceeds max allowed {BasisBeeConstants.MaxConnectorBytes}.");

        long remainingAfterHeader = fs.Length - fs.Position;
        if (connectorLength > remainingAfterHeader)
            return BeeResult<BeeReadResult>.Fail($"ReadRemoteBeeFromDiskEx: Connector length {connectorLength} exceeds file remainder {remainingAfterHeader}.");

        byte[] connectorBytes = await ReadExactAsync(fs, checked((int)connectorLength), cancellationToken).ConfigureAwait(false);
        if (connectorBytes.LongLength != connectorLength)
            return BeeResult<BeeReadResult>.Fail($"ReadRemoteBeeFromDiskEx: Failed to read full connector block. Expected {connectorLength}, got {connectorBytes.Length}.");

        BasisBundleConnector connector = await BasisEncryptionToData.GenerateMetaFromBytes(vp, connectorBytes, progressCallback).ConfigureAwait(false);
        if (connector == null)
            return BeeResult<BeeReadResult>.Fail("ReadRemoteBeeFromDiskEx: Failed to parse connector metadata (null).");

        if (!includeSection)
            return BeeResult<BeeReadResult>.Ok(new BeeReadResult(connector, null));

        if (connector.BasisBundleGenerated == null || connector.BasisBundleGenerated.Length == 0)
            return BeeResult<BeeReadResult>.Fail("ReadRemoteBeeFromDiskEx: Connector contains no sections.");

        long cursor = fs.Position;
        long matchOffset = -1;
        long matchLength = 0;
        long genericOffset = -1;
        long genericLength = 0;

        for (int index = 0; index < connector.BasisBundleGenerated.Length; index++)
        {
            BasisBundleGenerated entry = connector.BasisBundleGenerated[index];
            if (entry == null)
                return BeeResult<BeeReadResult>.Fail($"ReadRemoteBeeFromDiskEx: Null section entry at index {index}.");

            long sectionLength = entry.EndByte;
            if (sectionLength <= 0)
                return BeeResult<BeeReadResult>.Fail($"ReadRemoteBeeFromDiskEx: Invalid section length at index {index}: {sectionLength}.");

            if (sectionLength > BasisBeeConstants.MaxSectionBytes)
                return BeeResult<BeeReadResult>.Fail($"ReadRemoteBeeFromDiskEx: Section length {sectionLength} at index {index} exceeds max allowed {BasisBeeConstants.MaxSectionBytes}.");

            long sectionEndExclusive = cursor + sectionLength;
            if (sectionEndExclusive > fs.Length)
                return BeeResult<BeeReadResult>.Fail($"ReadRemoteBeeFromDiskEx: Section at index {index} runs past end of file.");

            bool isPlatform;
            try
            {
                isPlatform = BasisBundleConnector.IsPlatform(entry);
            }
            catch (Exception ex)
            {
                return BeeResult<BeeReadResult>.Fail($"ReadRemoteBeeFromDiskEx: Exception while checking platform for section {index}: {ex.Message}");
            }

            if (isPlatform)
            {
                matchOffset = cursor;
                matchLength = sectionLength;
            }
            else if (genericOffset < 0 && BasisBundleConnector.IsGenericBundle(entry))
            {
                genericOffset = cursor;
                genericLength = sectionLength;
            }

            cursor = sectionEndExclusive;
        }

        // Exact platform sections win; the Generic (glTF) section only fills in when this
        // platform has no AssetBundle in the bee.
        if (matchOffset < 0 && genericOffset >= 0)
        {
            BasisDebug.Log($"No section for {Application.platform} in local bee; using Generic (glTF) section.");
            matchOffset = genericOffset;
            matchLength = genericLength;
        }

        if (matchOffset < 0)
            return BeeResult<BeeReadResult>.Fail($"ReadRemoteBeeFromDiskEx: No platform-matching section found. Platform Request was {Application.platform}. {BasisBundleConnector.DebugOfPlatforms(connector)}");

        fs.Seek(matchOffset, SeekOrigin.Begin);
        byte[] platformSectionData = await ReadExactAsync(fs, checked((int)matchLength), cancellationToken).ConfigureAwait(false);
        if (platformSectionData.LongLength != matchLength)
            return BeeResult<BeeReadResult>.Fail($"ReadRemoteBeeFromDiskEx: Expected section length {matchLength}, got {platformSectionData.Length}.");

        return BeeResult<BeeReadResult>.Ok(new BeeReadResult(connector, platformSectionData));
    }

    public static string GenerateFilePath(string fileName, string subFolder)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("GenerateFilePath: fileName is null or empty.", nameof(fileName));

        string folderPath = GenerateFolderPath(subFolder);
        string localPath = Path.Combine(folderPath, fileName);

        string fullFolder = Path.GetFullPath(folderPath);
        if (fullFolder.Length == 0 || fullFolder[fullFolder.Length - 1] != Path.DirectorySeparatorChar)
            fullFolder += Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(localPath).StartsWith(fullFolder, StringComparison.Ordinal))
            throw new ArgumentException($"GenerateFilePath: resolved path escapes cache folder: {fileName}", nameof(fileName));

        BasisDebug.Log($"Generated folder path: {localPath}");
        return localPath;
    }

    public static string GenerateFolderPath(string subFolder)
    {
        if (string.IsNullOrWhiteSpace(subFolder))
            throw new ArgumentException("GenerateFolderPath: subFolder is null or empty.", nameof(subFolder));

        string basePath = PersistentDataPath;
        if (string.IsNullOrWhiteSpace(basePath))
            throw new InvalidOperationException("GenerateFolderPath: PersistentDataPath was not initialized on the main thread.");

        string folderPath = Path.Combine(basePath, subFolder);
        if (!Directory.Exists(folderPath))
        {
            BasisDebug.Log($"Directory {folderPath} does not exist. Creating directory.");
            Directory.CreateDirectory(folderPath);
        }
        return folderPath;
    }
    /// <summary>
    /// downloads a range of bytes
    /// </summary>
    /// <param name="url"></param>
    /// <param name="startByte"></param>
    /// <param name="endByteInclusive"></param>
    /// <param name="toFilePath"></param>
    /// <param name="progress"></param>
    /// <param name="ct"></param>
    /// <param name="MaxDownloadSizeInMB">Defaults to 4GB</param>
    /// <returns></returns>
    private static async Task<BeeResult<DownloadPayload>> DownloadRangeInternal(string url, long startByte, long? endByteInclusive, string toFilePath, BasisProgressReport progress, CancellationToken ct, long MaxDownloadSizeInMB = 4L * 1024 * 1024 * 1024, int redirectsRemaining = MaxValidatedRedirects)
    {
        if (!ValidateUrl(url, out url, out var urlErr))
            return BeeResult<DownloadPayload>.Fail(urlErr);

        string dnsErr = await ValidateUrlHostResolvesGlobalAsync(url);
        if (dnsErr != null)
            return BeeResult<DownloadPayload>.Fail($"Blocked URL: {dnsErr}");

        if (startByte < 0)
            return BeeResult<DownloadPayload>.Fail($"Invalid start byte: {startByte}");

        if (endByteInclusive.HasValue && endByteInclusive.Value < startByte)
            return BeeResult<DownloadPayload>.Fail($"Invalid byte range: {startByte}-{endByteInclusive.Value}");


        long expectedBytes = endByteInclusive.HasValue? (endByteInclusive.Value - startByte + 1): long.MaxValue; // open-ended range

        if (!endByteInclusive.HasValue)
            return BeeResult<DownloadPayload>.Fail("Open-ended ranges are not allowed when a max size is enforced.");

        if (expectedBytes <= 0)
            return BeeResult<DownloadPayload>.Fail($"Invalid expected byte count: {expectedBytes}");


        if (expectedBytes > MaxDownloadSizeInMB)
            return BeeResult<DownloadPayload>.Fail($"Refusing download: requested {expectedBytes} bytes exceeds limit {MaxDownloadSizeInMB}.");

        string requestId = BasisGenerateUniqueID.GenerateUniqueID();

        using var req = UnityWebRequest.Get(url);
        req.redirectLimit = 0;

        string rangeHeader = endByteInclusive.HasValue ? $"bytes={startByte}-{endByteInclusive.Value}" : $"bytes={startByte}-";

        req.SetRequestHeader("Range", rangeHeader);

        // The handler appends, so a redirect body would be left in the file before the retry.
        long fileLengthBeforeRequest = -1;

        if (string.IsNullOrEmpty(toFilePath) == false)
        {
            // Ensure parent directory exists if the caller passed a path
            string dir = Path.GetDirectoryName(toFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            try { fileLengthBeforeRequest = File.Exists(toFilePath) ? new FileInfo(toFilePath).Length : 0; }
            catch { fileLengthBeforeRequest = -1; }

            req.downloadHandler = new DownloadHandlerFile(toFilePath, true) { removeFileOnAbort = true };
        }
        else
        {
            req.downloadHandler = new DownloadHandlerBuffer();
        }

        var op = req.SendWebRequest();

        float lastProgress = 0f;
        const float threshold = 0.5f; // percentage points

        while (!op.isDone)
        {
            if (ct.IsCancellationRequested)
            {
                BasisDebug.Log("Download cancelled.");
                req.Abort();
                return BeeResult<DownloadPayload>.Fail("Cancelled");
            }

            float p = req.downloadProgress * 100f;
            if (progress != null && MathF.Abs(p - lastProgress) >= threshold)
            {
                progress.ReportProgress(requestId, p, "Downloading data...");
                lastProgress = p;
            }

            await Task.Yield();
        }

        long code = req.responseCode;

        (bool redirected, string nextUrl, string redirectError) = await TryFollowRedirectAsync(req, url);
        if (redirectError != null)
        {
            progress?.ReportProgress(requestId, 100, "Downloading Complete");
            return BeeResult<DownloadPayload>.Fail(redirectError, code);
        }
        if (redirected)
        {
            progress?.ReportProgress(requestId, 100, "Downloading Complete");
            if (redirectsRemaining <= 0)
                return BeeResult<DownloadPayload>.Fail($"Too many redirects (limit {MaxValidatedRedirects}).", code);

            // Drop anything the redirect appended so the retry writes at the right offset.
            req.Dispose();
            if (fileLengthBeforeRequest >= 0)
            {
                try
                {
                    if (File.Exists(toFilePath) && new FileInfo(toFilePath).Length != fileLengthBeforeRequest)
                        using (FileStream fs = new FileStream(toFilePath, FileMode.Open, FileAccess.Write))
                            fs.SetLength(fileLengthBeforeRequest);
                }
                catch (Exception ex)
                {
                    return BeeResult<DownloadPayload>.Fail($"Could not reset '{toFilePath}' before following redirect: {ex.Message}", code);
                }
            }
            return await DownloadRangeInternal(nextUrl, startByte, endByteInclusive, toFilePath, progress, ct, MaxDownloadSizeInMB, redirectsRemaining - 1);
        }

        // Normalize network errors first
        if (req.result != UnityWebRequest.Result.Success)
        {
            progress?.ReportProgress(requestId, 100, "Downloading Complete");
            var errDetail = BuildNetworkErrorDetail(req);
            return BeeResult<DownloadPayload>.Fail($"Network error: {req.error}. {errDetail}", code);
        }

        // Enforce partial content semantics and provide actionable reasons
        switch (code)
        {
            case 206:
                // Validate Content-Range if present to ensure the server honored our request
                string contentRange = req.GetResponseHeader("Content-Range") ?? string.Empty;
                if (!string.IsNullOrEmpty(contentRange))
                {
                    // Basic sanity check; we avoid parsing fully to keep dependencies light
                    if (!contentRange.StartsWith("bytes ", StringComparison.OrdinalIgnoreCase))
                    {
                        progress?.ReportProgress(requestId, 100, $"Error! {code}");
                        return BeeResult<DownloadPayload>.Fail($"Unexpected Content-Range header: {contentRange}", code);
                    }
                }
                break;

            case 200:
                progress?.ReportProgress(requestId, 100, $"Error! {code}");
                return BeeResult<DownloadPayload>.Fail("Server returned 200 (full file). Host must support HTTP range requests (206).", code);

            case 416:
                progress?.ReportProgress(requestId, 100, $"Error! {code}");
                return BeeResult<DownloadPayload>.Fail($"Requested Range {startByte}-{(endByteInclusive?.ToString() ?? "end")} not satisfiable. The requested range may exceed the file size.", code);

            case 304:
                // This path never sends conditional headers itself, so a 304 here means an
                // intermediary revalidated on our behalf and returned no body. Previously this fell
                // through to "Unexpected response code", which hid the real cause behind a generic
                // failure; the caller needs to know the bytes are simply absent, not corrupt.
                progress?.ReportProgress(requestId, 100, $"Not Modified {code}");
                return BeeResult<DownloadPayload>.Fail("Server returned 304 (not modified) for a byte-range request, so no content was returned. A caching proxy may be revalidating on our behalf.", code);

            default:
                progress?.ReportProgress(requestId, 100, $"Error! {code}");
                var details = BuildNetworkErrorDetail(req);
                return BeeResult<DownloadPayload>.Fail($"Unexpected response code: {code}. {details}", code);
        }

        var payload = new DownloadPayload { Validator = ReadValidator(req) };
        if (toFilePath == null)
        {
            var data = req.downloadHandler.data;
            if (data == null)
                return BeeResult<DownloadPayload>.Fail("No payload returned (buffer was null).", code);

            // Optional: verify Content-Length when present
            var contentLengthHeader = req.GetResponseHeader("Content-Length");
            if (long.TryParse(contentLengthHeader, out var contentLen) && contentLen >= 0 && data.LongLength != contentLen)
            {
                return BeeResult<DownloadPayload>.Fail($"Content-Length mismatch. Header={contentLen}, Received={data.LongLength}.", code);
            }

            payload.Data = data;
        }
        else
        {
            if (!File.Exists(toFilePath))
                return BeeResult<DownloadPayload>.Fail($"Download handler reported success but file was not created: {toFilePath}", code);

            payload.Path = toFilePath;
        }

        return BeeResult<DownloadPayload>.Ok(payload);
    }

    /// <summary>
    /// Writes local .bee with 4-byte little-endian Int32 header (connector size) + connector [+ optional section].
    /// If <paramref name="IgnoreSectionBytes"/> is true, the section is not written even if provided.
    /// </summary>
    private static async Task<BeeResult<bool>> WriteBeeFileAsync(string path, byte[] connectorBytes, byte[] sectionBytes, bool IgnoreSectionBytes)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BeeResult<bool>.Fail("WriteBeeFileAsync: Output path is null or empty.");

        if (connectorBytes == null || connectorBytes.Length == 0)
            return BeeResult<bool>.Fail("WriteBeeFileAsync: Connector bytes are empty.");

        // If we are not ignoring the section, it must be non-null (zero-length is allowed)
        if (!IgnoreSectionBytes && sectionBytes == null)
            return BeeResult<bool>.Fail("WriteBeeFileAsync: Section bytes are null.");

        // Prepare directory
        string dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        // Header: little-endian Int32 of connector size
        byte[] sizeLE = GetBytesInt32LE(connectorBytes.Length);

        // Decide whether we'll actually write the section
        bool writeSection = !IgnoreSectionBytes && (sectionBytes?.Length ?? 0) > 0;

        // Compute total size we expect to write
        long totalSize = sizeLE.Length + connectorBytes.Length + (writeSection ? sectionBytes.Length : 0);

        // Auto-tune buffer: min 32KB, max 1MB
        int buffer = Clamp((int)(totalSize / 8), 32 * 1024, 1 * 1024 * 1024);

        // Write to a temp file then atomic-rename to avoid sharing violations
        // when multiple concurrent downloads target the same .BEE path.
        string tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, buffer, useAsync: true))
            {
                await fs.WriteAsync(sizeLE, 0, sizeLE.Length).ConfigureAwait(false);
                await fs.WriteAsync(connectorBytes, 0, connectorBytes.Length).ConfigureAwait(false);

                if (writeSection)
                {
                    await fs.WriteAsync(sectionBytes, 0, sectionBytes.Length).ConfigureAwait(false);
                }
            }

            long actual = new FileInfo(tempPath).Length;
            BasisDebug.Log($"Expected File Size: {totalSize} bytes");
            BasisDebug.Log($"Actual File Size on Disk: {actual} bytes");

            if (totalSize != actual)
            {
                BasisDebug.LogError("File size does not match expected size!");
                try { File.Delete(tempPath); } catch { }
                return BeeResult<bool>.Fail($"WriteBeeFileAsync: Size mismatch after write. Expected {totalSize}, actual {actual}.");
            }

            // Replace destination if it already exists. On Windows, plain File.Move throws
            // when the destination is present; deleting first lets re-downloads overwrite a
            // stale or corrupt cached file instead of silently keeping the old bytes.
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            File.Move(tempPath, path);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            return BeeResult<bool>.Fail($"WriteBeeFileAsync: {ex.GetType().Name}: {ex.Message}");
        }

        return BeeResult<bool>.Ok(true);
    }

    private static bool ValidateUrl(string url, out string normalizedUrl, out string error)
    {
        normalizedUrl = url;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(url))
        {
            error = "The provided URL is null or empty.";
            BasisDebug.LogError(error);
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            error = $"The provided URL is not a valid absolute URI: '{url}'.";
            BasisDebug.LogError(error);
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Unsupported URL scheme '{uri.Scheme}'. Only HTTP/HTTPS are supported.";
            BasisDebug.LogError(error);
            return false;
        }

        // These URLs arrive over the wire, so a scheme check alone lets a remote player aim
        // every other client at loopback, the LAN, or cloud metadata.
        if (Basis.Scripts.Common.BasisUrlSecurity.IsBlockedHost(uri.Host, out string hostReason))
        {
            error = $"Blocked URL host '{uri.Host}': {hostReason}";
            BasisDebug.LogError(error);
            return false;
        }

        normalizedUrl = uri.AbsoluteUri;
        return true;
    }

    /// <summary>
    /// DNS half of the SSRF gate; <see cref="ValidateUrl"/> only sees literal addresses.
    /// Returns null when the host is allowed, otherwise the reason it was refused.
    /// </summary>
    private static Task<string> ValidateUrlHostResolvesGlobalAsync(string url)
        => Basis.Scripts.Common.BasisUrlSecurity.ValidateResolvedHostAsync(url);

    /// <summary>Hop budget for hand-followed redirects.</summary>
    private const int MaxValidatedRedirects = 5;

    /// <summary>
    /// UnityWebRequest's built-in redirect follower re-issues the request without consulting the
    /// SSRF gate, so every request here sets <c>redirectLimit = 0</c> and follows hops through
    /// this helper instead. Returns true only for a redirect whose target passed validation.
    /// </summary>
    private static async Task<(bool redirected, string nextUrl, string error)> TryFollowRedirectAsync(UnityWebRequest req, string currentUrl)
    {
        long code = req.responseCode;
        if (code != 301 && code != 302 && code != 303 && code != 307 && code != 308)
            return (false, null, null);

        string location = req.GetResponseHeader("Location");
        if (string.IsNullOrWhiteSpace(location))
            return (false, null, $"Redirect {code} without a Location header.");

        // Relative targets are legal; resolve against the URL that produced them.
        if (!Uri.TryCreate(new Uri(currentUrl), location, out Uri resolved))
            return (false, null, $"Redirect {code} with an unparseable Location '{location}'.");

        string next = resolved.AbsoluteUri;
        if (!ValidateUrl(next, out next, out string urlErr))
            return (false, null, $"Blocked redirect target: {urlErr}");

        string dnsErr = await ValidateUrlHostResolvesGlobalAsync(next);
        if (dnsErr != null)
            return (false, null, $"Blocked redirect target: {dnsErr}");

        return (true, next, null);
    }

    /// <summary>
    /// Returns true when <paramref name="location"/> is a local BEE location (a <c>file://</c> URI)
    /// rather than a HTTP/HTTPS download, regardless of whether the file currently exists. Use this
    /// for the "this content is local and can never be networked" invariant; use
    /// <see cref="TryResolveLocalBeePath"/> when you actually need to read the file off disk.
    /// </summary>
    public static bool IsLocalBeeUrl(string location)
    {
        if (string.IsNullOrWhiteSpace(location))
            return false;

        if (Uri.TryCreate(location, UriKind.Absolute, out Uri uri))
        {
            if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                return false;
            return uri.IsFile;
        }

        return false;
    }

    /// <summary>
    /// Returns true when <paramref name="location"/> points at an existing local BEE file
    /// (a <c>file://</c> URI or a raw filesystem path) rather than a HTTP/HTTPS download,
    /// resolving it to an absolute local path. Used to route a dropped-in local BEE through
    /// the on-disk reader instead of the network download path.
    /// </summary>
    public static bool TryResolveLocalBeePath(string location, out string localPath)
    {
        localPath = null;
        if (string.IsNullOrWhiteSpace(location))
            return false;

        if (Uri.TryCreate(location, UriKind.Absolute, out Uri uri))
        {
            if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                return false;

            if (uri.IsFile)
            {
                try { localPath = uri.LocalPath; }
                catch { return false; }
                return !string.IsNullOrEmpty(localPath) && File.Exists(localPath);
            }

            return false;
        }

        try
        {
            if (File.Exists(location))
            {
                localPath = location;
                return true;
            }
        }
        catch { }

        return false;
    }

    private static async Task<byte[]> ReadExactAsync(Stream s, int size, CancellationToken ct)
    {
        if (s == null) throw new ArgumentNullException(nameof(s));
        if (size < 0) throw new ArgumentOutOfRangeException(nameof(size));

        byte[] buf = new byte[size];
        int read = 0;

        while (read < size)
        {
            int n = await s.ReadAsync(buf, read, size - read, ct);
            if (n <= 0) break;
            read += n;
        }

        if (read == size)
            return buf;

        // Return what we have (caller checks length)
        if (read == 0)
            return Array.Empty<byte>();

        if (read < size)
        {
            var partial = new byte[read];
            Buffer.BlockCopy(buf, 0, partial, 0, read);
            return partial;
        }

        return buf;
    }

    private static byte[] GetBytesInt32LE(int value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        if (!BitConverter.IsLittleEndian)
            Array.Reverse(bytes);
        return bytes;
    }

    private static int ReadInt32LittleEndian(byte[] bytes)
    {
        if (bytes == null) throw new ArgumentNullException(nameof(bytes));
        if (bytes.Length < 4) throw new ArgumentException("ReadInt32LittleEndian: buffer too small.", nameof(bytes));

        if (!BitConverter.IsLittleEndian)
        {
            var tmp = (byte[])bytes.Clone();
            Array.Reverse(tmp);
            return BitConverter.ToInt32(tmp, 0);
        }
        return BitConverter.ToInt32(bytes, 0);
    }

    private static long ReadInt64LittleEndian(byte[] bytes)
    {
        if (bytes == null) throw new ArgumentNullException(nameof(bytes));
        if (bytes.Length < 8) throw new ArgumentException("ReadInt64LittleEndian: buffer too small.", nameof(bytes));

        if (!BitConverter.IsLittleEndian)
        {
            var tmp = (byte[])bytes.Clone();
            Array.Reverse(tmp);
            return BitConverter.ToInt64(tmp, 0);
        }
        return BitConverter.ToInt64(bytes, 0);
    }

    private static int Clamp(int value, int min, int max)
    {
        if (min > max) (min, max) = (max, min);
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    /// <summary>
    /// Sends an HTTP HEAD request to check if the remote file is reachable.
    /// Returns success if the server responds with a 2xx status code,
    /// indicating the file exists and is accessible.
    /// </summary>
    public static async Task<BeeResult<bool>> CheckRemoteFileReachable(string url, CancellationToken cancellationToken = default, int redirectsRemaining = MaxValidatedRedirects)
    {
        if (!ValidateUrl(url, out url, out var urlErr))
            return BeeResult<bool>.Fail($"Invalid URL: {urlErr}");

        string dnsErr = await ValidateUrlHostResolvesGlobalAsync(url);
        if (dnsErr != null)
            return BeeResult<bool>.Fail($"Blocked URL: {dnsErr}");

        using var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbHEAD);
        req.redirectLimit = 0;
        req.downloadHandler = new DownloadHandlerBuffer();

        var op = req.SendWebRequest();

        while (!op.isDone)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                req.Abort();
                return BeeResult<bool>.Fail("Reachability check cancelled.");
            }
            await Task.Yield();
        }

        (bool redirected, string nextUrl, string redirectError) = await TryFollowRedirectAsync(req, url);
        if (redirectError != null)
            return BeeResult<bool>.Fail(redirectError, req.responseCode);
        if (redirected)
        {
            if (redirectsRemaining <= 0)
                return BeeResult<bool>.Fail($"Too many redirects (limit {MaxValidatedRedirects}).", req.responseCode);
            req.Dispose();
            return await CheckRemoteFileReachable(nextUrl, cancellationToken, redirectsRemaining - 1);
        }

        long code = req.responseCode;

        if (req.result == UnityWebRequest.Result.ConnectionError)
            return BeeResult<bool>.Fail($"Cannot connect to server: {req.error}", code);

        if (req.result == UnityWebRequest.Result.ProtocolError)
        {
            if (code == 404)
                return BeeResult<bool>.Fail("Avatar file not found on the server (404). The file may have been moved or deleted.", code);
            if (code == 403)
                return BeeResult<bool>.Fail("Access denied to avatar file (403). You may not have permission to access this file.", code);

            return BeeResult<bool>.Fail($"Server returned error {code}: {req.error}", code);
        }

        if (req.result != UnityWebRequest.Result.Success)
            return BeeResult<bool>.Fail($"Request failed: {req.error}", code);

        return BeeResult<bool>.Ok(true);
    }

    /// <summary>
    /// Asks the host whether the bee at <paramref name="url"/> is still the one we cached, without
    /// downloading it. Pass the cached tag as <paramref name="cachedVersionTag"/> to send a
    /// conditional request: a compliant host answers 304 and the result reports
    /// <see cref="BasisRemoteValidator.NotModified"/>, which is the cheapest possible answer.
    ///
    /// <para>Hosts that ignore conditional headers (Google Drive serves <c>Last-Modified</c> but
    /// does not honour <c>If-Modified-Since</c>) still return their validators on the normal
    /// response, so the caller can compare tags itself. Hosts that publish neither return a result
    /// with no value, which callers must treat as "cannot tell" rather than "unchanged".</para>
    /// </summary>
    public static async Task<BeeResult<BasisRemoteValidator>> FetchRemoteValidatorAsync(string url, string cachedVersionTag = null, CancellationToken cancellationToken = default)
    {
        if (!ValidateUrl(url, out url, out var urlErr))
        {
            return BeeResult<BasisRemoteValidator>.Fail($"FetchRemoteValidatorAsync: {urlErr}");
        }

        string dnsErr = await ValidateUrlHostResolvesGlobalAsync(url);
        if (dnsErr != null)
        {
            return BeeResult<BasisRemoteValidator>.Fail($"FetchRemoteValidatorAsync: blocked URL: {dnsErr}");
        }

        BasisContentVersion.ToConditionalHeaders(cachedVersionTag, out string ifNoneMatch, out string ifModifiedSince);

        // HEAD first: no body at all, and it is what a compliant host answers validators on.
        BeeResult<BasisRemoteValidator> head = await SendValidatorRequest(url, UnityWebRequest.kHttpVerbHEAD, ifNoneMatch, ifModifiedSince, null, cancellationToken);
        if (head.IsSuccess && (head.Value.NotModified || head.Value.HasValue))
        {
            return head;
        }

        // Some hosts reject HEAD or answer it without validators while the ranged GET the loader
        // already relies on carries them. One byte is enough to read the response headers.
        BeeResult<BasisRemoteValidator> ranged = await SendValidatorRequest(url, UnityWebRequest.kHttpVerbGET, ifNoneMatch, ifModifiedSince, "bytes=0-0", cancellationToken);
        if (ranged.IsSuccess)
        {
            return ranged;
        }

        return head.IsSuccess ? head : ranged;
    }

    private static async Task<BeeResult<BasisRemoteValidator>> SendValidatorRequest(string url, string verb, string ifNoneMatch, string ifModifiedSince, string rangeHeader, CancellationToken cancellationToken, int redirectsRemaining = MaxValidatedRedirects)
    {
        using var req = new UnityWebRequest(url, verb);
        req.redirectLimit = 0;
        req.downloadHandler = new DownloadHandlerBuffer();

        if (!string.IsNullOrEmpty(rangeHeader))
        {
            req.SetRequestHeader("Range", rangeHeader);
        }
        if (!string.IsNullOrWhiteSpace(ifNoneMatch))
        {
            req.SetRequestHeader("If-None-Match", ifNoneMatch);
        }
        if (!string.IsNullOrWhiteSpace(ifModifiedSince))
        {
            req.SetRequestHeader("If-Modified-Since", ifModifiedSince);
        }

        var op = req.SendWebRequest();
        while (!op.isDone)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                req.Abort();
                return BeeResult<BasisRemoteValidator>.Fail("Cancelled");
            }
            await Task.Yield();
        }

        long code = req.responseCode;

        (bool redirected, string nextUrl, string redirectError) = await TryFollowRedirectAsync(req, url);
        if (redirectError != null)
            return BeeResult<BasisRemoteValidator>.Fail(redirectError, code);
        if (redirected)
        {
            if (redirectsRemaining <= 0)
                return BeeResult<BasisRemoteValidator>.Fail($"Too many redirects (limit {MaxValidatedRedirects}).", code);
            req.Dispose();
            return await SendValidatorRequest(nextUrl, verb, ifNoneMatch, ifModifiedSince, rangeHeader, cancellationToken, redirectsRemaining - 1);
        }

        // Checked BEFORE req.result: UnityWebRequest classifies 304 as a ProtocolError, so testing
        // the result first would report the single best outcome ("your copy is current") as failure.
        if (code == 304)
        {
            return BeeResult<BasisRemoteValidator>.Ok(new BasisRemoteValidator(req.GetResponseHeader("ETag"), req.GetResponseHeader("Last-Modified"), notModified: true));
        }

        if (req.result != UnityWebRequest.Result.Success)
        {
            return BeeResult<BasisRemoteValidator>.Fail($"Network error: {req.error}. {BuildNetworkErrorDetail(req)}", code);
        }

        return BeeResult<BasisRemoteValidator>.Ok(ReadValidator(req));
    }

    /// <summary>
    /// Builds a concise, actionable detail string from a UnityWebRequest result without leaking nulls.
    /// </summary>
    private static string BuildNetworkErrorDetail(UnityWebRequest req)
    {
        if (req != null)
        {
            string acceptRanges = req.GetResponseHeader("Accept-Ranges") ?? "n/a";
            string contentRange = req.GetResponseHeader("Content-Range") ?? "n/a";
            string contentLen = req.GetResponseHeader("Content-Length") ?? "n/a";

            return $"Accept-Ranges={acceptRanges}, Content-Range={contentRange}, Content-Length={contentLen}";
        }
        return "No response header details available.";
    }
}
