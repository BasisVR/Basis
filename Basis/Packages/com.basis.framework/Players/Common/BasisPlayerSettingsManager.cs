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

    static BasisPlayerSettingsManager()
    {
        Directory.CreateDirectory(Dir);
    }

    public static async Task<BasisPlayerSettingsData> RequestPlayerSettings(string uuid)
    {
        if (string.IsNullOrWhiteSpace(uuid))
        {
            BasisDebug.LogError("Missing UUID");
            return null;
        }

        var key = Sanitize(uuid);
        var path = GetPath(key);

        var sem = locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync();

        try
        {
            if (!File.Exists(path))
            {
                var defaults = CreateDefaults(uuid);
                await SaveInternal(path, defaults);
                return defaults;
            }

            var json = await File.ReadAllTextAsync(path);
            var data = JsonUtility.FromJson<BasisPlayerSettingsData>(json);

            if (data == null)
            {
                return CreateDefaults(uuid);
            }

            if (string.IsNullOrEmpty(data.UUID))
            {
                data.UUID = uuid;
            }

            return data;
        }
        finally
        {
            sem.Release();
        }
    }

    public static async Task SetPlayerSettings(BasisPlayerSettingsData settings)
    {
        if (settings == null || string.IsNullOrWhiteSpace(settings.UUID))
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
