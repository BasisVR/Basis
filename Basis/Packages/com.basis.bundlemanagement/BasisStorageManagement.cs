using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Manages BEE file storage on disk: tracks total size, enforces a configurable max (default 128 GB),
/// evicts oldest files (LRU by file write time), and provides listing/deletion APIs.
/// </summary>
public static class BasisStorageManagement
{
    /// <summary>
    /// Default maximum cache size in bytes (128 GB).
    /// </summary>
    public const long DefaultMaxCacheSizeBytes = 128L * 1024 * 1024 * 1024;

    /// <summary>
    /// Current maximum cache size in bytes. Updated by settings.
    /// </summary>
    public static long MaxCacheSizeBytes = DefaultMaxCacheSizeBytes;

    /// <summary>
    /// Represents a single stored BEE file entry with its metadata.
    /// </summary>
    public class StoredBeeFileInfo
    {
        public string RemoteUrl;
        public string LocalPath;
        public string MetaPath;
        public string UniqueVersion;
        public long FileSizeBytes;
        public DateTime LastWriteTimeUtc;
        public bool IsLoadedInMemory;
    }

    /// <summary>
    /// Returns the total size in bytes of all files in the BEEData folder.
    /// </summary>
    public static long GetTotalCacheSizeBytes()
    {
        string folderPath = GetCacheFolderPath();
        if (!Directory.Exists(folderPath))
            return 0;

        long total = 0;
        foreach (string file in Directory.GetFiles(folderPath))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch (Exception)
            {
                // File may have been deleted between enumeration and access
            }
        }
        return total;
    }

    /// <summary>
    /// Returns a list of all stored BEE files with their metadata.
    /// </summary>
    public static List<StoredBeeFileInfo> GetAllStoredFiles()
    {
        var result = new List<StoredBeeFileInfo>();
        string folderPath = GetCacheFolderPath();

        if (!Directory.Exists(folderPath))
            return result;

        foreach (var kvp in BasisLoadHandler.OnDiscData)
        {
            string remoteUrl = kvp.Key;
            BasisBEEExtensionMeta meta = kvp.Value;

            string beePath = meta.StoredLocal.DownloadedBeeFileLocation;
            if (string.IsNullOrEmpty(beePath))
            {
                beePath = BasisIOManagement.GenerateFilePath(
                    $"{meta.UniqueVersion}{BasisBeeConstants.BasisEncryptedExtension}",
                    BasisBeeConstants.AssetBundlesFolder);
            }

            string metaFilePath = BasisIOManagement.GenerateFilePath(
                $"{meta.UniqueVersion}{BasisBeeConstants.BasisMetaExtension}",
                BasisBeeConstants.AssetBundlesFolder);

            long fileSize = 0;
            DateTime lastWrite = DateTime.MinValue;

            if (File.Exists(beePath))
            {
                try
                {
                    var fi = new FileInfo(beePath);
                    fileSize = fi.Length;
                    lastWrite = fi.LastWriteTimeUtc;
                }
                catch (Exception)
                {
                    // Ignore access errors
                }
            }

            bool isLoaded = BasisLoadHandler.LoadedBundles.ContainsKey(remoteUrl);

            result.Add(new StoredBeeFileInfo
            {
                RemoteUrl = remoteUrl,
                LocalPath = beePath,
                MetaPath = metaFilePath,
                UniqueVersion = meta.UniqueVersion,
                FileSizeBytes = fileSize,
                LastWriteTimeUtc = lastWrite,
                IsLoadedInMemory = isLoaded,
            });
        }

        // Sort by last write time (oldest first)
        result.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
        return result;
    }

    /// <summary>
    /// Deletes a single stored BEE file by its remote URL key.
    /// Cleans up the .BEE file, .BME meta file, and all in-memory references.
    /// Returns true if the entry was found and cleaned up.
    /// </summary>
    public static bool DeleteStoredFile(string remoteUrl)
    {
        if (string.IsNullOrEmpty(remoteUrl))
            return false;

        // Remove from OnDiscData and get the meta info
        if (BasisLoadHandler.OnDiscData.TryRemove(remoteUrl, out BasisBEEExtensionMeta meta))
        {
            // Delete the .BEE file
            string beePath = meta.StoredLocal.DownloadedBeeFileLocation;
            if (string.IsNullOrEmpty(beePath))
            {
                beePath = BasisIOManagement.GenerateFilePath(
                    $"{meta.UniqueVersion}{BasisBeeConstants.BasisEncryptedExtension}",
                    BasisBeeConstants.AssetBundlesFolder);
            }
            TryDeleteFile(beePath);

            // Delete the .BME meta file
            string metaPath = BasisIOManagement.GenerateFilePath(
                $"{meta.UniqueVersion}{BasisBeeConstants.BasisMetaExtension}",
                BasisBeeConstants.AssetBundlesFolder);
            TryDeleteFile(metaPath);

            BasisDebug.Log($"Deleted stored BEE file: {meta.UniqueVersion} (source: {remoteUrl})", BasisDebug.LogTag.Event);
        }
        else
        {
            BasisDebug.LogWarning($"No OnDiscData entry found for URL: {remoteUrl}", BasisDebug.LogTag.Event);
            return false;
        }

        // Unload from memory if loaded
        if (BasisLoadHandler.LoadedBundles.TryRemove(remoteUrl, out BasisTrackedBundleWrapper wrapper))
        {
            if (wrapper.AssetBundle != null)
            {
                try
                {
                    BasisDebug.Log($"Unloading in-memory AssetBundle for: {remoteUrl}", BasisDebug.LogTag.Event);
                    wrapper.AssetBundle.Unload(true);
                }
                catch (Exception ex)
                {
                    BasisDebug.LogError($"Error unloading AssetBundle: {ex.Message}");
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Deletes all stored BEE files, clearing the entire cache.
    /// </summary>
    public static void ClearAllCache()
    {
        // Get all keys first to avoid concurrent modification
        var keys = BasisLoadHandler.OnDiscData.Keys.ToList();

        foreach (string key in keys)
        {
            DeleteStoredFile(key);
        }

        // Also clean up any orphaned files not tracked in OnDiscData
        string folderPath = GetCacheFolderPath();
        if (Directory.Exists(folderPath))
        {
            foreach (string file in Directory.GetFiles(folderPath))
            {
                TryDeleteFile(file);
            }
        }

        BasisDebug.Log("All BEE cache cleared.", BasisDebug.LogTag.Event);
    }

    /// <summary>
    /// Enforces the cache size limit by evicting the oldest (LRU) files
    /// that are not currently loaded in memory, until total size is under MaxCacheSizeBytes.
    /// Call this after downloading a new file.
    /// </summary>
    public static void EnforceCacheSizeLimit()
    {
        long currentSize = GetTotalCacheSizeBytes();
        if (currentSize <= MaxCacheSizeBytes)
            return;

        BasisDebug.Log($"Cache size {FormatBytes(currentSize)} exceeds limit {FormatBytes(MaxCacheSizeBytes)}. Evicting oldest files...", BasisDebug.LogTag.Event);

        // Get all files sorted oldest first, skip currently loaded ones
        var allFiles = GetAllStoredFiles();
        var evictable = allFiles.Where(f => !f.IsLoadedInMemory && f.FileSizeBytes > 0).ToList();

        foreach (var file in evictable)
        {
            if (currentSize <= MaxCacheSizeBytes)
                break;

            long freed = file.FileSizeBytes;
            if (DeleteStoredFile(file.RemoteUrl))
            {
                currentSize -= freed;
                BasisDebug.Log($"Evicted: {file.UniqueVersion} ({FormatBytes(freed)}). Remaining: {FormatBytes(currentSize)}", BasisDebug.LogTag.Event);
            }
        }

        if (currentSize > MaxCacheSizeBytes)
        {
            BasisDebug.LogWarning($"Cache still exceeds limit after eviction ({FormatBytes(currentSize)} / {FormatBytes(MaxCacheSizeBytes)}). Some files may be in use.", BasisDebug.LogTag.Event);
        }
    }

    /// <summary>
    /// Formats a byte count into a human-readable string (B, KB, MB, GB).
    /// </summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "0 B";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024L * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }

    private static string GetCacheFolderPath()
    {
        return BasisIOManagement.GenerateFolderPath(BasisBeeConstants.AssetBundlesFolder);
    }

    private static void TryDeleteFile(string path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"Failed to delete file {path}: {ex.Message}");
        }
    }
}
