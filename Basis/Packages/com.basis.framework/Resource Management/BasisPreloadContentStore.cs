using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using UnityEngine;

/// <summary>
/// Persists the "load on boot" content list as XML at
/// <c>Application.persistentDataPath/PreloadContent.xml</c>. Each entry is a BEE location
/// (local <c>file://</c> or remote http/https) + password + content mode, loaded automatically
/// on startup by <see cref="BasisBootContentLoader"/>. Built on System.Xml.Linq so it stays
/// IL2CPP-safe (no runtime serializer codegen).
/// </summary>
[AutoStaticsCleanup]
public static partial class BasisPreloadContentStore
{
    [Serializable]
    public struct PreloadEntry
    {
        public string Url;
        public string Pass;
        public BundledContentHolder.Mode Mode;
        public BundledContentHolder.PlacementType PlacementType;
        public BasisPropSpawnPlacement PlacementOverride;

        // Placed transform (world position/rotation + local scale) for props, so they restore where
        // they were put. Worlds have no placement and leave HasTransform false.
        public bool HasTransform;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Scale;
    }

    private static string _filePath;
    public static string FilePath
    {
        get
        {
            if (string.IsNullOrEmpty(_filePath))
                _filePath = Path.Combine(Application.persistentDataPath, "PreloadContent.xml");
            return _filePath;
        }
    }

    private static readonly List<PreloadEntry> _entries = new List<PreloadEntry>();
    [NoAutoStaticsCleanup] private static readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
    private static bool _loaded;

    public static async Task<IReadOnlyList<PreloadEntry>> GetAllAsync()
    {
        await EnsureLoaded();
        await _lock.WaitAsync();
        try
        {
            return _entries.ToArray();
        }
        finally
        {
            _lock.Release();
        }
    }

    public static async Task<bool> Add(PreloadEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Url)) return false;

        await EnsureLoaded();
        await _lock.WaitAsync();
        try
        {
            if (IndexOfUrl(entry.Url) >= 0) return false;
            _entries.Add(entry);
            await SaveToFile();
            BasisDebug.Log($"Preload content added: {entry.Url}");
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public static async Task<bool> Remove(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        await EnsureLoaded();
        await _lock.WaitAsync();
        try
        {
            int index = IndexOfUrl(url);
            if (index < 0) return false;
            _entries.RemoveAt(index);
            await SaveToFile();
            BasisDebug.Log($"Preload content removed: {url}");
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public static async Task<bool> Contains(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        await EnsureLoaded();
        await _lock.WaitAsync();
        try
        {
            return IndexOfUrl(url) >= 0;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static int IndexOfUrl(string url)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            if (string.Equals(_entries[i].Url, url, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    private static async Task EnsureLoaded()
    {
        if (_loaded) return;

        await _lock.WaitAsync();
        try
        {
            if (!_loaded)
            {
                await LoadInternal();
                _loaded = true;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private static async Task LoadInternal()
    {
        _entries.Clear();

        if (!File.Exists(FilePath))
            return;

        string text;
        try
        {
            text = await File.ReadAllTextAsync(FilePath);
        }
        catch (Exception e)
        {
            BasisDebug.LogError($"Failed to read preload content file: {e.Message}");
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
            return;

        try
        {
            XDocument doc = XDocument.Parse(text);
            XElement root = doc.Root;
            if (root == null) return;

            foreach (XElement item in root.Elements("Item"))
            {
                string url = (string)item.Element("Url");
                if (string.IsNullOrWhiteSpace(url)) continue;

                PreloadEntry entry = new PreloadEntry
                {
                    Url = url,
                    Pass = (string)item.Element("Pass") ?? string.Empty,
                    Mode = ParseEnum(item.Element("Mode")?.Value, BundledContentHolder.Mode.World),
                    PlacementType = ParseEnum(item.Element("PlacementType")?.Value, BundledContentHolder.PlacementType.SpawnAtPlayerOrigin),
                    PlacementOverride = ParseEnum(item.Element("PlacementOverride")?.Value, BasisPropSpawnPlacement.Unspecified),
                };

                XElement tf = item.Element("Transform");
                if (tf != null)
                {
                    entry.HasTransform = true;
                    entry.Position = new Vector3((float?)tf.Element("Px") ?? 0f, (float?)tf.Element("Py") ?? 0f, (float?)tf.Element("Pz") ?? 0f);
                    entry.Rotation = new Quaternion((float?)tf.Element("Rx") ?? 0f, (float?)tf.Element("Ry") ?? 0f, (float?)tf.Element("Rz") ?? 0f, (float?)tf.Element("Rw") ?? 1f);
                    entry.Scale = new Vector3((float?)tf.Element("Sx") ?? 1f, (float?)tf.Element("Sy") ?? 1f, (float?)tf.Element("Sz") ?? 1f);
                }

                _entries.Add(entry);
            }
        }
        catch (Exception e)
        {
            BasisDebug.LogError($"Failed to parse preload content file: {e}");
            _entries.Clear();
        }
    }

    private static T ParseEnum<T>(string value, T fallback) where T : struct
    {
        return Enum.TryParse(value, out T parsed) ? parsed : fallback;
    }

    private static XElement BuildItemElement(PreloadEntry e)
    {
        XElement item = new XElement("Item",
            new XElement("Url", e.Url ?? string.Empty),
            new XElement("Pass", e.Pass ?? string.Empty),
            new XElement("Mode", e.Mode.ToString()),
            new XElement("PlacementType", e.PlacementType.ToString()),
            new XElement("PlacementOverride", e.PlacementOverride.ToString()));

        if (e.HasTransform)
        {
            item.Add(new XElement("Transform",
                new XElement("Px", e.Position.x), new XElement("Py", e.Position.y), new XElement("Pz", e.Position.z),
                new XElement("Rx", e.Rotation.x), new XElement("Ry", e.Rotation.y), new XElement("Rz", e.Rotation.z), new XElement("Rw", e.Rotation.w),
                new XElement("Sx", e.Scale.x), new XElement("Sy", e.Scale.y), new XElement("Sz", e.Scale.z)));
        }

        return item;
    }

    private static async Task SaveToFile()
    {
        XDocument doc = new XDocument(
            new XElement("PreloadContent", _entries.Select(BuildItemElement)));

        string tempPath = FilePath + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tempPath, doc.ToString());
        }
        catch (Exception e)
        {
            BasisDebug.LogError($"Failed to write preload content temp file: {e}");
            return;
        }

        try
        {
            if (File.Exists(FilePath))
                File.Replace(tempPath, FilePath, null);
            else
                File.Move(tempPath, FilePath);
            BasisDebug.Log($"Preload content saved to {FilePath}");
        }
        catch (Exception e)
        {
            BasisDebug.LogError($"Failed to replace preload content file: {e}");
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }
}
