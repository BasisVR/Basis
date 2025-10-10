using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Manages per-player settings with a two-tier storage strategy:
/// an in-memory LRU cache for hot entries and JSON files on disk for persistence.
/// </summary>
/// <remarks>
/// <para><b>Scope</b>: Player-scoped settings keyed by UUID, stored under
/// <see cref="Application.persistentDataPath"/>/<c>PlayerSettings</c>.</para>
/// <para><b>Concurrency</b>:
/// - Reads: lock-free for disk, guarded for cache access.
/// - Writes: serialized per UUID via <see cref="SemaphoreSlim"/> to avoid races and partial writes.</para>
/// <para><b>Durability</b>:
/// Writes use a best-effort atomic protocol (temp file + replace/move) to minimize corruption on crashes.</para>
/// </remarks>
public static class BasisPlayerSettingsManager
{
    /// <summary>
    /// Directory path used to persist settings files (one JSON per UUID).
    /// </summary>
    private static readonly string settingsDirectory = Path.Combine(Application.persistentDataPath, "PlayerSettings");

    /// <summary>
    /// Maximum number of entries retained in the in-memory LRU cache.
    /// </summary>
    private const int CacheSizeLimit = 1024;

    // In-memory LRU cache for recently accessed player settings
    private static readonly Dictionary<string, BasisPlayerSettingsData> settingsCache = new Dictionary<string, BasisPlayerSettingsData>();
    private static readonly LinkedList<string> cacheOrder = new LinkedList<string>();

    // Cache read/write protection (not used for file writes!)
    private static readonly object cacheLock = new object();

    // Async, per-UUID write locks to avoid races like the .tmp missing error
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> writeLocks =
        new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal);

    static BasisPlayerSettingsManager()
    {
        if (!Directory.Exists(settingsDirectory))
        {
            Directory.CreateDirectory(settingsDirectory);
        }
    }

    /// <summary>
    /// Returns a snapshot of the player's settings.
    /// Uses LRU cache when possible, otherwise reads from disk,
    /// repairing corrupted files where feasible and persisting defaults if missing.
    /// </summary>
    /// <param name="uuid">Unique player identifier used as the key for lookup and persistence.</param>
    /// <returns>
    /// A defensive copy of <see cref="BasisPlayerSettingsData"/> for the given <paramref name="uuid"/>.
    /// The returned instance is detached from the cache to prevent unintended external mutation.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="uuid"/> is null, empty, or whitespace.
    /// </exception>
    /// <remarks>
    /// If the on-disk file is missing or corrupted, a default settings record is created,
    /// persisted, cached, and returned.
    /// </remarks>
    public static async Task<BasisPlayerSettingsData> RequestPlayerSettings(string uuid)
    {
        if (string.IsNullOrWhiteSpace(uuid))
            throw new ArgumentException("UUID cannot be null or empty.", nameof(uuid));

        string sanitizedUuid = SanitizeFileName(uuid);

        // Try in-memory cache first
        lock (cacheLock)
        {
            if (settingsCache.TryGetValue(sanitizedUuid, out var cachedData))
            {
                MoveToMostRecent(sanitizedUuid);
                // Return a defensive copy so external mutation doesn't desync cache
                return Clone(cachedData);
            }
        }

        // Try disk
        string filePath = GetFilePath(sanitizedUuid);

        if (File.Exists(filePath))
        {
            try
            {
                string json = await File.ReadAllTextAsync(filePath);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    try
                    {
                        var data = JsonUtility.FromJson<BasisPlayerSettingsData>(json);
                        if (data == null)
                        {
                            BasisDebug.LogError($"Failed to parse settings for {uuid}: FromJson returned null. JSON content: {json}");
                        }
                        else
                        {
                            if (string.IsNullOrEmpty(data.UUID))
                                data.UUID = uuid;

                            lock (cacheLock)
                            {
                                CacheSettings(sanitizedUuid, data);
                            }

                            return Clone(data);
                        }
                    }
                    catch (Exception ex)
                    {
                        BasisDebug.LogError($"Failed to parse settings for {uuid}: {ex.Message}. JSON content: {json}");
                    }
                }
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"Failed to read settings file for {uuid}: {ex.Message}");
            }

            // Corrupt/unreadable -> nuke and recreate defaults
            TryDelete(filePath);
        }

        // Create default settings and persist them (so future reads don't "reset")
        var defaultData = new BasisPlayerSettingsData(uuid, 1.0f, true, true);
        await SetPlayerSettings(defaultData);
        return Clone(defaultData);
    }

    /// <summary>
    /// Persists the provided settings to disk and updates the in-memory cache
    /// with the exact version that was successfully written.
    /// </summary>
    /// <param name="settings">Settings object to persist. Must include a non-empty <see cref="BasisPlayerSettingsData.UUID"/>.</param>
    /// <returns>A task that completes when the write and cache update have finished.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="settings"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="settings"/>.<see cref="BasisPlayerSettingsData.UUID"/> is null, empty, or whitespace.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Volume is clamped to <c>[0, 5]</c> as a safety bound. Writes use a unique temporary file
    /// and attempt <see cref="File.Replace(string, string, string)"/> for atomicity on Windows,
    /// falling back to delete+move elsewhere.
    /// </para>
    /// <para>
    /// Multiple concurrent writes to the same UUID are serialized via a per-UUID semaphore to prevent
    /// partial/overlapping writes and inconsistent cache entries.
    /// </para>
    /// </remarks>
    public static async Task SetPlayerSettings(BasisPlayerSettingsData settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        if (string.IsNullOrWhiteSpace(settings.UUID))
            throw new ArgumentException("Settings.UUID cannot be null or empty.", nameof(settings));

        // Clamp to a sane range (UI may allow up to 1.5, but we allow up to 5f here per original code)
        settings.VolumeLevel = Mathf.Clamp(settings.VolumeLevel, 0f, 5f);

        string sanitizedUuid = SanitizeFileName(settings.UUID);
        string filePath = GetFilePath(sanitizedUuid);

        // Ensure directory exists in case of external cleanup
        if (!Directory.Exists(settingsDirectory))
        {
            Directory.CreateDirectory(settingsDirectory);
        }

        // Serialize once to ensure what we cache matches what we write
        string json = JsonUtility.ToJson(settings, false);

        // Get per-UUID semaphore to serialize writes for the same file
        var sem = writeLocks.GetOrAdd(sanitizedUuid, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync().ConfigureAwait(false);
        try
        {
            // Write atomically (unique temp file + Replace on Windows)
            bool wrote = await AtomicWriteText(filePath, json).ConfigureAwait(false);
            if (!wrote)
            {
                // If write failed, do not update cache — avoid "lying" cache entries.
                return;
            }

            // Only now update the cache with a snapshot that exactly matches disk
            lock (cacheLock)
            {
                CacheSettings(sanitizedUuid, settings);
            }
        }
        finally
        {
            sem.Release();
        }
    }

    #region Helpers

    /// <summary>
    /// Best-effort atomic write that avoids partially written files being observed by readers.
    /// </summary>
    /// <param name="targetPath">Final destination path for the settings file.</param>
    /// <param name="content">Serialized JSON content to write.</param>
    /// <returns>
    /// <c>true</c> if the content was successfully written into place; otherwise <c>false</c>.
    /// </returns>
    /// <remarks>
    /// Strategy:
    /// <list type="number">
    /// <item>Write to a unique temp file adjacent to the target.</item>
    /// <item>Attempt <see cref="File.Replace(string, string, string)"/> (atomic on Windows).</item>
    /// <item>Fallback to delete+move if replace is unavailable.</item>
    /// </list>
    /// Any leftover temp file is cleaned up in a <c>finally</c> block.
    /// </remarks>
    private static async Task<bool> AtomicWriteText(string targetPath, string content)
    {
        // Unique temp name prevents concurrency conflicts (.tmp missing)
        string tmpPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";

        try
        {
            // Write to temp file first
            await File.WriteAllTextAsync(tmpPath, content).ConfigureAwait(false);

            // Prefer atomic replace when possible
            try
            {
                // On Windows this is atomic; on other platforms it may throw
                if (File.Exists(targetPath))
                {
                    File.Replace(tmpPath, targetPath, null);
                }
                else
                {
                    // If target doesn't exist yet, move is fine
                    File.Move(tmpPath, targetPath);
                }
            }
            catch
            {
                // Fallback: delete+move (not strictly atomic, but acceptable fallback)
                try
                {
                    if (File.Exists(targetPath))
                    {
                        File.Delete(targetPath);
                    }
                }
                catch (Exception delEx)
                {
                    BasisDebug.LogWarning($"Failed to delete existing settings before replace: {delEx.Message}");
                }

                // Move temp into place (may still throw, handled by outer catch)
                File.Move(tmpPath, targetPath);
            }

            return true;
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"Failed to write settings file '{targetPath}': {ex.Message}");
            return false;
        }
        finally
        {
            // Cleanup any leftover temp if it still exists
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Deletes a file at the given path while swallowing any IO exceptions into logs.
    /// </summary>
    /// <param name="filePath">Absolute path to a settings file to delete.</param>
    private static void TryDelete(string filePath)
    {
        try
        {
            if (File.Exists(filePath)) File.Delete(filePath);
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"Failed to delete settings file {filePath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Computes the absolute file path for a sanitized UUID.
    /// </summary>
    /// <param name="sanitizedUuid">File-system safe UUID (see <see cref="SanitizeFileName"/>).</param>
    private static string GetFilePath(string sanitizedUuid)
    {
        return Path.Combine(settingsDirectory, $"{sanitizedUuid}.json");
    }

    /// <summary>
    /// Replaces any file-name invalid characters with underscores to make a safe file name.
    /// </summary>
    /// <param name="fileName">Original file name/UUID.</param>
    /// <returns>A sanitized string safe for use as a file name.</returns>
    private static string SanitizeFileName(string fileName)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(c, '_');
        }
        return fileName;
    }

    /// <summary>
    /// Inserts or updates a settings snapshot into the LRU cache, evicting the least
    /// recently used entry when the cache exceeds <see cref="CacheSizeLimit"/>.
    /// </summary>
    /// <param name="uuid">Sanitized UUID key.</param>
    /// <param name="data">Settings data to cache.</param>
    /// <remarks>
    /// A defensive copy is stored to keep the cache isolated from external references.
    /// </remarks>
    private static void CacheSettings(string uuid, BasisPlayerSettingsData data)
    {
        // Store a copy to prevent later external mutation from affecting cached instance
        var snapshot = Clone(data);

        if (settingsCache.ContainsKey(uuid))
        {
            settingsCache[uuid] = snapshot;
            MoveToMostRecent(uuid);
        }
        else
        {
            if (settingsCache.Count >= CacheSizeLimit)
            {
                string oldestUuid = cacheOrder.Last.Value;
                cacheOrder.RemoveLast();
                settingsCache.Remove(oldestUuid);
            }

            settingsCache[uuid] = snapshot;
            cacheOrder.AddFirst(uuid);
        }
    }

    /// <summary>
    /// Marks the specified UUID as the most recently used element in the LRU list.
    /// </summary>
    /// <param name="uuid">Sanitized UUID key.</param>
    private static void MoveToMostRecent(string uuid)
    {
        cacheOrder.Remove(uuid);
        cacheOrder.AddFirst(uuid);
    }

    /// <summary>
    /// Creates a deep copy of the provided settings using <see cref="JsonUtility"/>.
    /// </summary>
    /// <param name="src">Source settings object.</param>
    /// <returns>
    /// A deep-cloned instance, or the original reference if serialization fails.
    /// </returns>
    private static BasisPlayerSettingsData Clone(BasisPlayerSettingsData src)
    {
        if (src == null) return null;
        try
        {
            var json = JsonUtility.ToJson(src, false);
            return JsonUtility.FromJson<BasisPlayerSettingsData>(json);
        }
        catch
        {
            // Fallback; should not normally happen
            return src;
        }
    }

    #endregion
}
