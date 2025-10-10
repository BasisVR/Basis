using UnityEngine;
using UnityEngine.SceneManagement;
using System.IO;
using System.Collections.Generic;
using System;
using System.Globalization;

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
            if (!string.IsNullOrEmpty(kv?.key) && !settings.ContainsKey(kv.key))
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
    private static readonly string filePath = Path.Combine(Application.persistentDataPath, SettingsJson);
    private static readonly string currentVersion = Application.version;
    private static SettingsData settingsData = new SettingsData();

    // --- NEW: simple concurrency/reentrancy guards ---
    private static readonly object ioLock = new object();
    private static bool isLoading;
    private static bool isSaving;
    private static bool pendingSave; // request to save after a load finishes
    private static bool suppressEvents; // avoid recursive SaveString from listeners during load

    /// <summary>
    /// UniqueName,Optionvalue
    /// </summary>
    public static event Action<string, string> OnSettingChanged;

    static BasisSettingsSystem()
    {
        LoadAllSettings();
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        LoadAllSettings();
    }

    public static void SaveString(string uniqueSettingsName, string value)
    {
        bool changed = false;

        // In-memory update (no I/O yet)
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

        // If we're in the middle of a load, defer the actual save.
        if (changed)
        {
            if (isLoading)
            {
                // mark that we owe a save after load
                pendingSave = true;
            }
            else
            {
                SaveAllSettings();
            }

            // Only notify listeners if not in a load-broadcast phase
            if (!suppressEvents)
                OnSettingChanged?.Invoke(uniqueSettingsName, value);
        }
    }

    public static string LoadString(string uniqueSettingsName, string defaultValue = "")
    {
        if (settingsData.settings.TryGetValue(uniqueSettingsName, out string value))
            return value;

        // record default but don't force a save if we're currently loading
        settingsData.settings[uniqueSettingsName] = defaultValue;
        if (!isLoading) SaveAllSettings(); else pendingSave = true;

        return defaultValue;
    }

    public static void LoadAllSettings()
    {
        lock (ioLock)
        {
            if (isLoading) return;   // already loading
            isLoading = true;
            suppressEvents = true;   // avoid SaveString from listeners reentering

            try
            {
                if (!File.Exists(filePath))
                {
                    BasisDebug.Log("Settings file not found, creating defaults.");
                    ResetToDefault_Internal(); // internal to avoid double locking
                }
                else
                {
                    string json = File.ReadAllText(filePath);
                    var loaded = JsonUtility.FromJson<SettingsData>(json);

                    if (loaded == null || loaded.version != currentVersion)
                    {
                        BasisDebug.LogWarning("Settings version mismatch or corrupt file. Resetting.");
                        ResetToDefault_Internal();
                    }
                    else
                    {
                        settingsData = loaded;
                        settingsData.RebuildDictionary();
                    }
                }

                // Notify listeners for all keys after load (outside of suppress once finished)
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"Failed to load settings: {e.Message}. Resetting.");
                ResetToDefault_Internal();
            }
            finally
            {
                suppressEvents = false;
                isLoading = false;
            }
        }

        // Fire notifications outside the lock (safe: read-only iteration on our dictionary snapshot)
        foreach (var kv in settingsData.settings)
            OnSettingChanged?.Invoke(kv.Key, kv.Value);

        // If anyone tried to save during the load, honor it now.
        if (pendingSave)
        {
            pendingSave = false;
            SaveAllSettings();
        }
    }

    public static void SaveAllSettings()
    {
        lock (ioLock)
        {
            if (isSaving) return;     // already saving
            if (isLoading)            // don't save while loading; defer
            {
                pendingSave = true;
                return;
            }

            isSaving = true;
            try
            {
                settingsData.version = currentVersion;
                settingsData.RebuildList();
                string json = JsonUtility.ToJson(settingsData, true);

                string dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(filePath, json);
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"Failed to save settings: {e.Message}");
            }
            finally
            {
                isSaving = false;
            }
        }
    }

    private static void ResetToDefault()
    {
        lock (ioLock)
        {
            ResetToDefault_Internal();
        }
        // After a reset, write immediately
        SaveAllSettings();
    }

    // Internal version assumes ioLock is already held (or we're in a controlled path)
    private static void ResetToDefault_Internal()
    {
        settingsData = new SettingsData { version = currentVersion };
        settingsData.RebuildDictionary();
    }

    public static void NukeSettings()
    {
        BasisDebug.Log("Nuking settings file and resetting to defaults.");
        ResetToDefault();
    }

    // -------------------------
    // Strongly-typed accessors
    // -------------------------

    public static int LoadInt(string key, int defaultValue = 0)
    {
        string val = LoadString(key, defaultValue.ToString(CultureInfo.InvariantCulture));
        return int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
            ? result
            : defaultValue;
    }

    public static float LoadFloat(string key, float defaultValue = 0f)
    {
        string val = LoadString(key, defaultValue.ToString(CultureInfo.InvariantCulture));
        return float.TryParse(val, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out float result)
            ? result
            : defaultValue;
    }

    public static bool LoadBool(string key, bool defaultValue = false)
    {
        string val = LoadString(key, defaultValue ? "1" : "0");
        return val == "1" || string.Equals(val, "true", StringComparison.OrdinalIgnoreCase);
    }

    public static void SetInt(string key, int value) => SaveString(key, value.ToString(CultureInfo.InvariantCulture));
    public static void SetFloat(string key, float value) => SaveString(key, value.ToString(CultureInfo.InvariantCulture));
    public static void SetBool(string key, bool value) => SaveString(key, value ? "1" : "0");
}
