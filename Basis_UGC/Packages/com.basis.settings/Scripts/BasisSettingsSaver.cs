using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class BasisSettingsSaver
{
    private const string FileName = "BasisSettings.json";
    private static readonly string FilePath = Path.Combine(Application.persistentDataPath, FileName);

    public static BasisSettingsData SettingsData = new BasisSettingsData();
    // Events
    public static event Action OnDataSaved;
    public static event Action OnDataLoaded;
    public static event Action<BasisDataItem> OnOptionLoaded;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    public static async void Initialize()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
        await LoadDataAsync();
        ApplyChanges();
    }
    /// <summary>
    /// when a scene is loaded we call this from the delegate
    /// </summary>
    /// <param name="arg0"></param>
    /// <param name="arg1"></param>
    private static void OnSceneLoaded(Scene arg0, LoadSceneMode arg1)
    {
        //apply data
        ApplyChanges();
    }
    /// <summary>
    /// save to json on disc
    /// </summary>
    /// <returns></returns>
    public static async Task SaveDataAsync()
    {
        try
        {
            // Convert dictionary back to list for serialization
            var itemsList = new List<BasisDataItem>(SettingsData.itemsDictionary.Values);
            SettingsData.itemsDictionary.Clear();
            foreach (var item in itemsList)
            {
                SettingsData.itemsDictionary.Add(item.identifier, item);
            }

            string json = JsonUtility.ToJson(SettingsData, true);
            await File.WriteAllTextAsync(FilePath, json);
            BasisDebug.Log($"Data successfully saved to {FilePath}");

            OnDataSaved?.Invoke();
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"Error saving data: {ex.Message}");
        }
    }
    /// <summary>
    /// load json data on disc
    /// </summary>
    /// <returns></returns>
    public static async Task LoadDataAsync()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = await File.ReadAllTextAsync(FilePath);
                SettingsData = JsonUtility.FromJson<BasisSettingsData>(json);
                BasisDebug.Log($"Data successfully loaded from {FilePath}");

                OnDataLoaded?.Invoke();
            }
            else
            {
                BasisDebug.Log("No save file found. Generating default data.");
                await SaveDataAsync(); // Save the default data if file doesn't exist
            }
        }
        catch (Exception ex)
        {

            BasisDebug.LogError($"Error loading data: {ex.Message}");
        }
    }
    /// <summary>
    /// at runtime setup all the options
    /// </summary>
    public static void ApplyChanges()
    {
        // Invoke loading events concurrently
        foreach (BasisDataItem item in SettingsData.itemsDictionary.Values)
        {
            OnOptionLoaded?.Invoke(item);
        }
    }
}
