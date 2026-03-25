using UnityEngine;
using UnityEngine.SceneManagement;
using System.IO;
using System.Collections.Generic;
using System;
using System.Globalization;
using System.Linq;
[Serializable]
public class KeyValue
{
    public string key;
    public string value;
}

[Serializable]
public class SettingsData
{
    //  public string version;
    [SerializeField]
    public List<KeyValue> settingsList = new List<KeyValue>();

    [NonSerialized]
    public Dictionary<string, string> settings = new Dictionary<string, string>();

    public SettingsData()
    {
        settings = new Dictionary<string, string>();
        settingsList = new List<KeyValue>();
    }

    public void RebuildDictionary()
    {
        settings.Clear();
        for (int Index = 0; Index < settingsList.Count; Index++)
        {
            KeyValue kv = settingsList[Index];
            if (kv == null)
            {
                continue;
            }

            settings[kv.key] = kv.value;
        }
    }

    public void RebuildList()
    {
        settingsList.Clear();
        foreach (var pair in settings)
        {
            settingsList.Add(new KeyValue
            {
                key = pair.Key,
                value = pair.Value
            });
        }
    }
}

public static class BasisSettingsSystem
{
    public const string SettingsJson = "settingsConfig.json";
    private static readonly string filePath = Path.Combine(Application.persistentDataPath, SettingsJson);
    // private static readonly string currentVersion = "2.0.5";
    private static SettingsData settingsData = new SettingsData();

    /// <summary>
    /// UniqueName, OptionValue
    /// </summary>
    public static event Action<string, string> OnSettingChanged;
    public static event Action OnSettingsFinishedChanges;
    public static void Initalize()
    {
        BasisSettingsSystem.LoadAllSettings();
        SceneManager.sceneLoaded += OnSceneLoaded;

    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (settingsData.settings == null || settingsData.settings.Count == 0)
        {
            BasisDebug.LogError("Loading Scene Before Settings Exist!");
        }

        var settings = settingsData.settings;
        if (settings != null)
        {
            KeyValuePair<string, string>[] array = settings.ToArray();
            foreach (KeyValuePair<string, string> kv in array)
            {
                OnSettingChanged?.Invoke(kv.Key, kv.Value);
            }
        }

        OnSettingsFinishedChanges?.Invoke();
        ForceQualityRefresh();
    }
    /// <summary>
    /// this forces unity to wake up for graphics changes.
    /// </summary>
    public static void ForceQualityRefresh()
    {
        QualitySettings.SetQualityLevel(QualitySettings.GetQualityLevel(), true);
    }
    public static bool HasSaveData(string uniqueSettingsName)
    {
        return settingsData.settings.TryGetValue(uniqueSettingsName, out var existing);
    }
    public static void SaveString(string uniqueSettingsName, string value)
    {
        bool changed = false;

        if (settingsData.settings.TryGetValue(uniqueSettingsName, out var existing))
        {
            // existing is already normalized
            if (existing != value)
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

        if (changed)
        {
            SaveAllSettings();
            OnSettingChanged?.Invoke(uniqueSettingsName, value);
            OnSettingsFinishedChanges?.Invoke();
            ForceQualityRefresh();
        }
    }

    public static string LoadString(string uniqueSettingsName, string defaultValue)
    {

        if (settingsData.settings.TryGetValue(uniqueSettingsName, out string value))
        {
            // value should already be normalized, but normalize anyway for safety
            return value;
        }

        // Store default so future loads see the key (normalized)
        settingsData.settings[uniqueSettingsName] = defaultValue;
        SaveAllSettings();
        return defaultValue;
    }

    public static void LoadAllSettings()
    {
        // Default blank (will fill from file or remain empty)
        settingsData.RebuildDictionary();

        if (!File.Exists(filePath))
        {
            // First run: no file yet. Just create an empty file at current version.
            BasisDebug.LogError("Settings file not found, creating new settings file.");
            //create the file and then just load it once done
            SaveAllSettings();
        }

        string json = null;
        SettingsData loaded = null;

        try
        {
            json = File.ReadAllText(filePath);
            loaded = JsonUtility.FromJson<SettingsData>(json);
        }
        catch (Exception e)
        {
            BasisDebug.LogError($"Failed to read/parse settings file. Creating a fresh one. Exception: {e}");
            // If parsing failed, we fall through to writing a fresh file.
        }

        if (loaded == null)
        {
            // Corrupt or unreadable file. OPTIONAL: backup the bad file for debugging.
            try
            {
                string backupPath = filePath + ".corrupt_backup";
                File.Copy(filePath, backupPath, true);
            }
            catch
            {

            }

            BasisDebug.LogError("Settings file corrupt/unreadable. Rebuilding empty settings.");
            settingsData = new SettingsData { };// version = currentVersion
            settingsData.RebuildDictionary();

            SaveAllSettings();
            OnSettingsFinishedChanges?.Invoke();
            ForceQualityRefresh();
            return;
        }

        // Rebuild dictionary WITH normalization
        loaded.RebuildDictionary();
        // Assign and bump version
        settingsData = loaded;
        var settings = settingsData.settings;
        KeyValuePair<string, string>[] array = settings.ToArray();
        foreach (KeyValuePair<string, string> kv in array)
        {
            OnSettingChanged?.Invoke(kv.Key, kv.Value);
        }

        OnSettingsFinishedChanges?.Invoke();
        // Persist rewritten version + normalized list/dict
        SaveAllSettings();
        ForceQualityRefresh();
    }

    public static void SaveAllSettings()
    {
        // Hard-normalize entire dictionary before writing (belt + suspenders)
        var normalized = new Dictionary<string, string>();
        foreach (var pair in settingsData.settings)
        {
            string k = pair.Key;
            if (string.IsNullOrEmpty(k))
            {
                continue;
            }

            string v = pair.Value;
            normalized[k] = v; // latest wins
        }
        settingsData.settings = normalized;

        //  settingsData.version = currentVersion;
        settingsData.RebuildList();

        string json = JsonUtility.ToJson(settingsData, true);

        string dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(filePath, json);
    }

    public static int LoadInt(string key, int defaultValue)
    {
        // default is numeric, ToLowerInvariant doesn't change it
        string val = LoadString(key, defaultValue.ToString(CultureInfo.InvariantCulture));
        if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result))
        {
            return result;
        }
        else
        {
            return defaultValue;
        }
    }

    public static float LoadFloat(string key, float defaultValue)
    {
        string val = LoadString(key, defaultValue.ToString(CultureInfo.InvariantCulture));
        if (float.TryParse(val, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out float result))
        {
            return (float)result;
        }
        else
        {
            return (float)defaultValue;
        }
    }
    public static bool LoadBool(string key, bool defaultValue)
    {
        // stored as "true"/"false" (lowercase) always
        return LoadString(key, defaultValue ? "true" : "false") == "true";
    }
    public static void SaveInt(string key, int value) => SaveString(key, value.ToString(CultureInfo.InvariantCulture));

    public static void SaveFloat(string key, float value) => SaveString(key, value.ToString(CultureInfo.InvariantCulture));

    public static void SaveBool(string key, bool value) => SaveString(key, value ? "true" : "false");
}
