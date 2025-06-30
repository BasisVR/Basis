using BasisSerializer.OdinSerializer;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace Basis.Scripts.UI.UI_Panels
{
    public static class BasisDataStoreAvatarKeys
    {
        [System.Serializable]
        public class AvatarKey
        {
            public string Url;
            public string Pass;
        }
        public static string FilePath = Path.Combine(Application.persistentDataPath, "KeyStore.json");

        [SerializeField]
        private static List<AvatarKey> keys = new List<AvatarKey>();

        public static async Task AddNewKey(AvatarKey newKey)
        {
            if (keys.Contains(newKey) == false)
            {
                keys.Add(newKey);
                await SaveKeysToFile();
                BasisDebug.Log($"Key added: {newKey.Url}");
            }
        }

        public static async Task RemoveKey(AvatarKey keyToRemove)
        {
            var key = keys.Find(k => k.Url == keyToRemove.Url && k.Pass == keyToRemove.Pass);
            if (key != null)
            {
                keys.Remove(key);
                await SaveKeysToFile();
                BasisDebug.Log($"Key removed: {keyToRemove.Url}");
            }
            else
            {
                BasisDebug.Log("Key not found.");
            }
        }

        public static async Task LoadKeys()
        {
            BasisDebug.Log($"Loading keys from file at path: {FilePath}");
            if (File.Exists(FilePath))
            {
                try
                {
                    byte[] byteData = await File.ReadAllBytesAsync(FilePath);
                    keys = SerializationUtility.DeserializeValue<List<AvatarKey>>(byteData, DataFormat.Binary);
                    BasisDebug.Log("Keys loaded successfully. Count: " + keys.Count);
                }
                catch (System.Exception e)
                {
                    BasisDebug.LogError($"Failed to load keys: {e.Message}");
                }
            }
            else
            {
                BasisDebug.Log("No key file found. Starting fresh.");
            }
        }

        private static async Task SaveKeysToFile()
        {
            try
            {
                byte[] byteData = SerializationUtility.SerializeValue<List<AvatarKey>>(keys, DataFormat.Binary);
                await File.WriteAllBytesAsync(FilePath, byteData);
                BasisDebug.Log($"Keys saved to file at: {FilePath}");
            }
            catch (System.Exception e)
            {
                BasisDebug.LogError($"Failed to save keys: {e.Message}");
            }
        }

        public static List<AvatarKey> DisplayKeys()
        {
            return keys;
        }
    }
}
