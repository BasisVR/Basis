using UnityEngine;
using UnityEngine.SceneManagement;
using System.IO;
using System.Collections.Generic;
using System;
using System.Globalization;
using System.Linq;

namespace Basis.Scripts.Settings
{
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
    private static string _filePath;
    private static string FilePath => _filePath ??= Path.Combine(Application.persistentDataPath, SettingsJson);
    // private static readonly string currentVersion = "2.0.5";
    private static SettingsData settingsData = new SettingsData();
    private static bool _settingsLoaded = false;
    /// <summary>
    /// True once LoadAllSettings has read the settings file. Bindings constructed earlier (static
    /// init in a RuntimeInitializeOnLoadMethod runs before Initialize, which is called from a
    /// Start hook) only saw defaults and must re-load after the store is populated.
    /// </summary>
    public static bool SettingsLoaded => _settingsLoaded;
    private static bool _freshSettingsFile;
    /// <summary>
    /// True when no settings file existed on disk at load — the app has never run on this
    /// machine before. A corrupt-but-present file does not count as fresh.
    /// </summary>
    public static bool FreshSettingsFile => _freshSettingsFile;

    /// <summary>
    /// UniqueName, OptionValue
    /// </summary>
    public static event Action<string, string> OnSettingChanged;
    public static event Action OnSettingsFinishedChanges;

    private static int _batchDepth;
    private static bool _batchSavePending;
    private static bool _batchFinishPending;

    /// <summary>
    /// Coalesces the per-write tail of <see cref="SaveString"/> — the full-file save, the
    /// <see cref="OnSettingsFinishedChanges"/> broadcast and <see cref="ForceQualityRefresh"/> —
    /// across a burst of writes, so a caller that changes fifty settings at once pays for that
    /// tail once instead of fifty times.
    ///
    /// <para>Per-key <see cref="OnSettingChanged"/> still fires inline, in write order, so every
    /// module applies its value exactly when it did before. Only the once-per-change tail moves
    /// to the end of the burst, which is where it always belonged: the save writes the whole
    /// dictionary anyway, the finished-changes broadcast is a "push current state" pass, and
    /// <c>SetQualityLevel(level, applyExpensiveChanges: true)</c> re-uploads every texture mip.
    /// Running that chain per setting is what made a Performance Mode level change a
    /// multi-second main-thread stall — long enough for the XR compositor to drop the app.</para>
    ///
    /// <para>Nesting is counted, so a batch inside a batch flushes once at the outermost exit.
    /// Always pair with <see cref="EndBatch"/> in a <c>finally</c>, or use <see cref="Batch"/>.</para>
    /// </summary>
    public static void BeginBatch()
    {
        _batchDepth++;
    }

    /// <summary>Closes a <see cref="BeginBatch"/> scope, flushing the deferred tail at depth zero.</summary>
    public static void EndBatch()
    {
        if (_batchDepth == 0)
        {
            return;
        }

        _batchDepth--;
        if (_batchDepth != 0)
        {
            return;
        }

        bool save = _batchSavePending;
        bool finish = _batchFinishPending;
        _batchSavePending = false;
        _batchFinishPending = false;

        if (save)
        {
            SaveAllSettings();
        }
        if (finish)
        {
            OnSettingsFinishedChanges?.Invoke();
            ForceQualityRefresh();
        }
    }

    /// <summary><c>using (BasisSettingsSystem.Batch()) { ... }</c> form of <see cref="BeginBatch"/>.</summary>
    public static BatchScope Batch()
    {
        BeginBatch();
        return new BatchScope(_batchDepth);
    }

    public readonly struct BatchScope : IDisposable
    {
        // Depth this scope opened, always at least 1. A default(BatchScope) never opened one and
        // carries 0, so disposing it can't close a batch it does not own.
        private readonly int _depth;

        internal BatchScope(int depth)
        {
            _depth = depth;
        }

        public void Dispose()
        {
            if (_depth != 0)
            {
                EndBatch();
            }
        }
    }

    public static void Initialize()
    {
        BasisSettingsSystem.LoadAllSettings();
        SceneManager.sceneLoaded += OnSceneLoaded;
        Application.quitting += FlushPendingSaves;
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
    /// <summary>
    /// Re-fires <see cref="OnSettingsFinishedChanges"/> so subscribers that read
    /// <c>BasisSettingsBinding.RawValue</c> can apply the loaded values. The first
    /// firing inside <see cref="LoadAllSettings"/> happens before
    /// <see cref="Basis.BasisUI.BasisSettingsDefaults.LoadAll"/> refreshes binding
    /// RawValues from the dictionary, so subscribers there see static-init defaults.
    /// Call this after LoadAll to re-notify with correct RawValues.
    /// </summary>
    public static void NotifyFinishedChanges()
    {
        OnSettingsFinishedChanges?.Invoke();
        ForceQualityRefresh();
    }
    /// <summary>Re-applies current settings to live targets without forcing a quality refresh.</summary>
    public static void ReapplySettings()
    {
        OnSettingsFinishedChanges?.Invoke();
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

        // Saving before LoadAllSettings has read the file would clobber user-saved
        // values with the static-init binding defaults sitting in the dict. Update
        // the in-memory dict so reads stay consistent, but defer disk + events
        // until Initialize has run; LoadAllSettings will repopulate the dict from
        // disk anyway. This also means a premature change is dropped on the floor,
        // which is the right thing — callers (e.g. BasisLocalization auto-detect)
        // must not race the load.
        if (changed && _settingsLoaded)
        {
            if (_batchDepth > 0)
            {
                // The value is already in the dictionary, so the deferred save will carry it.
                // The per-key notify still goes out now: modules apply in write order, and some
                // of them depend on it (the quality level re-clamps shadows and HDR behind it).
                _batchSavePending = true;
                _batchFinishPending = true;
                OnSettingChanged?.Invoke(uniqueSettingsName, value);
            }
            else
            {
                SaveAllSettings();
                OnSettingChanged?.Invoke(uniqueSettingsName, value);
                OnSettingsFinishedChanges?.Invoke();
                ForceQualityRefresh();
            }
        }
    }

    public static void SaveStringQuiet(string uniqueSettingsName, string value)
    {
        bool changed = false;

        if (settingsData.settings.TryGetValue(uniqueSettingsName, out var existing))
        {
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

        if (changed && _settingsLoaded)
        {
            if (_batchDepth > 0)
            {
                _batchSavePending = true;
            }
            else
            {
                SaveAllSettings();
            }
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
        if (_settingsLoaded)
        {
            if (_batchDepth > 0)
            {
                _batchSavePending = true;
            }
            else
            {
                SaveAllSettings();
            }
        }
        return defaultValue;
    }

    public static void LoadAllSettings()
    {
        // Default blank (will fill from file or remain empty)
        settingsData.RebuildDictionary();

        _freshSettingsFile = !File.Exists(FilePath);
        if (_freshSettingsFile)
        {
            // First run: no file yet. Just create an empty file at current version.
            BasisDebug.LogError("Settings file not found, creating new settings file.");
            _settingsLoaded = true;
            SaveAllSettings();
            OnSettingsFinishedChanges?.Invoke();
            ForceQualityRefresh();
            return;
        }

        string json = null;
        SettingsData loaded = null;

        try
        {
            json = File.ReadAllText(FilePath);
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
                string backupPath = FilePath + ".corrupt_backup";
                File.Copy(FilePath, backupPath, true);
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"Failed to backup corrupt settings file: {e}");
            }

            BasisDebug.LogError("Settings file corrupt/unreadable. Rebuilding empty settings.");
            settingsData = new SettingsData { };// version = currentVersion
            settingsData.RebuildDictionary();
            _settingsLoaded = true;
            SaveAllSettings();
            OnSettingsFinishedChanges?.Invoke();
            ForceQualityRefresh();
            return;
        }

        // Rebuild dictionary WITH normalization
        loaded.RebuildDictionary();
        // Assign and bump version
        settingsData = loaded;
        _settingsLoaded = true;
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

    private static readonly object _saveLock = new object();
    private static string _pendingSaveJson;
    private static bool _saveInFlight;

    public static void SaveAllSettings()
    {
        try
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

            string dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            QueueSave(json);
        }
        catch (Exception e)
        {
            BasisDebug.LogError($"Failed to save settings to {FilePath}: {e}");
        }
    }

    // Latest-wins snapshot handed to a single-flight background writer: a burst of saves costs
    // one serialize each but at most one disk write is ever queued behind the one in progress.
    private static void QueueSave(string json)
    {
        lock (_saveLock)
        {
            _pendingSaveJson = json;
            if (_saveInFlight)
            {
                return;
            }
            _saveInFlight = true;
        }
        System.Threading.Tasks.Task.Run(SaveWorker);
    }

    private static void SaveWorker()
    {
        while (true)
        {
            string json;
            lock (_saveLock)
            {
                json = _pendingSaveJson;
                _pendingSaveJson = null;
                if (json == null)
                {
                    _saveInFlight = false;
                    return;
                }
            }
            try
            {
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(FilePath))
                {
                    File.Replace(tmp, FilePath, null);
                }
                else
                {
                    File.Move(tmp, FilePath);
                }
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"Failed to save settings to {FilePath}: {e}");
            }
        }
    }

    /// <summary>Blocks until any queued settings write has landed. Wired to Application.quitting.</summary>
    public static void FlushPendingSaves()
    {
        for (int i = 0; i < 200; i++)
        {
            lock (_saveLock)
            {
                if (!_saveInFlight && _pendingSaveJson == null)
                {
                    return;
                }
            }
            System.Threading.Thread.Sleep(5);
        }
        BasisDebug.LogError("Timed out waiting for the settings file write to finish on quit.");
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
}
