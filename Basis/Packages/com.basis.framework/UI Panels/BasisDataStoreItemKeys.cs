using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Basis.Scripts.UI.UI_Panels
{
    /// <summary>
    /// Separate keystore for ITEMS (so items don't collide with avatar keys).
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
                IsPinned = false,
                NetworkType = BundledContentHolder.NetworkType.Local,
                IsEphemeral = true
            };

            public static PinnedSettings Embedded => new PinnedSettings
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
            public PinnedSettings PinnedSettings = PinnedSettings.Default;
        }

        [System.Serializable]
        public class ItemKeys
        {
            public ItemKey[] Data;
        }

        // FIX: FilePath is now a lazy property instead of a static field initializer.
        // Application.persistentDataPath must be accessed on Unity's main thread after
        // engine init. A field initializer runs at class-load time which may be too early.
        private static string _filePath;
        public static string FilePath
        {
            get
            {
                if (string.IsNullOrEmpty(_filePath))
                    _filePath = Path.Combine(Application.persistentDataPath, "ItemKeyStore.json");
                return _filePath;
            }
        }

        private static ItemKeys keys = new ItemKeys { Data = System.Array.Empty<ItemKey>() };

        // FIX: Track whether LoadKeys has been called so mutation methods can guard
        // against overwriting disk state with an empty in-memory array.
        private static bool _loaded = false;

        // FIX: SemaphoreSlim(1,1) prevents concurrent async mutations from corrupting
        // the shared keys array or causing lost updates between read and SaveKeysToFile.
        private static readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

        public static async Task AddNewKey(ItemKey newKey)
        {
            // FIX: Ensure disk state is loaded before mutating, so we never silently
            // overwrite saved data with whatever happens to be in memory.
            await EnsureLoaded();

            await _lock.WaitAsync();
            try
            {
                if (!ContainsKey(newKey))
                {
                    int oldLen = keys.Data.Length;
                    System.Array.Resize(ref keys.Data, oldLen + 1);
                    keys.Data[oldLen] = newKey;

                    await SaveKeysToFile();
                    BasisDebug.Log($"Item key added: {newKey.Url}");
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        public static async Task RemoveKey(ItemKey keyToRemove)
        {
            await EnsureLoaded();

            await _lock.WaitAsync();
            try
            {
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
            finally
            {
                _lock.Release();
            }
        }

        public static async Task<bool> UpdatePinnedSettings(ItemKey key, PinnedSettings updatedPinnedSettings)
        {
            await EnsureLoaded();

            await _lock.WaitAsync();
            try
            {
                int index = IndexOfKey(key);
                if (index < 0)
                    return false;

                keys.Data[index].PinnedSettings = updatedPinnedSettings;

                await SaveKeysToFile();
                return true;
            }
            finally
            {
                _lock.Release();
            }
        }

        public static async Task LoadKeys()
        {
            BasisDebug.Log($"Loading Item keys from file at path: {FilePath}");

            await _lock.WaitAsync();
            try
            {
                await LoadKeysInternal();
                _loaded = true;
            }
            finally
            {
                _lock.Release();
            }
        }

        private static async Task WipeMemoryKeys_ValidateEmbedded_AndSave()
        {
            BasisDebug.LogWarning("Creating new json. old was bogo binted.");
            keys.Data = System.Array.Empty<ItemKey>();
            ValidateEmbeddedKeys(); // ensures hardcoded embedded stuff is there
            await SaveKeysToFile(); // overwrite the empty file with a valid one 
        }

        // FIX: Core load logic extracted so it can be called from EnsureLoaded without
        // re-acquiring the lock (which would deadlock since the lock is not re-entrant).
        private static async Task LoadKeysInternal()
        {
            EnsureInit();

            if (!File.Exists(FilePath))
            {
                BasisDebug.Log("No Item key file found. Starting fresh.");
                await WipeMemoryKeys_ValidateEmbedded_AndSave();
                return;
            }

            byte[] byteData;
            try
            {
                byteData = await File.ReadAllBytesAsync(FilePath);
            }
            catch (System.Exception e)
            {
                BasisDebug.LogError($"Failed to read Item key file: {e.Message}");
                //keys = new ItemKeys { Data = System.Array.Empty<ItemKey>() };
                await WipeMemoryKeys_ValidateEmbedded_AndSave();
                return;
            }

            // FIX: Guard against empty file. An empty byte array passed to the
            // deserializer will throw or silently return null, both of which would
            // destroy the existing in-memory state or misreport success.
            if (byteData == null || byteData.Length == 0)
            {
                BasisDebug.LogWarning("Item key file exists but is empty. Starting fresh.");
                //keys = new ItemKeys { Data = System.Array.Empty<ItemKey>() };
                await WipeMemoryKeys_ValidateEmbedded_AndSave();
                return;
            }

            try
            {
                // FIX: Deserialize into a local variable first so that a bad file never
                // partially overwrites the live keys object before we can validate it.
                ItemKeys loaded = BasisSerialization.DeserializeValue<ItemKeys>(byteData);

                // FIX: Explicit null check after deserialization. If the JSON is present
                // but malformed or schema-mismatched, the deserializer may return null
                // instead of throwing. Without this check we'd silently use a null ref.
                if (loaded == null)
                {
                    BasisDebug.LogWarning("Item key file deserialized to null (malformed JSON?). Starting fresh.");
                    //keys = new ItemKeys { Data = System.Array.Empty<ItemKey>() };
                    await WipeMemoryKeys_ValidateEmbedded_AndSave();
                    return;
                }

                // FIX: Guard missing Data array separately from a missing root object.
                // A valid JSON object with no "Data" field deserializes fine but leaves
                // Data null, which would crash every downstream caller.
                loaded.Data ??= System.Array.Empty<ItemKey>();

                // FIX: Scrub any null entries inside the array that could have been
                // written by a previous bug or a hand-edited file.
                loaded.Data = ScrubNullEntries(loaded.Data);

                // FIX: Also ensure each key has sane defaults for fields that may be
                // absent from older JSON (e.g. EmbeddedSettings / PinnedSettings added
                // after the file was first created). Without this, zero-init struct
                // values silently replace the intended defaults.
                foreach (var key in loaded.Data)
                {
                    if (key == null) continue;
                    EnsureKeyDefaults(key);
                }

                keys = loaded;

                ValidateEmbeddedKeys();

                BasisDebug.Log("Item keys loaded successfully. Count: " + keys.Data.Length);
            }
            catch (System.Exception e)
            {
                // FIX: Log the full exception (not just Message) so stack traces are
                // visible. The old code swallowed the type and location of the error.
                BasisDebug.LogError($"Failed to deserialize Item keys: {e}");
                //keys = new ItemKeys { Data = System.Array.Empty<ItemKey>() };
                await WipeMemoryKeys_ValidateEmbedded_AndSave();
            }
        }

        private static async Task SaveKeysToFile()
        {
            EnsureInit();

            // FIX: Write to a temp file first, then replace the real file atomically.
            // A crash or power loss mid-write previously produced a corrupt keystore
            // with no recovery path. Temp-swap ensures the old file survives until the
            // new one is fully flushed.
            string tempPath = FilePath + ".tmp";

            // FIX: Separate try/catch for serialization so a failure here never touches
            // the real file or the temp file.
            byte[] byteData;
            try
            {
                byteData = BasisSerialization.SerializeValue(keys);
            }
            catch (System.Exception e)
            {
                BasisDebug.LogError($"Failed to serialize Item keys: {e}");
                return;
            }

            // FIX: Validate that serialization produced non-empty output before
            // touching the real file. An empty serialize result would wipe the store.
            if (byteData == null || byteData.Length == 0)
            {
                BasisDebug.LogError("Serialization produced empty output. Aborting save to protect existing data.");
                return;
            }

            // FIX: Separate try/catch for the write so a failed write bails out before
            // we attempt to swap — previously a write failure would fall through to
            // File.Move on a non-existent tmp file, causing a FileNotFoundException.
            try
            {
                await File.WriteAllBytesAsync(tempPath, byteData);
            }
            catch (System.Exception e)
            {
                BasisDebug.LogError($"Failed to write temp file: {e}");
                return; // bail before touching the real file
            }

            // Temp file is confirmed written — now do the atomic swap.
            try
            {
                if (File.Exists(FilePath))
                    File.Replace(tempPath, FilePath, null); // atomic swap, no window where FilePath is absent
                else
                    File.Move(tempPath, FilePath); // first ever save, Replace would throw if destination missing

                BasisDebug.Log($"Item keys saved to file at: {FilePath}");
            }
            catch (System.Exception e)
            {
                BasisDebug.LogError($"Failed to replace Item key file: {e}");

                // Clean up orphaned temp file if the swap failed.
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch { /* best-effort cleanup */ }
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

        // FIX: EnsureLoaded guarantees disk state is in memory before any mutation.
        // Uses double-check pattern: fast path avoids lock overhead after first load.
        private static async Task EnsureLoaded()
        {
            if (_loaded) return;

            await _lock.WaitAsync();
            try
            {
                // Re-check inside lock in case another caller loaded between our check
                // and acquiring the lock.
                if (!_loaded)
                {
                    await LoadKeysInternal();
                    _loaded = true;
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        private static bool ContainsKey(ItemKey k) => IndexOfKey(k) >= 0;

        private static int IndexOfKey(ItemKey k)
        {
            if (k == null) return -1;

            for (int i = 0; i < keys.Data.Length; i++)
            {
                var cur = keys.Data[i];
                if (cur == null) continue;

                if (k.EmbeddedSettings.IsEmbedded && cur.EmbeddedSettings.IsEmbedded)
                {
                    if (cur.Url == k.Url)
                        return i;
                }
                else
                {
                    if (cur.Url == k.Url && cur.Pass == k.Pass)
                        return i;
                }
            }
            return -1;
        }

        private static void ValidateEmbeddedKeys()
        {
            // FIX: Guard against a null hardcoded list. Previously this would throw
            // mid-filter and leave keys.Data in a partially replaced state.
            var hardcoded = BasisUI.EmbeddedItems.HardcodedKeys;
            if (hardcoded == null)
            {
                BasisDebug.LogWarning("HardcodedKeys list is null. Skipping embedded key validation.");
                return;
            }

            var filtered = new List<ItemKey>();

            foreach (var key in keys.Data)
            {
                if (key == null) continue;

                if (!key.EmbeddedSettings.IsEmbedded)
                {
                    filtered.Add(key);
                    continue;
                }

                bool existsInHardcoded = false;
                foreach (var item in hardcoded)
                {
                    if (item != null && item.Url == key.Url)
                    {
                        existsInHardcoded = true;
                        break;
                    }
                }

                if (existsInHardcoded)
                    filtered.Add(key);
            }

            keys.Data = filtered.ToArray();

            foreach (var item in hardcoded)
            {
                if (item == null) continue;

                if (IndexOfKey(item) < 0)
                {
                    var copy = new ItemKey
                    {
                        Mode = item.Mode,
                        PlacementType = item.PlacementType,
                        Url = item.Url,
                        Pass = item.Pass,
                        EmbeddedSettings = item.EmbeddedSettings,
                        PinnedSettings = item.PinnedSettings
                    };

                    int oldLen = keys.Data.Length;
                    System.Array.Resize(ref keys.Data, oldLen + 1);
                    keys.Data[oldLen] = copy;
                }
            }
        }

        // FIX: Scrub null entries that a corrupted or hand-edited JSON file might
        // introduce. Downstream code assumes every element is non-null.
        private static ItemKey[] ScrubNullEntries(ItemKey[] data)
        {
            var clean = new List<ItemKey>(data.Length);
            foreach (var k in data)
            {
                if (k != null)
                    clean.Add(k);
            }
            return clean.ToArray();
        }

        // FIX: When deserializing older JSON that predates a field being added, structs
        // are zero-initialized rather than using their Default property. This method
        // detects that case and applies the correct defaults so old save files remain
        // compatible without corrupting settings.
        private static void EnsureKeyDefaults(ItemKey key)
        {
            // EmbeddedSettings: zero-init produces IsEmbedded=false, SourceType=0 (BEEUrl).
            // That happens to match EmbeddedSettings.Default, so no correction needed there.

            // PinnedSettings: zero-init produces IsPinned=false, NetworkType=0, IsEphemeral=false.
            // IsEphemeral=false is NOT the intended default (Default has IsEphemeral=true),
            // so we detect and correct it here.
            if (!key.PinnedSettings.IsPinned && !key.PinnedSettings.IsEphemeral)
            {
                key.PinnedSettings = PinnedSettings.Default;
            }
        }
    }
}