using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public static class BasisPlayerSettingsManager
{
    private static readonly string Dir = Path.Combine(Application.persistentDataPath, "PlayerSettings");

    // Hot-path cache: once loaded, requests are instant.
    private static readonly ConcurrentDictionary<string, BasisPlayerSettingsData> cache = new ConcurrentDictionary<string, BasisPlayerSettingsData>(StringComparer.Ordinal);

    // Coalesce reads per UUID.
    private static readonly ConcurrentDictionary<string, Task<BasisPlayerSettingsData>> inflightReads = new ConcurrentDictionary<string, Task<BasisPlayerSettingsData>>(StringComparer.Ordinal);

    // Serialize writes per UUID.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> writeLocks = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal);

    // Debounce flush per UUID.
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> flushDebounce = new ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.Ordinal);

    // Tune: smaller = more disk churn, larger = less churn.
    private static readonly TimeSpan FlushDelay = TimeSpan.FromMilliseconds(500);

    static BasisPlayerSettingsManager()
    {
        Directory.CreateDirectory(Dir);
    }

    public static Task<BasisPlayerSettingsData> RequestPlayerSettings(string uuid)
    {
        if (string.IsNullOrWhiteSpace(uuid))
        {
            throw new ArgumentException("uuid cannot be null/empty.", nameof(uuid));
        }

        var key = Sanitize(uuid);

        // Fast path: already in memory.
        if (cache.TryGetValue(key, out var cached))
        {
            return Task.FromResult(cached);
        }

        // Otherwise coalesce disk read.
        return inflightReads.GetOrAdd(key, _ => LoadOrCreateAndCacheAsync(key, uuid));
    }

    /// <summary>
    /// Updates cache immediately and schedules a debounced background save.
    /// Call this often (slider ticks etc.) without killing IO performance.
    /// </summary>
    public static Task SetPlayerSettings(BasisPlayerSettingsData settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        if (string.IsNullOrWhiteSpace(settings.UUID))
            throw new ArgumentException("Settings.UUID cannot be null/empty.", nameof(settings));

        settings.VolumeLevel = Mathf.Clamp(settings.VolumeLevel, 0f, 5f);

        var key = Sanitize(settings.UUID);

        // Update in-memory immediately (this is the "fast as possible" part).
        cache[key] = settings;

        // Debounced save (does not block caller).
        ScheduleFlush(key);

        return Task.CompletedTask;
    }
    public static async Task FlushAllNow()
    {
        // Cancel any pending debounced flush tasks so we don't double-write.
        foreach (var kv in flushDebounce.ToArray())
        {
            if (flushDebounce.TryRemove(kv.Key, out var cts))
            {
                try { cts.Cancel(); } catch { }
                cts.Dispose();
            }
        }

        // Snapshot current cache to avoid holding up writers while we iterate.
        var snapshot = cache.ToArray();

        // Write in parallel, but not unbounded (avoid thrashing).
        // Tune this number: 2-4 is usually good on mobile/VR.
        const int maxParallel = 4;
        using var gate = new SemaphoreSlim(maxParallel, maxParallel);

        var tasks = snapshot.Select(async kv =>
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await WriteUnderLockAsync(kv.Key, kv.Value).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
    /// <summary>
    /// Optional: force immediate write (e.g., on app pause/quit).
    /// </summary>
    public static async Task FlushNow(string uuid)
    {
        if (string.IsNullOrWhiteSpace(uuid)) return;
        var key = Sanitize(uuid);
        if (!cache.TryGetValue(key, out var data)) return;

        // cancel any pending debounce
        if (flushDebounce.TryRemove(key, out var cts))
        {
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }

        await WriteUnderLockAsync(key, data).ConfigureAwait(false);
    }

    /// <summary>
    /// Non-blocking cache lookup. Returns true if the settings are already loaded in memory.
    /// Useful for hot-path code that cannot await (e.g., incoming chat message handling).
    /// </summary>
    public static bool TryGetCachedPlayerSettings(string uuid, out BasisPlayerSettingsData settings)
    {
        settings = null;
        if (string.IsNullOrWhiteSpace(uuid)) return false;
        return cache.TryGetValue(Sanitize(uuid), out settings);
    }

    // ---- internals ---------------------------------------------------------

    private static async Task<BasisPlayerSettingsData> LoadOrCreateAndCacheAsync(string key, string originalUuid)
    {
        try
        {
            var path = GetPath(key);

            // Read existing
            if (File.Exists(path))
            {
                var loaded = await TryLoad(path, originalUuid).ConfigureAwait(false);
                var data = loaded ?? RecreateDefaults(originalUuid);
                cache[key] = data;
                return data;
            }

            // Create defaults atomically under same lock used by writers.
            var sem = writeLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await sem.WaitAsync().ConfigureAwait(false);
            try
            {
                if (File.Exists(path))
                {
                    var loaded = await TryLoad(path, originalUuid).ConfigureAwait(false);
                    var data = loaded ?? RecreateDefaults(originalUuid);
                    cache[key] = data;
                    return data;
                }

                var defaults = new BasisPlayerSettingsData(originalUuid, 1.0f, true, true);
                await SaveAsync(path, defaults).ConfigureAwait(false);
                cache[key] = defaults;
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
    }

    private static void ScheduleFlush(string key)
    {
        // Replace existing timer for this key.
        var cts = new CancellationTokenSource();
        flushDebounce.AddOrUpdate(key, cts, (_, old) =>
        {
            try { old.Cancel(); } catch { }
            // Don't dispose here — the old task's finally block handles disposal.
            return cts;
        });

        // Fire-and-forget background task.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(FlushDelay, cts.Token).ConfigureAwait(false);

                if (!cache.TryGetValue(key, out var data))
                {
                    return;
                }

                await WriteUnderLockAsync(key, data).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* expected */ }
            catch (Exception ex)
            {
                BasisDebug.LogError($"Flush failed for '{key}': {ex.Message}");
            }
            finally
            {
                // Only remove if we're still the current cts for this key.
                if (flushDebounce.TryGetValue(key, out var current) && ReferenceEquals(current, cts))
                {
                    flushDebounce.TryRemove(key, out _);
                }

                cts.Dispose();
            }
        });
    }

    private static async Task WriteUnderLockAsync(string key, BasisPlayerSettingsData data)
    {
        var sem = writeLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync().ConfigureAwait(false);
        try
        {
            await SaveAsync(GetPath(key), data).ConfigureAwait(false);
        }
        finally
        {
            sem.Release();
        }
    }

    /// <summary>
    /// Upgrades settings from older schema versions.
    /// Fields added in newer versions are missing from old JSON and default to
    /// their CLR zero-value (false for bool), so we explicitly set correct defaults here.
    /// </summary>
    private static void MigrateIfNeeded(BasisPlayerSettingsData data)
    {
        if (data.Version < 3)
        {
            data.ChatVisible = true;
            data.Version = 3;
        }
    }

    private static async Task<BasisPlayerSettingsData> TryLoad(string p, string orig)
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
                    MigrateIfNeeded(data);
                    return data;
                }
            }
            BasisDebug.LogError($"Parse failed for {orig}.");
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"Read failed for {orig}: {ex.Message}");
        }

        TryDelete(p);
        return null;
    }

    private static BasisPlayerSettingsData RecreateDefaults(string uuid) => new BasisPlayerSettingsData(uuid, 1.0f, true, true);

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
                try { if (File.Exists(targetPath)) File.Delete(targetPath); } catch { }
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
            try {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
            catch { }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) { BasisDebug.LogError($"Delete failed '{path}': {ex.Message}"); }
    }

    private static string GetPath(string key) => Path.Combine(Dir, $"{key}.json");

    private static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            s = s.Replace(c, '_');
        }

        return s;
    }
}
