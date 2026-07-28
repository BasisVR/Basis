using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public static class BasisPlayerSettingsManager
{
    private static readonly string Dir = Path.Combine(Application.persistentDataPath, "PlayerSettings");
    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    // One lock per file
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new ConcurrentDictionary<string, SemaphoreSlim>();

    // In-memory cache so repeated requests for the same UUID don't hit disk.
    private static readonly ConcurrentDictionary<string, BasisPlayerSettingsData> cache = new ConcurrentDictionary<string, BasisPlayerSettingsData>();

    static BasisPlayerSettingsManager()
    {
        Directory.CreateDirectory(Dir);
    }

    public static async Task<BasisPlayerSettingsData> RequestPlayerSettings(string uuid)
    {
        if (string.IsNullOrWhiteSpace(uuid))
        {
            BasisDebug.LogError("Missing UUID");
            return default;
        }

        var key = Sanitize(uuid);

        // Hot path: already cached. No disk, no thread hop — critical during join storms where every
        // remote player is requested by several subsystems (nameplate, audio, avatar, init).
        if (cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var path = GetPath(key);
        var sem = locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync();

        try
        {
            if (cache.TryGetValue(key, out cached))
            {
                return cached;
            }

            // Read off the main thread; only the (trivial) JSON parse resumes on it. A missing file
            // returns defaults WITHOUT writing — the record is created only when a setting actually
            // changes (SetPlayerSettings), so joining never touches the disk for write.
            string json = await Task.Run(() => File.Exists(path) ? File.ReadAllText(path) : null);

            BasisPlayerSettingsData data = default;
            bool valid = false;
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    BasisPlayerSettingsData loaded = JsonUtility.FromJson<BasisPlayerSettingsData>(json);
                    if (loaded.Version != 0)
                    {
                        if (string.IsNullOrEmpty(loaded.UUID)) loaded.UUID = uuid;
                        loaded.UpgradeSchema();
                        data = loaded;
                        valid = true;
                    }
                }
                catch (Exception e)
                {
                    BasisDebug.LogWarning($"Player settings at {path} were unreadable ({e.Message}); using defaults.");
                }
            }

            if (!valid) data = CreateDefaults(uuid);

            cache[key] = data;
            return data;
        }
        finally
        {
            sem.Release();
        }
    }

    public static async Task SetPlayerSettings(BasisPlayerSettingsData settings)
    {
        if (string.IsNullOrWhiteSpace(settings.UUID))
        {
            BasisDebug.LogError("Invalid Settings");
            return;
        }

        settings.VolumeLevel = Mathf.Clamp(settings.VolumeLevel, 0f, 5f);

        var key = Sanitize(settings.UUID);
        var path = GetPath(key);

        var sem = locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync();

        try
        {
            cache[key] = settings;
            string json = JsonUtility.ToJson(settings, false);
            await Task.Run(() => File.WriteAllText(path, json));
        }
        finally
        {
            sem.Release();
        }
    }

    // --- internals ---

    private static BasisPlayerSettingsData CreateDefaults(string uuid)
    {
        return new BasisPlayerSettingsData(uuid, 1.0f, true, true);
    }

    private static string GetPath(string key)
    {
        return Path.Combine(Dir, $"{key}.json");
    }

    private static string Sanitize(string s)
    {
        foreach (var c in InvalidChars)
        {
            s = s.Replace(c, '_');
        }

        return s;
    }
}
