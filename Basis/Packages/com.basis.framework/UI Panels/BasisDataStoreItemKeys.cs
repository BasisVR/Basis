using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace Basis.Scripts.UI.UI_Panels
{
    /// <summary>
    /// Separate keystore for ITEMS (so items don’t collide with avatar keys).
    /// Writes to: Application.persistentDataPath/ItemKeyStore.json
    /// </summary>
    public static class BasisDataStoreItemKeys
    {
        [System.Serializable]
        public struct PinnedSettings
        {
            public bool IsPinned;
            public BundledContentHolder.NetworkType NetworkType;
            public bool IsEphemeral;

            public static PinnedSettings Default => new PinnedSettings
            {
                IsPinned = false, // default for every item is not to be pinned
                NetworkType = BundledContentHolder.NetworkType.Local, // default network type for all objects if they are to be pinned should be local
                IsEphemeral = true // and default for any spawned objects should not persistent for late joiners
            };

            public static PinnedSettings Embedded => new PinnedSettings // default settings for embedded items
            {
                IsPinned = true,
                NetworkType = BundledContentHolder.NetworkType.Local,
                IsEphemeral = true
            };
        }

        [System.Serializable]
        public enum EmbeddedSource
        {
            BEEUrl,
            Addressable
        }

        [System.Serializable]
        public struct EmbeddedSettings
        {
            public bool IsEmbedded;
            public EmbeddedSource SourceType;

            public static EmbeddedSettings Default => new EmbeddedSettings
            {
                IsEmbedded = false,
                SourceType = EmbeddedSource.BEEUrl
            };

            public static EmbeddedSettings Addressable => new EmbeddedSettings
            {
                IsEmbedded = true,
                SourceType = EmbeddedSource.Addressable
            };

            public static EmbeddedSettings BEEUrl => new EmbeddedSettings
            {
                IsEmbedded = true,
                SourceType = EmbeddedSource.BEEUrl
            };
        }

        [System.Serializable]
        public class ItemKey
        {
            public BundledContentHolder.Mode Mode;
            public BundledContentHolder.PlacementType PlacementType;
            public string Url;
            public string Pass;
            public EmbeddedSettings EmbeddedSettings = EmbeddedSettings.Default;
            public PinnedSettings PinnedSettings = PinnedSettings.Default; // contains the pinned settings of an item key if it was pinned
        }


        [System.Serializable]
        public class ItemKeys
        {
            [SerializeField]
            public ItemKey[] Data;
        }

        public static string FilePath = Path.Combine(Application.persistentDataPath, "ItemKeyStore.json");

        [SerializeField]
        private static ItemKeys keys = new ItemKeys { Data = System.Array.Empty<ItemKey>() };

        public static async Task AddNewKey(ItemKey newKey)
        {
            EnsureInit();

            if (!ContainsKey(newKey))
            {
                int oldLen = keys.Data.Length;
                System.Array.Resize(ref keys.Data, oldLen + 1);
                keys.Data[oldLen] = newKey;

                await SaveKeysToFile();
                BasisDebug.Log($"Item key added: {newKey.Url}");
            }
        }

        public static async Task RemoveKey(ItemKey keyToRemove)
        {
            EnsureInit();

            int index = IndexOfKey(keyToRemove);
            if (index < 0)
            {
                BasisDebug.Log("Item key not found.");
                return;
            }

            int oldLen = keys.Data.Length;
            var newArr = new ItemKey[oldLen - 1];

            if (index > 0)
                System.Array.Copy(keys.Data, 0, newArr, 0, index);

            if (index < oldLen - 1)
                System.Array.Copy(keys.Data, index + 1, newArr, index, oldLen - index - 1);

            keys.Data = newArr;

            await SaveKeysToFile();
            BasisDebug.Log($"Item key removed: {keyToRemove.Url}");
        }

        /// <summary>
        /// Will adjust the pinned boolean and save
        /// </summary>
        public static async Task<bool> UpdatePinnedSettings(ItemKey key, PinnedSettings updatedPinnedSettings)
        {
            EnsureInit();

            int index = IndexOfKey(key);
            if (index < 0)
                return false;

            keys.Data[index].PinnedSettings = updatedPinnedSettings;

            await SaveKeysToFile();
            return true;
        }

        public static async Task LoadKeys()
        {
            BasisDebug.Log($"Loading Item keys from file at path: {FilePath}");

            EnsureInit();

            if (!File.Exists(FilePath))
            {
                BasisDebug.Log("No Item key file found. Starting fresh.");
                keys.Data = System.Array.Empty<ItemKey>();
                return;
            }

            try
            {
                byte[] byteData = await File.ReadAllBytesAsync(FilePath);

                // Deserialize the container (which contains a ItemKey[]).
                keys = BasisSerialization.DeserializeValue<ItemKeys>(byteData);

                keys ??= new ItemKeys();

                keys.Data ??= System.Array.Empty<ItemKey>();

                ValidateEmbeddedKeys();

                BasisDebug.Log("Item keys loaded successfully. Count: " + keys.Data.Length);
            }
            catch (System.Exception e)
            {
                BasisDebug.LogError($"Failed to load Item keys: {e.Message}");
                keys = new ItemKeys { Data = System.Array.Empty<ItemKey>() };
            }
        }

        private static async Task SaveKeysToFile()
        {
            EnsureInit();

            try
            {
                byte[] byteData = BasisSerialization.SerializeValue(keys);
                await File.WriteAllBytesAsync(FilePath, byteData);

                BasisDebug.Log($"Item keys saved to file at: {FilePath}");
            }
            catch (System.Exception e)
            {
                BasisDebug.LogError($"Failed to save Item keys: {e.Message}");
            }
        }
        public static ItemKey[] DisplayKeys()
        {
            EnsureInit();
            return keys.Data;
        }
        private static void EnsureInit()
        {
            keys ??= new ItemKeys();

            keys.Data ??= System.Array.Empty<ItemKey>();
        }
        private static bool ContainsKey(ItemKey k) => IndexOfKey(k) >= 0;
        private static int IndexOfKey(ItemKey k)
        {
            if (k == null)
            {
                return -1;
            }

            for (int i = 0; i < keys.Data.Length; i++)
            {
                var cur = keys.Data[i];
                // Embedded items: match by Url only
                if (k.EmbeddedSettings.IsEmbedded && cur.EmbeddedSettings.IsEmbedded)
                {
                    if (cur.Url == k.Url)
                        return i;
                }
                else
                {
                    // Normal items: match by Url + Pass
                    if (cur.Url == k.Url && cur.Pass == k.Pass)
                        return i;
                }
            }
            return -1;
        }

        // Remove embedded items that are NOT hardcoded
        private static void ValidateEmbeddedKeys()
        {
            var hardcoded = BasisUI.EmbeddedItems.HardcodedKeys;
            var filtered = new List<ItemKey>();

            foreach (var key in keys.Data)
            {
                if (key == null)
                    continue;

                if (!key.EmbeddedSettings.IsEmbedded)
                {
                    filtered.Add(key);
                    continue;
                }

                // If embedded, check if it exists in hardcoded list
                bool existsInHardcoded = false;

                foreach (var item in hardcoded)
                {
                    if (item.Url == key.Url)
                    {
                        existsInHardcoded = true;
                        break;
                    }
                }

                if (existsInHardcoded)
                    filtered.Add(key);
            }

            keys.Data = filtered.ToArray();

            // Ensure all hardcoded embedded keys exist
            foreach (var item in hardcoded)
            {
                if (IndexOfKey(item) < 0)
                {
                    var copy = new ItemKey
                    {
                        Mode = item.Mode,
                        PlacementType = item.PlacementType,
                        Url = item.Url,
                        Pass = item.Pass,
                        EmbeddedSettings = item.EmbeddedSettings,
                        PinnedSettings = PinnedSettings.Default
                    };

                    int oldLen = keys.Data.Length;
                    System.Array.Resize(ref keys.Data, oldLen + 1);
                    keys.Data[oldLen] = copy;
                }
            }
        }
    }
}
