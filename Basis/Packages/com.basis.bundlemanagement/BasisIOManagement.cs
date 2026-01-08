using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

public static class BasisIOManagement
{
    public sealed class BeeDownloadResult
    {
        public BasisBundleConnector Connector { get; }
        public string LocalPath { get; }
        public byte[] SectionData { get; }

        public BeeDownloadResult(BasisBundleConnector connector, string localPath, byte[] sectionData)
        {
            Connector = connector ?? throw new ArgumentNullException(nameof(connector));
            LocalPath = localPath ?? throw new ArgumentNullException(nameof(localPath));
            SectionData = sectionData ?? throw new ArgumentNullException(nameof(sectionData));
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
    }

    /// <summary>
    /// Downloads a remote BEE blob (with 8-byte Int64 header), decrypts/parses the connector,
    /// downloads the platform-matching section, writes a local .bee file (4-byte Int32 header),
    /// and returns all artifacts.
    /// </summary>
    public static async Task<BeeResult<BeeDownloadResult>> DownloadBEEEx(string url, string vp, BasisProgressReport progressCallback, CancellationToken cancellationToken = default, long MaxDownloadSizeInMB = 4L * 1024 * 1024 * 1024)
    {
        // Validate inputs with actionable messages
        if (!ValidateUrl(url, out var urlErr))
            return BeeResult<BeeDownloadResult>.Fail($"DownloadBEEEx: {urlErr}");

        if (string.IsNullOrWhiteSpace(vp))
            return BeeResult<BeeDownloadResult>.Fail("DownloadBEEEx: VP is null or empty.");

        // 1) Read 8-byte remote header (Int64)
        var headerRes = await DownloadRangeInternal(url, startByte: 0, endByteInclusive: BasisBeeConstants.RemoteHeaderSize - 1, toFilePath: null, progressCallback, cancellationToken, MaxDownloadSizeInMB);

        if (!headerRes.IsSuccess || headerRes.Value?.Data == null)
            return BeeResult<BeeDownloadResult>.Fail($"DownloadBEEEx: Failed to read remote header. {headerRes.Error ?? "No data"}", headerRes.ResponseCode);

        if (headerRes.Value.Data.Length != BasisBeeConstants.RemoteHeaderSize)
            return BeeResult<BeeDownloadResult>.Fail($"DownloadBEEEx: Remote header size mismatch. Expected {BasisBeeConstants.RemoteHeaderSize} bytes, got {headerRes.Value.Data.Length}.", headerRes.ResponseCode);

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
        BasisDebug.Log("GenerateMetaFromBytes", BasisDebug.LogTag.Event);

        if (connector == null)
            return BeeResult<BeeDownloadResult>.Fail("DownloadBEEEx: Failed to parse connector metadata (null).");

        if (connector.BasisBundleGenerated == null || connector.BasisBundleGenerated.Length == 0)
            return BeeResult<BeeDownloadResult>.Fail("DownloadBEEEx: Connector contains no sections.");

        // 4) Walk sections, compute ranges, download only the platform-matching section
        long previousEnd = connectorEndInclusive; // End of connector region in the remote file
        byte[] platformSectionData = null;

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

            previousEnd = end;
        }

        if (platformSectionData == null || platformSectionData.Length == 0)
            return BeeResult<BeeDownloadResult>.Fail("DownloadBEEEx: No platform-matching section found in connector.");

        // 5) Write local .bee (Int32 header + connector + section)
        string fileName = $"{connector.UniqueVersion}{BasisBeeConstants.BasisEncryptedExtension}";
        if (string.IsNullOrWhiteSpace(fileName))
            return BeeResult<BeeDownloadResult>.Fail("DownloadBEEEx: Connector has no UniqueVersion / file extension.");

        string localPath;
        try
        {
            localPath = GenerateFilePath(fileName, BasisBeeConstants.AssetBundlesFolder);
        }
        catch (Exception ex)
        {
            return BeeResult<BeeDownloadResult>.Fail($"DownloadBEEEx: Failed to generate local file path: {ex.Message}");
        }

        var writeRes = await WriteBeeFileAsync(localPath, connectorBytes, platformSectionData, false);
        if (!writeRes.IsSuccess)
            return BeeResult<BeeDownloadResult>.Fail($"DownloadBEEEx: {writeRes.Error}");

        return BeeResult<BeeDownloadResult>.Ok(new BeeDownloadResult(connector, localPath, platformSectionData));
    }
    /// <summary>
    /// Downloads only the connector bytes from the remote BEE (8-byte Int64 header) and parses them.
    /// </summary>
    public static async Task<BeeResult<(BasisBundleConnector, string)>> DownloadConnectorOnlyEx(string url, string vp, BasisProgressReport progressCallback, CancellationToken cancellationToken = default, long MaxDownloadSizeInMB = 4L * 1024 * 1024 * 1024)
    {
        if (!ValidateUrl(url, out var urlErr))
            return BeeResult<(BasisBundleConnector, string)>.Fail($"DownloadConnectorOnlyEx: {urlErr}");

        if (string.IsNullOrWhiteSpace(vp))
            return BeeResult<(BasisBundleConnector, string)>.Fail("DownloadConnectorOnlyEx: VP is null or empty.");

        // Header
        var headerRes = await DownloadRangeInternal(url, 0, BasisBeeConstants.RemoteHeaderSize - 1, null, progressCallback, cancellationToken, MaxDownloadSizeInMB);
        if (!headerRes.IsSuccess || headerRes.Value?.Data == null)
            return BeeResult<(BasisBundleConnector, string)>.Fail($"DownloadConnectorOnlyEx: Failed to read header. {headerRes.Error ?? "No data"}", headerRes.ResponseCode);

        if (headerRes.Value.Data.Length != BasisBeeConstants.RemoteHeaderSize)
            return BeeResult<(BasisBundleConnector, string)>.Fail($"DownloadConnectorOnlyEx: Header size mismatch. Expected {BasisBeeConstants.RemoteHeaderSize}, got {headerRes.Value.Data.Length}.", headerRes.ResponseCode);

        long connectorLength = ReadInt64LittleEndian(headerRes.Value.Data);
        if (connectorLength <= 0)
            return BeeResult<(BasisBundleConnector, string)>.Fail($"DownloadConnectorOnlyEx: Invalid connector length {connectorLength}.");

        if (connectorLength > BasisBeeConstants.MaxConnectorBytes)
            return BeeResult<(BasisBundleConnector, string)>.Fail($"DownloadConnectorOnlyEx: Connector length {connectorLength} exceeds max allowed {BasisBeeConstants.MaxConnectorBytes}.");

        // Connector bytes
        long start = BasisBeeConstants.RemoteHeaderSize;
        long end = BasisBeeConstants.RemoteHeaderSize + connectorLength - 1;

        var connectorRes = await DownloadRangeInternal(url, start, end, null, progressCallback, cancellationToken, MaxDownloadSizeInMB);

        if (connectorRes.IsSuccess == false && connectorRes.Error != string.Empty)
        {
            return BeeResult<(BasisBundleConnector, string)>.Fail(connectorRes.Error, connectorRes.ResponseCode);
        }

        if (!connectorRes.IsSuccess || connectorRes.Value?.Data == null)
            return BeeResult<(BasisBundleConnector, string)>.Fail($"DownloadConnectorOnlyEx: Failed to read connector bytes. {connectorRes.Error ?? "No data"}", connectorRes.ResponseCode);

        if (connectorRes.Value.Data.LongLength != connectorLength)
            return BeeResult<(BasisBundleConnector, string)>.Fail($"DownloadConnectorOnlyEx: Expected {connectorLength} bytes, got {connectorRes.Value.Data.LongLength}.", connectorRes.ResponseCode);

        var connector = await BasisEncryptionToData.GenerateMetaFromBytes(vp, connectorRes.Value.Data, progressCallback);
        BasisDebug.Log("GenerateMetaFromBytes", BasisDebug.LogTag.Event);
        if (connector == null)
        {
            return BeeResult<(BasisBundleConnector, string)>.Fail("DownloadConnectorOnlyEx: Failed to parse connector metadata (null).");
        }

        // 5) Write local .bee (Int32 header + connector + section)
        string fileName = $"{connector.UniqueVersion}{BasisBeeConstants.BasisEncryptedExtension}";
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return BeeResult<(BasisBundleConnector, string)>.Fail("DownloadBEEEx: Connector has no UniqueVersion / file extension.");

        }
        string localPath = GenerateFilePath(fileName, BasisBeeConstants.AssetBundlesFolder);
        var connectorBytes = connectorRes.Value.Data;

        var writeRes = await WriteBeeFileAsync(localPath, connectorBytes, null, true);

        if (!writeRes.IsSuccess)
        {
            return BeeResult<(BasisBundleConnector, string)>.Fail($"DownloadBEEEx: {writeRes.Error}");
        }
        (BasisBundleConnector, string) Data = new(connector, localPath);

        return BeeResult<(BasisBundleConnector, string)>.Ok(Data);
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
        byte[] sizeBytes = await ReadExactAsync(fs, BasisBeeConstants.DiskHeaderSize, cancellationToken);
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
        byte[] connectorBytes = await ReadExactAsync(fs, connectorSize, cancellationToken);
        if (connectorBytes.Length != connectorSize)
        {
            return BeeResult<BeeReadResult>.Fail($"ReadBEEFileEx: Failed to read full connector block. Expected {connectorSize}, got {connectorBytes.Length}.");
        }

        BasisBundleConnector connector = await BasisEncryptionToData.GenerateMetaFromBytes(vp, connectorBytes, progressCallback);
        BasisDebug.Log("GenerateMetaFromBytes", BasisDebug.LogTag.Event);

        if (connector == null)
            return BeeResult<BeeReadResult>.Fail("ReadBEEFileEx: Failed to regenerate connector metadata (null).");

        // Remaining is section data
        long remaining = fs.Length - fs.Position;
        if (remaining < 0) remaining = 0;

        byte[] sectionData = remaining == 0 ? null : await ReadExactAsync(fs, checked((int)remaining), cancellationToken);
        if(sectionData == null)
        {
            return BeeResult<BeeReadResult>.Fail($"ReadBEEFileEx: Failed to read full section data. Expected {remaining}, got {null}.");
        }
        if (sectionData.LongLength != remaining)
        {
            return BeeResult<BeeReadResult>.Fail($"ReadBEEFileEx: Failed to read full section data. Expected {remaining}, got {sectionData.LongLength}.");
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
        byte[] sizeBytes = await ReadExactAsync(fs, BasisBeeConstants.DiskHeaderSize, cancellationToken);
        if (sizeBytes.Length != BasisBeeConstants.DiskHeaderSize)
            return BeeResult<BeeReadResult>.Fail($"ReadBEEFileEx: Failed to read connector size (header). Got {sizeBytes.Length} bytes.");

        int connectorSize = ReadInt32LittleEndian(sizeBytes);
        long remainingPossible = fs.Length - fs.Position;
        if (connectorSize <= 0 || connectorSize > remainingPossible)
            return BeeResult<BeeReadResult>.Fail($"ReadBEEFileEx: Invalid connector size {connectorSize}. Remaining file bytes: {remainingPossible}. File may be corrupt.");

        // Read connector bytes
        byte[] connectorBytes = await ReadExactAsync(fs, connectorSize, cancellationToken);
        if (connectorBytes.Length != connectorSize)
            return BeeResult<BeeReadResult>.Fail($"ReadBEEFileEx: Failed to read full connector block. Expected {connectorSize}, got {connectorBytes.Length}.");

        BasisBundleConnector connector = await BasisEncryptionToData.GenerateMetaFromBytes(vp, connectorBytes, progressCallback);
        BasisDebug.Log("GenerateMetaFromBytes", BasisDebug.LogTag.Event);

        if (connector == null)
            return BeeResult<BeeReadResult>.Fail("ReadBEEFileEx: Failed to regenerate connector metadata (null).");

        return BeeResult<BeeReadResult>.Ok(new BeeReadResult(connector, null));
    }

    public static string GenerateFilePath(string fileName, string subFolder)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("GenerateFilePath: fileName is null or empty.", nameof(fileName));

        string folderPath = GenerateFolderPath(subFolder);
        string localPath = Path.Combine(folderPath, fileName);
        BasisDebug.Log($"Generated folder path: {localPath}");
        return localPath;
    }

    public static string GenerateFolderPath(string subFolder)
    {
        if (string.IsNullOrWhiteSpace(subFolder))
            throw new ArgumentException("GenerateFolderPath: subFolder is null or empty.", nameof(subFolder));

        string basePath = Application.persistentDataPath;
        if (string.IsNullOrWhiteSpace(basePath))
            throw new InvalidOperationException("GenerateFolderPath: Application.persistentDataPath is null/empty.");

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
    private static async Task<BeeResult<DownloadPayload>> DownloadRangeInternal(string url, long startByte, long? endByteInclusive, string toFilePath, BasisProgressReport progress, CancellationToken ct, long MaxDownloadSizeInMB = 4L * 1024 * 1024 * 1024)
    {
        if (!ValidateUrl(url, out var urlErr))
            return BeeResult<DownloadPayload>.Fail(urlErr);

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

        string rangeHeader = endByteInclusive.HasValue ? $"bytes={startByte}-{endByteInclusive.Value}" : $"bytes={startByte}-";

        req.SetRequestHeader("Range", rangeHeader);

        if (string.IsNullOrEmpty(toFilePath) == false)
        {
            // Ensure parent directory exists if the caller passed a path
            string dir = Path.GetDirectoryName(toFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

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

            default:
                progress?.ReportProgress(requestId, 100, $"Error! {code}");
                var details = BuildNetworkErrorDetail(req);
                return BeeResult<DownloadPayload>.Fail($"Unexpected response code: {code}. {details}", code);
        }

        var payload = new DownloadPayload();
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

        // Write file
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, buffer, useAsync: true))
        {
            await fs.WriteAsync(sizeLE, 0, sizeLE.Length).ConfigureAwait(false);
            await fs.WriteAsync(connectorBytes, 0, connectorBytes.Length).ConfigureAwait(false);

            if (writeSection)
            {
                await fs.WriteAsync(sectionBytes, 0, sectionBytes.Length).ConfigureAwait(false);
            }
        }

        long actual = new FileInfo(path).Length;
        BasisDebug.Log($"Expected File Size: {totalSize} bytes");
        BasisDebug.Log($"Actual File Size on Disk: {actual} bytes");

        if (totalSize != actual)
        {
            BasisDebug.LogError("File size does not match expected size!");
            return BeeResult<bool>.Fail($"WriteBeeFileAsync: Size mismatch after write. Expected {totalSize}, actual {actual}.");
        }

        return BeeResult<bool>.Ok(true);
    }

    private static bool ValidateUrl(string url, out string error)
    {
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

        return true;
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
