using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

public static class BasisPlayerSettingsManager
{
    private static readonly string settingsDirectory = Path.Combine(Application.persistentDataPath, "PlayerSettings");

    private const int CacheSizeLimit = 200;

    // In-memory cache for recently accessed player settings
    private static readonly Dictionary<string, BasisPlayerSettingsData> settingsCache = new Dictionary<string, BasisPlayerSettingsData>();
    private static readonly LinkedList<string> cacheOrder = new LinkedList<string>();

    static BasisPlayerSettingsManager()
    {
        if (!Directory.Exists(settingsDirectory))
        {
            Directory.CreateDirectory(settingsDirectory);
        }
    }

    public static async Task<BasisPlayerSettingsData> RequestPlayerSettings(string uuid)
    {
        string sanitizedUuid = SanitizeFileName(uuid);

        // Try to get from cache
        if (settingsCache.TryGetValue(sanitizedUuid, out var cachedData))
        {
            MoveToMostRecent(sanitizedUuid);
            return cachedData;
        }

        string filePath = GetFilePath(sanitizedUuid);

        if (File.Exists(filePath))
        {
            try
            {
                string json = await File.ReadAllTextAsync(filePath);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var data = JsonUtility.FromJson<BasisPlayerSettingsData>(json);
                    CacheSettings(sanitizedUuid, data);
                    return data;
                }
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"Failed to load settings for {uuid}: {ex.Message}. Resetting file.");
            }

            File.Delete(filePath);//runs if a error or file is bad.
        }

        BasisPlayerSettingsData defaultData = new BasisPlayerSettingsData(uuid, 1.0f, true);
        await SetPlayerSettings(defaultData);
        return defaultData;
    }

    public static async Task SetPlayerSettings(BasisPlayerSettingsData settings)
    {
        string sanitizedUuid = SanitizeFileName(settings.UUID);
        string filePath = GetFilePath(sanitizedUuid);

        CacheSettings(sanitizedUuid, settings);

        if (BasisPlayerSettingsData.Default.VolumeLevel == settings.VolumeLevel
            && BasisPlayerSettingsData.Default.AvatarVisible == settings.AvatarVisible)
        {
         //   BasisDebug.Log("Player Settings where default no need to store a copy");
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
            return;
        }

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
        string json = JsonUtility.ToJson(settings, false);
        await File.WriteAllTextAsync(filePath, json);
    }

    private static string GetFilePath(string sanitizedUuid)
    {
        return Path.Combine(settingsDirectory, $"{sanitizedUuid}.json");
    }

    private static string SanitizeFileName(string fileName)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(c, '_');
        }
        return fileName;
    }

    private static void CacheSettings(string uuid, BasisPlayerSettingsData data)
    {
        if (settingsCache.ContainsKey(uuid))
        {
            settingsCache[uuid] = data;
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

            settingsCache[uuid] = data;
            cacheOrder.AddFirst(uuid);
        }
    }

    private static void MoveToMostRecent(string uuid)
    {
        cacheOrder.Remove(uuid);
        cacheOrder.AddFirst(uuid);
    }
}
