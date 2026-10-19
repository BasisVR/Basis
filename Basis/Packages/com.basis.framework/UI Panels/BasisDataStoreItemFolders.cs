using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using static SerializableBasis;

namespace Basis.Scripts.UI.UI_Panels
{
    [AutoStaticsCleanup]
    public static partial class BasisDataStoreItemFolders
    {
        [Serializable]
        public class FolderItem
        {
            public string Url;
            public string Pass;
        }

        [Serializable]
        public class ItemFolder
        {
            public string Id;
            public string Name;
            public long CreatedUtcTicks;
            public FolderItem[] Items = Array.Empty<FolderItem>();
        }

        [Serializable]
        public class ItemFolders
        {
            public ItemFolder[] Data;
        }

        private static string _filePath;
        public static string FilePath
        {
            get
            {
                if (string.IsNullOrEmpty(_filePath)) _filePath = Path.Combine(Application.persistentDataPath, "ItemFolderStore.json");
                return _filePath;
            }
        }
        private static ItemFolders folders = new ItemFolders { Data = Array.Empty<ItemFolder>() };
        private static bool _loaded = false;
        [NoAutoStaticsCleanup] private static readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

        public static ItemFolder[] DisplayFolders() => folders?.Data ?? Array.Empty<ItemFolder>();

        public static ItemFolder Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            ItemFolder[] data = DisplayFolders();
            for (int i = 0; i < data.Length; i++)
            {
                if (data[i].Id == id) return data[i];
            }
            return null;
        }

        public static string CleanName(string name) => ContentShareFolder.CleanName(name, ContentShareFolder.MaxNameLength);

        public static bool Matches(FolderItem entry, BasisDataStoreItemKeys.ItemKey key) => entry != null && key != null && entry.Url == key.Url && (key.EmbeddedSettings.IsEmbedded || entry.Pass == key.Pass);

        public static bool Contains(ItemFolder folder, BasisDataStoreItemKeys.ItemKey key) => folder?.Items != null && IndexOf(folder.Items, key) >= 0;

        public static List<BasisDataStoreItemKeys.ItemKey> Resolve(ItemFolder folder, IReadOnlyList<BasisDataStoreItemKeys.ItemKey> library)
        {
            List<BasisDataStoreItemKeys.ItemKey> resolved = new List<BasisDataStoreItemKeys.ItemKey>(folder?.Items?.Length ?? 0);
            if (folder?.Items == null || library == null) return resolved;
            for (int i = 0; i < folder.Items.Length; i++)
            {
                for (int k = 0; k < library.Count; k++)
                {
                    if (!Matches(folder.Items[i], library[k])) continue;
                    if (!resolved.Contains(library[k])) resolved.Add(library[k]);
                    break;
                }
            }
            return resolved;
        }

        public static async Task<ItemFolder> CreateFolder(string name, IReadOnlyList<BasisDataStoreItemKeys.ItemKey> items = null)
        {
            ItemFolder folder = new ItemFolder { Id = Guid.NewGuid().ToString("N"), Name = CleanName(name), CreatedUtcTicks = DateTime.UtcNow.Ticks, Items = Merge(Array.Empty<FolderItem>(), items) };
            await Mutate(() =>
            {
                Append(folder);
                return true;
            });
            return folder;
        }

        public static Task<bool> RenameFolder(string id, string name) => Mutate(() =>
        {
            ItemFolder folder = Find(id);
            string cleaned = CleanName(name);
            if (folder == null || cleaned.Length == 0 || folder.Name == cleaned) return false;
            folder.Name = cleaned;
            return true;
        });

        public static Task<bool> RemoveFolder(string id) => Mutate(() =>
        {
            ItemFolder[] data = DisplayFolders();
            ItemFolder[] kept = Array.FindAll(data, folder => folder.Id != id);
            if (kept.Length == data.Length) return false;
            folders.Data = kept;
            return true;
        });

        public static Task<bool> SetItems(string id, IReadOnlyList<BasisDataStoreItemKeys.ItemKey> items) => Mutate(() =>
        {
            ItemFolder folder = Find(id);
            if (folder == null) return false;
            folder.Items = Merge(Array.Empty<FolderItem>(), items);
            return true;
        });

        public static Task<bool> AddItems(string id, IReadOnlyList<BasisDataStoreItemKeys.ItemKey> items) => Mutate(() =>
        {
            ItemFolder folder = Find(id);
            if (folder == null) return false;
            FolderItem[] merged = Merge(folder.Items, items);
            if (merged.Length == folder.Items.Length) return false;
            folder.Items = merged;
            return true;
        });

        public static Task<bool> RemoveItems(string id, IReadOnlyList<BasisDataStoreItemKeys.ItemKey> items) => Mutate(() =>
        {
            ItemFolder folder = Find(id);
            if (folder == null || items == null) return false;
            List<FolderItem> kept = new List<FolderItem>(folder.Items.Length);
            for (int i = 0; i < folder.Items.Length; i++)
            {
                bool removed = false;
                for (int k = 0; k < items.Count && !removed; k++)
                {
                    removed = Matches(folder.Items[i], items[k]);
                }
                if (!removed) kept.Add(folder.Items[i]);
            }
            if (kept.Count == folder.Items.Length) return false;
            folder.Items = kept.ToArray();
            return true;
        });

        public static async Task<ItemFolder> AddShared(string id, string name, IReadOnlyList<BasisDataStoreItemKeys.ItemKey> items)
        {
            ItemFolder result = null;
            await Mutate(() =>
            {
                result = Find(id);
                if (result == null)
                {
                    result = new ItemFolder { Id = id, Name = CleanName(name), CreatedUtcTicks = DateTime.UtcNow.Ticks, Items = Merge(Array.Empty<FolderItem>(), items) };
                    Append(result);
                    return true;
                }
                FolderItem[] merged = Merge(result.Items, items);
                if (merged.Length == result.Items.Length) return false;
                result.Items = merged;
                return true;
            });
            return result;
        }

        public static async Task EnsureLoaded()
        {
            if (_loaded) return;
            await _lock.WaitAsync();
            try
            {
                if (!_loaded)
                {
                    folders = await Load();
                    _loaded = true;
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        private static async Task<bool> Mutate(Func<bool> change)
        {
            await EnsureLoaded();
            bool changed;
            await _lock.WaitAsync();
            try
            {
                changed = change();
                if (changed) await Save();
            }
            finally
            {
                _lock.Release();
            }
            return changed;
        }

        private static void Append(ItemFolder folder)
        {
            ItemFolder[] data = DisplayFolders();
            ItemFolder[] grown = new ItemFolder[data.Length + 1];
            Array.Copy(data, grown, data.Length);
            grown[data.Length] = folder;
            folders.Data = grown;
        }

        private static int IndexOf(IReadOnlyList<FolderItem> entries, BasisDataStoreItemKeys.ItemKey key)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                if (Matches(entries[i], key)) return i;
            }
            return -1;
        }

        private static FolderItem[] Merge(FolderItem[] existing, IReadOnlyList<BasisDataStoreItemKeys.ItemKey> items)
        {
            List<FolderItem> merged = new List<FolderItem>(existing);
            if (items == null) return merged.ToArray();
            for (int i = 0; i < items.Count; i++)
            {
                BasisDataStoreItemKeys.ItemKey item = items[i];
                if (item == null || string.IsNullOrEmpty(item.Url) || IndexOf(merged, item) >= 0) continue;
                merged.Add(new FolderItem { Url = item.Url, Pass = item.Pass ?? string.Empty });
            }
            return merged.ToArray();
        }

        private static async Task<ItemFolders> Load()
        {
            ItemFolders loaded = await TryRead(FilePath);
            if (loaded == null && File.Exists(FilePath))
            {
                try
                {
                    File.Copy(FilePath, FilePath + ".corrupt_backup", true);
                }
                catch (Exception e)
                {
                    BasisDebug.LogError($"Could not keep a copy of the unreadable item folder file: {e.Message}");
                }
            }
            loaded ??= await TryRead(FilePath + ".bak");
            BasisDebug.Log($"Item folders loaded: {loaded?.Data?.Length ?? 0}");
            return loaded ?? new ItemFolders { Data = Array.Empty<ItemFolder>() };
        }

        private static async Task<ItemFolders> TryRead(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                byte[] bytes = await BasisFiles.ReadAllBytesAsync(path);
                if (bytes == null || bytes.Length == 0) return null;
                ItemFolders read = BasisSerialization.DeserializeValue<ItemFolders>(bytes);
                if (read == null) return null;
                read.Data = Scrub(read.Data);
                return read;
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"Failed to read item folders from {path}: {e}");
                return null;
            }
        }

        private static ItemFolder[] Scrub(ItemFolder[] data)
        {
            if (data == null) return Array.Empty<ItemFolder>();
            List<ItemFolder> clean = new List<ItemFolder>(data.Length);
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < data.Length; i++)
            {
                ItemFolder folder = data[i];
                if (folder == null) continue;
                if (!ContentShareFolder.IsValidId(folder.Id) || ids.Contains(folder.Id)) folder.Id = Guid.NewGuid().ToString("N");
                ids.Add(folder.Id);
                folder.Name = CleanName(folder.Name);
                folder.Items = folder.Items == null ? Array.Empty<FolderItem>() : Array.FindAll(folder.Items, entry => entry != null && !string.IsNullOrEmpty(entry.Url));
                clean.Add(folder);
            }
            return clean.ToArray();
        }

        private static async Task Save()
        {
            byte[] bytes;
            try
            {
                bytes = BasisSerialization.SerializeValue(folders);
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"Failed to serialize item folders: {e}");
                return;
            }
            if (bytes == null || bytes.Length == 0) return;

            string temp = FilePath + ".tmp";
            try
            {
                await BasisFiles.WriteAllBytesAsync(temp, bytes);
                if (File.Exists(FilePath)) File.Replace(temp, FilePath, FilePath + ".bak");
                else File.Move(temp, FilePath);
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"Failed to save item folders: {e}");
                try
                {
                    if (File.Exists(temp)) File.Delete(temp);
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
