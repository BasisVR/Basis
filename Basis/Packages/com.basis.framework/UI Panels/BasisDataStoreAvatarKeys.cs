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

        [System.Serializable]
        public class AvatarKeys
        {
            [SerializeField]
            public AvatarKey[] Data;
        }

        public static string FilePath = Path.Combine(Application.persistentDataPath, "KeyStore.json");

        [SerializeField]
        private static AvatarKeys keys = new AvatarKeys { Data = System.Array.Empty<AvatarKey>() };

        public static async Task AddNewKey(AvatarKey newKey)
        {
            EnsureInit();

            if (!ContainsKey(newKey))
            {
                int oldLen = keys.Data.Length;
                System.Array.Resize(ref keys.Data, oldLen + 1);
                keys.Data[oldLen] = newKey;

                await SaveKeysToFile();
                BasisDebug.Log($"Key added: {newKey.Url}");
            }
        }

        public static async Task RemoveKey(AvatarKey keyToRemove)
        {
            EnsureInit();

            int index = IndexOfKey(keyToRemove);
            if (index < 0)
            {
                BasisDebug.Log("Key not found.");
                return;
            }

            // Create a new array one smaller and copy everything except the removed index.
            int oldLen = keys.Data.Length;
            var newArr = new AvatarKey[oldLen - 1];

            if (index > 0)
            {
                System.Array.Copy(keys.Data, 0, newArr, 0, index);
            }

            if (index < oldLen - 1)
            {
                System.Array.Copy(keys.Data, index + 1, newArr, index, oldLen - index - 1);
            }

            keys.Data = newArr;

            await SaveKeysToFile();
            BasisDebug.Log($"Key removed: {keyToRemove.Url}");
        }

        public static async Task LoadKeys()
        {
            BasisDebug.Log($"Loading keys from file at path: {FilePath}");

            EnsureInit();

            if (!File.Exists(FilePath))
            {
                BasisDebug.Log("No key file found. Starting fresh.");
                keys.Data = System.Array.Empty<AvatarKey>();
                return;
            }

            try
            {
                byte[] byteData = await File.ReadAllBytesAsync(FilePath);

                // If you actually serialized AvatarKeys before, use this:
                keys = BasisSerialization.DeserializeValue<AvatarKeys>(byteData);

                // Safety: never allow null Data.
                if (keys == null)
                    keys = new AvatarKeys();

                if (keys.Data == null)
                    keys.Data = System.Array.Empty<AvatarKey>();

                BasisDebug.Log("Keys loaded successfully. Count: " + keys.Data.Length);
            }
            catch (System.Exception e)
            {
                BasisDebug.LogError($"Failed to load keys: {e.Message}");
                keys = new AvatarKeys { Data = System.Array.Empty<AvatarKey>() };
            }
        }

        private static async Task SaveKeysToFile()
        {
            EnsureInit();

            try
            {
                // Serialize the container (which contains an AvatarKey[]).
                byte[] byteData = BasisSerialization.SerializeValue(keys);
                await File.WriteAllBytesAsync(FilePath, byteData);

                BasisDebug.Log($"Keys saved to file at: {FilePath}");
            }
            catch (System.Exception e)
            {
                BasisDebug.LogError($"Failed to save keys: {e.Message}");
            }
        }

        public static AvatarKey[] DisplayKeys()
        {
            EnsureInit();
            return keys.Data;
        }

        // ---------- Helpers (array-only) ----------

        private static void EnsureInit()
        {
            if (keys == null)
                keys = new AvatarKeys();

            if (keys.Data == null)
                keys.Data = System.Array.Empty<AvatarKey>();
        }

        private static bool ContainsKey(AvatarKey k) => IndexOfKey(k) >= 0;

        private static int IndexOfKey(AvatarKey k)
        {
            if (k == null) return -1;

            // Compare by Url+Pass (value equality) rather than reference equality.
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
