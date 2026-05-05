using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public static class BasisPlayerSettingsManager
{
    private static readonly string Dir = Path.Combine(Application.persistentDataPath, "PlayerSettings");

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

            BasisPlayerSettingsData data;
            if (!File.Exists(path))
            {
                data = CreateDefaults(uuid);
                await SaveInternal(path, data);
            }
            else
            {
                var json = await File.ReadAllTextAsync(path);
                data = JsonUtility.FromJson<BasisPlayerSettingsData>(json);

                // Version==0 after deserialize signals a zero-initialised struct —
                // either the JSON was empty/corrupt or predates the Version field.
                if (data.Version == 0)
                {
                    data = CreateDefaults(uuid);
                    await SaveInternal(path, data);
                }
                else if (string.IsNullOrEmpty(data.UUID))
                {
                    data.UUID = uuid;
                }
            }

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
            await SaveInternal(path, settings);
        }
        finally
        {
            sem.Release();
        }
    }

    // --- internals ---

    private static async Task SaveInternal(string path, BasisPlayerSettingsData data)
    {
        var json = JsonUtility.ToJson(data, false);
        await File.WriteAllTextAsync(path, json);
    }

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
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            s = s.Replace(c, '_');
        }

        return s;
    }
}
