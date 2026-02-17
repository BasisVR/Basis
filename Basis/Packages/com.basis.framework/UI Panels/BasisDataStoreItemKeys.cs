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
        public class ItemKey
        {
            public BundledContentHolder.Mode Mode;
            public string Url;
            public string Pass;
            public bool ISEmbedded = false;
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
                if (cur != null && cur.Url == k.Url && cur.Pass == k.Pass)
                {
                    return i;
                }
            }
            return -1;
        }
    }
}
