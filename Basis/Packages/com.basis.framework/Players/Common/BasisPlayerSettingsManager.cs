using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public static class BasisPlayerSettingsManager
{
    private static readonly string Dir = Path.Combine(Application.persistentDataPath, "PlayerSettings");

    // Coalesce reads: if a read for a UUID is already running, await the same Task.
    private static readonly ConcurrentDictionary<string, Task<BasisPlayerSettingsData>> inflightReads =  new ConcurrentDictionary<string, Task<BasisPlayerSettingsData>>(StringComparer.Ordinal);

    // Serialize writes per UUID.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> writeLocks = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal);

    static BasisPlayerSettingsManager()
    {
        if (!Directory.Exists(Dir))
        {
            Directory.CreateDirectory(Dir);
        }
    }

    public static Task<BasisPlayerSettingsData> RequestPlayerSettings(string uuid)
    {
        if (string.IsNullOrWhiteSpace(uuid))
        {
            throw new ArgumentException("uuid cannot be null/empty.", nameof(uuid));
        }

        var key = Sanitize(uuid);

        // If a read is already running, await the same Task. Otherwise, start one.
        return inflightReads.GetOrAdd(key, _ => LoadOrCreateAsync(key, uuid));
    }

    public static async Task SetPlayerSettings(BasisPlayerSettingsData settings)
    {
        if (settings == null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        if (string.IsNullOrWhiteSpace(settings.UUID))
        {
            throw new ArgumentException("Settings.UUID cannot be null/empty.", nameof(settings));
        }

        settings.VolumeLevel = Mathf.Clamp(settings.VolumeLevel, 0f, 5f);

        var key = Sanitize(settings.UUID);
        var sem = writeLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

        await sem.WaitAsync().ConfigureAwait(false);
        try
        {
            await SaveAsync(GetPath(key), settings).ConfigureAwait(false);
        }
        finally
        {
            sem.Release();
        }
    }

    // ---- internals ---------------------------------------------------------

    private static async Task<BasisPlayerSettingsData> LoadOrCreateAsync(string key, string originalUuid)
    {
        try
        {
            var path = GetPath(key);
            if (File.Exists(path))
            {
                return await TryLoad(path, originalUuid)  ??  RecreateDefaults();
            }

            // --- Create defaults atomically with writes ---
            var sem = writeLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await sem.WaitAsync();
            try
            {
                // Another writer might have produced the file while we were waiting.
                if (File.Exists(path))
                {
                    return await TryLoad(path, originalUuid)??  RecreateDefaults();
                }

                var defaults = new BasisPlayerSettingsData(originalUuid, 1.0f, true, true);
                await SaveAsync(path, defaults);
                return defaults;
            }
            finally
            {
                sem.Release();
            }
        }
        finally
        {
            inflightReads.TryRemove(key, out _);
        }

        // local helpers
        static async Task<BasisPlayerSettingsData> TryLoad(string p, string orig)
        {
            try
            {
                var json = await File.ReadAllTextAsync(p).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var data = JsonUtility.FromJson<BasisPlayerSettingsData>(json);
                    if (data != null)
                    {
                        if (string.IsNullOrEmpty(data.UUID)) data.UUID = orig;
                        return data;
                    }
                }
                BasisDebug.LogError($"Parse failed for {orig}.");
            }
            catch (Exception ex) { BasisDebug.LogError($"Read failed for {orig}: {ex.Message}"); }
            TryDelete(p);
            return null;
        }

        BasisPlayerSettingsData RecreateDefaults()
        {
            var d = new BasisPlayerSettingsData(originalUuid, 1.0f, true, true);
            // No SetPlayerSettings here; SaveAsync already did atomic write under lock.
            return d;
        }
    }

    private static async Task SaveAsync(string targetPath, BasisPlayerSettingsData data)
    {
        var json = JsonUtility.ToJson(data, false);
        var tmp = $"{targetPath}.{Guid.NewGuid():N}.tmp";

        try
        {
            await File.WriteAllTextAsync(tmp, json).ConfigureAwait(false);

            try
            {
                if (File.Exists(targetPath))
                    File.Replace(tmp, targetPath, null);
                else
                    File.Move(tmp, targetPath);
            }
            catch
            {
                try { if (File.Exists(targetPath)) File.Delete(targetPath); } catch { /* ignore */ }
                File.Move(tmp, targetPath);
            }
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"Write failed '{targetPath}': {ex.Message}");
            throw;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { BasisDebug.LogError($"Delete failed '{path}': {ex.Message}"); }
    }

    private static string GetPath(string key) => Path.Combine(Dir, $"{key}.json");

    private static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return s;
    }
}
