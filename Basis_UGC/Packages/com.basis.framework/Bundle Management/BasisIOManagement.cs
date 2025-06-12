using System.IO;
using System.Threading.Tasks;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;
using System;

public static class BasisIOManagement
{
    public static int HeaderSize = 8; // 8 bytes

    public static async Task<(BasisBundleConnector, string, byte[])> DownloadBEE(string url, string vp, BasisProgressReport progressCallback, CancellationToken cancellationToken = default)
    {
        byte[] ConnectorSize = await DownloadFileRange(url, null, progressCallback, cancellationToken, 0, HeaderSize, true);
        long LengthOfSection = BitConverter.ToInt64(ConnectorSize, 0);
        byte[] ConnectorBytes = await DownloadFileRange(url, null, progressCallback, cancellationToken, HeaderSize, HeaderSize + LengthOfSection - 1, true);

        BasisDebug.Log("Downloaded Connector file size is " + ConnectorBytes.Length);
        BasisBundleConnector Connector = await BasisEncryptionToData.GenerateMetaFromBytes(vp, ConnectorBytes, progressCallback);

        long previousEnd = HeaderSize + LengthOfSection - 1; // Correct start position after header
        byte[] lastSectionData = null;

        // Download all necessary sections
        for (int Index = 0; Index < Connector.BasisBundleGenerated.Length; Index++)
        {
            BasisBundleGenerated pair = Connector.BasisBundleGenerated[Index];

            long startPosition = previousEnd + 1;
            long sectionLength = pair.EndByte;
            long endPosition = startPosition + sectionLength - 1;

            if (BasisBundleConnector.IsPlatform(pair))
            {
                Console.WriteLine($"Downloading from {startPosition} to {endPosition}");

                lastSectionData = await DownloadFileRange(url, null, progressCallback, cancellationToken, startPosition, endPosition, true);
                BasisDebug.Log("Section length is " + lastSectionData.Length);
            }
            previousEnd = endPosition;
        }

        string BEEPath = BasisIOManagement.GenerateFilePath($"{Connector.UniqueVersion}{BasisBundleManagement.BasisEncryptedExtension}", BasisBundleManagement.AssetBundlesFolder);

        await SaveFileDataToDiscAsync(BEEPath, ConnectorBytes, lastSectionData);

        return new(Connector, BEEPath, lastSectionData);
    }

    public static async Task SaveFileDataToDiscAsync(string BEEPath, byte[] ConnectorBytes, byte[] lastSectionData)
    {
        byte[] connectorSizeBytes = BitConverter.GetBytes(ConnectorBytes.Length);
        if (!BitConverter.IsLittleEndian)
            Array.Reverse(connectorSizeBytes); // Ensure little-endian format

        try
        {
            using (FileStream fileStream = new FileStream(BEEPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
            {
                await fileStream.WriteAsync(connectorSizeBytes, 0, connectorSizeBytes.Length);
                await fileStream.WriteAsync(ConnectorBytes, 0, ConnectorBytes.Length);
                await fileStream.WriteAsync(lastSectionData, 0, lastSectionData.Length);
            }

            long expectedSize = connectorSizeBytes.Length + ConnectorBytes.Length + lastSectionData.Length;
            long actualSize = new FileInfo(BEEPath).Length;

            BasisDebug.Log($"Expected File Size: {expectedSize} bytes");
            BasisDebug.Log($"Actual File Size on Disk: {actualSize} bytes");

            if (expectedSize == actualSize)
            {
                BasisDebug.Log("File size is correct.");
            }
            else
            {
                BasisDebug.LogError("File size does not match expected size!");
            }
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"Error writing file: {ex.Message}");
        }
    }

    public static async Task<(BasisBundleConnector, byte[])> ReadBEEFile(string filePath, string vp, BasisProgressReport progressCallback, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            BasisDebug.LogError($"File not found: {filePath}");
            return (null, null);
        }

        BasisBundleConnector connector = null;
        byte[] sectionData = null;

        using FileStream fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);

        byte[] sizeBytes = new byte[sizeof(int)];
        int read = await fileStream.ReadAsync(sizeBytes, 0, sizeof(int), cancellationToken);
        if (read < sizeof(int))
        {
            BasisDebug.LogError("Failed to read the connector size - file might be corrupted.");
            return (null, null);
        }

        if (!BitConverter.IsLittleEndian)
        {
            Array.Reverse(sizeBytes);
        }

        int connectorSize = BitConverter.ToInt32(sizeBytes, 0);
        long remaining = fileStream.Length - fileStream.Position;

        if (connectorSize <= 0 || connectorSize > remaining)
        {
            BasisDebug.LogError("Invalid connector size detected! Possible corruption or incorrect file format.");
            return (null, null);
        }

        byte[] connectorBytes = new byte[connectorSize];
        int connectorRead = await fileStream.ReadAsync(connectorBytes, 0, connectorSize, cancellationToken);
        if (connectorRead < connectorSize)
        {
            BasisDebug.LogError("Failed to read the full connector block.");
            return (null, null);
        }

        connector = await BasisEncryptionToData.GenerateMetaFromBytes(vp, connectorBytes, progressCallback);

        long sectionLength = fileStream.Length - fileStream.Position;
        sectionData = new byte[sectionLength];
        int sectionRead = await fileStream.ReadAsync(sectionData, 0, (int)sectionLength, cancellationToken);

        if (sectionRead < sectionLength)
        {
            BasisDebug.LogError("Failed to read the full section data.");
            return (null, null);
        }

        return (connector, sectionData);
    }
    public static async Task<byte[]> DownloadFileRange(string url, string localFilePath, BasisProgressReport progressCallback, CancellationToken cancellationToken = default,long startByte = 0, long? endByte = null, bool loadToMemory = false)
    {
        BasisDebug.Log($"Starting file download from {url} (Range: {startByte}-{(endByte.HasValue ? endByte.ToString() : "end")})");
        string uniqueID = BasisGenerateUniqueID.GenerateUniqueID();

        localFilePath = loadToMemory ? "memory" : localFilePath;

        if (!ValidateInputs(url, localFilePath))
        {
            return null;
        }

        using (UnityWebRequest request = CreateRequest(url, startByte, endByte, loadToMemory ? null : localFilePath))
        {
            return await ProcessDownload(request, uniqueID, progressCallback, cancellationToken, url, localFilePath, startByte, endByte, loadToMemory);
        }
    }

    private static bool ValidateInputs(string url, string localFilePath)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            BasisDebug.LogError("The provided URL is null or empty.");
            return false;
        }
        if (string.IsNullOrWhiteSpace(localFilePath))
        {
            BasisDebug.LogError("The provided local file path is null or empty.");
            return false;
        }
        return true;
    }

    private static UnityWebRequest CreateRequest(string url, long startByte, long? endByte, string localFilePath = null)
    {
        UnityWebRequest request = UnityWebRequest.Get(url);
        string rangeHeader = endByte.HasValue ? $"bytes={startByte}-{endByte}" : $"bytes={startByte}-";
        request.SetRequestHeader("Range", rangeHeader);

        if (localFilePath != null)
        {
            request.downloadHandler = new DownloadHandlerFile(localFilePath, true) { removeFileOnAbort = true };
        }
        else
        {
            request.downloadHandler = new DownloadHandlerBuffer();
        }

        return request;
    }

    private static async Task<byte[]> ProcessDownload(UnityWebRequest request, string uniqueID, BasisProgressReport progressCallback, CancellationToken cancellationToken,string url, string localFilePath, long startByte, long? endByte, bool loadToMemory)
    {
        UnityWebRequestAsyncOperation asyncOperation = request.SendWebRequest();

        while (!asyncOperation.isDone)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                BasisDebug.Log("Download cancelled.");
                request.Abort();
                return null;
            }
            progressCallback.ReportProgress(uniqueID, asyncOperation.webRequest.downloadProgress * 100, "Downloading data...");
            await Task.Yield();
        }

        if (request.result != UnityWebRequest.Result.Success)
        {
            BasisDebug.LogError($"Failed to download file: {request.error} for URL {url}");
            return null;
        }

        long responseCode = request.responseCode;

        switch (responseCode)
        {
            case 200: // OK
                // TODO: delete the file if loadToMemory is false?
                // Future Work: cut this off early by performing a HEAD request to see if the server supports range requests?
                //  peek at response codes/headers while the request isn't finished to abort as soon as possible?
                BasisDebug.LogError($"Server replied with whole file! Please use a host that supports range requests.");
                return null;
            case 206: // Partial Content
                // Success, continue.
                break;
            case 416: // Requested Range Not Satisfiable
                // TODO: is this considered an error by UnityWebRequest? if it is, than this is likely dead code.
                BasisDebug.LogError($"Requested Range {startByte}-{(endByte.HasValue ? endByte.ToString() : "end")} not satisfiable.");
                return null;
            default:
                // This case is likely mostly already covered by checking if UnityWebRequest called this a success.
                BasisDebug.LogError($"Unknown Response code: {responseCode}");
                return null;
        }

        if (loadToMemory)
        {
            BasisDebug.Log($"Successfully downloaded range {startByte}-{(endByte.HasValue ? endByte.ToString() : "end")} to memory");
            return request.downloadHandler.data;  // Return as byte array
        }
        else
        {
            if (!File.Exists(localFilePath))
            {
                BasisDebug.LogError("The file was not created.");
                return null;
            }
            BasisDebug.Log($"Successfully downloaded range {startByte}-{(endByte.HasValue ? endByte.ToString() : "end")} from {url} to {localFilePath}");
            return null;  // No data returned for file-based download
        }
    }

    public static string GenerateFilePath(string fileName, string subFolder)
    {
        BasisDebug.Log($"Generating folder path for {fileName} in subfolder {subFolder}");

        string folderPath = GenerateFolderPath(subFolder);
        string localPath = Path.Combine(folderPath, fileName);
        BasisDebug.Log($"Generated folder path: {localPath}");

        return localPath;
    }

    public static string GenerateFolderPath(string subFolder)
    {
        BasisDebug.Log($"Generating folder path in subfolder {subFolder}");

        string folderPath = Path.Combine(Application.persistentDataPath, subFolder);

        if (!Directory.Exists(folderPath))
        {
            BasisDebug.Log($"Directory {folderPath} does not exist. Creating directory.");
            Directory.CreateDirectory(folderPath);
        }
        return folderPath;
    }
}
