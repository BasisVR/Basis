using UnityEngine;
using UnityEngine.SceneManagement;
using System.IO;
using System.Collections.Generic;
using System;
using System.Threading.Tasks;

[Serializable]
public class KeyValue
{
    public string key;
    public string value;
}

[Serializable]
public class SettingsData
{
    public string version;
    public List<KeyValue> settingsList = new List<KeyValue>();

    // Runtime helper dictionary (not serialized directly)
    [NonSerialized]
    public Dictionary<string, string> settings = new Dictionary<string, string>();

    // Convert List -> Dictionary after loading
    public void RebuildDictionary()
    {
        settings.Clear();
        foreach (var kv in settingsList)
        {
            if (!settings.ContainsKey(kv.key))
                settings[kv.key] = kv.value;
        }
    }

    // Convert Dictionary -> List before saving
    public void RebuildList()
    {
        settingsList.Clear();
        foreach (var pair in settings)
        {
            settingsList.Add(new KeyValue { key = pair.Key, value = pair.Value });
        }
    }
}

public static class BasisSettingsSystem
{
    public const string SettingsJson = "settingsConfig.json";
    private static string filePath = Path.Combine(Application.persistentDataPath, SettingsJson);
    private static string currentVersion = Application.version;
    private static SettingsData settingsData = new SettingsData();

    /// <summary>
    /// UniqueName,Optionvalue
    /// </summary>
    public static event Action<string, string> OnSettingChanged;

    static BasisSettingsSystem()
    {
        // Load once at startup
        _ = LoadAllSettingsAsync();

        // 🔹 Subscribe to scene load
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        // Fire & forget reload whenever a scene loads
        _ = LoadAllSettingsAsync();
    }

    public static async Task SaveString(string uniqueSettingsName, string value)
    {
        bool changed = false;

        if (settingsData.settings.ContainsKey(uniqueSettingsName))
        {
            if (settingsData.settings[uniqueSettingsName] != value)
            {
                settingsData.settings[uniqueSettingsName] = value;
                changed = true;
            }
        }
        else
        {
            settingsData.settings[uniqueSettingsName] = value;
            changed = true;
        }

        await SaveAllSettingsAsync();

        if (changed)
            OnSettingChanged?.Invoke(uniqueSettingsName, value);
    }

    public static string LoadString(string uniqueSettingsName, string defaultValue = "")
    {
        if (settingsData.settings.TryGetValue(uniqueSettingsName, out string value))
            return value;

        _ = SaveString(uniqueSettingsName, defaultValue); // async save default
        return defaultValue;
    }

    public static async Task LoadAllSettingsAsync()
    {
        try
        {
            if (!File.Exists(filePath))
            {
                BasisDebug.Log("Settings file not found, creating defaults.");
                await ResetToDefaultAsync();
                return;
            }

            string json = await File.ReadAllTextAsync(filePath);
            settingsData = JsonUtility.FromJson<SettingsData>(json);

            if (settingsData == null || settingsData.version != currentVersion)
            {
                BasisDebug.LogWarning("Settings version mismatch or corrupt file. Resetting.");
                await ResetToDefaultAsync();
            }
            else
            {
                settingsData.RebuildDictionary();
            }
            foreach (var KeyStore in settingsData.settings)
            {
                OnSettingChanged?.Invoke(KeyStore.Key, KeyStore.Value);
            }
        }
        catch (Exception e)
        {
            BasisDebug.LogError($"Failed to load settings: {e.Message}. Resetting.");
            await ResetToDefaultAsync();
        }
    }

    public static async Task SaveAllSettingsAsync()
    {
        try
        {
            settingsData.version = currentVersion;
            settingsData.RebuildList(); // sync dictionary into list
            string json = JsonUtility.ToJson(settingsData, true);
            await File.WriteAllTextAsync(filePath, json);
        }
        catch (Exception e)
        {
            BasisDebug.LogError($"Failed to save settings: {e.Message}");
        }
    }

    private static async Task ResetToDefaultAsync()
    {
        settingsData = new SettingsData();
        settingsData.version = currentVersion;
        settingsData.RebuildDictionary();
        await SaveAllSettingsAsync();
    }

    public static async Task NukeSettingsAsync()
    {
        BasisDebug.Log("Nuking settings file and resetting to defaults.");
        await ResetToDefaultAsync();
    }

    // 🔹 Strongly-typed accessors
    public static int LoadInt(string key, int defaultValue = 0)
    {
        string val = LoadString(key, defaultValue.ToString());
        return int.TryParse(val, out int result) ? result : defaultValue;
    }

    public static float LoadFloat(string key, float defaultValue = 0f)
    {
        string val = LoadString(key, defaultValue.ToString());
        return float.TryParse(val, out float result) ? result : defaultValue;
    }

    public static bool LoadBool(string key, bool defaultValue = false)
    {
        string val = LoadString(key, defaultValue ? "1" : "0");
        return val == "1" || val.ToLower() == "true";
    }

    public static async Task SetIntAsync(string key, int value) => await SaveString(key, value.ToString());
    public static async Task SetFloatAsync(string key, float value) => await SaveString(key, value.ToString());
    public static async Task SetBoolAsync(string key, bool value) => await SaveString(key, value ? "1" : "0");
}
