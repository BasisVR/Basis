using BattlePhaze.SaveSystem;
using BattlePhaze.SettingsManager.DebugSystem;
using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace BattlePhaze.SettingsManager
{
    public class SMSaveJson : SMSaveModuleBase
    {
        private string FileExtension = ".json";

        public string GetCurrentFilePath(SettingsManager Manager)
        {
            return Path.Combine(Application.persistentDataPath, Manager.ManagerSettings.FileName + FileExtension);
        }

        public override bool Load(SettingsManager Manager, SettingsManagerSaveSystem Save)
        {
            string filePath = GetCurrentFilePath(Manager);
            if (File.Exists(filePath))
            {
                try
                {
                    using (var reader = new StreamReader(filePath))
                    {
                        Save.Clear();
                        string jsonData = reader.ReadToEnd();

                        if (string.IsNullOrEmpty(jsonData))
                        {
                            BasisDebug.LogError("File is empty. Deleting corrupted file.");
                            Delete(Manager, Save);
                            return false;
                        }

                        OptionMappings Mappings = JsonUtility.FromJson<OptionMappings>(jsonData);
                        if (Mappings.Information == null)
                        {
                            BasisDebug.LogError("Invalid JSON format. Deleting corrupted file.");
                            Delete(Manager, Save);
                            return false;
                        }

                        foreach (var mapping in Mappings.Information)
                        {
                            Save.Set(mapping.key, mapping.value, mapping.comment);
                        }
                    }
                }
                catch (Exception e)
                {
                    BasisDebug.LogError($"Failed to read file: {filePath}\n{e.Message}");
                    BasisDebug.Log(e.StackTrace);
                    Delete(Manager, Save);
                    return false;
                }
            }
            else
            {
                BasisDebug.LogWarning("File does not exist.");
                return false;
            }
            return true;
        }

        public override string ModuleName()
        {
            return "JSON";
        }

        [System.Serializable]
        public struct OptionMappings
        {
            [SerializeField]
            public SMOptionInformation[] Information;
        }

        public override bool Save(SettingsManager Manager, SettingsManagerSaveSystem Save)
        {
            string filePath = GetCurrentFilePath(Manager);
            Encoding encoding = Encoding.UTF8;
            BasisDebug.Log("Saving Data JSON");
            try
            {
                OptionMappings mappings = new OptionMappings
                {
                    Information = Save.OptionMapping.Values.ToArray()
                };

                string jsonData = JsonUtility.ToJson(mappings, true);
                using (StreamWriter writer = new StreamWriter(filePath, false, encoding))
                {
                    writer.Write(jsonData);
                }
                BasisDebug.Log("Save completed successfully.");
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"Error saving file: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
            return true;
        }

        public override bool Delete(SettingsManager Manager, SettingsManagerSaveSystem Save)
        {
            string filePath = GetCurrentFilePath(Manager);
            if (File.Exists(filePath))
            {
                try
                {
                    File.Delete(filePath);
                    BasisDebug.Log("File deleted successfully: " + filePath);
                }
                catch (Exception ex)
                {
                    BasisDebug.LogError($"Failed to delete file: {filePath}\n{ex.Message}");
                    return false;
                }
            }
            return true;
        }

        public override string Location(SettingsManager Manager, SettingsManagerSaveSystem Save)
        {
            return GetCurrentFilePath(Manager);
        }

        public override SaveSystemType Type()
        {
            return SaveSystemType.Normal;
        }
    }
}
