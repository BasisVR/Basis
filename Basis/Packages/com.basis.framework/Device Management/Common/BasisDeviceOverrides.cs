using Unity.Scripting.LifecycleManagement;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Basis.Scripts.Device_Management
{
    [Flags]
    public enum BasisDeviceIgnore
    {
        None = 0,
        Pose = 1,
        Buttons = 2,
        Sticks = 4,
        Fingers = 8,
        Pointer = 16,
        Device = 32,
    }
    [AutoStaticsCleanup]
    public static partial class BasisDeviceOverrides
    {
        private const string FileName = "deviceOverrides.json";
        private static string filePath;
        private static string FilePath => filePath ??= Path.Combine(Application.persistentDataPath, FileName);
        private sealed class Entry
        {
            public bool HasHand;
            public BasisBoneTrackedRole Hand;
            public BasisDeviceIgnore Ignore;
        }
        private static readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
        private static bool loaded;
        public static event Action OnChanged;
        public static string KeyFor(BasisInput input)
        {
            if (input == null || string.IsNullOrEmpty(input.UniqueDeviceIdentifier))
            {
                return null;
            }
            string identity = string.IsNullOrEmpty(input.DeviceSerial) ? input.UniqueDeviceIdentifier : input.DeviceSerial;
            return input.SubSystemIdentifier + "|" + identity;
        }
        public static bool IsHandRole(BasisBoneTrackedRole role)
        {
            return role == BasisBoneTrackedRole.LeftHand || role == BasisBoneTrackedRole.RightHand;
        }
        public static bool TryGetHand(string key, out BasisBoneTrackedRole role)
        {
            EnsureLoaded();
            if (!string.IsNullOrEmpty(key) && entries.TryGetValue(key, out Entry entry) && entry.HasHand)
            {
                role = entry.Hand;
                return true;
            }
            role = BasisBoneTrackedRole.CenterEye;
            return false;
        }
        public static void SetHand(string key, BasisBoneTrackedRole role)
        {
            if (string.IsNullOrEmpty(key) || !IsHandRole(role))
            {
                return;
            }
            EnsureLoaded();
            Entry entry = GetOrCreate(key);
            if (entry.HasHand && entry.Hand == role)
            {
                return;
            }
            entry.HasHand = true;
            entry.Hand = role;
            Changed();
        }
        public static void ClearHand(string key)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(key) || !entries.TryGetValue(key, out Entry entry) || !entry.HasHand)
            {
                return;
            }
            entry.HasHand = false;
            Prune(key, entry);
            Changed();
        }
        public static BasisDeviceIgnore GetIgnore(string key)
        {
            EnsureLoaded();
            return !string.IsNullOrEmpty(key) && entries.TryGetValue(key, out Entry entry) ? entry.Ignore : BasisDeviceIgnore.None;
        }
        public static void SetIgnore(string key, BasisDeviceIgnore ignore)
        {
            if (string.IsNullOrEmpty(key))
            {
                return;
            }
            EnsureLoaded();
            if (GetIgnore(key) == ignore)
            {
                return;
            }
            Entry entry = GetOrCreate(key);
            entry.Ignore = ignore;
            Prune(key, entry);
            Changed();
        }
        public static void SetIgnorePart(string key, BasisDeviceIgnore part, bool ignored)
        {
            BasisDeviceIgnore current = GetIgnore(key);
            SetIgnore(key, ignored ? current | part : current & ~part);
        }
        public static void ClearAll()
        {
            EnsureLoaded();
            if (entries.Count == 0)
            {
                return;
            }
            entries.Clear();
            Changed();
        }
        internal static void SetStoragePath(string path)
        {
            filePath = path;
            loaded = false;
            entries.Clear();
        }
        private static Entry GetOrCreate(string key)
        {
            if (!entries.TryGetValue(key, out Entry entry))
            {
                entry = new Entry();
                entries[key] = entry;
            }
            return entry;
        }
        private static void Prune(string key, Entry entry)
        {
            if (!entry.HasHand && entry.Ignore == BasisDeviceIgnore.None)
            {
                entries.Remove(key);
            }
        }
        private static void Changed()
        {
            Save();
            PushToDevices();
            OnChanged?.Invoke();
        }
        private static void PushToDevices()
        {
            BasisDeviceManagement management = BasisDeviceManagement.Instance;
            if (management == null)
            {
                return;
            }
            var devices = management.AllInputDevices;
            int count = devices.Count;
            for (int index = 0; index < count; index++)
            {
                BasisInput input = devices[index];
                if (input != null && !string.IsNullOrEmpty(input.OverrideKey))
                {
                    input.RefreshOverrides();
                }
            }
        }
        [Serializable]
        private class OverrideEntry
        {
            public string key;
            public int hand = -1;
            public int ignore;
        }
        [Serializable]
        private class OverridesFile
        {
            public List<OverrideEntry> entries = new List<OverrideEntry>();
        }
        private static void EnsureLoaded()
        {
            if (loaded)
            {
                return;
            }
            loaded = true;
            try
            {
                if (!File.Exists(FilePath))
                {
                    return;
                }
                string json = File.ReadAllText(FilePath);
                if (string.IsNullOrEmpty(json))
                {
                    return;
                }
                OverridesFile data = JsonUtility.FromJson<OverridesFile>(json);
                if (data?.entries == null)
                {
                    return;
                }
                for (int index = 0; index < data.entries.Count; index++)
                {
                    OverrideEntry stored = data.entries[index];
                    if (stored == null || string.IsNullOrEmpty(stored.key))
                    {
                        continue;
                    }
                    Entry entry = new Entry { Ignore = (BasisDeviceIgnore)stored.ignore };
                    if (stored.hand >= 0 && Enum.IsDefined(typeof(BasisBoneTrackedRole), stored.hand) && IsHandRole((BasisBoneTrackedRole)stored.hand))
                    {
                        entry.HasHand = true;
                        entry.Hand = (BasisBoneTrackedRole)stored.hand;
                    }
                    if (entry.HasHand || entry.Ignore != BasisDeviceIgnore.None)
                    {
                        entries[stored.key] = entry;
                    }
                }
            }
            catch (Exception exception)
            {
                BasisDebug.LogError($"[BasisDeviceOverrides] Failed to load {FilePath}: {exception}");
            }
        }
        private static void Save()
        {
            try
            {
                OverridesFile data = new OverridesFile();
                foreach (KeyValuePair<string, Entry> pair in entries)
                {
                    data.entries.Add(new OverrideEntry { key = pair.Key, hand = pair.Value.HasHand ? (int)pair.Value.Hand : -1, ignore = (int)pair.Value.Ignore });
                }
                File.WriteAllText(FilePath, JsonUtility.ToJson(data, true));
            }
            catch (Exception exception)
            {
                BasisDebug.LogError($"[BasisDeviceOverrides] Failed to save {FilePath}: {exception}");
            }
        }
    }
}
